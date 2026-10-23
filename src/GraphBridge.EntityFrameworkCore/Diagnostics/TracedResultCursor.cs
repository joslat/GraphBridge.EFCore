using System.Diagnostics;
using Neo4j.Driver;

namespace GraphBridge.EntityFrameworkCore.Diagnostics;

// Pinned Driver 6.3 cursor contracts are forwarded unchanged. No wrapper without a sampled span.
internal sealed class TracedResultCursor(IResultCursor inner, Activity statement) : IResultCursor
{
    private bool _ended;
    public IRecord Current => Observe(() => inner.Current);
    public bool IsOpen => Observe(() => inner.IsOpen);
    public Task<string[]> KeysAsync() => ObserveAsync(inner.KeysAsync);
    public Task<IRecord> PeekAsync() => ObserveAsync(inner.PeekAsync);
    public async Task<bool> FetchAsync()
    {
        var next = await ObserveAsync(inner.FetchAsync).ConfigureAwait(false);
        if (!next) End();
        return next;
    }
    public async Task<IResultSummary> ConsumeAsync()
    {
        try
        {
            var summary = await ObserveAsync(inner.ConsumeAsync).ConfigureAwait(false);
            // ADR 0050: a fully read result has already ended its statement, so its counters go to the operation only.
            GraphWriteCounters.Record(statement, !_ended, summary);
            return summary;
        }
        finally { End(); }
    }
    public IAsyncEnumerator<IRecord> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        => new Enumerator(this, Observe(() => inner.GetAsyncEnumerator(cancellationToken)));

    internal void Fail(Exception error, bool driver = false)
    {
        if (!_ended) GraphBridgeDiagnostics.Fail(statement, error, driver);
    }
    internal void Complete() => End();
    private T Observe<T>(Func<T> operation)
    {
        var previous = Activity.Current;
        if (!_ended) Activity.Current = statement;
        try { return operation(); }
        catch (Exception error) { Fail(error, driver: true); End(); throw; }
        finally { Activity.Current = previous; }
    }
    private async Task<T> ObserveAsync<T>(Func<Task<T>> operation)
    {
        var previous = Activity.Current;
        if (!_ended) Activity.Current = statement;
        try { return await operation().ConfigureAwait(false); }
        catch (Exception error) { Fail(error, driver: true); End(); throw; }
        finally { Activity.Current = previous; }
    }
    private void End()
    {
        if (_ended) return;
        _ended = true;
        if (statement.IsAllDataRequested)
            statement.AddEvent(new(statement.Status == ActivityStatusCode.Error
                ? GraphBridgeDiagnostics.CommandFailedEventName : GraphBridgeDiagnostics.CommandCompletedEventName));
        statement.Dispose();
    }
    private sealed class Enumerator(TracedResultCursor owner, IAsyncEnumerator<IRecord> enumerator) : IAsyncEnumerator<IRecord>
    {
        public IRecord Current => owner.Observe(() => enumerator.Current);
        public async ValueTask<bool> MoveNextAsync()
        {
            var next = await owner.ObserveAsync(() => enumerator.MoveNextAsync().AsTask()).ConfigureAwait(false);
            if (!next) owner.End();
            return next;
        }
        public async ValueTask DisposeAsync()
        {
            try { await owner.ObserveAsync(async () => { await enumerator.DisposeAsync().ConfigureAwait(false); return true; }).ConfigureAwait(false); }
            finally { owner.End(); }
        }
    }
}
