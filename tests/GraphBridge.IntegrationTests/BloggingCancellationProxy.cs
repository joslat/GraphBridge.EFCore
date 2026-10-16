using System.Collections;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Neo4j.Driver;

namespace GraphBridge.IntegrationTests;

internal sealed class BloggingCancellationObservation(CancellationTokenSource cancellation)
{
    public int Calls { get; set; }
    public bool Committed { get; private set; }
    public HashSet<Guid> CreatedIds { get; } = [];

    public void Capture(object? value)
    {
        if (Committed) return;
        if (value is string text && Guid.TryParseExact(text, "D", out var id)) CreatedIds.Add(id);
        else if (value is IDictionary dictionary)
            foreach (var item in dictionary.Values) Capture(item);
    }

    public async Task AfterCommitAsync(Task commit)
    {
        await commit;
        if (!Committed) { Committed = true; cancellation.Cancel(); }
    }
}

// Cancel only after the real server acknowledges the first commit. Subsequent cleanup uses the real driver.
public class BloggingCancellationProxy : DispatchProxy
{
    private object _target = null!;
    private BloggingCancellationObservation _observation = null!;

    internal static T Wrap<T>(T target, BloggingCancellationObservation observation) where T : class
    {
        var wrapped = Create<T, BloggingCancellationProxy>();
        var proxy = (BloggingCancellationProxy)(object)wrapped;
        proxy._target = target;
        proxy._observation = observation;
        return wrapped;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        args ??= [];
        _observation.Calls++;
        if (targetMethod.Name == "RunAsync" && args.FirstOrDefault() is string query
            && query.StartsWith("CREATE", StringComparison.Ordinal) && args.Length > 1)
            _observation.Capture(args[1]);
        object? result;
        try { result = targetMethod.Invoke(_target, args); }
        catch (TargetInvocationException error) when (error.InnerException is { } inner)
        {
            ExceptionDispatchInfo.Capture(inner).Throw();
            throw;
        }
        if (targetMethod.Name == "CommitAsync" && result is Task commit) return _observation.AfterCommitAsync(commit);
        return result switch
        {
            IAsyncSession session => Wrap(session, _observation),
            Task<IAsyncTransaction> transaction => WrapTransactionAsync(transaction),
            _ => result
        };
    }

    private async Task<IAsyncTransaction> WrapTransactionAsync(Task<IAsyncTransaction> transaction)
        => Wrap(await transaction, _observation);
}
