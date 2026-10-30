using GraphBridge.EntityFrameworkCore.Diagnostics;
using GraphBridge.EntityFrameworkCore.Infrastructure;
using GraphBridge.EntityFrameworkCore.Transport;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Neo4j.Driver;

namespace Microsoft.EntityFrameworkCore;

/// <summary>Explicit, untracked native Cypher operations for a GraphBridge context.</summary>
public static class Neo4jDatabaseFacadeExtensions
{
    /// <summary>Executes caller-supplied Cypher and buffers results using an explicit record projector.</summary>
    /// <remarks>
    /// Values must be supplied as parameters; the caller owns the Cypher text and its identifiers.
    /// Results are never attached to EF, including projected entity instances. The top-level parameter
    /// dictionary is copied before execution; callers must not mutate nested parameter values in flight.
    /// An existing EF transaction is borrowed without commit, rollback or disposal. Projection, row-limit
    /// and cancellation failures leave that transaction caller-owned, potentially with native writes pending.
    /// Without an existing transaction, an explicit transaction is committed only after successful projection;
    /// failures attempt rollback and cleanup. A failed commit response may have an unknown server outcome.
    /// Driver operations have no cancellation-token overload: cancellation is observed between awaited operations,
    /// never by abandoning an in-flight operation. No cancellation check follows successful commit.
    /// The row limit bounds buffered results, not server work; use bounded Cypher for expensive queries.
    /// No query text or parameter values are logged by this helper. Driver/projector exceptions are propagated.
    /// Await completion before using the same context again. No automatic retry or automatic DTO mapping is performed.
    /// </remarks>
    public static Task<IReadOnlyList<T>> QueryCypherAsync<T>(this DatabaseFacade database,
        string cypher, IReadOnlyDictionary<string, object?> parameters, Func<IRecord, T> project,
        int maxRows = 10_000, CancellationToken cancellationToken = default)
        => GraphBridgeDiagnostics.OperationAsync(GraphBridgeDiagnostics.NativeQueryActivityName,
            () => QueryCypherCoreAsync(database, cypher, parameters, project, maxRows, cancellationToken));

    /// <summary>Runs caller-supplied Cypher for its effect and returns what it changed (ADR 0050).</summary>
    /// <remarks>Transaction ownership, parameters and diagnostics are those of <see cref="QueryCypherAsync{T}"/>. Rows the
    /// statement returns are read and discarded, at most <paramref name="maxRows"/>. The counters come from the
    /// statement's own summary; no extra query runs.</remarks>
    public static async Task<GraphWriteCounters> ExecuteCypherAsync(this DatabaseFacade database,
        string cypher, IReadOnlyDictionary<string, object?> parameters, int maxRows = 10_000, CancellationToken cancellationToken = default)
    {
        var counters = GraphWriteCounters.None;
        await GraphBridgeDiagnostics.OperationAsync(GraphBridgeDiagnostics.NativeQueryActivityName,
            () => QueryCypherCoreAsync(database, cypher, parameters, _ => 0, maxRows, cancellationToken,
                summary: result => counters = GraphWriteCounters.From(result.Counters))).ConfigureAwait(false);
        return counters;
    }

    /// <summary>A Fluent query with its own timeout, which applies only to a transaction this call owns (ADR 0050).</summary>
    internal static Task<IReadOnlyList<T>> QueryCypherWithTimeoutAsync<T>(DatabaseFacade database, string cypher,
        IReadOnlyDictionary<string, object?> parameters, Func<IRecord, T> project, int maxRows, TimeSpan? timeout,
        CancellationToken cancellationToken, Func<IAsyncTransaction, CancellationToken, Task>? preflight = null)
        => GraphBridgeDiagnostics.OperationAsync(GraphBridgeDiagnostics.NativeQueryActivityName,
            () => QueryCypherCoreAsync(database, cypher, parameters, project, maxRows, cancellationToken, preflight, timeout: timeout));

    internal static async Task<IReadOnlyList<T>> QueryCypherCoreAsync<T>(DatabaseFacade database,
        string cypher, IReadOnlyDictionary<string, object?> parameters, Func<IRecord, T> project,
        int maxRows = 10_000, CancellationToken cancellationToken = default,
        Func<IAsyncTransaction, CancellationToken, Task>? preflight = null, TimeSpan? timeout = null,
        Action<IResultSummary>? summary = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentException.ThrowIfNullOrWhiteSpace(cypher);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(project);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxRows);
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = new Dictionary<string, object>(parameters.Count, StringComparer.Ordinal);
        foreach (var (name, value) in parameters)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            snapshot.Add(name, value!);
        }

        var context = ((IDatabaseFacadeDependenciesAccessor)database).Context;
        if (context.GetService<IDbContextOptions>().FindExtension<Neo4jDbContextOptionsExtension>() is null)
            throw new InvalidOperationException("QueryCypherAsync requires a GraphBridge context configured with UseNeo4j.");
        using var concurrency = context.GetService<IConcurrencyDetector>().EnterCriticalSection();
        var manager = context.GetService<Neo4jTransactionManager>();
        using var nativeOperation = manager.EnterNativeOperation();
        IDbContextTransaction? owned = null;
        IResultCursor? cursor = null;
        try
        {
            var runner = manager.CurrentDriverTransaction;
            // Neo4j times out transactions, not statements: an application transaction keeps the timeout it began with.
            if (runner is not null && timeout is not null)
                throw new InvalidOperationException("A per-query timeout cannot apply inside an application transaction: the timeout in effect when the transaction began covers all of it. Set Database.SetGraphCommandTimeout before BeginTransactionAsync instead.");
            if (runner is null)
            {
                owned = await manager.BeginTransactionAsync(timeout ?? manager.CommandTimeout, cancellationToken).ConfigureAwait(false);
                runner = manager.CurrentDriverTransaction!;
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (preflight is not null)
                await preflight(runner, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            cursor = await GraphBridgeDiagnostics.RunAsync(runner, cypher, snapshot, kind: GraphCommandKind.Native).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var rows = new List<T>();
            while (await cursor.FetchAsync().ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (rows.Count == maxRows)
                    throw new InvalidOperationException("Native Cypher results exceeded maxRows; bound the query or explicitly raise the buffer limit.");
                rows.Add(project(cursor.Current));
                cancellationToken.ThrowIfCancellationRequested();
            }
            cancellationToken.ThrowIfCancellationRequested();
            var result = await cursor.ConsumeAsync().ConfigureAwait(false);
            cursor = null;
            summary?.Invoke(result);
            cancellationToken.ThrowIfCancellationRequested();
            if (owned is not null)
                await owned.CommitAsync(cancellationToken).ConfigureAwait(false);
            return rows.AsReadOnly();
        }
        catch (Exception error)
        {
            GraphBridgeDiagnostics.FailCursor(cursor, error);
            // Drain borrowed cursors, but do not complete/dispose a caller's transaction.
            try
            {
                if (cursor is { IsOpen: true })
                    await cursor.ConsumeAsync().ConfigureAwait(false);
            }
            catch { /* Preserve the operation's primary failure. */ }
            if (owned is not null)
            {
                try { await owned.DisposeAsync().ConfigureAwait(false); }
                catch { /* Preserve the primary failure after attempting rollback and both disposals. */ }
            }
            throw;
        }
        finally { GraphBridgeDiagnostics.CompleteCursor(cursor); }
    }
}
