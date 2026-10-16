using GraphBridge.EntityFrameworkCore.Diagnostics;
using System.Runtime.ExceptionServices;
using Microsoft.EntityFrameworkCore.Storage;
using Neo4j.Driver;

namespace GraphBridge.EntityFrameworkCore.Transport;

internal sealed class Neo4jContextTransaction(
    Neo4jTransactionManager manager,
    IAsyncSession session,
    IAsyncTransaction transaction) : IDbContextTransaction
{
    private bool _completed;
    private bool _disposed;
    public Guid TransactionId { get; } = Guid.NewGuid();
    internal bool IsCommitted { get; private set; }
    internal IAsyncTransaction DriverTransaction => !_completed && !_disposed ? transaction
        : throw new InvalidOperationException("The Neo4j transaction is no longer active.");

    public void Commit() => throw AsyncRequired();
    public void Rollback() => throw AsyncRequired();
    public async Task CommitAsync(CancellationToken cancellationToken = default)
    {
        EnsureActive();
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            // Driver 6.3.0 has no token overload. Do not abandon an in-flight commit or retry its unknown outcome.
            await GraphBridgeDiagnostics.DriverCallAsync(GraphBridgeDiagnostics.CommitActivityName, transaction.CommitAsync).ConfigureAwait(false);
            IsCommitted = true;
        }
        catch
        {
            _completed = true;
            try { await DisposeAsync().ConfigureAwait(false); }
            catch { /* Preserve the original commit failure; its outcome can be unknown. */ }
            throw;
        }
        _completed = true;
        await DisposeAsync().ConfigureAwait(false);
    }

    public async Task RollbackAsync(CancellationToken cancellationToken = default)
    {
        EnsureActive();
        cancellationToken.ThrowIfCancellationRequested();
        try { await GraphBridgeDiagnostics.DriverCallAsync(GraphBridgeDiagnostics.RollbackActivityName, transaction.RollbackAsync).ConfigureAwait(false); }
        catch
        {
            _completed = true;
            try { await DisposeAsync().ConfigureAwait(false); }
            catch { /* Preserve the original rollback failure after attempting resource cleanup. */ }
            throw;
        }
        _completed = true;
        await DisposeAsync().ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (!_disposed)
            throw AsyncRequired();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        manager.Clear(this);
        Exception? firstError = null;
        if (!_completed)
        {
            try { await GraphBridgeDiagnostics.DriverCallAsync(GraphBridgeDiagnostics.RollbackActivityName, transaction.RollbackAsync).ConfigureAwait(false); }
            catch (Exception error) { firstError = error; }
        }
        _completed = true;
        try { await transaction.DisposeAsync().ConfigureAwait(false); }
        catch (Exception error) { firstError ??= error; }
        try { await session.DisposeAsync().ConfigureAwait(false); }
        catch (Exception error) { firstError ??= error; }
        if (firstError is not null)
            ExceptionDispatchInfo.Capture(firstError).Throw();
    }

    private void EnsureActive()
    {
        if (_completed || _disposed)
            throw new InvalidOperationException("The Neo4j transaction has already completed.");
    }
    private static NotSupportedException AsyncRequired()
        => new("Neo4j transactions require async commit, rollback, and disposal. Use await using.");
}
