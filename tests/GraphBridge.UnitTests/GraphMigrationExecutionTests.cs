using System.Reflection;
using GraphBridge.EntityFrameworkCore.Infrastructure;
using GraphBridge.EntityFrameworkCore.Migrations;
using GraphBridge.EntityFrameworkCore.Schema;
using Neo4j.Driver;

namespace GraphBridge.UnitTests;

/// <summary>Journal invariants and driver-protocol boundaries; live tests establish locking/DDL/data semantics.</summary>
public sealed class GraphMigrationExecutionTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Timeouts_and_caps_cannot_disable_execution_bounds(int invalid)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new GraphMigrationOptions(transactionTimeout: TimeSpan.FromMilliseconds(invalid)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GraphMigrationOptions(indexWaitTimeout: TimeSpan.FromMilliseconds(invalid)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GraphMigrationOptions(maxSchemaRows: invalid));
    }

    [Fact]
    public void Recovery_requires_explicit_quiescence_and_expected_generation()
    {
        Assert.Throws<ArgumentException>(() => new GraphMigrationRecovery("owner", 1, false, "stopped"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GraphMigrationRecovery("owner", 0, true, "stopped"));
        Assert.Throws<ArgumentException>(() => new GraphMigrationRecovery("owner", 1, true, ""));
        var recovery = new GraphMigrationRecovery("owner", 7, true, "Prior process terminated; pending transactions resolved.");
        Assert.Equal(7, recovery.ExpectedGeneration);
        Assert.Equal("owner", recovery.ExpectedOwnerToken);
    }

    [Fact]
    public void Bootstrap_preview_is_fixed_create_only_and_does_not_initialize_any_driver()
    {
        var commands = GraphMigrationJournal.PreviewInitialization();
        Assert.Equal(3, commands.Count);
        Assert.All(commands, command =>
        {
            Assert.StartsWith("CREATE CONSTRAINT `gb_migration_", command);
            Assert.DoesNotContain("IF NOT EXISTS", command);
            Assert.EndsWith("REQUIRE n.`id` IS UNIQUE", command);
        });
    }

    [Theory]
    [InlineData("POPULATING")]
    [InlineData("FAILED")]
    public void Journal_uniqueness_requires_online_correctly_owned_backing_indexes(string state)
    {
        var constraints = JournalConstraints();
        var indexes = JournalIndexes(state);
        Assert.Contains("ONLINE", GraphMigrationJournal.SchemaProblem(new(constraints, indexes))!);
        Assert.Null(GraphMigrationJournal.SchemaProblem(new(constraints, JournalIndexes("ONLINE"))));
        indexes[0] = new(GraphMigrationJournal.ControlConstraint, "ONLINE", "RANGE", "NODE",
            [GraphMigrationJournal.ControlLabel], ["id"], "some_other_constraint");
        Assert.NotNull(GraphMigrationJournal.SchemaProblem(new(constraints, indexes)));
    }

    [Fact]
    public void Journal_constraint_schema_drift_is_not_equivalent_initialization()
    {
        var constraints = JournalConstraints();
        constraints[0] = new(GraphMigrationJournal.ControlConstraint, "NODE_PROPERTY_UNIQUENESS", "NODE", ["WrongLabel"], ["id"]);
        Assert.Contains("conflicting", GraphMigrationJournal.SchemaProblem(new(constraints, JournalIndexes("ONLINE")))!);
        Assert.NotNull(GraphMigrationJournal.SchemaProblem(new([], [])));
    }

    [Fact]
    public void Journal_accepts_only_a_linear_completed_prefix_and_one_owned_active_migration()
    {
        var plan = Plan();
        var control = Control();
        var history = History(plan);
        var prepared = Step(plan, "Prepared");
        var snapshot = GraphMigrationJournal.Parse([control], [history], [prepared]);
        Assert.Null(GraphMigrationJournal.PlanProblem(snapshot, plan));
        Assert.Equal("Prepared", Assert.Single(snapshot.Steps).Status);
        GraphMigrationJournal.RequireOwner(snapshot, "owner", 1, plan.Id);
        Assert.Throws<InvalidOperationException>(() => GraphMigrationJournal.RequireOwner(snapshot, "owner", 2, plan.Id));
        Assert.Throws<InvalidOperationException>(() => GraphMigrationJournal.RequireOwner(snapshot, "replacement", 1, plan.Id));
    }

    [Theory]
    [InlineData("status", "Unknown")]
    [InlineData("kind", "Unknown")]
    [InlineData("checksum", "not-a-checksum")]
    [InlineData("id", "incorrect-key")]
    [InlineData("migrationId", "missing-parent")]
    public void Unknown_or_mismatched_step_records_fail_closed(string field, string value)
    {
        var plan = Plan();
        var step = Step(plan, "Prepared");
        step[field] = value;
        Assert.Throws<InvalidOperationException>(() => GraphMigrationJournal.Parse([Control()], [History(plan)], [step]));
    }

    [Fact]
    public void Duplicate_records_gapped_steps_and_missing_owner_are_not_accepted_as_recovery_state()
    {
        var plan = Plan();
        var control = Control();
        var history = History(plan);
        var step = Step(plan, "Prepared");
        Assert.Throws<InvalidOperationException>(() => GraphMigrationJournal.Parse([control, control], [history], [step]));
        Assert.Throws<InvalidOperationException>(() => GraphMigrationJournal.Parse([control], [history, history], [step]));
        Assert.Throws<InvalidOperationException>(() => GraphMigrationJournal.Parse([control], [history], [step, step]));
        step["index"] = 1L;
        Assert.Throws<InvalidOperationException>(() => GraphMigrationJournal.Parse([control], [history], [step]));
        control.Remove("ownerToken");
        Assert.Throws<InvalidOperationException>(() => GraphMigrationJournal.Parse([control], [history], []));
    }

    [Fact]
    public void Completed_history_requires_all_steps_and_matching_plan_checksum()
    {
        var plan = Plan();
        var control = Control();
        control.Remove("ownerToken");
        control.Remove("activeMigrationId");
        control["headId"] = plan.Id;
        var history = History(plan);
        history["status"] = "Completed";
        Assert.Throws<InvalidOperationException>(() => GraphMigrationJournal.Parse([control], [history], []));
        var snapshot = GraphMigrationJournal.Parse([control], [history], [Step(plan, "Completed")]);
        Assert.Null(GraphMigrationJournal.PlanProblem(snapshot, plan));
        Assert.NotNull(GraphMigrationJournal.PlanProblem(snapshot, Plan(expected: 0)));
        Assert.NotNull(GraphMigrationJournal.PlanProblem(snapshot,
            new("next", null, plan.Steps, GraphMigrationRollbackPolicy.NoRollback("test"))));
    }

    [Theory]
    [InlineData("POPULATING", true, false)]
    [InlineData("FAILED", false, true)]
    [InlineData("ONLINE", false, false)]
    public void Index_postcondition_distinguishes_waiting_failure_and_readiness(string state, bool waiting, bool failed)
    {
        var definition = IndexDefinition();
        var step = GraphMigrationStep.CreateIndex("index", definition);
        var observation = new GraphSchemaObservation([], [new(definition.Name, state, "RANGE", "NODE", ["Thing"], ["id"])]);
        var problem = GraphMigrationExecutor.SchemaPostcondition(step, observation, out var pending);
        Assert.Equal(waiting, pending);
        Assert.Equal(failed, problem is not null);
        if (failed) Assert.Contains("failed", problem!);
    }

    [Fact]
    public void Exact_preexisting_schema_is_adoptable_only_after_validated_preparation()
    {
        var definition = IndexDefinition();
        var observation = new GraphSchemaObservation([], [new(definition.Name, "ONLINE", "RANGE", "NODE", ["Thing"], ["id"])]);
        var step = GraphMigrationStep.CreateIndex("index", definition);
        Assert.NotNull(GraphMigrationExecutor.SchemaPrecondition(step, observation, recovery: false, out var fresh));
        Assert.False(fresh);
        Assert.Null(GraphMigrationExecutor.SchemaPrecondition(step, observation, recovery: true, out var recovered));
        Assert.True(recovered);
        var drop = GraphMigrationStep.DropIndex("drop", definition, "Explicit removal");
        Assert.NotNull(GraphMigrationExecutor.SchemaPrecondition(drop, new([], []), recovery: false, out _));
    }

    [Fact]
    public void A_constraint_backing_index_is_never_treated_as_a_standalone_drop_target()
    {
        var definition = IndexDefinition();
        var observation = new GraphSchemaObservation([], [new(definition.Name, "ONLINE", "RANGE", "NODE", ["Thing"], ["id"], "owner")]);
        var drop = GraphMigrationStep.DropIndex("drop", definition, "Reviewed removal");
        Assert.NotNull(GraphMigrationExecutor.SchemaPrecondition(drop, observation, recovery: false, out _));
    }

    [Fact]
    public void Data_count_contract_rejects_partial_eligibility_overflow_and_unexpected_mutation()
    {
        var step = Plan().Steps[0];
        Assert.Null(GraphMigrationExecutor.DataResultProblem(step, [Counts(1, 1, 1)], validation: false));
        Assert.NotNull(GraphMigrationExecutor.DataResultProblem(step, [Counts(1, 0, 0)], validation: false));
        Assert.NotNull(GraphMigrationExecutor.DataResultProblem(step, [Counts(3, 3, 0)], validation: false));
        Assert.NotNull(GraphMigrationExecutor.DataResultProblem(step, [Counts(1, 1, 1)], validation: true));
        Assert.Null(GraphMigrationExecutor.DataResultProblem(step, [Counts(1, 1, 0)], validation: true));
    }

    [Fact]
    public async Task Readonly_validation_does_not_initialize_missing_journal_or_commit()
    {
        var script = new Protocol();
        var result = await new GraphMigrationExecutor(script.Provider).ValidateAsync(Plan());
        Assert.False(result.IsInitialized);
        Assert.False(result.CanApply);
        Assert.NotEmpty(result.Issues);
        Assert.Equal(0, script.Count("transaction.CommitAsync"));
        Assert.Equal(1, script.Count("transaction.RollbackAsync"));
        Assert.DoesNotContain(script.Queries, query => query.StartsWith("CREATE", StringComparison.Ordinal)
            || query.Contains(" SET ", StringComparison.Ordinal) || query.StartsWith("MERGE", StringComparison.Ordinal));
        Assert.Equal(1, script.Count("session.DisposeAsync"));
    }

    [Fact]
    public async Task Concurrent_journal_change_returns_unstable_observation_instead_of_false_corruption()
    {
        var controlReads = 0;
        var script = new Protocol { Rows = query =>
        {
            if (query != GraphMigrationJournal.ReadControlQuery) return [];
            return [new() { ["record"] = new Dictionary<string, object>
            {
                ["id"] = "database", ["formatVersion"] = 1L, ["generation"] = 0L, ["lock"] = (long)controlReads++
            } }];
        } };
        var result = await new GraphMigrationExecutor(script.Provider).ValidateAsync(Plan());
        Assert.False(result.CanApply);
        Assert.Contains(result.Issues, issue => issue.Contains("changed during observation", StringComparison.Ordinal));
        Assert.Equal(0, script.Count("transaction.CommitAsync"));
    }

    [Theory]
    [InlineData("FAILED")]
    [InlineData("POPULATING")]
    public async Task Actual_readiness_loop_fails_or_times_out_without_releasing_owner_or_completing_step(string state)
    {
        var plan = new GraphMigrationPlan("migration", null,
            [GraphMigrationStep.CreateIndex("index", IndexDefinition())], GraphMigrationRollbackPolicy.NoRollback("Reviewed index creation"));
        var control = Control();
        var history = History(plan);
        var prepared = Step(plan, "Prepared");
        var script = ReadinessProtocol(plan, control, history, prepared, () => state);
        var options = new GraphMigrationOptions(indexWaitTimeout: TimeSpan.FromMilliseconds(5));
        var executor = new GraphMigrationExecutor(script.Provider, options);

        var operation = GraphMigrationTransport.UseAsync(script.Provider, options, true, async transport =>
        {
            await executor.AwaitSchemaPostconditionAsync(transport, plan, plan.Steps[0], new("owner", 1, false), default);
            return true;
        }, default);
        if (state == "FAILED")
            Assert.Contains("failed", (await Assert.ThrowsAsync<InvalidOperationException>(() => operation)).Message);
        else
            Assert.Contains("ownership and pending state are retained", (await Assert.ThrowsAsync<TimeoutException>(() => operation)).Message);

        Assert.Equal(0, script.Count("transaction.CommitAsync"));
        Assert.Equal(script.Count("session.BeginTransactionAsync"), script.Count("transaction.RollbackAsync"));
        Assert.Equal(1, script.Count("session.DisposeAsync"));
        Assert.DoesNotContain(script.Queries, query => query.Contains(" SET ", StringComparison.Ordinal)
            || query.Contains(" REMOVE ", StringComparison.Ordinal) || query.StartsWith("CREATE", StringComparison.Ordinal));
        var journal = GraphMigrationJournal.Parse([control], [history], [prepared]);
        GraphMigrationJournal.RequireOwner(journal, "owner", 1, plan.Id);
        Assert.Equal("Prepared", Assert.Single(journal.Steps).Status);
    }

    [Fact]
    public async Task Actual_readiness_loop_reobserves_population_until_online_without_reissuing_ddl()
    {
        var plan = new GraphMigrationPlan("migration", null,
            [GraphMigrationStep.CreateIndex("index", IndexDefinition())], GraphMigrationRollbackPolicy.NoRollback("Reviewed index creation"));
        var observations = 0;
        var script = ReadinessProtocol(plan, Control(), History(plan), Step(plan, "Prepared"),
            () => observations++ == 0 ? "POPULATING" : "ONLINE");
        var options = new GraphMigrationOptions(indexWaitTimeout: TimeSpan.FromSeconds(2));
        var executor = new GraphMigrationExecutor(script.Provider, options);
        await GraphMigrationTransport.UseAsync(script.Provider, options, true, async transport =>
        {
            await executor.AwaitSchemaPostconditionAsync(transport, plan, plan.Steps[0], new("owner", 1, false), default);
            return true;
        }, default);
        Assert.Equal(2, observations);
        Assert.Equal(2, script.Count("transaction.RollbackAsync"));
        Assert.Equal(0, script.Count("transaction.CommitAsync"));
        Assert.DoesNotContain(script.Queries, query => query.StartsWith("CREATE", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(GraphMigrationJournal.ControlConstraint)]
    public async Task Initialization_reobserves_inflight_index_until_actual_constraint_and_owned_online_index_exist(string? intermediateOwner)
    {
        var observations = 0;
        var script = InitializationProtocol(() => (++observations) switch
        {
            1 => new GraphSchemaObservation([], [BootstrapIndex("POPULATING", intermediateOwner)]),
            2 => new GraphSchemaObservation([], [BootstrapIndex("ONLINE", intermediateOwner)]),
            _ => new GraphSchemaObservation(JournalConstraints(), JournalIndexes("ONLINE"))
        });
        Assert.NotNull(GraphMigrationJournal.SchemaProblem(new([], [BootstrapIndex("ONLINE", intermediateOwner)]), permitMissing: true));
        // This verifies three schema observations, not a two-second performance SLA.
        // Allow hosted-runner scheduling pauses; dedicated timeout cases stay short.
        var result = await ObserveInitialization(script, TimeSpan.FromSeconds(30));
        Assert.Equal(3, observations);
        Assert.Null(GraphMigrationJournal.SchemaProblem(result.Observation));
        Assert.Equal(GraphMigrationJournal.ControlConstraint,
            Assert.Single(result.Observation.Indexes, index => index.Name == GraphMigrationJournal.ControlConstraint).OwningConstraint);
        AssertSchemaReadsOnly(script, expectedObservations: 3);
    }

    [Theory]
    [InlineData("POPULATING")]
    [InlineData("ONLINE")]
    public async Task Persistent_ownerless_bootstrap_index_times_out_without_acceptance_or_ddl_retry(string state)
    {
        var observations = 0;
        var script = InitializationProtocol(() =>
        {
            observations++;
            return new([], [BootstrapIndex(state, owner: null)]);
        });
        await Assert.ThrowsAsync<TimeoutException>(() => ObserveInitialization(script, TimeSpan.FromMilliseconds(5)));
        Assert.True(observations >= 1);
        AssertSchemaReadsOnly(script, observations);
    }

    [Fact]
    public async Task Initialization_wait_cancellation_disposes_first_observation_without_further_reads_or_writes()
    {
        using var cancellation = new CancellationTokenSource();
        var observations = 0;
        var script = InitializationProtocol(() =>
        {
            observations++;
            return new([], [BootstrapIndex("POPULATING", owner: null)]);
        }, cancellation.Cancel);
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ObserveInitialization(script, TimeSpan.FromSeconds(10), cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(1, observations);
        AssertSchemaReadsOnly(script, expectedObservations: 1);
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("index-type")]
    [InlineData("entity-type")]
    [InlineData("label")]
    [InlineData("property")]
    [InlineData("failed")]
    public async Task Conflicting_bootstrap_index_is_returned_for_existing_schema_rejection_without_waiting(string conflict)
    {
        var index = new GraphObservedIndex(GraphMigrationJournal.ControlConstraint,
            conflict == "failed" ? "FAILED" : "ONLINE", conflict == "index-type" ? "TEXT" : "RANGE",
            conflict == "entity-type" ? "RELATIONSHIP" : "NODE",
            [conflict == "label" ? "OtherLabel" : GraphMigrationJournal.ControlLabel],
            [conflict == "property" ? "otherProperty" : "id"], conflict == "owner" ? "unrelated_constraint" : null);
        var observations = 0;
        var script = InitializationProtocol(() =>
        {
            // A second read is a failure even if the test runner is slow: conflicts must not enter the wait loop.
            Assert.Equal(1, ++observations);
            return new([], [index]);
        });
        var result = await ObserveInitialization(script, TimeSpan.FromSeconds(10));
        Assert.NotNull(GraphMigrationJournal.SchemaProblem(result.Observation, permitMissing: true));
        AssertSchemaReadsOnly(script, expectedObservations: 1);
    }

    [Fact]
    public async Task Transient_bootstrap_index_does_not_hide_an_independent_conflict_or_filter_returned_observations()
    {
        var transient = BootstrapIndex("POPULATING", owner: null);
        var conflicting = new GraphObservedIndex(GraphMigrationJournal.HistoryConstraint,
            "ONLINE", "RANGE", "NODE", [GraphMigrationJournal.HistoryLabel], ["wrongProperty"]);
        var observations = 0;
        var script = InitializationProtocol(() =>
        {
            Assert.Equal(1, ++observations);
            return new([], [transient, conflicting]);
        });

        var result = await ObserveInitialization(script, TimeSpan.FromSeconds(10));

        Assert.Empty(result.Observation.Constraints);
        Assert.Equal(2, result.Observation.Indexes.Count);
        var returnedTransient = Assert.Single(result.Observation.Indexes,
            index => index.Name == GraphMigrationJournal.ControlConstraint);
        Assert.Equal("POPULATING", returnedTransient.State);
        Assert.Null(returnedTransient.OwningConstraint);
        var returnedConflict = Assert.Single(result.Observation.Indexes,
            index => index.Name == GraphMigrationJournal.HistoryConstraint);
        Assert.Equal("wrongProperty", Assert.Single(returnedConflict.Properties!));
        Assert.NotNull(GraphMigrationJournal.SchemaProblem(result.Observation, permitMissing: true));
        AssertSchemaReadsOnly(script, expectedObservations: 1);
    }

    [Fact]
    public async Task Cancellation_during_successful_commit_does_not_change_the_acknowledged_result()
    {
        using var cancellation = new CancellationTokenSource();
        var script = new Protocol { OnCommit = cancellation.Cancel };
        var result = await RunTransaction(script, () => Task.FromResult(42), cancellation.Token);
        Assert.Equal(42, result);
        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(1, script.Count("transaction.CommitAsync"));
        Assert.Equal(0, script.Count("transaction.RollbackAsync"));
        Assert.Equal(1, script.Count("transaction.DisposeAsync"));
    }

    [Fact]
    public async Task Uncertain_commit_is_not_retried_and_cleanup_does_not_replace_the_commit_error()
    {
        var primary = new IOException("commit acknowledgement lost");
        var script = new Protocol { CommitFailure = primary, CleanupFailure = true };
        Assert.Same(primary, await Assert.ThrowsAsync<IOException>(() => RunTransaction(script, () => Task.FromResult(42))));
        Assert.Equal(1, script.Count("transaction.CommitAsync"));
        Assert.Equal(1, script.Count("transaction.RollbackAsync"));
        Assert.Equal(1, script.Count("transaction.DisposeAsync"));
        Assert.Equal(1, script.Count("session.DisposeAsync"));
    }

    [Fact]
    public async Task Operation_failure_rolls_back_and_preserves_primary_exception_through_cleanup()
    {
        var primary = new InvalidOperationException("precondition failed");
        var script = new Protocol { CleanupFailure = true };
        Assert.Same(primary, await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RunTransaction(script, () => Task.FromException<int>(primary))));
        Assert.Equal(0, script.Count("transaction.CommitAsync"));
        Assert.Equal(1, script.Count("transaction.RollbackAsync"));
        Assert.Equal(1, script.Count("session.DisposeAsync"));
    }

    [Fact]
    public async Task Session_disposal_failure_after_known_commit_does_not_attempt_to_rollback_committed_work()
    {
        var script = new Protocol { SessionDisposalFailure = true };
        await Assert.ThrowsAsync<IOException>(() => RunTransaction(script, () => Task.FromResult(42)));
        Assert.Equal(1, script.Count("transaction.CommitAsync"));
        Assert.Equal(0, script.Count("transaction.RollbackAsync"));
    }

    [Fact]
    public async Task Precancellation_opens_no_driver_resources()
    {
        var script = new Protocol();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new GraphMigrationExecutor(script.Provider).ApplyAsync(Plan(), new CancellationToken(true)));
        Assert.Empty(script.Calls);
    }

    private static Task<int> RunTransaction(Protocol script, Func<Task<int>> action, CancellationToken cancellation = default)
        => GraphMigrationTransport.UseAsync(script.Provider, new GraphMigrationOptions(), false,
            transport => transport.TransactionAsync("test", "migration", "step", true, _ => action(), cancellation), cancellation);

    private static Task<(GraphSchemaCapabilities Capabilities, GraphSchemaObservation Observation)> ObserveInitialization(
        Protocol script, TimeSpan timeout, CancellationToken cancellation = default)
    {
        var options = new GraphMigrationOptions(indexWaitTimeout: timeout);
        var executor = new GraphMigrationExecutor(script.Provider, options);
        return GraphMigrationTransport.UseAsync(script.Provider, options, readOnly: true,
            transport => executor.ObserveInitializationAsync(transport, cancellation), cancellation);
    }

    private static GraphObservedIndex BootstrapIndex(string state, string? owner)
        => new(GraphMigrationJournal.ControlConstraint, state, "RANGE", "NODE", [GraphMigrationJournal.ControlLabel], ["id"], owner);

    private static void AssertSchemaReadsOnly(Protocol script, int expectedObservations)
    {
        Assert.Equal(expectedObservations, script.Count("session.BeginTransactionAsync"));
        Assert.Equal(expectedObservations, script.Count("transaction.RollbackAsync"));
        Assert.Equal(expectedObservations, script.Count("transaction.DisposeAsync"));
        Assert.Equal(0, script.Count("transaction.CommitAsync"));
        Assert.Equal(1, script.Count("session.DisposeAsync"));
        Assert.All(script.Queries, query => Assert.True(query is GraphSchemaInspector.ComponentsQuery
            or GraphSchemaInspector.ConstraintsQuery or GraphSchemaInspector.IndexesQuery,
            "Initialization observation must execute only component/schema reads."));
    }

    private static Protocol InitializationProtocol(Func<GraphSchemaObservation> nextObservation, Action? onRollback = null)
    {
        GraphSchemaObservation? current = null;
        return new()
        {
            OnRollback = onRollback,
            Rows = query =>
            {
                if (query == GraphSchemaInspector.ConstraintsQuery)
                {
                    current = nextObservation();
                    return current.Constraints.Select(constraint => new Dictionary<string, object>
                    {
                        ["name"] = constraint.Name, ["type"] = constraint.Type, ["entityType"] = constraint.EntityType,
                        ["labelsOrTypes"] = constraint.LabelsOrTypes!, ["properties"] = constraint.Properties!, ["propertyType"] = null!
                    }).ToArray();
                }
                if (query == GraphSchemaInspector.IndexesQuery)
                    return (current ?? throw new InvalidOperationException("SHOW INDEXES must follow SHOW CONSTRAINTS."))
                        .Indexes.Select(index => new Dictionary<string, object>
                        {
                            ["name"] = index.Name, ["state"] = index.State, ["type"] = index.Type, ["entityType"] = index.EntityType,
                            ["labelsOrTypes"] = index.LabelsOrTypes!, ["properties"] = index.Properties!, ["owningConstraint"] = index.OwningConstraint!
                        }).ToArray();
                return [];
            }
        };
    }

    private static GraphMigrationPlan Plan(long expected = 1) => new("migration", null,
        [GraphMigrationStep.Backfill("fill", new("NODE", "Thing", new Dictionary<string, object?>(), 2), "name", "value", expected)],
        GraphMigrationRollbackPolicy.NoRollback("Reviewed one-way test"));
    private static GraphMigrationSchemaDefinition IndexDefinition() => new("thing_index", GraphMigrationSchemaKind.RangeIndex, "NODE", "Thing", ["id"]);
    private static Dictionary<string, object?> Control() => new()
    {
        ["id"] = "database", ["formatVersion"] = 1L, ["generation"] = 1L, ["lock"] = 1L,
        ["ownerToken"] = "owner", ["activeMigrationId"] = "migration"
    };
    private static Dictionary<string, object?> History(GraphMigrationPlan plan) => new()
    {
        ["id"] = plan.Id, ["formatVersion"] = 1L, ["checksum"] = plan.Checksum, ["status"] = "Applying", ["stepCount"] = (long)plan.Steps.Count
    };
    private static Dictionary<string, object?> Step(GraphMigrationPlan plan, string status) => new()
    {
        ["id"] = GraphMigrationJournal.StepKey(plan.Id, plan.Steps[0].Id), ["migrationId"] = plan.Id,
        ["stepId"] = plan.Steps[0].Id, ["index"] = 0L, ["kind"] = plan.Steps[0].Kind.ToString(),
        ["checksum"] = plan.Checksum, ["status"] = status, ["ownerGeneration"] = 1L
    };
    private static IReadOnlyDictionary<string, object?> Counts(long matched, long eligible, long affected)
        => new Dictionary<string, object?> { ["matched"] = matched, ["eligible"] = eligible, ["affected"] = affected };
    private static GraphObservedConstraint[] JournalConstraints() => GraphMigrationJournal.Definitions.Select(definition =>
        new GraphObservedConstraint(definition.Name, "NODE_PROPERTY_UNIQUENESS", "NODE", [definition.Label], ["id"])).ToArray();
    private static GraphObservedIndex[] JournalIndexes(string state) => GraphMigrationJournal.Definitions.Select(definition =>
        new GraphObservedIndex(definition.Name, state, "RANGE", "NODE", [definition.Label], ["id"], definition.Name)).ToArray();

    private static Protocol ReadinessProtocol(GraphMigrationPlan plan, Dictionary<string, object?> control,
        Dictionary<string, object?> history, Dictionary<string, object?> prepared, Func<string> nextState)
        => new() { Rows = query => query switch
        {
            GraphMigrationJournal.ReadControlQuery => [new() { ["record"] = control }],
            GraphMigrationJournal.ReadHistoryQuery => [new() { ["record"] = history }],
            GraphMigrationJournal.ReadStepsQuery => [new() { ["record"] = prepared }],
            GraphSchemaInspector.ConstraintsQuery => JournalConstraints().Select(constraint => new Dictionary<string, object>
            {
                ["name"] = constraint.Name, ["type"] = constraint.Type, ["entityType"] = constraint.EntityType,
                ["labelsOrTypes"] = constraint.LabelsOrTypes!, ["properties"] = constraint.Properties!, ["propertyType"] = null!
            }).ToArray(),
            GraphSchemaInspector.IndexesQuery => JournalIndexes("ONLINE").Append(new GraphObservedIndex(
                plan.Steps[0].Schema!.Name, nextState(), "RANGE", "NODE", ["Thing"], ["id"]))
                .Select(index => new Dictionary<string, object>
                {
                    ["name"] = index.Name, ["state"] = index.State, ["type"] = index.Type, ["entityType"] = index.EntityType,
                    ["labelsOrTypes"] = index.LabelsOrTypes!, ["properties"] = index.Properties!, ["owningConstraint"] = index.OwningConstraint!
                }).ToArray(),
            _ => []
        } };

    private sealed class Protocol
    {
        public List<string> Calls { get; } = [];
        public List<string> Queries { get; } = [];
        public Action? OnCommit { get; init; }
        public Action? OnRollback { get; init; }
        public Exception? CommitFailure { get; init; }
        public bool CleanupFailure { get; init; }
        public bool SessionDisposalFailure { get; init; }
        public Func<string, Dictionary<string, object>[]>? Rows { get; init; }
        public Neo4jDbContextOptionsExtension Provider { get; }
        public int Count(string name) => Calls.Count(value => value == name);

        public Protocol()
        {
            var transaction = NativeQueryTests.Proxy.For<IAsyncTransaction>((method, args) =>
            {
                Calls.Add("transaction." + method.Name);
                if (method.Name == "RunAsync")
                {
                    var query = Assert.IsType<string>(args![0]);
                    Queries.Add(query);
                    return Task.FromResult(Cursor(query));
                }
                if (method.Name == "CommitAsync")
                {
                    OnCommit?.Invoke();
                    return CommitFailure is null ? Task.CompletedTask : Task.FromException(CommitFailure);
                }
                if (method.Name == "RollbackAsync") OnRollback?.Invoke();
                return Complete(method, CleanupFailure);
            });
            var session = NativeQueryTests.Proxy.For<IAsyncSession>((method, args) =>
            {
                Calls.Add("session." + method.Name);
                if (method.Name == "BeginTransactionAsync")
                {
                    Assert.IsType<Action<TransactionConfigBuilder>>(args![0]);
                    return Task.FromResult(transaction);
                }
                return Complete(method, CleanupFailure || SessionDisposalFailure);
            });
            var driver = NativeQueryTests.Proxy.For<IDriver>((method, _) =>
            {
                Calls.Add("driver." + method.Name);
                return method.Name == "AsyncSession" ? session : throw new NotSupportedException(method.Name);
            });
            Provider = new(driver, "selected");
        }

        private IResultCursor Cursor(string query)
        {
            Dictionary<string, object>[] rows = query == GraphSchemaInspector.ComponentsQuery
                ? [new() { ["name"] = "Neo4j Kernel", ["versions"] = new[] { "2026.09.0" }, ["edition"] = "community" },
                   new() { ["name"] = "Cypher", ["versions"] = new[] { "5", "25" }, ["edition"] = "community" }]
                : Rows?.Invoke(query) ?? [];
            var position = -1;
            var open = true;
            return NativeQueryTests.Proxy.For<IResultCursor>((method, _) => method.Name switch
            {
                "FetchAsync" => Task.FromResult(++position < rows.Length),
                "get_Current" => NativeQueryTests.Proxy.For<IRecord>((member, _) => member.Name == "get_Values" ? rows[position] : throw new NotSupportedException(member.Name)),
                "get_IsOpen" => open,
                "ConsumeAsync" => Consume(),
                _ => throw new NotSupportedException(method.Name)
            });
            Task<IResultSummary> Consume() { open = false; return Task.FromResult<IResultSummary>(null!); }
        }

        private static object Complete(MethodInfo method, bool fail)
        {
            var task = fail ? Task.FromException(new IOException("cleanup failed")) : Task.CompletedTask;
            return method.ReturnType == typeof(ValueTask) ? new ValueTask(task) : task;
        }
    }
}
