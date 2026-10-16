using System.Reflection;
using System.Runtime.ExceptionServices;
using Neo4j.Driver;

namespace GraphBridge.IntegrationTests;

/// <summary>Arms a real network cut; delegates every driver operation, including COMMIT, unchanged.</summary>
public class CommitBoundaryDriver : DispatchProxy
{
    private object _target = null!;
    private CommitFaultProxy _proxy = null!;
    private CommitCut _mode;

    internal static T Wrap<T>(T target, CommitFaultProxy proxy, CommitCut mode) where T : class
    {
        var wrapped = Create<T, CommitBoundaryDriver>();
        var wrapper = (CommitBoundaryDriver)(object)wrapped;
        wrapper._target = target;
        wrapper._proxy = proxy;
        wrapper._mode = mode;
        return wrapped;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        if (_target is IAsyncTransaction && targetMethod.Name == nameof(IAsyncTransaction.CommitAsync))
            _proxy.Arm(_mode);
        object? result;
        try { result = targetMethod.Invoke(_target, args); }
        catch (TargetInvocationException error) when (error.InnerException is { } inner)
        {
            ExceptionDispatchInfo.Capture(inner).Throw();
            throw;
        }
        return result switch
        {
            IAsyncSession session => Wrap(session, _proxy, _mode),
            Task<IAsyncTransaction> transaction => WrapTransactionAsync(transaction),
            _ => result
        };
    }

    private async Task<IAsyncTransaction> WrapTransactionAsync(Task<IAsyncTransaction> transaction)
        => Wrap(await transaction, _proxy, _mode);
}
