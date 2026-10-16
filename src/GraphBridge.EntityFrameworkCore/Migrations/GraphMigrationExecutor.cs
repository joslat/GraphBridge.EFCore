using System.Diagnostics;
using GraphBridge.EntityFrameworkCore.Infrastructure;
using GraphBridge.EntityFrameworkCore.Schema;
using Neo4j.Driver;

namespace GraphBridge.EntityFrameworkCore.Migrations;

/// <summary>Stepwise executor. A failed run never automatically releases durable ownership or compensates prior steps.</summary>
internal sealed class GraphMigrationExecutor(Neo4jDbContextOptionsExtension providerOptions,
    GraphMigrationOptions? options = null, Func<GraphMigrationCheckpoint, Task>? checkpoint = null)
{
    private readonly GraphMigrationOptions _options = options ?? new GraphMigrationOptions();

    internal async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await GraphMigrationTransport.UseAsync(providerOptions, _options, readOnly: false, async transport =>
        {
            for (var index = 0; index < GraphMigrationJournal.Definitions.Length; index++)
            {
                var name = GraphMigrationJournal.Definitions[index].Name;
                var current = await ObserveInitializationAsync(transport, cancellationToken).ConfigureAwait(false);
                if (current.Capabilities.NodeUniqueness != GraphSchemaCapabilitySupport.Supported)
                    throw new NotSupportedException("Migration initialization requires known node uniqueness support.");
                ThrowProblem(GraphMigrationJournal.SchemaProblem(current.Observation, permitMissing: true));
                if (current.Observation.Constraints.Any(value => value.Name == name)) continue;
                try
                {
                    await transport.TransactionAsync("initialize-schema", null, null, commit: true, async transaction =>
                    {
                        await GraphMigrationTransport.QueryAsync(transaction, GraphMigrationJournal.PreviewInitialization()[index],
                            null, 1, cancellationToken).ConfigureAwait(false);
                        return true;
                    }, cancellationToken).ConfigureAwait(false);
                }
                catch (Neo4jException error) when (error.Code is "Neo.ClientError.Schema.EquivalentSchemaRuleAlreadyExists"
                    or "Neo.ClientError.Schema.ConstraintWithNameAlreadyExists" or "Neo.ClientError.Schema.ConstraintAlreadyExists")
                {
                    // Only a known concurrent-create error may be reconciled. Other errors retain their meaning.
                    var raced = await ObserveInitializationAsync(transport, cancellationToken).ConfigureAwait(false);
                    ThrowProblem(GraphMigrationJournal.SchemaProblem(raced.Observation, permitMissing: true));
                    if (!raced.Observation.Constraints.Any(value => value.Name == name)) throw;
                }
            }
            await transport.TransactionAsync("initialize-control", null, null, commit: true, async transaction =>
            {
                var observed = await InspectAsync(transaction, cancellationToken).ConfigureAwait(false);
                ThrowProblem(GraphMigrationJournal.SchemaProblem(observed.Observation));
                await GraphMigrationTransport.QueryAsync(transaction, GraphMigrationJournal.InitializeControlQuery,
                    new Dictionary<string, object?> { ["id"] = GraphMigrationJournal.ControlId }, 1, cancellationToken).ConfigureAwait(false);
                await GraphMigrationJournal.LockAsync(transaction, cancellationToken).ConfigureAwait(false);
                _ = await ReadJournalAsync(transaction, cancellationToken).ConfigureAwait(false);
                return true;
            }, cancellationToken).ConfigureAwait(false);
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    internal Task<GraphMigrationValidation> ValidateAsync(GraphMigrationPlan plan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return GraphMigrationTransport.UseAsync(providerOptions, _options, readOnly: true, transport =>
            transport.TransactionAsync("validate", plan.Id, null, commit: false, async transaction =>
            {
                var observed = await InspectAsync(transaction, cancellationToken).ConfigureAwait(false);
                MigrationJournalSnapshot journal;
                try { journal = await ReadJournalAsync(transaction, cancellationToken).ConfigureAwait(false); }
                catch (GraphMigrationObservationChangedException error)
                {
                    return new GraphMigrationValidation(false, [error.Message], null, 0, null, [], false);
                }
                var issues = CapabilityProblems(plan, observed.Capabilities).ToList();
                var schemaProblem = GraphMigrationJournal.SchemaProblem(observed.Observation);
                if (schemaProblem is not null) issues.Add(schemaProblem);
                if (journal.Control is null) issues.Add("Migration control record is missing; explicitly initialize the journal.");
                var planProblem = GraphMigrationJournal.PlanProblem(journal, plan);
                if (planProblem is not null) issues.Add(planProblem);
                var history = journal.History.SingleOrDefault(value => value.Id == plan.Id);
                var completed = journal.Steps.Where(value => value.MigrationId == plan.Id && value.Status == "Completed")
                    .OrderBy(value => value.Index).Select(value => value.StepId).ToArray();
                var alreadyApplied = history?.Status == "Completed" && planProblem is null;
                // Only the first unfinished step has a meaningful current-state precondition.
                // Later steps may depend on the schema/data changed by earlier steps and are rechecked during apply.
                if (issues.Count == 0 && !alreadyApplied)
                {
                    var next = plan.Steps.Skip(completed.Length).FirstOrDefault();
                    if (next is not null)
                    {
                        var prepared = journal.Steps.Any(value => value.MigrationId == plan.Id && value.StepId == next.Id && value.Status == "Prepared");
                        if (next.Schema is not null)
                        {
                            var problem = SchemaPrecondition(next, observed.Observation, prepared, out _);
                            if (problem is not null) issues.Add(problem);
                        }
                        else
                        {
                            var command = GraphMigrationStepRenderer.RenderPrecondition(next);
                            var rows = await GraphMigrationTransport.QueryAsync(transaction, command.Cypher, command.Parameters, 1, cancellationToken).ConfigureAwait(false);
                            var problem = DataResultProblem(next, rows, validation: true);
                            if (problem is not null) issues.Add(problem);
                        }
                    }
                }
                return new GraphMigrationValidation(schemaProblem is null && journal.Control is not null, issues,
                    journal.Control?.OwnerToken, journal.Control?.Generation ?? 0, history?.Status, completed, alreadyApplied);
            }, cancellationToken), cancellationToken);
    }

    internal Task<GraphMigrationResult> ApplyAsync(GraphMigrationPlan plan, CancellationToken cancellationToken = default)
        => RunAsync(plan, null, cancellationToken);

    internal Task<GraphMigrationResult> RecoverAsync(GraphMigrationPlan plan, GraphMigrationRecovery recovery,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recovery);
        return RunAsync(plan, recovery, cancellationToken);
    }

    private Task<GraphMigrationResult> RunAsync(GraphMigrationPlan plan, GraphMigrationRecovery? recovery,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return GraphMigrationTransport.UseAsync(providerOptions, _options, readOnly: false, async transport =>
        {
            var ownership = await transport.TransactionAsync(recovery is null ? "acquire" : "recover", plan.Id, null,
                commit: true, async transaction =>
                {
                    var observed = await InspectAsync(transaction, cancellationToken).ConfigureAwait(false);
                    ThrowProblem(GraphMigrationJournal.SchemaProblem(observed.Observation));
                    ThrowProblem(CapabilityProblems(plan, observed.Capabilities).FirstOrDefault());
                    await GraphMigrationJournal.LockAsync(transaction, cancellationToken).ConfigureAwait(false);
                    var journal = await ReadJournalAsync(transaction, cancellationToken).ConfigureAwait(false);
                    return await GraphMigrationJournalWriter.AcquireAsync(transaction, journal, plan, recovery, cancellationToken).ConfigureAwait(false);
                }, cancellationToken).ConfigureAwait(false);
            if (ownership.AlreadyApplied)
                return new GraphMigrationResult(plan.Id, plan.Checksum, true, plan.Steps.Count);
            await CheckpointAsync(GraphMigrationCheckpointStage.OwnerAcquired, plan, null, ownership).ConfigureAwait(false);
            for (var index = 0; index < plan.Steps.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var step = plan.Steps[index];
                var prior = await PrepareAsync(transport, plan, step, index, ownership, cancellationToken).ConfigureAwait(false);
                if (prior == "Completed") continue;
                await CheckpointAsync(GraphMigrationCheckpointStage.StepPrepared, plan, step, ownership).ConfigureAwait(false);
                if (step.Schema is not null)
                    await ExecuteSchemaAsync(transport, plan, step, ownership, prior == "Prepared", cancellationToken).ConfigureAwait(false);
                else
                    await ExecuteDataAsync(transport, plan, step, ownership, cancellationToken).ConfigureAwait(false);
            }
            await CheckpointAsync(GraphMigrationCheckpointStage.BeforeOwnerReleased, plan, null, ownership).ConfigureAwait(false);
            await OwnedTransactionAsync(transport, "complete", plan, null, ownership, async (transaction, journal) =>
            {
                if (journal.Steps.Count(value => value.MigrationId == plan.Id && value.Status == "Completed") != plan.Steps.Count)
                    throw new InvalidOperationException("Cannot complete a migration with unfinished steps.");
                await GraphMigrationJournalWriter.CompleteMigrationAsync(transaction, plan, ownership, cancellationToken).ConfigureAwait(false);
                return true;
            }, cancellationToken).ConfigureAwait(false);
            return new GraphMigrationResult(plan.Id, plan.Checksum, false, plan.Steps.Count);
        }, cancellationToken);
    }

    private Task<string?> PrepareAsync(GraphMigrationTransport transport, GraphMigrationPlan plan, GraphMigrationStep step,
        int index, MigrationOwnership ownership, CancellationToken cancellationToken)
        => OwnedTransactionAsync<string?>(transport, "prepare", plan, step, ownership, async (transaction, journal) =>
        {
            var existing = journal.Steps.SingleOrDefault(value => value.MigrationId == plan.Id && value.StepId == step.Id);
            if (existing is not null) return existing.Status;
            if (journal.Steps.Count(value => value.MigrationId == plan.Id && value.Status == "Completed") != index)
                throw new InvalidOperationException("Migration step order does not match the completed journal prefix.");
            if (step.Schema is not null)
            {
                // Prepared is durable evidence that a fresh schema precondition was established.
                // Otherwise a preexisting object could be incorrectly adopted by a later recovery.
                var observed = await InspectAsync(transaction, cancellationToken).ConfigureAwait(false);
                ThrowProblem(SchemaPrecondition(step, observed.Observation, recovery: false, out _));
            }
            await GraphMigrationJournalWriter.PrepareStepAsync(transaction, plan, step, index, ownership, cancellationToken).ConfigureAwait(false);
            return null;
        }, cancellationToken);

    private async Task ExecuteDataAsync(GraphMigrationTransport transport, GraphMigrationPlan plan,
        GraphMigrationStep step, MigrationOwnership ownership, CancellationToken cancellationToken)
    {
        await OwnedTransactionAsync(transport, "data-step", plan, step, ownership, async (transaction, _) =>
        {
            var command = GraphMigrationStepRenderer.Render(step);
            var rows = await GraphMigrationTransport.QueryAsync(transaction, command.Cypher, command.Parameters, 1, cancellationToken).ConfigureAwait(false);
            ThrowProblem(DataResultProblem(step, rows, validation: false));
            await CheckpointAsync(GraphMigrationCheckpointStage.DataExecuted, plan, step, ownership).ConfigureAwait(false);
            await GraphMigrationJournalWriter.CompleteStepAsync(transaction, plan, step, ownership, recovered: false, cancellationToken).ConfigureAwait(false);
            return true;
        }, cancellationToken).ConfigureAwait(false);
        await CheckpointAsync(GraphMigrationCheckpointStage.DataCommitted, plan, step, ownership).ConfigureAwait(false);
    }

    private async Task ExecuteSchemaAsync(GraphMigrationTransport transport, GraphMigrationPlan plan,
        GraphMigrationStep step, MigrationOwnership ownership, bool wasPrepared, CancellationToken cancellationToken)
    {
        var executed = await transport.TransactionAsync("schema-step", plan.Id, step.Id, commit: true, async transaction =>
        {
            // Read-only ownership verification is allowed alongside DDL. It does not fence a paused former worker;
            // recovery therefore requires the explicit prior-process quiescence declaration.
            var journal = await ReadJournalAsync(transaction, cancellationToken).ConfigureAwait(false);
            GraphMigrationJournal.RequireOwner(journal, ownership.Token, ownership.Generation, plan.Id);
            ThrowProblem(GraphMigrationJournal.PlanProblem(journal, plan));
            var observed = await InspectAsync(transaction, cancellationToken).ConfigureAwait(false);
            ThrowProblem(GraphMigrationJournal.SchemaProblem(observed.Observation));
            ThrowProblem(SchemaPrecondition(step, observed.Observation, wasPrepared, out var postconditionAlreadyPresent));
            if (postconditionAlreadyPresent) return false;
            var command = GraphMigrationStepRenderer.Render(step);
            await GraphMigrationTransport.QueryAsync(transaction, command.Cypher, command.Parameters, 1, cancellationToken).ConfigureAwait(false);
            return true;
        }, cancellationToken).ConfigureAwait(false);
        if (executed)
            await CheckpointAsync(GraphMigrationCheckpointStage.SchemaCommitted, plan, step, ownership).ConfigureAwait(false);
        await AwaitSchemaPostconditionAsync(transport, plan, step, ownership, cancellationToken).ConfigureAwait(false);
        await OwnedTransactionAsync(transport, "schema-completed", plan, step, ownership, async (transaction, _) =>
        {
            var observed = await InspectAsync(transaction, cancellationToken).ConfigureAwait(false);
            ThrowProblem(SchemaPostcondition(step, observed.Observation, out var waiting));
            if (waiting) throw new InvalidOperationException("Index readiness changed before journal completion; the step remains prepared.");
            await GraphMigrationJournalWriter.CompleteStepAsync(transaction, plan, step, ownership, recovered: !executed, cancellationToken).ConfigureAwait(false);
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    internal async Task AwaitSchemaPostconditionAsync(GraphMigrationTransport transport, GraphMigrationPlan plan,
        GraphMigrationStep step, MigrationOwnership ownership, CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        while (true)
        {
            var waiting = await transport.TransactionAsync("schema-readiness", plan.Id, step.Id, commit: false, async transaction =>
            {
                var journal = await ReadJournalAsync(transaction, cancellationToken).ConfigureAwait(false);
                GraphMigrationJournal.RequireOwner(journal, ownership.Token, ownership.Generation, plan.Id);
                var observed = await InspectAsync(transaction, cancellationToken).ConfigureAwait(false);
                ThrowProblem(GraphMigrationJournal.SchemaProblem(observed.Observation));
                ThrowProblem(SchemaPostcondition(step, observed.Observation, out var pending));
                return pending;
            }, cancellationToken).ConfigureAwait(false);
            if (!waiting) return;
            if (timer.Elapsed >= _options.IndexWaitTimeout)
                throw new TimeoutException("Migration index did not become ONLINE within the configured wait; ownership and pending state are retained for recovery.");
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Clamp((_options.IndexWaitTimeout - timer.Elapsed).TotalMilliseconds, 0, 100)), cancellationToken).ConfigureAwait(false);
        }
    }

    private Task<T> OwnedTransactionAsync<T>(GraphMigrationTransport transport, string purpose, GraphMigrationPlan plan,
        GraphMigrationStep? step, MigrationOwnership ownership,
        Func<IAsyncTransaction, MigrationJournalSnapshot, Task<T>> operation, CancellationToken cancellationToken)
        => transport.TransactionAsync(purpose, plan.Id, step?.Id, commit: true, async transaction =>
        {
            var observed = await InspectAsync(transaction, cancellationToken).ConfigureAwait(false);
            ThrowProblem(GraphMigrationJournal.SchemaProblem(observed.Observation));
            await GraphMigrationJournal.LockAsync(transaction, cancellationToken).ConfigureAwait(false);
            var journal = await ReadJournalAsync(transaction, cancellationToken).ConfigureAwait(false);
            GraphMigrationJournal.RequireOwner(journal, ownership.Token, ownership.Generation, plan.Id);
            ThrowProblem(GraphMigrationJournal.PlanProblem(journal, plan));
            return await operation(transaction, journal).ConfigureAwait(false);
        }, cancellationToken);

    private Task<(GraphSchemaCapabilities Capabilities, GraphSchemaObservation Observation)> ObserveAsync(
        GraphMigrationTransport transport, CancellationToken cancellationToken)
        => transport.TransactionAsync("schema-observation", null, null, commit: false,
            transaction => InspectAsync(transaction, cancellationToken), cancellationToken);

    internal async Task<(GraphSchemaCapabilities Capabilities, GraphSchemaObservation Observation)> ObserveInitializationAsync(
        GraphMigrationTransport transport, CancellationToken cancellationToken)
    {
        // Neo4j commits a constraint's backing index before committing the constraint itself.
        // SHOW can expose that exact index with no owner while another initializer is still working.
        // Wait only for this recognized shape. Every observation remains read-only; no DDL is retried.
        var timer = Stopwatch.StartNew();
        while (true)
        {
            var result = await ObserveAsync(transport, cancellationToken).ConfigureAwait(false);
            var transitionalNames = GraphMigrationJournal.Definitions.Where(definition =>
                !result.Observation.Constraints.Any(value => value.Name == definition.Name)
                && result.Observation.Indexes.Any(value => value.Name == definition.Name
                    && (value.OwningConstraint is null || value.OwningConstraint == definition.Name)
                    && value.State is "POPULATING" or "ONLINE"
                    && value.Type == "RANGE" && value.EntityType == "NODE"
                    && value.LabelsOrTypes is { Count: 1 } && value.LabelsOrTypes[0] == definition.Label
                    && value.Properties is { Count: 1 } && value.Properties[0] == "id"))
                .Select(definition => definition.Name).ToHashSet(StringComparer.Ordinal);
            if (transitionalNames.Count == 0) return result;
            // A transitional index never hides an independently conflicting journal definition.
            var otherSchema = new GraphSchemaObservation(result.Observation.Constraints,
                result.Observation.Indexes.Where(value => !transitionalNames.Contains(value.Name)));
            if (GraphMigrationJournal.SchemaProblem(otherSchema, permitMissing: true) is not null) return result;
            if (timer.Elapsed >= _options.IndexWaitTimeout)
                throw new TimeoutException("Migration journal initialization observed a reserved-name backing index without its expected constraint within the configured wait. No index was adopted; inspect concurrent initialization or conflicting schema before retrying explicitly.");
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Clamp((_options.IndexWaitTimeout - timer.Elapsed).TotalMilliseconds, 0, 100)), cancellationToken).ConfigureAwait(false);
        }
    }

    private Task<(GraphSchemaCapabilities Capabilities, GraphSchemaObservation Observation)> InspectAsync(
        IAsyncTransaction transaction, CancellationToken cancellationToken)
        => GraphSchemaInspector.InspectAsync(providerOptions, transaction, _options.MaxSchemaRows, cancellationToken);

    private Task<MigrationJournalSnapshot> ReadJournalAsync(IAsyncTransaction transaction, CancellationToken cancellationToken)
        => GraphMigrationJournal.ReadAsync(transaction, _options.MaxSchemaRows, cancellationToken);

    private Task CheckpointAsync(GraphMigrationCheckpointStage stage, GraphMigrationPlan plan, GraphMigrationStep? step,
        MigrationOwnership ownership)
        => checkpoint?.Invoke(new GraphMigrationCheckpoint(stage, plan.Id, step?.Id, ownership.Token, ownership.Generation)) ?? Task.CompletedTask;

    internal static string? DataResultProblem(GraphMigrationStep step,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, bool validation)
    {
        if (rows.Count != 1) return "Migration data command must return exactly one bounded-count result.";
        var matched = GraphMigrationJournal.Integer(rows[0], "matched");
        var eligible = GraphMigrationJournal.Integer(rows[0], "eligible");
        var affected = GraphMigrationJournal.Integer(rows[0], "affected");
        var expectedAffected = validation || step.Kind == GraphMigrationStepKind.AssertPrecondition ? 0 : step.ExpectedCount;
        return matched == step.ExpectedCount && matched <= step.Scope!.MaxRows
            && eligible == step.ExpectedCount && affected == expectedAffected ? null
            : $"Migration step '{step.Id}' precondition/count failed (matched {matched}, eligible {eligible}, affected {affected}; expected {step.ExpectedCount}).";
    }

    internal static string? SchemaPrecondition(GraphMigrationStep step, GraphSchemaObservation observation,
        bool recovery, out bool postconditionAlreadyPresent)
    {
        var definition = step.Schema!;
        var state = GraphMigrationSchemaState.Observe(definition, observation);
        var create = step.Kind is GraphMigrationStepKind.CreateConstraint or GraphMigrationStepKind.CreateIndex;
        postconditionAlreadyPresent = recovery && (create
            ? state.State is MigrationSchemaState.Ready or MigrationSchemaState.Waiting
            : state.State == MigrationSchemaState.Absent);
        if (postconditionAlreadyPresent) return null;
        if (state.State == MigrationSchemaState.Conflict || create && state.State == MigrationSchemaState.Failed)
            return state.Problem;
        if (create)
            return state.State == MigrationSchemaState.Absent
                ? GraphMigrationSchemaState.CreationConflict(definition, observation)
                : "Create step requires an absent name; adopting preexisting schema requires a prepared recovery record.";
        return state.State is MigrationSchemaState.Ready or MigrationSchemaState.Waiting or MigrationSchemaState.Failed
            ? null : "Drop step requires the exact expected existing schema definition.";
    }

    internal static string? SchemaPostcondition(GraphMigrationStep step, GraphSchemaObservation observation, out bool waiting)
    {
        var state = GraphMigrationSchemaState.Observe(step.Schema!, observation);
        var create = step.Kind is GraphMigrationStepKind.CreateConstraint or GraphMigrationStepKind.CreateIndex;
        waiting = create && state.State == MigrationSchemaState.Waiting;
        if (create && state.State is MigrationSchemaState.Ready or MigrationSchemaState.Waiting
            || !create && state.State == MigrationSchemaState.Absent) return null;
        return state.Problem ?? "Migration schema postcondition does not match the expected named definition.";
    }

    private static IEnumerable<string> CapabilityProblems(GraphMigrationPlan plan, GraphSchemaCapabilities capabilities)
    {
        foreach (var required in plan.Steps.SelectMany(step => step.RequiredCapabilities).Distinct())
        {
            var support = required switch
            {
                GraphMigrationCapability.NodeUniqueness => capabilities.NodeUniqueness,
                GraphMigrationCapability.RelationshipUniqueness => capabilities.RelationshipUniqueness,
                GraphMigrationCapability.PropertyExistence => capabilities.PropertyExistence,
                GraphMigrationCapability.PropertyType => capabilities.PropertyType,
                GraphMigrationCapability.RangeIndexes => capabilities.RangeIndexes,
                _ => GraphSchemaCapabilitySupport.Unknown
            };
            if (support != GraphSchemaCapabilitySupport.Supported)
                yield return $"Migration capability '{required}' is {support}; execution is blocked.";
        }
    }

    private static void ThrowProblem(string? problem)
    {
        if (problem is not null) throw new InvalidOperationException(problem);
    }
}
