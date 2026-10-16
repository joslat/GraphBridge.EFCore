using System.Reflection;
using System.Runtime.ExceptionServices;
using Neo4j.Driver;

namespace GraphBridge.IntegrationTests;

/// <summary>Limits owned fault-test transactions on the server; never abandons a running driver task.</summary>
public class BoundedTransactionDriver : DispatchProxy
{
    private object _target = null!;

    internal static T Wrap<T>(T target) where T : class
    {
        var wrapped = Create<T, BoundedTransactionDriver>();
        ((BoundedTransactionDriver)(object)wrapped)._target = target;
        return wrapped;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        if (_target is IAsyncSession session && targetMethod.Name == nameof(IAsyncSession.BeginTransactionAsync)
            && targetMethod.GetParameters().Length == 0)
            return session.BeginTransactionAsync(config => config.WithTimeout(TimeSpan.FromSeconds(60)));
        object? result;
        try { result = targetMethod.Invoke(_target, args); }
        catch (TargetInvocationException error) when (error.InnerException is { } inner)
        {
            ExceptionDispatchInfo.Capture(inner).Throw();
            throw;
        }
        return result is IAsyncSession createdSession ? Wrap(createdSession) : result;
    }
}
