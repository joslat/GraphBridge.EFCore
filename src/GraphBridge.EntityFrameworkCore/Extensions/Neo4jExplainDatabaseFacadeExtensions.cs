using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using GraphBridge.EntityFrameworkCore.Diagnostics;
using GraphBridge.EntityFrameworkCore.Infrastructure;
using GraphBridge.EntityFrameworkCore.Transport;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Neo4j.Driver;

namespace Microsoft.EntityFrameworkCore;

/// <summary>Explicit plan inspection without executing the supplied statement.</summary>
public static class Neo4jExplainDatabaseFacadeExtensions
{
    /// <summary>Prefixes a caller-owned statement with EXPLAIN and returns its real server plan.</summary>
    /// <remarks>
    /// Supply a bare MATCH/OPTIONAL/WITH/UNWIND/RETURN/CALL/CREATE/MERGE statement with explicit
    /// driver-representation parameters. Leading comments, execution modes and dialect preambles
    /// are rejected rather than rewritten. Generated ToQueryString text can be explained when its parameter placeholders are
    /// supplied correctly; this prototype does not extract LINQ closure parameters automatically.
    /// Owned transactions use read routing and are rolled back/disposed. An existing EF transaction
    /// is borrowed unchanged. Cancellation is checked between awaited driver operations, not by
    /// abandoning in-flight I/O. The plan-node cap is checked after the driver buffers the summary;
    /// it does not bound server planning work or argument byte size. Plan arguments may contain
    /// application literals; no query/parameter/plan values are logged automatically. PROFILE is
    /// never selected by this API. There is no automatic retry.
    /// </remarks>
    public static Task<GraphQueryExplanation> ExplainCypherAsync(this DatabaseFacade database,
        string cypher, IReadOnlyDictionary<string, object?> parameters, int maxPlanNodes = 1024,
        CancellationToken cancellationToken = default)
        => GraphBridgeDiagnostics.OperationAsync(GraphBridgeDiagnostics.ExplainActivityName,
            () => ExplainCypherCoreAsync(database, cypher, parameters, maxPlanNodes, cancellationToken));

    private static async Task<GraphQueryExplanation> ExplainCypherCoreAsync(DatabaseFacade database,
        string cypher, IReadOnlyDictionary<string, object?> parameters, int maxPlanNodes = 1024,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentException.ThrowIfNullOrWhiteSpace(cypher);
        ArgumentNullException.ThrowIfNull(parameters);
        if (maxPlanNodes is < 1 or > 100_000) throw new ArgumentOutOfRangeException(nameof(maxPlanNodes));
        var statementStart = cypher.AsSpan().TrimStart();
        var keywordLength = 0;
        while (keywordLength < statementStart.Length && char.IsAsciiLetter(statementStart[keywordLength])) keywordLength++;
        var keyword = statementStart[..keywordLength].ToString().ToUpperInvariant();
        if (keyword is not ("MATCH" or "OPTIONAL" or "WITH" or "UNWIND" or "RETURN" or "CALL" or "CREATE" or "MERGE"))
            throw new ArgumentException("EXPLAIN requires a bare supported statement; PROFILE, EXPLAIN, CYPHER preambles and leading comments are not accepted.", nameof(cypher));
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = new Dictionary<string, object>(parameters.Count, StringComparer.Ordinal);
        foreach (var (name, value) in parameters)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            snapshot.Add(name, value!);
        }
        var context = ((IDatabaseFacadeDependenciesAccessor)database).Context;
        var options = context.GetService<IDbContextOptions>().FindExtension<Neo4jDbContextOptionsExtension>()
            ?? throw new InvalidOperationException("ExplainCypherAsync requires a GraphBridge context configured with UseNeo4j.");
        using var concurrency = context.GetService<IConcurrencyDetector>().EnterCriticalSection();
        var manager = context.GetService<Neo4jTransactionManager>();
        using var operation = manager.EnterNativeOperation();
        IAsyncSession? session = null;
        IAsyncTransaction? owned = null;
        IResultCursor? cursor = null;
        var completed = false;
        try
        {
            var runner = manager.CurrentDriverTransaction;
            if (runner is null)
            {
                session = options.Driver.AsyncSession(o => o.WithDatabase(options.DatabaseName).WithDefaultAccessMode(AccessMode.Read));
                owned = await GraphBridgeDiagnostics.DriverCallAsync(GraphBridgeDiagnostics.BeginTransactionActivityName,
                    () => session.BeginTransactionAsync()).ConfigureAwait(false);
                runner = owned;
            }
            cancellationToken.ThrowIfCancellationRequested();
            cursor = await GraphBridgeDiagnostics.RunAsync(runner, "EXPLAIN " + cypher, snapshot, kind: GraphCommandKind.Explain).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (await cursor.FetchAsync().ConfigureAwait(false))
                throw new InvalidOperationException("EXPLAIN unexpectedly produced execution rows; no plan accepted.");
            var summary = await cursor.ConsumeAsync().ConfigureAwait(false);
            cursor = null;
            cancellationToken.ThrowIfCancellationRequested();
            if (!summary.HasPlan || summary.HasProfile || summary.Counters.ContainsUpdates
                || summary.Counters.ContainsSystemUpdates || summary.Plan is null)
                throw new InvalidOperationException("Expected an EXPLAIN plan without execution/profile/update evidence.");
            var pending = new Stack<IPlan>();
            pending.Push(summary.Plan);
            var count = 0;
            while (pending.TryPop(out var node))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++count > maxPlanNodes || node.Children.Count > maxPlanNodes - count - pending.Count)
                    throw new InvalidOperationException("Server plan exceeds maxPlanNodes; no partial plan returned.");
                foreach (var child in node.Children) pending.Push(child);
            }
            var result = new GraphQueryExplanation(
                Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(cypher))), summary.Plan,
                summary.ResultAvailableAfter, summary.ResultConsumedAfter);
            completed = true;
            return result;
        }
        catch (Exception error)
        {
            GraphBridgeDiagnostics.FailCursor(cursor, error);
            throw;
        }
        finally
        {
            Exception? cleanupFailure = null;
            if (cursor is not null)
            {
                try { if (cursor.IsOpen) await cursor.ConsumeAsync().ConfigureAwait(false); }
                catch (Exception error) { cleanupFailure = error; }
            }
            if (owned is not null)
            {
                try { await GraphBridgeDiagnostics.DriverCallAsync(GraphBridgeDiagnostics.RollbackActivityName, owned.RollbackAsync).ConfigureAwait(false); }
                catch (Exception error) { cleanupFailure ??= error; }
                try { await owned.DisposeAsync().ConfigureAwait(false); }
                catch (Exception error) { cleanupFailure ??= error; }
            }
            if (session is not null)
            {
                try { await session.DisposeAsync().ConfigureAwait(false); }
                catch (Exception error) { cleanupFailure ??= error; }
            }
            GraphBridgeDiagnostics.CompleteCursor(cursor);
            if (completed && cleanupFailure is not null) ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
        }
    }
}
