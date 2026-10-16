using GraphBridge.EntityFrameworkCore.Infrastructure;
using GraphBridge.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore;
using Neo4j.Driver;

namespace GraphBridge.IntegrationTests;

[Collection(Neo4jIntegrationCollection.Name)]
public sealed partial class ProviderMigrationTests(DisposableNeo4jFixture fixture) : IAsyncLifetime
{
    private readonly string _suffix = Guid.NewGuid().ToString("N");
    private readonly List<GraphMigrationSchemaDefinition> _schema = [];
    private string Label => "GraphBridgeMigrationTest_" + _suffix;

    public Task InitializeAsync() => Enabled ? ResetJournal() : Task.CompletedTask;
    public async Task DisposeAsync()
    {
        if (!Enabled) return;
        foreach (var schema in _schema)
            await Execute($"DROP {(schema.IsIndex ? "INDEX" : "CONSTRAINT")} `{schema.Name}` IF EXISTS");
        await Execute($"MATCH ()-[r:`{Label}`]->() DELETE r");
        await Execute($"MATCH (n:`{Label}`) DELETE n");
        await ResetJournal();
    }
    private static bool Enabled => Environment.GetEnvironmentVariable("GRAPHBRIDGE_INTEGRATION") == "1";

    [Neo4jIntegrationFact]
    public async Task Preview_and_validation_do_not_initialize_or_mutate_database()
    {
        await using var db = Context();
        var plan = Plan(GraphMigrationStep.AssertPrecondition("empty", Scope(), 0));
        var before = await SchemaNames();
        var preview = plan.Preview();
        Assert.Equal(plan.Checksum, preview.Checksum);
        Assert.Single(preview.Steps);
        var validation = await db.Database.ValidateGraphMigrationAsync(plan);
        Assert.False(validation.IsInitialized);
        Assert.False(validation.CanApply);
        Assert.Equal(before, await SchemaNames());
        Assert.Equal(0L, await Scalar("MATCH (n:__GraphBridgeMigrationControl) RETURN count(n) AS value"));
        Assert.Null(db.Database.CurrentTransaction);
        Assert.Empty(db.ChangeTracker.Entries());
    }

    [Neo4jIntegrationFact]
    public async Task Concurrent_initialization_creates_one_control_record_and_is_repeatable()
    {
        for (var iteration = 0; iteration < 3; iteration++)
        {
            if (iteration > 0) await ResetJournal();
            await using var first = Context();
            await using var second = Context();
            var outcomes = await Task.WhenAll(
                Record.ExceptionAsync(() => first.Database.InitializeGraphMigrationsAsync()),
                Record.ExceptionAsync(() => second.Database.InitializeGraphMigrationsAsync()));
            Assert.True(outcomes.Any(error => error is null), string.Join("\n", outcomes.Select(error => error?.ToString())));
            foreach (var error in outcomes.Where(error => error is not null))
                Assert.True(error is Neo4jException { Code: "Neo.TransientError.Transaction.DeadlockDetected" },
                    $"Initialization race iteration {iteration}: {error}");
            // Neo4j may abort one simultaneous schema creator. Explicit rerun reconciles exact schema;
            // the provider does not silently retry failed/uncertain transactions.
            await first.Database.InitializeGraphMigrationsAsync();
            Assert.Equal(1L, await Scalar("MATCH (n:__GraphBridgeMigrationControl) RETURN count(n) AS value"));
            var validation = await first.Database.ValidateGraphMigrationAsync(Plan(GraphMigrationStep.AssertPrecondition("empty", Scope(), 0)));
            Assert.True(validation.IsInitialized);
            Assert.True(validation.CanApply, string.Join("; ", validation.Issues));
            Assert.Null(validation.OwnerToken);
        }
    }

    [Neo4jIntegrationFact]
    public async Task Schema_and_data_steps_apply_in_order_replay_is_noop_and_changed_checksum_rejects()
    {
        await Seed(new Dictionary<string, object>() { ["id"] = 1L, ["legacy"] = "Ada" }, new() { ["id"] = 2L, ["legacy"] = "Grace" });
        var unique = Definition("key", GraphMigrationSchemaKind.UniquenessConstraint, "id");
        var index = Definition("name", GraphMigrationSchemaKind.RangeIndex, "name");
        var plan = Plan(
            GraphMigrationStep.CreateConstraint("key", unique),
            GraphMigrationStep.AssertPrecondition("count", Scope(), 2),
            GraphMigrationStep.RenameProperty("rename", Scope(), "legacy", "name", 2, "Rename test data explicitly"),
            GraphMigrationStep.Backfill("flag", Scope(), "enabled", true, 2),
            GraphMigrationStep.CreateIndex("index", index));
        await using var db = Context();
        await db.Database.InitializeGraphMigrationsAsync();
        var applied = await db.Database.ApplyGraphMigrationAsync(plan);
        Assert.False(applied.AlreadyApplied);
        Assert.Equal(5, applied.CompletedSteps);
        Assert.Equal(2L, await Scalar($"MATCH (n:`{Label}`) WHERE n.name IS NOT NULL AND n.legacy IS NULL AND n.enabled=true RETURN count(n) AS value"));
        var validation = await db.Database.ValidateGraphMigrationAsync(plan);
        Assert.True(validation.AlreadyApplied);
        Assert.Null(validation.OwnerToken);
        Assert.Equal(5, validation.CompletedStepIds.Count);
        Assert.True((await db.Database.ApplyGraphMigrationAsync(plan)).AlreadyApplied);
        var changed = new GraphMigrationPlan(plan.Id, null, [GraphMigrationStep.AssertPrecondition("different", Scope(), 2)],
            GraphMigrationRollbackPolicy.NoRollback("Explicit test plan"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.Database.ApplyGraphMigrationAsync(changed));
        Assert.Equal(2L, await Scalar($"MATCH (n:`{Label}` {{enabled:true}}) RETURN count(n) AS value"));
    }

    [Neo4jIntegrationFact]
    public async Task Data_failure_rolls_back_mutation_and_completion_record_before_explicit_recovery()
    {
        await Seed(new Dictionary<string, object>() { ["id"] = 1L });
        var plan = Plan(GraphMigrationStep.Backfill("fill", Scope(), "value", 7L, 1));
        await using var db = Context();
        await db.Database.InitializeGraphMigrationsAsync();
        var primary = new IOException("Injected interruption after data execution before journal commit");
        var executor = Executor(checkpoint => checkpoint.Stage == GraphMigrationCheckpointStage.DataExecuted
            ? Task.FromException(primary) : Task.CompletedTask);
        Assert.Same(primary, await Assert.ThrowsAsync<IOException>(() => executor.ApplyAsync(plan, default)));
        Assert.Equal(0L, await Scalar($"MATCH (n:`{Label}`) WHERE n.value IS NOT NULL RETURN count(n) AS value"));
        var failed = await db.Database.ValidateGraphMigrationAsync(plan);
        Assert.NotNull(failed.OwnerToken);
        Assert.Empty(failed.CompletedStepIds);
        var recovered = await db.Database.RecoverGraphMigrationAsync(plan, Recovery(failed));
        Assert.Equal(1, recovered.CompletedSteps);
        Assert.Equal(1L, await Scalar($"MATCH (n:`{Label}` {{value:7}}) RETURN count(n) AS value"));
    }

    [Neo4jIntegrationFact]
    public async Task Crash_after_schema_commit_recovers_exact_postcondition_without_repeating_DDL()
    {
        var unique = Definition("recovery", GraphMigrationSchemaKind.UniquenessConstraint, "id");
        var plan = Plan(GraphMigrationStep.CreateConstraint("key", unique));
        await using var db = Context();
        await db.Database.InitializeGraphMigrationsAsync();
        var executor = Executor(c => c.Stage == GraphMigrationCheckpointStage.SchemaCommitted
            ? Task.FromException(new IOException("Simulated crash after acknowledged schema commit")) : Task.CompletedTask);
        await Assert.ThrowsAsync<IOException>(() => executor.ApplyAsync(plan, default));
        Assert.Contains("constraint:" + unique.Name, await SchemaNames());
        var failed = await db.Database.ValidateGraphMigrationAsync(plan);
        Assert.NotNull(failed.OwnerToken);
        Assert.Empty(failed.CompletedStepIds);
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.Database.ApplyGraphMigrationAsync(plan));
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.Database.RecoverGraphMigrationAsync(plan,
            new GraphMigrationRecovery(failed.OwnerToken!, failed.OwnerGeneration + 1, true, "Wrong generation must reject")));
        Assert.Equal(1, (await db.Database.RecoverGraphMigrationAsync(plan, Recovery(failed))).CompletedSteps);
        Assert.Null((await db.Database.ValidateGraphMigrationAsync(plan)).OwnerToken);
        Assert.Contains("constraint:" + unique.Name, await SchemaNames());
    }

    [Neo4jIntegrationFact]
    public async Task Crash_after_data_commit_uses_atomic_journal_to_avoid_duplicate_backfill()
    {
        await Seed(new Dictionary<string, object>() { ["id"] = 1L });
        var plan = Plan(GraphMigrationStep.Backfill("fill", Scope(), "value", "once", 1));
        await using var db = Context();
        await db.Database.InitializeGraphMigrationsAsync();
        var executor = Executor(c => c.Stage == GraphMigrationCheckpointStage.DataCommitted
            ? Task.FromException(new IOException("Simulated lost completion response")) : Task.CompletedTask);
        await Assert.ThrowsAsync<IOException>(() => executor.ApplyAsync(plan, default));
        var failed = await db.Database.ValidateGraphMigrationAsync(plan);
        Assert.Equal(new[] { "fill" }, failed.CompletedStepIds);
        Assert.Equal(1L, await Scalar($"MATCH (n:`{Label}` {{value:'once'}}) RETURN count(n) AS value"));
        Assert.Equal(1, (await db.Database.RecoverGraphMigrationAsync(plan, Recovery(failed))).CompletedSteps);
        Assert.True((await db.Database.ApplyGraphMigrationAsync(plan)).AlreadyApplied);
    }

    [Neo4jIntegrationFact]
    public async Task Two_migrators_have_one_owner_and_loser_executes_no_steps()
    {
        await Seed(new Dictionary<string, object>() { ["id"] = 1L });
        var plan = Plan(GraphMigrationStep.Backfill("fill", Scope(), "winner", true, 1));
        await using var db = Context();
        await db.Database.InitializeGraphMigrationsAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var owner = Executor(async c =>
        {
            if (c.Stage != GraphMigrationCheckpointStage.OwnerAcquired) return;
            entered.TrySetResult();
            await release.Task.WaitAsync(TimeSpan.FromSeconds(30));
        });
        var applying = owner.ApplyAsync(plan, default);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            var held = await db.Database.ValidateGraphMigrationAsync(plan);
            Assert.NotNull(held.OwnerToken);
            await Execute("MATCH (c:__GraphBridgeMigrationControl {id:'database'}) SET c.acquiredAt='2000-01-01T00:00:00Z'");
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.Database.ApplyGraphMigrationAsync(plan));
            var after = await db.Database.ValidateGraphMigrationAsync(plan);
            Assert.Equal(held.OwnerToken, after.OwnerToken);
            Assert.Equal(held.OwnerGeneration, after.OwnerGeneration);
            Assert.Equal(0L, await Scalar($"MATCH (n:`{Label}`) WHERE n.winner IS NOT NULL RETURN count(n) AS value"));
        }
        finally { release.TrySetResult(); await applying; }
        Assert.Equal(1L, await Scalar($"MATCH (n:`{Label}` {{winner:true}}) RETURN count(n) AS value"));
    }

    [Neo4jIntegrationFact]
    public async Task Failed_schema_step_preserves_earlier_commit_and_recovers_after_explicit_data_repair()
    {
        await Seed(new Dictionary<string, object>() { ["id"] = 1L, ["duplicate"] = "same" }, new() { ["id"] = 2L, ["duplicate"] = "same" });
        var first = Definition("first", GraphMigrationSchemaKind.UniquenessConstraint, "id");
        var second = Definition("second", GraphMigrationSchemaKind.UniquenessConstraint, "duplicate");
        var plan = Plan(GraphMigrationStep.CreateConstraint("first", first), GraphMigrationStep.CreateConstraint("second", second));
        await using var db = Context();
        await db.Database.InitializeGraphMigrationsAsync();
        await Assert.ThrowsAnyAsync<Neo4jException>(() => db.Database.ApplyGraphMigrationAsync(plan));
        var failed = await db.Database.ValidateGraphMigrationAsync(plan);
        Assert.Equal(new[] { "first" }, failed.CompletedStepIds);
        Assert.Contains("constraint:" + first.Name, await SchemaNames());
        Assert.DoesNotContain("constraint:" + second.Name, await SchemaNames());
        await Execute($"MATCH (n:`{Label}` {{id:2}}) SET n.duplicate='different'");
        Assert.Equal(2, (await db.Database.RecoverGraphMigrationAsync(plan, Recovery(failed))).CompletedSteps);
        Assert.Contains("constraint:" + second.Name, await SchemaNames());
    }

    [Neo4jIntegrationFact]
    public async Task Bounded_scope_rejects_excess_rows_without_partial_backfill()
    {
        await Seed(new Dictionary<string, object>() { ["id"] = 1L }, new() { ["id"] = 2L });
        var plan = Plan(GraphMigrationStep.Backfill("fill", Scope(maxRows: 1), "value", true, 1));
        await using var db = Context();
        await db.Database.InitializeGraphMigrationsAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.Database.ApplyGraphMigrationAsync(plan));
        Assert.Equal(0L, await Scalar($"MATCH (n:`{Label}`) WHERE n.value IS NOT NULL RETURN count(n) AS value"));
        Assert.Empty((await db.Database.ValidateGraphMigrationAsync(plan)).CompletedStepIds);
    }

    [Neo4jIntegrationFact]
    public async Task Rename_conflict_preserves_source_and_destination_then_recovers_after_explicit_repair()
    {
        await Seed(new Dictionary<string, object>() { ["id"] = 1L, ["source"] = "original", ["destination"] = "existing" });
        var plan = Plan(GraphMigrationStep.RenameProperty("rename", Scope(), "source", "destination", 1, "Explicit test rename"));
        await using var db = Context();
        await db.Database.InitializeGraphMigrationsAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.Database.ApplyGraphMigrationAsync(plan));
        Assert.Equal(1L, await Scalar($"MATCH (n:`{Label}` {{source:'original',destination:'existing'}}) RETURN count(n) AS value"));
        var failed = await db.Database.ValidateGraphMigrationAsync(plan);
        await Execute($"MATCH (n:`{Label}` {{id:1}}) REMOVE n.destination");
        await db.Database.RecoverGraphMigrationAsync(plan, Recovery(failed));
        Assert.Equal(1L, await Scalar($"MATCH (n:`{Label}` {{destination:'original'}}) WHERE n.source IS NULL RETURN count(n) AS value"));
    }

    [Neo4jIntegrationFact]
    public async Task Explicit_drop_checks_definition_and_predecessor_before_removing_only_expected_schema()
    {
        var definition = Definition("drop", GraphMigrationSchemaKind.UniquenessConstraint, "id");
        var create = Plan(GraphMigrationStep.CreateConstraint("create", definition));
        await using var db = Context();
        await db.Database.InitializeGraphMigrationsAsync();
        await db.Database.ApplyGraphMigrationAsync(create);
        var wrongDefinition = new GraphMigrationSchemaDefinition(definition.Name, definition.Kind, "NODE", Label, ["different"]);
        var wrong = new GraphMigrationPlan("wrong_" + _suffix, create.Id,
            [GraphMigrationStep.DropConstraint("drop", wrongDefinition, "Test definition guard")],
            GraphMigrationRollbackPolicy.NoRollback("Test only"));
        var validation = await db.Database.ValidateGraphMigrationAsync(wrong);
        Assert.False(validation.CanApply);
        Assert.NotEmpty(validation.Issues);
        Assert.Contains("constraint:" + definition.Name, await SchemaNames());
        var drop = new GraphMigrationPlan("drop_" + _suffix, create.Id,
            [GraphMigrationStep.DropConstraint("drop", definition, "Remove the explicitly named test constraint")],
            GraphMigrationRollbackPolicy.NoRollback("Explicit removal"));
        await db.Database.ApplyGraphMigrationAsync(drop);
        Assert.DoesNotContain("constraint:" + definition.Name, await SchemaNames());
        Assert.True((await db.Database.ApplyGraphMigrationAsync(create)).AlreadyApplied);
        var unrelated = new GraphMigrationPlan("unrelated_" + _suffix, null,
            [GraphMigrationStep.AssertPrecondition("empty", Scope(), 0)], GraphMigrationRollbackPolicy.NoRollback("Test only"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.Database.ApplyGraphMigrationAsync(unrelated));
    }

    [Neo4jIntegrationFact]
    public async Task Cancellation_after_owner_acquisition_retains_recoverable_owner_without_data_changes()
    {
        await Seed(new Dictionary<string, object>() { ["id"] = 1L });
        var plan = Plan(GraphMigrationStep.Backfill("fill", Scope(), "value", 1L, 1));
        await using var db = Context();
        await db.Database.InitializeGraphMigrationsAsync();
        using var cancellation = new CancellationTokenSource();
        var executor = Executor(c =>
        {
            if (c.Stage == GraphMigrationCheckpointStage.OwnerAcquired) cancellation.Cancel();
            return Task.CompletedTask;
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => executor.ApplyAsync(plan, cancellation.Token));
        var failed = await db.Database.ValidateGraphMigrationAsync(plan);
        Assert.NotNull(failed.OwnerToken);
        Assert.Empty(failed.CompletedStepIds);
        Assert.Equal(0L, await Scalar($"MATCH (n:`{Label}`) WHERE n.value IS NOT NULL RETURN count(n) AS value"));
        await db.Database.RecoverGraphMigrationAsync(plan, Recovery(failed));
        Assert.Null((await db.Database.ValidateGraphMigrationAsync(plan)).OwnerToken);
    }

    [Neo4jIntegrationFact]
    public async Task Relationship_steps_use_directed_bounded_scope_and_parameterized_values()
    {
        await Execute($"CREATE (a:`{Label}` {{id:1}}), (b:`{Label}` {{id:2}}), (a)-[:`{Label}` {{id:3,legacy:'old'}}]->(b)");
        var scope = new GraphMigrationDataScope("RELATIONSHIP", Label, new Dictionary<string, object?> { ["id"] = 3L }, 1);
        const string value = "' }) MATCH (n) DELETE n //";
        var plan = Plan(GraphMigrationStep.RenameProperty("rename", scope, "legacy", "name", 1, "Explicit relationship rename"),
            GraphMigrationStep.Backfill("fill", scope, "description", value, 1));
        await using var db = Context();
        await db.Database.InitializeGraphMigrationsAsync();
        Assert.Equal(2, (await db.Database.ApplyGraphMigrationAsync(plan)).CompletedSteps);
        await using var session = fixture.OpenSession();
        var record = await (await session.RunAsync($"MATCH (a:`{Label}`)-[r:`{Label}`]->(b:`{Label}`) RETURN a.id AS source,b.id AS target,r.name AS name,r.legacy AS legacy,r.description AS description")).SingleAsync();
        Assert.Equal(1L, record["source"].As<long>());
        Assert.Equal(2L, record["target"].As<long>());
        Assert.Equal("old", record["name"].As<string>());
        Assert.Null(record["legacy"]);
        Assert.Equal(value, record["description"].As<string>());
    }

    [Neo4jIntegrationFact]
    public async Task Selected_database_is_honored_and_active_EF_transaction_is_preserved()
    {
        await using (var missing = Context("graphbridge_missing_" + _suffix))
            await Assert.ThrowsAnyAsync<Neo4jException>(() => missing.Database.InitializeGraphMigrationsAsync());
        Assert.Equal(0L, await Scalar("MATCH (n:__GraphBridgeMigrationControl) RETURN count(n) AS value"));
        await using var db = Context();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await Assert.ThrowsAsync<NotSupportedException>(() => db.Database.InitializeGraphMigrationsAsync());
        Assert.Same(transaction, db.Database.CurrentTransaction);
        await transaction.RollbackAsync();
    }

    [Neo4jIntegrationFact]
    public Task Concurrent_application_write_is_rechecked_under_entity_lock_without_overwrite()
        => AssertConcurrentApplicationWrite(filterDestination: false);

    [Neo4jIntegrationFact]
    public Task Concurrent_application_write_is_rechecked_when_destination_was_in_initial_filter()
        => AssertConcurrentApplicationWrite(filterDestination: true);

    private async Task AssertConcurrentApplicationWrite(bool filterDestination)
    {
        await Seed(new Dictionary<string, object>() { ["id"] = 1L });
        var scope = filterDestination ? Scope(filters: new Dictionary<string, object?> { ["value"] = null }) : Scope();
        var plan = Plan(GraphMigrationStep.Backfill("fill", scope, "value", "migration", 1));
        await using var db = Context();
        await db.Database.InitializeGraphMigrationsAsync();
        await using var writerSession = fixture.OpenSession();
        await using var writer = await writerSession.BeginTransactionAsync(c => c.WithTimeout(TimeSpan.FromSeconds(30)));
        await (await writer.RunAsync($"MATCH (n:`{Label}` {{id:1}}) SET n.value='application'")).ConsumeAsync();
        var applying = db.Database.ApplyGraphMigrationAsync(plan);
        var observedBlocked = false;
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(15);
            await using var observer = fixture.OpenSession();
            while (DateTime.UtcNow < deadline && !applying.IsCompleted)
            {
                var rows = await (await observer.RunAsync(
                    "SHOW TRANSACTIONS YIELD currentQuery,status WHERE currentQuery CONTAINS $label AND status STARTS WITH 'Blocked' RETURN status",
                    new { label = Label })).ToListAsync();
                if (rows.Count > 0) { observedBlocked = true; break; }
                await Task.Delay(50);
            }
        }
        finally { await writer.CommitAsync(); }
        await Assert.ThrowsAsync<InvalidOperationException>(() => applying);
        Assert.True(observedBlocked, "Migration must actually wait on the application writer's entity lock.");
        Assert.Equal(1L, await Scalar($"MATCH (n:`{Label}` {{value:'application'}}) RETURN count(n) AS value"));
        Assert.Empty((await db.Database.ValidateGraphMigrationAsync(plan)).CompletedStepIds);
    }

    [Neo4jIntegrationFact]
    public async Task Preexisting_exact_schema_cannot_be_adopted_as_an_interrupted_create()
    {
        var definition = Definition("preexisting", GraphMigrationSchemaKind.UniquenessConstraint, "id");
        var step = GraphMigrationStep.CreateConstraint("create", definition);
        await Execute(GraphMigrationStepRenderer.Render(step).Cypher);
        var plan = Plan(step);
        await using var db = Context();
        await db.Database.InitializeGraphMigrationsAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.Database.ApplyGraphMigrationAsync(plan));
        var failed = await db.Database.ValidateGraphMigrationAsync(plan);
        Assert.Empty(failed.CompletedStepIds);
        if (failed.OwnerToken is not null)
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.Database.RecoverGraphMigrationAsync(plan, Recovery(failed)));
        Assert.Contains("constraint:" + definition.Name, await SchemaNames());
        Assert.Equal(0L, await Scalar("MATCH (s:__GraphBridgeMigrationStep) RETURN count(s) AS value"));
    }

    private DbContext Context(string? database = null)
        => new(new DbContextOptionsBuilder().UseNeo4j(fixture.Driver, database ?? fixture.Database).Options);
    private GraphMigrationExecutor Executor(Func<GraphMigrationCheckpoint, Task> checkpoint)
        => new(new Neo4jDbContextOptionsExtension(fixture.Driver, fixture.Database), checkpoint: checkpoint);
    private GraphMigrationPlan Plan(params GraphMigrationStep[] steps)
        => new("test_" + _suffix, null, steps, GraphMigrationRollbackPolicy.NoRollback("Explicit disposable-fixture migration; no automatic compensation"));
    private GraphMigrationDataScope Scope(int maxRows = 10, IReadOnlyDictionary<string, object?>? filters = null)
        => new("NODE", Label, filters ?? new Dictionary<string, object?>(), maxRows);
    private GraphMigrationSchemaDefinition Definition(string suffix, GraphMigrationSchemaKind kind, string property)
    {
        var definition = new GraphMigrationSchemaDefinition("gb_test_" + _suffix + "_" + suffix, kind, "NODE", Label, [property]);
        _schema.Add(definition);
        return definition;
    }
    private static GraphMigrationRecovery Recovery(GraphMigrationValidation failed)
        => new(failed.OwnerToken!, failed.OwnerGeneration, true, "Test worker has exited and all its transactions are disposed");
    private async Task Seed(params Dictionary<string, object>[] rows)
    {
        foreach (var row in rows) await Execute($"CREATE (n:`{Label}`) SET n=$properties", new { properties = row });
    }
    private async Task Execute(string query, object? parameters = null)
    {
        await using var session = fixture.OpenSession();
        await (parameters is null ? await session.RunAsync(query) : await session.RunAsync(query, parameters)).ConsumeAsync();
    }
    private async Task<long> Scalar(string query)
    {
        await using var session = fixture.OpenSession();
        return (await (await session.RunAsync(query)).SingleAsync())["value"].As<long>();
    }
    private async Task<string[]> SchemaNames()
    {
        await using var session = fixture.OpenSession();
        var constraints = await (await session.RunAsync("SHOW CONSTRAINTS YIELD name RETURN name")).ToListAsync(r => "constraint:" + r["name"].As<string>());
        var indexes = await (await session.RunAsync("SHOW INDEXES YIELD name RETURN name")).ToListAsync(r => "index:" + r["name"].As<string>());
        return constraints.Concat(indexes).Order(StringComparer.Ordinal).ToArray();
    }
    private async Task ResetJournal()
    {
        // Only the dedicated disposable fixture's reserved migration artifacts, never application data.
        foreach (var label in new[] { GraphMigrationJournal.StepLabel, GraphMigrationJournal.HistoryLabel, GraphMigrationJournal.ControlLabel })
            await Execute($"MATCH (n:{label}) DELETE n");
        foreach (var name in new[] { GraphMigrationJournal.ControlConstraint, GraphMigrationJournal.HistoryConstraint, GraphMigrationJournal.StepConstraint })
            await Execute($"DROP CONSTRAINT {name} IF EXISTS");
    }
}
