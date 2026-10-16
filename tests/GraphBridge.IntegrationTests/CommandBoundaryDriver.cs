using System.Reflection;
using System.Runtime.ExceptionServices;
using Neo4j.Driver;

namespace GraphBridge.IntegrationTests;

/// <summary>Awaits a test hook before real transaction RUN; no result or server behavior is simulated.</summary>
public class CommandBoundaryDriver : DispatchProxy
{
    private object _target = null!;
    private Func<string, Task> _beforeRun = null!;
    internal static T Wrap<T>(T target, Func<string, Task> beforeRun) where T : class
    {
        var proxy = Create<T, CommandBoundaryDriver>();
        var state = (CommandBoundaryDriver)(object)proxy;
        state._target = target; state._beforeRun = beforeRun;
        return proxy;
    }
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(method);
        if (_target is IAsyncTransaction && method.Name == nameof(IAsyncTransaction.RunAsync))
            return Run(method, args!);
        return Forward(method, args) switch
        {
            IAsyncSession session => Wrap(session, _beforeRun),
            Task<IAsyncTransaction> transaction => WrapTransaction(transaction),
            var result => result
        };
    }
    private async Task<IResultCursor> Run(MethodInfo method, object?[] args)
    {
        var text = args[0] is Query query ? query.Text : (string)args[0]!;
        await _beforeRun(text).ConfigureAwait(false);
        return await ((Task<IResultCursor>)Forward(method, args)!).ConfigureAwait(false);
    }
    private async Task<IAsyncTransaction> WrapTransaction(Task<IAsyncTransaction> transaction)
        => Wrap(await transaction.ConfigureAwait(false), _beforeRun);
    private object? Forward(MethodInfo method, object?[]? args)
    {
        try { return method.Invoke(_target, args); }
        catch (TargetInvocationException error) when (error.InnerException is { } inner)
        {
            ExceptionDispatchInfo.Capture(inner).Throw();
            throw;
        }
    }
}
