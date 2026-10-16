using System.Collections;
using System.Diagnostics;
using GraphBridge.EntityFrameworkCore.Cypher;
using GraphBridge.EntityFrameworkCore.Diagnostics;
using GraphBridge.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Neo4j.Driver;

namespace GraphBridge.EntityFrameworkCore.Query;

internal sealed class Neo4jQueryingEnumerable<T>(
    Neo4jQueryContext queryContext,
    Neo4jQueryExpression query,
    string command,
    Func<QueryContext, ValueBuffer, T> shaper,
    bool standAloneStateManager,
    Func<QueryContext, ValueBuffer, object>? includedShaper,
    QueryTrackingBehavior trackingBehavior) : IEnumerable<T>, IAsyncEnumerable<T>, IQueryingEnumerable
{
    public string ToQueryString() => command;
    public IEnumerator<T> GetEnumerator() => throw new NotSupportedException("GraphBridge queries require async enumeration. Use ToListAsync or another async EF operation.");
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        => new Enumerator(queryContext, query, command, shaper, standAloneStateManager, includedShaper, trackingBehavior, cancellationToken);

    private sealed class Enumerator(
        Neo4jQueryContext context,
        Neo4jQueryExpression query,
        string command,
        Func<QueryContext, ValueBuffer, T> shaper,
        bool standAloneStateManager,
        Func<QueryContext, ValueBuffer, object>? includedShaper,
        QueryTrackingBehavior trackingBehavior,
        CancellationToken cancellationToken) : IAsyncEnumerator<T>
    {
        private IAsyncSession? _ownedSession;
        private IResultCursor? _cursor;
        private bool _disposed;
        private bool _started;
        private Activity? _operation;
        public T Current { get; private set; } = default!;

        private void CheckCancellation()
        {
            cancellationToken.ThrowIfCancellationRequested();
            context.CancellationToken.ThrowIfCancellationRequested();
        }

        public async ValueTask<bool> MoveNextAsync()
        {
            if (_disposed)
                return false;
            using var concurrency = context.ConcurrencyDetector.EnterCriticalSection();
            var previousActivity = Activity.Current;
            try
            {
                if (!_started)
                {
                    _started = true;
                    _operation = GraphBridgeDiagnostics.StartOperation(GraphBridgeDiagnostics.QueryActivityName);
                }
                else if (_operation is not null) Activity.Current = _operation;
                CheckCancellation();
                if (_cursor is null)
                {
                    var parameters = query.Parameters.ToDictionary(p => p.Name, p => p.Resolve(context.Parameters)!);
                    IAsyncQueryRunner runner;
                    if (context.Transactions.CurrentDriverTransaction is { } transaction)
                        runner = transaction;
                    else
                        runner = _ownedSession = context.Options.Driver.AsyncSession(o => o.WithDatabase(context.Options.DatabaseName));
                    if (query.Binding.RequiredUniqueEndpoints.Count > 0 || query.Binding.RequiredUniqueIdentifiedEdges.Count > 0
                        || query.Include is not null)
                        await VerifyConstraintsAsync(runner).ConfigureAwait(false);
                    CheckCancellation();
                    context.CommandLogger.Logger.LogInformation("Executing GraphBridge Cypher: {Cypher}", command);
                    _cursor = await GraphBridgeDiagnostics.RunAsync(runner, command, parameters).ConfigureAwait(false);
                    CheckCancellation();
                    context.InitializeStateManager(standAloneStateManager);
                }

                var next = await _cursor.FetchAsync().ConfigureAwait(false);
                CheckCancellation();
                if (!next)
                {
                    await DisposeAsync().ConfigureAwait(false);
                    return false;
                }
                var rootValues = ReadRow(_cursor.Current);
                IReadOnlyList<ValueBuffer>? relatedValues = null;
                if (query.Include is { } include)
                {
                    // A complete included collection is buffered for this root. Never truncate it,
                    // and validate every related diagnostic before creating/tracking the root.
                    relatedValues = include.ReadRows(_cursor.Current[CypherIncludePlan.RowsAlias], CheckCancellation);
                }
                CheckCancellation();
                Current = shaper(context, rootValues);
                if (query.Include is { } currentInclude)
                {
                    currentInclude.InitializeCollection(Current!);
                    foreach (var relatedValue in relatedValues!)
                    {
                        CheckCancellation();
                        var related = includedShaper!(context, relatedValue);
                        currentInclude.Fixup(context, Current!, related, trackingBehavior);
                    }
                    currentInclude.MarkLoaded(context, Current!, trackingBehavior, relatedValues!.Count == 0);
                }
                return true;
            }
            catch (Exception error)
            {
                GraphBridgeDiagnostics.Fail(_operation, error);
                GraphBridgeDiagnostics.FailCursor(_cursor, error);
                try { await DisposeAsync().ConfigureAwait(false); }
                catch { /* Preserve the operation's original exception after attempting all cleanup. */ }
                throw;
            }
            finally { Activity.Current = previousActivity; }
        }

        private ValueBuffer ReadRow(IRecord record)
        {
            if (query.AggregateType is { } aggregateType)
            {
                if (query.Plan.Aggregate is CypherAggregate.Min or CypherAggregate.Max)
                    return new ValueBuffer([GraphIntegerExtremumReader.Read(record.Values, aggregateType)]);
                var raw = record["aggregate"];
                object value = aggregateType == typeof(int)
                    ? checked((int)Convert.ToInt64(raw, System.Globalization.CultureInfo.InvariantCulture))
                    : aggregateType == typeof(long) ? Convert.ToInt64(raw, System.Globalization.CultureInfo.InvariantCulture)
                    : Convert.ToBoolean(raw, System.Globalization.CultureInfo.InvariantCulture);
                return new ValueBuffer([value]);
            }

            return query.Reader!.ReadRow(record.Values);
        }

        private async Task VerifyConstraintsAsync(IAsyncQueryRunner runner)
        {
            // Observe schema through the same selected database/borrowed transaction as this query.
            // Keep the cursor in the enumerator so cancellation/error cleanup can consume it too.
            CheckCancellation();
            _cursor = await GraphBridgeDiagnostics.RunAsync(runner,
                "SHOW CONSTRAINTS YIELD type, entityType, labelsOrTypes, properties RETURN type, entityType, labelsOrTypes, properties", null, schema: true)
                .ConfigureAwait(false);
            CheckCancellation();
            var constraints = await _cursor.ToListAsync(record => new GraphSchemaConstraint(
                record["entityType"].As<string>(), record["type"].As<string>(),
                record["labelsOrTypes"].As<List<string>>(), record["properties"].As<List<string>>())).ConfigureAwait(false);
            CheckCancellation();
            GraphKeyConstraintValidator.Validate(query.Binding.RequiredUniqueEndpoints
                .Concat(query.Include?.RequiredUniqueEndpoints ?? []).Distinct().ToArray(), constraints);
            GraphKeyConstraintValidator.ValidateIdentifiedEdges(query.Binding.RequiredUniqueIdentifiedEdges, constraints);
            _cursor = null;
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
                return;
            _disposed = true;
            var previousActivity = Activity.Current;
            if (_operation is not null) Activity.Current = _operation;
            try
            {
                try
                {
                    if (_cursor is { IsOpen: true })
                        await _cursor.ConsumeAsync().ConfigureAwait(false);
                }
                finally
                {
                    if (_ownedSession is not null)
                        await _ownedSession.DisposeAsync().ConfigureAwait(false);
                }
            }
            catch (Exception error) { GraphBridgeDiagnostics.Fail(_operation, error); throw; }
            finally
            {
                GraphBridgeDiagnostics.CompleteCursor(_cursor);
                _cursor = null;
                _ownedSession = null;
                Current = default!;
                _operation?.Dispose();
                Activity.Current = previousActivity;
            }
        }
    }
}
