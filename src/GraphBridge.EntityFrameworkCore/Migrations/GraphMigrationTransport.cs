using GraphBridge.EntityFrameworkCore.Diagnostics;
using System.Runtime.ExceptionServices;
using GraphBridge.EntityFrameworkCore.Infrastructure;
using Neo4j.Driver;

namespace GraphBridge.EntityFrameworkCore.Migrations;

/// <summary>Owned, serial explicit transactions. No managed driver retry callbacks are used.</summary>
internal sealed class GraphMigrationTransport(IAsyncSession session, GraphMigrationOptions options)
{
    internal static Task<T> UseAsync<T>(Neo4jDbContextOptionsExtension provider,
        GraphMigrationOptions options, bool readOnly, Func<GraphMigrationTransport, Task<T>> operation,
        CancellationToken cancellationToken)
        => GraphBridgeDiagnostics.OperationAsync(GraphBridgeDiagnostics.MigrationActivityName,
            () => UseCoreAsync(provider, options, readOnly, operation, cancellationToken));

    private static async Task<T> UseCoreAsync<T>(Neo4jDbContextOptionsExtension provider,
        GraphMigrationOptions options, bool readOnly, Func<GraphMigrationTransport, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var session = provider.Driver.AsyncSession(configuration => configuration.WithDatabase(provider.DatabaseName)
            .WithDefaultAccessMode(readOnly ? AccessMode.Read : AccessMode.Write));
        var completed = false;
        try
        {
            var result = await operation(new GraphMigrationTransport(session, options)).ConfigureAwait(false);
            completed = true;
            return result;
        }
        finally
        {
            try { await session.DisposeAsync().ConfigureAwait(false); }
            catch when (!completed) { /* Preserve the primary operation/commit error. */ }
        }
    }

    internal async Task<T> TransactionAsync<T>(string purpose, string? migrationId, string? stepId,
        bool commit, Func<IAsyncTransaction, Task<T>> operation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var metadata = new Dictionary<string, object> { ["graphbridge"] = "migration", ["purpose"] = purpose };
        if (migrationId is not null) metadata["migrationId"] = migrationId;
        if (stepId is not null) metadata["stepId"] = stepId;
        // Signatures verified in the pinned Neo4j.Driver 6.3.0 XML reference.
        var transaction = await GraphBridgeDiagnostics.DriverCallAsync(GraphBridgeDiagnostics.BeginTransactionActivityName,
            () => session.BeginTransactionAsync(configuration => configuration
                .WithTimeout(options.TransactionTimeout).WithMetadata(metadata))).ConfigureAwait(false);
        var committed = false;
        var completed = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await operation(transaction).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (commit)
            {
                await GraphBridgeDiagnostics.DriverCallAsync(GraphBridgeDiagnostics.CommitActivityName, transaction.CommitAsync).ConfigureAwait(false);
                committed = true;
            }
            completed = true;
            return result;
        }
        finally
        {
            Exception? cleanupFailure = null;
            if (!committed)
            {
                try { await GraphBridgeDiagnostics.DriverCallAsync(GraphBridgeDiagnostics.RollbackActivityName, transaction.RollbackAsync).ConfigureAwait(false); }
                catch (Exception error) { cleanupFailure = error; }
            }
            try { await transaction.DisposeAsync().ConfigureAwait(false); }
            catch (Exception error) { cleanupFailure ??= error; }
            if (completed && cleanupFailure is not null)
                ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
        }
    }

    internal static async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> QueryAsync(
        IAsyncTransaction transaction, string cypher, IReadOnlyDictionary<string, object?>? parameters,
        int maxRows, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IResultCursor? cursor = null;
        try
        {
            cursor = await GraphBridgeDiagnostics.RunAsync(transaction, cypher,
                parameters?.ToDictionary(pair => pair.Key, pair => pair.Value!, StringComparer.Ordinal)
                    ?? new Dictionary<string, object>(), kind: GraphCommandKind.Migration).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var rows = new List<IReadOnlyDictionary<string, object?>>();
            while (await cursor.FetchAsync().ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (rows.Count >= maxRows)
                    throw new InvalidOperationException("Migration observation exceeded its row bound; no partial success is returned.");
                rows.Add(cursor.Current.Values.ToDictionary(pair => pair.Key, pair => (object?)pair.Value, StringComparer.Ordinal));
            }
            await cursor.ConsumeAsync().ConfigureAwait(false);
            cursor = null;
            cancellationToken.ThrowIfCancellationRequested();
            return rows;
        }
        catch (Exception error)
        {
            GraphBridgeDiagnostics.FailCursor(cursor, error);
            try { if (cursor is { IsOpen: true }) await cursor.ConsumeAsync().ConfigureAwait(false); }
            catch { /* Await cleanup without replacing the primary failure. */ }
            throw;
        }
        finally { GraphBridgeDiagnostics.CompleteCursor(cursor); }
    }
}
