using Neo4j.Driver;

namespace GraphBridge.EntityFrameworkCore.Migrations;

/// <summary>Called only inside a transaction holding the control-node write lock.</summary>
internal static class GraphMigrationJournalWriter
{
    internal static async Task<MigrationOwnership> AcquireAsync(IAsyncTransaction transaction,
        MigrationJournalSnapshot snapshot, GraphMigrationPlan plan, GraphMigrationRecovery? recovery,
        CancellationToken cancellationToken)
    {
        var control = snapshot.Control ?? throw new InvalidOperationException("Explicit migration initialization is required.");
        var existing = snapshot.History.SingleOrDefault(value => value.Id == plan.Id);
        var problem = GraphMigrationJournal.PlanProblem(snapshot, plan);
        if (problem is not null) throw new InvalidOperationException(problem);
        if (existing?.Status == "Completed") return new("", control.Generation, AlreadyApplied: true);
        if (recovery is null)
        {
            if (control.OwnerToken is not null)
                throw new InvalidOperationException($"Migration ownership is held by token '{control.OwnerToken}' at generation {control.Generation}; explicit quiescent recovery is required.");
            if (existing is not null) throw new InvalidOperationException("An unfinished migration requires explicit recovery.");
        }
        else if (control.OwnerToken != recovery.ExpectedOwnerToken || control.Generation != recovery.ExpectedGeneration
            || control.ActiveMigrationId != plan.Id || existing?.Status != "Applying")
            throw new InvalidOperationException("Recovery owner token, generation or active migration does not match the durable journal.");

        var ownership = new MigrationOwnership(Guid.NewGuid().ToString("N"), checked(control.Generation + 1), AlreadyApplied: false);
        var parameters = Parameters(plan, ownership);
        parameters["oldGeneration"] = control.Generation;
        parameters["oldOwner"] = control.OwnerToken;
        parameters["recoveryReason"] = recovery?.Reason;
        await WriteOneAsync(transaction,
            "MATCH (c:__GraphBridgeMigrationControl {id: $controlId}) "
            + "WHERE c.generation = $oldGeneration AND (($oldOwner IS NULL AND c.ownerToken IS NULL) OR c.ownerToken = $oldOwner) "
            + "SET c.ownerToken = $owner, c.generation = $generation, c.activeMigrationId = $migrationId, c.acquiredAt = $now, c.recoveryReason = $recoveryReason "
            + "RETURN count(c) AS count", parameters, cancellationToken).ConfigureAwait(false);
        if (existing is null)
            await WriteOneAsync(transaction,
                "CREATE (m:__GraphBridgeMigration {id: $migrationId, checksum: $checksum, formatVersion: 1, status: 'Applying', stepCount: $stepCount, startedAt: $now}) "
                + "SET m.predecessorId = $predecessorId RETURN count(m) AS count", parameters, cancellationToken).ConfigureAwait(false);
        return ownership;
    }

    internal static async Task PrepareStepAsync(IAsyncTransaction transaction, GraphMigrationPlan plan,
        GraphMigrationStep step, int index, MigrationOwnership ownership, CancellationToken cancellationToken)
    {
        var parameters = Parameters(plan, ownership);
        parameters["stepKey"] = GraphMigrationJournal.StepKey(plan.Id, step.Id);
        parameters["stepId"] = step.Id;
        parameters["index"] = (long)index;
        parameters["kind"] = step.Kind.ToString();
        await WriteOneAsync(transaction,
            "CREATE (s:__GraphBridgeMigrationStep {id: $stepKey, migrationId: $migrationId, stepId: $stepId, index: $index, "
            + "kind: $kind, checksum: $checksum, status: 'Prepared', ownerGeneration: $generation, preparedAt: $now}) RETURN count(s) AS count",
            parameters, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task CompleteStepAsync(IAsyncTransaction transaction, GraphMigrationPlan plan,
        GraphMigrationStep step, MigrationOwnership ownership, bool recovered, CancellationToken cancellationToken)
    {
        var parameters = Parameters(plan, ownership);
        parameters["stepKey"] = GraphMigrationJournal.StepKey(plan.Id, step.Id);
        parameters["recovered"] = recovered;
        await WriteOneAsync(transaction,
            "MATCH (s:__GraphBridgeMigrationStep {id: $stepKey}) WHERE s.status = 'Prepared' AND s.checksum = $checksum "
            + "SET s.status = 'Completed', s.completedAt = $now, s.ownerGeneration = $generation, s.recovered = $recovered RETURN count(s) AS count",
            parameters, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task CompleteMigrationAsync(IAsyncTransaction transaction, GraphMigrationPlan plan,
        MigrationOwnership ownership, CancellationToken cancellationToken)
    {
        var parameters = Parameters(plan, ownership);
        await WriteOneAsync(transaction,
            "MATCH (m:__GraphBridgeMigration {id: $migrationId}) WHERE m.status = 'Applying' AND m.checksum = $checksum "
            + "SET m.status = 'Completed', m.appliedAt = $now RETURN count(m) AS count", parameters, cancellationToken).ConfigureAwait(false);
        await WriteOneAsync(transaction,
            "MATCH (c:__GraphBridgeMigrationControl {id: $controlId}) WHERE c.ownerToken = $owner AND c.generation = $generation AND c.activeMigrationId = $migrationId "
            + "SET c.headId = $migrationId, c.releasedAt = $now REMOVE c.ownerToken, c.activeMigrationId RETURN count(c) AS count",
            parameters, cancellationToken).ConfigureAwait(false);
    }

    private static Dictionary<string, object?> Parameters(GraphMigrationPlan plan, MigrationOwnership ownership)
        => new()
        {
            ["controlId"] = GraphMigrationJournal.ControlId, ["migrationId"] = plan.Id, ["checksum"] = plan.Checksum,
            ["predecessorId"] = plan.PredecessorId, ["stepCount"] = (long)plan.Steps.Count,
            ["owner"] = ownership.Token, ["generation"] = ownership.Generation, ["now"] = GraphMigrationJournal.Timestamp()
        };

    private static async Task WriteOneAsync(IAsyncTransaction transaction, string command,
        IReadOnlyDictionary<string, object?> parameters, CancellationToken cancellationToken)
    {
        var rows = await GraphMigrationTransport.QueryAsync(transaction, command, parameters, 1, cancellationToken).ConfigureAwait(false);
        if (rows.Count != 1 || GraphMigrationJournal.Integer(rows[0], "count") != 1)
            throw new InvalidOperationException("Migration journal update did not affect exactly one expected record.");
    }
}

internal sealed record MigrationOwnership(string Token, long Generation, bool AlreadyApplied);
