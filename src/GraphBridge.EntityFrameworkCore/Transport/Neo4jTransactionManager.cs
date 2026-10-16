using GraphBridge.EntityFrameworkCore.Diagnostics;
using System.Transactions;
using GraphBridge.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Neo4j.Driver;

namespace GraphBridge.EntityFrameworkCore.Transport;

internal sealed class Neo4jTransactionManager(IDbContextOptions options)
    : IDbContextTransactionManager, IDisposable, IAsyncDisposable
{
    private Neo4jContextTransaction? _current;
    private int _nativeOperation;

    internal IDisposable EnterNativeOperation()
    {
        if (Interlocked.CompareExchange(ref _nativeOperation, 1, 0) != 0)
            throw new InvalidOperationException("A native Cypher operation is already active on this context; recursive projector queries are unsupported.");
        return new NativeOperationLease(this);
    }

    private sealed class NativeOperationLease(Neo4jTransactionManager owner) : IDisposable
    {
        private Neo4jTransactionManager? _owner = owner;
        public void Dispose()
        {
            var current = Interlocked.Exchange(ref _owner, null);
            if (current is not null) Interlocked.Exchange(ref current._nativeOperation, 0);
        }
    }
    public IDbContextTransaction? CurrentTransaction => _current;
    internal IAsyncTransaction? CurrentDriverTransaction
    {
        get
        {
            RejectAmbientTransaction();
            return _current?.DriverTransaction;
        }
    }

    private static NotSupportedException Unsupported()
        => new("Neo4j transactions require asynchronous operations and disposal. Use the async EF transaction API and await using.");

    public IDbContextTransaction BeginTransaction() => throw Unsupported();
    public async Task<IDbContextTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RejectAmbientTransaction();
        if (_current is not null)
            throw new InvalidOperationException("A Neo4j transaction is already active; nested transactions are unsupported.");
        var configured = options.FindExtension<Neo4jDbContextOptionsExtension>()
            ?? throw new InvalidOperationException("UseNeo4j must be configured before starting a transaction.");
        var session = configured.Driver.AsyncSession(o => o.WithDatabase(configured.DatabaseName).WithDefaultAccessMode(AccessMode.Write));
        Neo4jContextTransaction? created = null;
        try
        {
            var transaction = await GraphBridgeDiagnostics.DriverCallAsync(GraphBridgeDiagnostics.BeginTransactionActivityName,
                () => session.BeginTransactionAsync()).ConfigureAwait(false);
            _current = created = new Neo4jContextTransaction(this, session, transaction);
            // The catch below awaits cleanup while preserving cancellation as the primary failure.
            cancellationToken.ThrowIfCancellationRequested();
            return _current!;
        }
        catch
        {
            try
            {
                if (created is not null)
                    await created.DisposeAsync().ConfigureAwait(false);
                else
                    await session.DisposeAsync().ConfigureAwait(false);
            }
            catch { /* Preserve the begin/cancellation failure after attempting cleanup. */ }
            throw;
        }
    }
    public void CommitTransaction() => throw Unsupported();
    public Task CommitTransactionAsync(CancellationToken cancellationToken = default)
        => Required().CommitAsync(cancellationToken);
    public void RollbackTransaction() => throw Unsupported();
    public Task RollbackTransactionAsync(CancellationToken cancellationToken = default)
        => Required().RollbackAsync(cancellationToken);
    public void ResetState() => Dispose();
    public Task ResetStateAsync(CancellationToken cancellationToken = default) => DisposeAsync().AsTask();
    public void Dispose()
    {
        if (_current is not null)
            throw Unsupported();
    }
    public ValueTask DisposeAsync() => _current?.DisposeAsync() ?? ValueTask.CompletedTask;

    private Neo4jContextTransaction Required()
        => _current ?? throw new InvalidOperationException("No Neo4j transaction is active.");
    internal void Clear(Neo4jContextTransaction transaction)
    {
        if (ReferenceEquals(_current, transaction))
            _current = null;
    }
    internal static void RejectAmbientTransaction()
    {
        if (Transaction.Current is not null)
            throw new NotSupportedException("Ambient and distributed transactions are unsupported by GraphBridge.");
    }
}
