using System.Reflection;
using Neo4j.Driver;

namespace GraphBridge.Workshop;

/// <summary>A stand-in driver for offline lessons. Compiling queries never touches it; any database access fails clearly.</summary>
public class OfflineDriver : DispatchProxy
{
    public static IDriver Instance { get; } = Create<IDriver, OfflineDriver>();

    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (method?.Name == nameof(IDisposable.Dispose)) return null;
        if (method?.Name == nameof(IAsyncDisposable.DisposeAsync)) return ValueTask.CompletedTask;
        throw new InvalidOperationException("This lesson step needs Neo4j, but the workshop is running offline. Configure a database and run it again.");
    }
}
