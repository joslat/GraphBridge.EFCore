using GraphBridge.EntityFrameworkCore.Infrastructure;
using GraphBridge.EntityFrameworkCore.Schema;
using GraphBridge.EntityFrameworkCore.Transport;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace Microsoft.EntityFrameworkCore;

/// <summary>Explicit schema witnesses and read-only schema inspection.</summary>
public static class Neo4jSchemaDatabaseFacadeExtensions
{
    /// <summary>Creates a deterministic storage witness from the finalized EF model without database access.</summary>
    public static GraphSchemaManifest GetGraphSchemaManifest(this DatabaseFacade database)
    {
        var context = Context(database);
        return GraphSchemaManifest.Create(context.Model);
    }

    /// <summary>Observes the selected database and compares it with the finalized EF storage witness.</summary>
    /// <remarks>
    /// Executes only metadata reads, never DDL or data scans. An active EF transaction is borrowed unchanged;
    /// otherwise a selected-database read session and transaction are disposed after inspection. Each metadata
    /// result is capped; exceeding the cap throws instead of returning an incomplete success report. The cap
    /// limits row count, not field size or server work. Permission/transport errors propagate; lack of visibility
    /// must not be mistaken for an empty schema. Sequential metadata reads are not a snapshot-isolation guarantee.
    /// Graph Type inspection requires a recognized Enterprise version and advertised Cypher 25, selected explicitly.
    /// Cancellation is observed between driver operations. No server default dialect is inferred.
    /// </remarks>
    public static async Task<GraphSchemaInspection> InspectGraphSchemaAsync(this DatabaseFacade database,
        int maxSchemaRows = 10_000, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxSchemaRows);
        cancellationToken.ThrowIfCancellationRequested();
        var context = Context(database);
        using var concurrency = context.GetService<IConcurrencyDetector>().EnterCriticalSection();
        var manager = context.GetService<Neo4jTransactionManager>();
        using var operation = manager.EnterNativeOperation();
        var manifest = GraphSchemaManifest.Create(context.Model);
        var options = context.GetService<IDbContextOptions>().FindExtension<Neo4jDbContextOptionsExtension>()!;
        var (capabilities, observation) = await GraphSchemaInspector.InspectAsync(options,
            manager.CurrentDriverTransaction, maxSchemaRows, cancellationToken).ConfigureAwait(false);
        return new GraphSchemaInspection(options.DatabaseName, manifest, capabilities, observation,
            GraphSchemaComparer.Compare(manifest, capabilities, observation));
    }

    private static DbContext Context(DatabaseFacade database)
    {
        ArgumentNullException.ThrowIfNull(database);
        var context = ((IDatabaseFacadeDependenciesAccessor)database).Context;
        if (context.GetService<IDbContextOptions>().FindExtension<Neo4jDbContextOptionsExtension>() is null)
            throw new InvalidOperationException("Schema inspection requires a GraphBridge context configured with UseNeo4j.");
        return context;
    }
}
