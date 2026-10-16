using System.Reflection;
using System.Runtime.ExceptionServices;
using Neo4j.Driver;

namespace GraphBridge.Benchmarks;

/// <summary>Counts public API invocations, not Bolt messages, network requests or round trips.</summary>
public sealed class DriverObservation
{
    private long _run, _fetch, _consume, _begin, _commit, _rollback;
    public Dictionary<string, CapturedCommand> Shapes { get; } = new(StringComparer.Ordinal);
    public void Reset()
    {
        _run = _fetch = _consume = _begin = _commit = _rollback = 0;
        Shapes.Clear();
    }
    internal void Record(string method, object?[] arguments)
    {
        switch (method)
        {
            case "RunAsync":
                Interlocked.Increment(ref _run);
                // This harness/provider uses RunAsync(string, object/dictionary), never managed retries.
                if (arguments.FirstOrDefault() is string text && !Shapes.ContainsKey(text))
                {
                    if (Shapes.Count >= 16) throw new InvalidOperationException("Unexpected benchmark command-shape growth.");
                    Shapes.Add(text, new(text, arguments.Length > 1 ? arguments[1] : null));
                }
                break;
            case "FetchAsync": Interlocked.Increment(ref _fetch); break;
            case "ConsumeAsync": Interlocked.Increment(ref _consume); break;
            case "BeginTransactionAsync": Interlocked.Increment(ref _begin); break;
            case "CommitAsync": Interlocked.Increment(ref _commit); break;
            case "RollbackAsync": Interlocked.Increment(ref _rollback); break;
        }
    }
    public CommandCounts Snapshot() => new(_run, _fetch, _consume, _begin, _commit, _rollback);
}

public sealed record CapturedCommand(string Text, object? Parameters);

// DispatchProxy keeps the complete pinned public transport interface delegated to the real driver.
// Both benchmark arms use this same observer; its overhead is included, never called provider-only cost.
public class ObservedDriverProxy : DispatchProxy
{
    private object _target = null!;
    private DriverObservation _observation = null!;
    public static T Wrap<T>(T target, DriverObservation observation) where T : class
    {
        var result = Create<T, ObservedDriverProxy>();
        var proxy = (ObservedDriverProxy)(object)result;
        proxy._target = target;
        proxy._observation = observation;
        return result;
    }
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        args ??= [];
        _observation.Record(targetMethod.Name, args);
        object? result;
        try { result = targetMethod.Invoke(_target, args); }
        catch (TargetInvocationException error) when (error.InnerException is { } inner)
        {
            ExceptionDispatchInfo.Capture(inner).Throw();
            throw;
        }
        return result switch
        {
            IAsyncSession session => Wrap(session, _observation),
            Task<IAsyncTransaction> transaction => WrapTransactionAsync(transaction),
            Task<IResultCursor> cursor => WrapCursorAsync(cursor),
            _ => result
        };
    }
    private async Task<IAsyncTransaction> WrapTransactionAsync(Task<IAsyncTransaction> task)
        => Wrap(await task.ConfigureAwait(false), _observation);
    private async Task<IResultCursor> WrapCursorAsync(Task<IResultCursor> task)
        => Wrap(await task.ConfigureAwait(false), _observation);
}
