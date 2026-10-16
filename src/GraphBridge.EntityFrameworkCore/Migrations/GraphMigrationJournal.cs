using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using GraphBridge.EntityFrameworkCore.Schema;
using Neo4j.Driver;

namespace GraphBridge.EntityFrameworkCore.Migrations;

/// <summary>The explicit, database-scoped migration store. Previewing initialization never accesses the database.</summary>
public static class GraphMigrationJournal
{
    internal const string ControlLabel = "__GraphBridgeMigrationControl";
    internal const string HistoryLabel = "__GraphBridgeMigration";
    internal const string StepLabel = "__GraphBridgeMigrationStep";
    internal const string ControlConstraint = "gb_migration_control_key";
    internal const string HistoryConstraint = "gb_migration_history_key";
    internal const string StepConstraint = "gb_migration_step_key";
    internal const string ControlId = "database";
    internal static readonly (string Name, string Label)[] Definitions =
        [(ControlConstraint, ControlLabel), (HistoryConstraint, HistoryLabel), (StepConstraint, StepLabel)];

    private static readonly IReadOnlyList<string> Initialization = Array.AsReadOnly(Definitions
        .Select(definition => $"CREATE CONSTRAINT `{definition.Name}` FOR (n:`{definition.Label}`) REQUIRE n.`id` IS UNIQUE").ToArray());

    /// <summary>Reviewed schema commands required before the single control record can be safely initialized.</summary>
    public static IReadOnlyList<string> PreviewInitialization() => Initialization;

    internal static string? SchemaProblem(GraphSchemaObservation observation, bool permitMissing = false)
    {
        foreach (var (name, label) in Definitions)
        {
            var constraint = observation.Constraints.SingleOrDefault(value => value.Name == name);
            if (constraint is null)
            {
                if (observation.Indexes.Any(value => value.Name == name))
                    return $"Migration constraint name '{name}' is occupied by an index.";
                if (!permitMissing) return $"Migration journal constraint '{name}' is missing; explicitly initialize the journal.";
                continue;
            }
            if (constraint.EntityType != "NODE" || constraint.Type is not ("NODE_PROPERTY_UNIQUENESS" or "UNIQUENESS")
                || constraint.LabelsOrTypes is not { Count: 1 } || constraint.LabelsOrTypes[0] != label
                || constraint.Properties is not { Count: 1 } || constraint.Properties[0] != "id")
                return $"Migration journal constraint '{name}' has a conflicting definition.";
            if (!observation.Indexes.Any(value => value.Type == "RANGE" && value.EntityType == "NODE"
                    && value.LabelsOrTypes is { Count: 1 } && value.LabelsOrTypes[0] == label
                    && value.Properties is { Count: 1 } && value.Properties[0] == "id"
                    && value.OwningConstraint == name && value.State == "ONLINE"))
                return $"Migration journal constraint '{name}' requires an observed ONLINE backing index.";
        }
        return null;
    }

    internal const string InitializeControlQuery = "MERGE (c:__GraphBridgeMigrationControl {id: $id}) ON CREATE SET c.formatVersion = 1, c.generation = 0, c.lock = 0 RETURN c.id AS id";
    internal const string LockControlQuery = "MATCH (c:__GraphBridgeMigrationControl {id: $id}) SET c.lock = c.lock + 1 RETURN c.id AS id";
    internal const string ReadControlQuery = "MATCH (c:__GraphBridgeMigrationControl) RETURN properties(c) AS record LIMIT $limit";
    internal const string ReadHistoryQuery = "MATCH (m:__GraphBridgeMigration) RETURN properties(m) AS record LIMIT $limit";
    internal const string ReadStepsQuery = "MATCH (s:__GraphBridgeMigrationStep) RETURN properties(s) AS record LIMIT $limit";

    internal static async Task LockAsync(IAsyncTransaction transaction, CancellationToken cancellationToken)
    {
        var rows = await GraphMigrationTransport.QueryAsync(transaction, LockControlQuery,
            new Dictionary<string, object?> { ["id"] = ControlId }, 1, cancellationToken).ConfigureAwait(false);
        if (rows.Count != 1 || Text(rows[0], "id") != ControlId)
            throw new InvalidOperationException("Migration control record is missing or ambiguous.");
    }

    internal static async Task<MigrationJournalSnapshot> ReadAsync(IAsyncTransaction transaction,
        int maxRows, CancellationToken cancellationToken)
    {
        var parameters = new Dictionary<string, object?> { ["limit"] = (long)maxRows + 1 };
        var controls = await GraphMigrationTransport.QueryAsync(transaction, ReadControlQuery, parameters, maxRows, cancellationToken).ConfigureAwait(false);
        var history = await GraphMigrationTransport.QueryAsync(transaction, ReadHistoryQuery, parameters, maxRows, cancellationToken).ConfigureAwait(false);
        var steps = await GraphMigrationTransport.QueryAsync(transaction, ReadStepsQuery, parameters, maxRows, cancellationToken).ConfigureAwait(false);
        var after = await GraphMigrationTransport.QueryAsync(transaction, ReadControlQuery, parameters, maxRows, cancellationToken).ConfigureAwait(false);
        var beforeRecords = controls.Select(Record).ToArray();
        var afterRecords = after.Select(Record).ToArray();
        if (beforeRecords.Length <= 1 && afterRecords.Length <= 1
            && (beforeRecords.Length != afterRecords.Length || beforeRecords.Length == 1
                && (beforeRecords[0].Count != afterRecords[0].Count
                    || beforeRecords[0].Any(pair => !afterRecords[0].TryGetValue(pair.Key, out var value) || !Equals(pair.Value, value)))))
            throw new GraphMigrationObservationChangedException();
        return Parse(beforeRecords, history.Select(Record), steps.Select(Record));
    }

    internal static MigrationJournalSnapshot Parse(IEnumerable<IReadOnlyDictionary<string, object?>> controlRows,
        IEnumerable<IReadOnlyDictionary<string, object?>> historyRows, IEnumerable<IReadOnlyDictionary<string, object?>> stepRows)
    {
        var controls = controlRows.ToArray();
        var histories = historyRows.ToArray();
        var stepRecords = stepRows.ToArray();
        if (controls.Length == 0 && histories.Length == 0 && stepRecords.Length == 0)
            return new(null, [], []);
        if (controls.Length != 1) throw Invalid("exactly one control record is required");
        var control = controls[0];
        if (Text(control, "id") != ControlId || Integer(control, "formatVersion") != 1)
            throw Invalid("unknown control identity or format version");
        var generation = Integer(control, "generation");
        if (generation < 0 || Integer(control, "lock") < 0) throw Invalid("invalid control counters");
        var owner = OptionalText(control, "ownerToken");
        var active = OptionalText(control, "activeMigrationId");
        if ((owner is null) != (active is null) || owner is not null && generation == 0)
            throw Invalid("control ownership fields disagree");
        var state = new MigrationControl(owner, generation, active, OptionalText(control, "headId"));
        var migrations = new Dictionary<string, MigrationHistory>(StringComparer.Ordinal);
        foreach (var row in histories)
        {
            var id = Text(row, "id");
            var status = Text(row, "status");
            var count = Integer(row, "stepCount");
            if (Integer(row, "formatVersion") != 1 || status is not ("Applying" or "Completed") || count < 1 || count > int.MaxValue)
                throw Invalid("unknown migration format/status or invalid step count");
            var migration = new MigrationHistory(id, Checksum(row), OptionalText(row, "predecessorId"), status, checked((int)count));
            if (!migrations.TryAdd(id, migration)) throw Invalid("duplicate migration identifiers");
        }
        var steps = new Dictionary<string, MigrationStepHistory>(StringComparer.Ordinal);
        foreach (var row in stepRecords)
        {
            var id = Text(row, "id");
            var migrationId = Text(row, "migrationId");
            var stepId = Text(row, "stepId");
            var index = Integer(row, "index");
            var status = Text(row, "status");
            var kind = Text(row, "kind");
            var ownerGeneration = Integer(row, "ownerGeneration");
            if (status is not ("Prepared" or "Completed") || !Enum.TryParse<GraphMigrationStepKind>(kind, out var parsedKind)
                || !Enum.IsDefined(parsedKind) || parsedKind.ToString() != kind || index < 0 || index > int.MaxValue
                || ownerGeneration < 1 || ownerGeneration > generation || id != StepKey(migrationId, stepId)
                || !migrations.TryGetValue(migrationId, out var migration) || Checksum(row) != migration.Checksum)
                throw Invalid("unknown/mismatched step identity, checksum, owner, kind or status");
            var step = new MigrationStepHistory(id, migrationId, stepId, checked((int)index), kind, status);
            if (!steps.TryAdd(id, step)) throw Invalid("duplicate step identifiers");
        }
        foreach (var migration in migrations.Values)
        {
            var own = steps.Values.Where(step => step.MigrationId == migration.Id).OrderBy(step => step.Index).ToArray();
            if (own.Length > migration.StepCount || own.Select(step => step.StepId).Distinct(StringComparer.Ordinal).Count() != own.Length)
                throw Invalid("duplicate or surplus migration steps");
            for (var index = 0; index < own.Length; index++)
                if (own[index].Index != index || index < own.Length - 1 && own[index].Status != "Completed")
                    throw Invalid("migration steps are not a completed prefix followed by at most one prepared step");
            if (migration.Status == "Completed" && (own.Length != migration.StepCount || own.Any(step => step.Status != "Completed")))
                throw Invalid("completed migration has incomplete steps");
            if (migration.Status == "Applying" && (state.OwnerToken is null || state.ActiveMigrationId != migration.Id || migration.PredecessorId != state.HeadId))
                throw Invalid("applying migration is not owned or has the wrong predecessor");
        }
        if (state.ActiveMigrationId is not null
            && (!migrations.TryGetValue(state.ActiveMigrationId, out var activeHistory) || activeHistory.Status != "Applying"))
            throw Invalid("owner has no applying migration");
        var chain = new HashSet<string>(StringComparer.Ordinal);
        var head = state.HeadId;
        while (head is not null)
        {
            if (!chain.Add(head) || !migrations.TryGetValue(head, out var history) || history.Status != "Completed")
                throw Invalid("completed migration chain is broken or cyclic");
            head = history.PredecessorId;
        }
        if (chain.Count != migrations.Values.Count(history => history.Status == "Completed"))
            throw Invalid("completed migration history is not one linear chain");
        return new(state, migrations.Values.ToArray(), steps.Values.ToArray());
    }

    internal static string? PlanProblem(MigrationJournalSnapshot snapshot, GraphMigrationPlan plan)
    {
        var history = snapshot.History.SingleOrDefault(value => value.Id == plan.Id);
        if (history is null)
            return snapshot.Control?.HeadId == plan.PredecessorId ? null : "Migration predecessor does not match the recorded head.";
        if (history.Checksum != plan.Checksum || history.PredecessorId != plan.PredecessorId || history.StepCount != plan.Steps.Count)
            return "Migration identifier already has a different checksum, predecessor or step count.";
        foreach (var step in snapshot.Steps.Where(value => value.MigrationId == plan.Id))
            if (step.Index >= plan.Steps.Count || step.StepId != plan.Steps[step.Index].Id || step.Kind != plan.Steps[step.Index].Kind.ToString())
                return "Recorded migration steps do not match this plan.";
        return null;
    }

    internal static void RequireOwner(MigrationJournalSnapshot snapshot, string token, long generation, string migrationId)
    {
        if (snapshot.Control is not { } control || control.OwnerToken != token
            || control.Generation != generation || control.ActiveMigrationId != migrationId)
            throw new InvalidOperationException("Migration ownership changed; this worker cannot continue.");
    }

    internal static string StepKey(string migrationId, string stepId)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(migrationId + "\0" + stepId))).ToLowerInvariant();
    internal static string Timestamp() => DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

    internal static long Integer(IReadOnlyDictionary<string, object?> row, string field)
        => row.TryGetValue(field, out var value) && value is long number ? number : throw Invalid($"'{field}' must be an Int64");
    internal static string Text(IReadOnlyDictionary<string, object?> row, string field)
        => row.TryGetValue(field, out var value) && value is string text && !string.IsNullOrWhiteSpace(text)
            ? text : throw Invalid($"'{field}' must be a nonempty string");
    private static string? OptionalText(IReadOnlyDictionary<string, object?> row, string field)
        => !row.TryGetValue(field, out var value) || value is null ? null
            : value is string text && !string.IsNullOrWhiteSpace(text) ? text : throw Invalid($"'{field}' must be a nonempty string or absent");
    private static string Checksum(IReadOnlyDictionary<string, object?> row)
    {
        var checksum = Text(row, "checksum");
        return checksum.Length == 64 && checksum.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f')
            ? checksum : throw Invalid("invalid checksum encoding");
    }
    private static IReadOnlyDictionary<string, object?> Record(IReadOnlyDictionary<string, object?> row)
        => row.TryGetValue("record", out var value) && value is IReadOnlyDictionary<string, object> record
            ? record.ToDictionary(pair => pair.Key, pair => (object?)pair.Value, StringComparer.Ordinal)
            : throw Invalid("expected property map");
    private static InvalidOperationException Invalid(string reason) => new("Invalid migration journal: " + reason + ".");
}

internal sealed record MigrationControl(string? OwnerToken, long Generation, string? ActiveMigrationId, string? HeadId);
internal sealed record MigrationHistory(string Id, string Checksum, string? PredecessorId, string Status, int StepCount);
internal sealed record MigrationStepHistory(string Id, string MigrationId, string StepId, int Index, string Kind, string Status);
internal sealed record MigrationJournalSnapshot(MigrationControl? Control,
    IReadOnlyList<MigrationHistory> History, IReadOnlyList<MigrationStepHistory> Steps);
internal sealed class GraphMigrationObservationChangedException()
    : InvalidOperationException("Migration journal changed during observation; repeat read-only validation after the writer completes.");
