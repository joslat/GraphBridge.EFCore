using System.Security.Cryptography;
using System.Text;

namespace GraphBridge.Workshop;

/// <summary>One workshop process per database on this machine: two runs at once change each other's data.</summary>
/// <remarks>The lock lives as long as the process. A run that was killed leaves an abandoned lock, which the next run takes over.</remarks>
public sealed class WorkshopLock : IDisposable
{
    private readonly Mutex _mutex;

    private WorkshopLock(Mutex mutex) => _mutex = mutex;

    public static WorkshopLock Acquire(Uri uri, string database)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{uri.Host.ToLowerInvariant()}:{uri.Port}/{database}")))[..16];
        var mutex = new Mutex(false, $"GraphBridgeWorkshop-{key}");
        bool owned;
        try { owned = mutex.WaitOne(0); }
        catch (AbandonedMutexException) { owned = true; }
        if (owned) return new WorkshopLock(mutex);
        mutex.Dispose();
        throw new WorkshopConfigurationException($"Another workshop process is using database '{database}' at {uri}. Finish or close it first: two runs at once change each other's data.");
    }

    // Closing the handle frees the lock; ReleaseMutex would have to run on the acquiring thread, which async code cannot promise.
    public void Dispose() => _mutex.Dispose();
}
