using GraphBridge.EntityFrameworkCore.Diagnostics;
using System.Runtime.ExceptionServices;
using GraphBridge.EntityFrameworkCore.Infrastructure;
using Neo4j.Driver;

namespace GraphBridge.EntityFrameworkCore.Schema;

internal static class GraphSchemaInspector
{
    internal const string ComponentsQuery = "CALL dbms.components() YIELD name, versions, edition RETURN name, versions, edition LIMIT $limit";
    internal const string ConstraintsQuery = "SHOW CONSTRAINTS YIELD name, type, entityType, labelsOrTypes, properties, propertyType RETURN name, type, entityType, labelsOrTypes, properties, propertyType ORDER BY name LIMIT $limit";
    internal const string ConstraintsLegacyQuery = "SHOW CONSTRAINTS YIELD name, type, entityType, labelsOrTypes, properties RETURN name, type, entityType, labelsOrTypes, properties, null AS propertyType ORDER BY name LIMIT $limit";
    internal const string IndexesQuery = "SHOW INDEXES YIELD name, state, type, entityType, labelsOrTypes, properties, owningConstraint RETURN name, state, type, entityType, labelsOrTypes, properties, owningConstraint ORDER BY name LIMIT $limit";
    internal const string GraphTypeQuery = "CYPHER 25 SHOW CURRENT GRAPH TYPE YIELD specification RETURN specification";

    internal static Task<(GraphSchemaCapabilities Capabilities, GraphSchemaObservation Observation)> InspectAsync(
        Neo4jDbContextOptionsExtension options, IAsyncTransaction? borrowed, int maxRows, CancellationToken cancellationToken)
        => GraphBridgeDiagnostics.OperationAsync(GraphBridgeDiagnostics.SchemaActivityName,
            () => InspectCoreAsync(options, borrowed, maxRows, cancellationToken));

    private static async Task<(GraphSchemaCapabilities Capabilities, GraphSchemaObservation Observation)> InspectCoreAsync(
        Neo4jDbContextOptionsExtension options, IAsyncTransaction? borrowed, int maxRows, CancellationToken cancellationToken)
    {
        IAsyncSession? session = null;
        IAsyncTransaction? owned = null;
        var completed = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var runner = borrowed;
            if (runner is null)
            {
                session = options.Driver.AsyncSession(o => o.WithDatabase(options.DatabaseName).WithDefaultAccessMode(AccessMode.Read));
                owned = await GraphBridgeDiagnostics.DriverCallAsync(GraphBridgeDiagnostics.BeginTransactionActivityName,
                () => session.BeginTransactionAsync()).ConfigureAwait(false);
                runner = owned;
            }
            var components = await ReadAsync(runner, ComponentsQuery, maxRows, cancellationToken).ConfigureAwait(false);
            var kernels = components.Where(r => Text(r, "name") == "Neo4j Kernel").ToArray();
            if (kernels.Length != 1)
                throw new InvalidOperationException("Schema inspection requires exactly one Neo4j Kernel component observation.");
            var versions = Strings(kernels[0], "versions");
            if (versions.Count != 1)
                throw new InvalidOperationException("Schema inspection requires an unambiguous kernel version.");
            var cyphers = components.Where(r => Text(r, "name") == "Cypher").ToArray();
            if (cyphers.Length > 1)
                throw new InvalidOperationException("Schema inspection received ambiguous Cypher components.");
            var capabilities = GraphSchemaCapabilities.Detect(versions[0], Text(kernels[0], "edition"),
                cyphers.Length == 0 ? [] : Strings(cyphers[0], "versions"));
            var constraintsQuery = capabilities.HasPropertyTypeObservation ? ConstraintsQuery : ConstraintsLegacyQuery;
            var constraints = (await ReadAsync(runner, constraintsQuery, maxRows, cancellationToken).ConfigureAwait(false))
                .Select(GraphSchemaObservationParser.ParseConstraint).ToArray();
            var indexes = (await ReadAsync(runner, IndexesQuery, maxRows, cancellationToken).ConfigureAwait(false))
                .Select(GraphSchemaObservationParser.ParseIndex).ToArray();
            string? specification = null;
            if (capabilities.CanShowCurrentGraphType)
            {
                var graphTypes = await ReadAsync(runner, GraphTypeQuery, 1, cancellationToken).ConfigureAwait(false);
                if (graphTypes.Count != 1)
                    throw new InvalidOperationException("Schema inspection requires one current graph type specification.");
                specification = Text(graphTypes[0], "specification");
            }
            cancellationToken.ThrowIfCancellationRequested();
            var observation = new GraphSchemaObservation(constraints, indexes, specification);
            completed = true;
            return (capabilities, observation);
        }
        finally
        {
            // Read-only owned transactions are rolled back. Attempt all cleanup and preserve an operation failure.
            Exception? cleanupFailure = null;
            if (owned is not null)
            {
                try { await GraphBridgeDiagnostics.DriverCallAsync(GraphBridgeDiagnostics.RollbackActivityName, owned.RollbackAsync).ConfigureAwait(false); }
                catch (Exception error) { cleanupFailure = error; }
                try { await owned.DisposeAsync().ConfigureAwait(false); }
                catch (Exception error) { cleanupFailure ??= error; }
            }
            if (session is not null)
            {
                try { await session.DisposeAsync().ConfigureAwait(false); }
                catch (Exception error) { cleanupFailure ??= error; }
            }
            if (completed && cleanupFailure is not null)
                ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
        }
    }

    private static async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ReadAsync(
        IAsyncTransaction runner, string query, int maxRows, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IResultCursor? cursor = null;
        try
        {
            cursor = await GraphBridgeDiagnostics.RunAsync(runner, query,
                new Dictionary<string, object> { ["limit"] = (long)maxRows + 1 }, kind: GraphCommandKind.Schema).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var rows = new List<IReadOnlyDictionary<string, object?>>();
            while (await cursor.FetchAsync().ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (rows.Count == maxRows)
                    throw new InvalidOperationException("Schema metadata exceeded maxSchemaRows; no partial schema report is returned.");
                rows.Add(cursor.Current.Values.ToDictionary(p => p.Key, p => (object?)p.Value, StringComparer.Ordinal));
            }
            await cursor.ConsumeAsync().ConfigureAwait(false);
            cursor = null;
            cancellationToken.ThrowIfCancellationRequested();
            return rows;
        }
        catch (Exception error)
        {
            GraphBridgeDiagnostics.FailCursor(cursor, error);
            try
            {
                if (cursor is { IsOpen: true }) await cursor.ConsumeAsync().ConfigureAwait(false);
            }
            catch { /* Preserve the primary error; borrowed transaction lifecycle stays with its owner. */ }
            throw;
        }
        finally { GraphBridgeDiagnostics.CompleteCursor(cursor); }
    }

    private static string Text(IReadOnlyDictionary<string, object?> row, string field)
        => row.TryGetValue(field, out var value) && value is string text && !string.IsNullOrWhiteSpace(text)
            ? text : throw new InvalidOperationException($"Invalid schema observation field '{field}'.");

    private static IReadOnlyList<string> Strings(IReadOnlyDictionary<string, object?> row, string field)
    {
        if (!row.TryGetValue(field, out var value) || value is not System.Collections.IEnumerable list || value is string)
            throw new InvalidOperationException($"Invalid schema observation field '{field}'.");
        var result = new List<string>();
        foreach (var item in list)
            result.Add(item is string text && !string.IsNullOrWhiteSpace(text) ? text
                : throw new InvalidOperationException($"Invalid schema observation field '{field}'."));
        return result;
    }
}
