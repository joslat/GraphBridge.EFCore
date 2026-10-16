using GraphBridge.EntityFrameworkCore.Diagnostics;
using GraphBridge.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Neo4j.Driver;

namespace GraphBridge.EntityFrameworkCore.Storage;

internal sealed class Neo4jDatabaseCreator(IDbContextOptions contextOptions) : IDatabaseCreator
{
    private static NotSupportedException SchemaUnsupported()
        => new("GraphBridge does not create or delete databases implicitly. Use an explicit reviewed schema plan.");

    public bool EnsureCreated() => throw SchemaUnsupported();
    public Task<bool> EnsureCreatedAsync(CancellationToken cancellationToken = default) => throw SchemaUnsupported();
    public bool EnsureDeleted() => throw SchemaUnsupported();
    public Task<bool> EnsureDeletedAsync(CancellationToken cancellationToken = default) => throw SchemaUnsupported();
    public bool CanConnect() => throw new NotSupportedException("Use CanConnectAsync for Neo4j connectivity checks.");

    public async Task<bool> CanConnectAsync(CancellationToken cancellationToken = default)
    {
        using var activity = GraphBridgeDiagnostics.StartOperation(GraphBridgeDiagnostics.ConnectivityActivityName);
        IResultCursor? cursor = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var options = contextOptions.FindExtension<Neo4jDbContextOptionsExtension>()
                ?? throw new InvalidOperationException("Configure the context with UseNeo4j before connecting.");
            try
            {
                await using var session = options.Driver.AsyncSession(builder => builder.WithDatabase(options.DatabaseName));
                cursor = await GraphBridgeDiagnostics.RunAsync(session, "RETURN 1 AS connected", null, kind: GraphCommandKind.Connectivity).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                var record = await cursor.SingleAsync().ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                return record["connected"].As<long>() == 1;
            }
            catch (Neo4jException error)
            {
                GraphBridgeDiagnostics.Fail(activity, error);
                GraphBridgeDiagnostics.FailCursor(cursor, error);
                cancellationToken.ThrowIfCancellationRequested();
                return false;
            }
        }
        catch (Exception error)
        {
            GraphBridgeDiagnostics.Fail(activity, error);
            GraphBridgeDiagnostics.FailCursor(cursor, error);
            throw;
        }
        finally { GraphBridgeDiagnostics.CompleteCursor(cursor); }
    }
}
