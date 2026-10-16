using System.Reflection;
using GraphBridge.EntityFrameworkCore.Transport;
using Microsoft.EntityFrameworkCore;
using Neo4j.Driver;

namespace GraphBridge.UnitTests;

/// <summary>
/// Protocol fault injection only. These tests verify client lifecycle behavior; they cannot determine
/// whether a server committed when an actual connection fails during commit.
/// </summary>
public sealed class TransactionFailureTests
{
    [Fact]
    public async Task Commit_failure_survives_both_disposal_failures_without_retry_or_second_cleanup()
    {
        var faults = new FaultScenario
        {
            CommitFailure = new IOException("commit response unavailable"),
            TransactionDisposeFailure = new InvalidOperationException("transaction cleanup failure"),
            SessionDisposeFailure = new InvalidOperationException("session cleanup failure")
        };
        var manager = faults.CreateManager();
        var transaction = Assert.IsType<Neo4jContextTransaction>(await manager.BeginTransactionAsync());
        Assert.Same(transaction, manager.CurrentTransaction);
        Assert.Same(faults.Transaction, manager.CurrentDriverTransaction);

        var observed = await Assert.ThrowsAsync<IOException>(() => manager.CommitTransactionAsync());

        Assert.Same(faults.CommitFailure, observed);
        Assert.False(transaction.IsCommitted);
        AssertCleared(manager);
        faults.AssertCalls(commit: 1, rollback: 0);
        Assert.Equal(new[]
        {
            "driver.AsyncSession", "session.BeginTransactionAsync", "transaction.CommitAsync",
            "transaction.DisposeAsync", "session.DisposeAsync"
        }, faults.Calls);
        await transaction.DisposeAsync();
        await manager.DisposeAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.CommitTransactionAsync());
        faults.AssertCalls(commit: 1, rollback: 0);
    }

    [Fact]
    public async Task Successful_commit_with_two_cleanup_failures_preserves_commit_state_and_first_cleanup_error()
    {
        var faults = new FaultScenario
        {
            TransactionDisposeFailure = new IOException("transaction cleanup failure after commit"),
            SessionDisposeFailure = new InvalidOperationException("session cleanup failure after commit")
        };
        var manager = faults.CreateManager();
        var transaction = Assert.IsType<Neo4jContextTransaction>(await manager.BeginTransactionAsync());

        var observed = await Assert.ThrowsAsync<IOException>(() => transaction.CommitAsync());

        Assert.Same(faults.TransactionDisposeFailure, observed);
        Assert.True(transaction.IsCommitted);
        AssertCleared(manager);
        faults.AssertCalls(commit: 1, rollback: 0);
        await transaction.DisposeAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => transaction.CommitAsync());
        faults.AssertCalls(commit: 1, rollback: 0);
    }

    [Fact]
    public async Task Successful_commit_with_only_session_cleanup_failure_does_not_roll_back_or_retry()
    {
        var faults = new FaultScenario { SessionDisposeFailure = new IOException("session cleanup failure") };
        var manager = faults.CreateManager();
        var transaction = Assert.IsType<Neo4jContextTransaction>(await manager.BeginTransactionAsync());

        var observed = await Assert.ThrowsAsync<IOException>(() => manager.CommitTransactionAsync());

        Assert.Same(faults.SessionDisposeFailure, observed);
        Assert.True(transaction.IsCommitted);
        AssertCleared(manager);
        faults.AssertCalls(commit: 1, rollback: 0);
        await manager.DisposeAsync();
        faults.AssertCalls(commit: 1, rollback: 0);
    }

    [Fact]
    public async Task Explicit_rollback_failure_survives_both_disposal_failures_and_clears_manager()
    {
        var faults = new FaultScenario
        {
            RollbackFailure = new IOException("rollback protocol failure"),
            TransactionDisposeFailure = new InvalidOperationException("transaction cleanup failure"),
            SessionDisposeFailure = new InvalidOperationException("session cleanup failure")
        };
        var manager = faults.CreateManager();
        var transaction = Assert.IsType<Neo4jContextTransaction>(await manager.BeginTransactionAsync());

        var observed = await Assert.ThrowsAsync<IOException>(() => manager.RollbackTransactionAsync());

        Assert.Same(faults.RollbackFailure, observed);
        Assert.False(transaction.IsCommitted);
        AssertCleared(manager);
        faults.AssertCalls(commit: 0, rollback: 1);
        await transaction.DisposeAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.RollbackTransactionAsync());
        faults.AssertCalls(commit: 0, rollback: 1);
    }

    [Fact]
    public async Task Active_transaction_disposal_preserves_rollback_failure_and_attempts_every_resource_once()
    {
        var faults = new FaultScenario
        {
            RollbackFailure = new IOException("rollback during disposal failed"),
            TransactionDisposeFailure = new InvalidOperationException("transaction cleanup failure"),
            SessionDisposeFailure = new InvalidOperationException("session cleanup failure")
        };
        var manager = faults.CreateManager();
        var transaction = Assert.IsType<Neo4jContextTransaction>(await manager.BeginTransactionAsync());

        var observed = await Assert.ThrowsAsync<IOException>(() => manager.DisposeAsync().AsTask());

        Assert.Same(faults.RollbackFailure, observed);
        Assert.False(transaction.IsCommitted);
        AssertCleared(manager);
        faults.AssertCalls(commit: 0, rollback: 1);
        await transaction.DisposeAsync();
        await manager.DisposeAsync();
        faults.AssertCalls(commit: 0, rollback: 1);
    }

    [Fact]
    public async Task Begin_failure_survives_session_cleanup_failure_without_transaction_operations()
    {
        var faults = new FaultScenario
        {
            BeginFailure = new IOException("begin protocol failure"),
            SessionDisposeFailure = new InvalidOperationException("session cleanup failure")
        };
        var manager = faults.CreateManager();

        var observed = await Assert.ThrowsAsync<IOException>(() => manager.BeginTransactionAsync());

        Assert.Same(faults.BeginFailure, observed);
        AssertCleared(manager);
        faults.AssertCalls(commit: 0, rollback: 0, transactionDispose: 0);
        await manager.DisposeAsync();
        faults.AssertCalls(commit: 0, rollback: 0, transactionDispose: 0);
    }

    [Fact]
    public async Task Cancellation_after_begin_survives_rollback_and_disposal_failures()
    {
        using var cancellation = new CancellationTokenSource();
        var faults = new FaultScenario
        {
            OnBegin = cancellation.Cancel,
            RollbackFailure = new IOException("rollback cleanup failure"),
            TransactionDisposeFailure = new InvalidOperationException("transaction cleanup failure"),
            SessionDisposeFailure = new InvalidOperationException("session cleanup failure")
        };
        var manager = faults.CreateManager();

        var observed = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => manager.BeginTransactionAsync(cancellation.Token));

        Assert.Equal(cancellation.Token, observed.CancellationToken);
        AssertCleared(manager);
        faults.AssertCalls(commit: 0, rollback: 1);
        await manager.DisposeAsync();
        faults.AssertCalls(commit: 0, rollback: 1);
    }

    private static void AssertCleared(Neo4jTransactionManager manager)
    {
        Assert.Null(manager.CurrentTransaction);
        Assert.Null(manager.CurrentDriverTransaction);
    }

    private sealed class FaultScenario
    {
        public Exception? BeginFailure { get; init; }
        public Exception? CommitFailure { get; init; }
        public Exception? RollbackFailure { get; init; }
        public Exception? TransactionDisposeFailure { get; init; }
        public Exception? SessionDisposeFailure { get; init; }
        public Action? OnBegin { get; init; }
        public List<string> Calls { get; } = [];
        public IAsyncTransaction Transaction { get; }
        private IAsyncSession Session { get; }
        private IDriver Driver { get; }

        public FaultScenario()
        {
            Transaction = FaultProxy.For<IAsyncTransaction>((method, _) =>
            {
                Calls.Add("transaction." + method.Name);
                return method.Name switch
                {
                    "CommitAsync" => Completion(method, CommitFailure),
                    "RollbackAsync" => Completion(method, RollbackFailure),
                    "DisposeAsync" => Completion(method, TransactionDisposeFailure),
                    _ => throw Unexpected(method)
                };
            });
            Session = FaultProxy.For<IAsyncSession>((method, _) =>
            {
                Calls.Add("session." + method.Name);
                switch (method.Name)
                {
                    case "BeginTransactionAsync":
                        OnBegin?.Invoke();
                        return BeginFailure is null ? Task.FromResult(Transaction)
                            : Task.FromException<IAsyncTransaction>(BeginFailure);
                    case "DisposeAsync":
                        return Completion(method, SessionDisposeFailure);
                    default:
                        throw Unexpected(method);
                }
            });
            Driver = FaultProxy.For<IDriver>((method, _) =>
            {
                Calls.Add("driver." + method.Name);
                return method.Name == "AsyncSession" ? Session : throw Unexpected(method);
            });
        }

        public Neo4jTransactionManager CreateManager()
            => new(new DbContextOptionsBuilder().UseNeo4j(Driver).Options);

        public void AssertCalls(int commit, int rollback, int transactionDispose = 1)
        {
            Assert.Equal(1, Count("driver.AsyncSession"));
            Assert.Equal(1, Count("session.BeginTransactionAsync"));
            Assert.Equal(commit, Count("transaction.CommitAsync"));
            Assert.Equal(rollback, Count("transaction.RollbackAsync"));
            Assert.Equal(transactionDispose, Count("transaction.DisposeAsync"));
            Assert.Equal(1, Count("session.DisposeAsync"));
            Assert.Equal(2 + commit + rollback + transactionDispose + 1, Calls.Count);
        }

        private int Count(string method) => Calls.Count(call => call == method);

        private static object Completion(MethodInfo method, Exception? failure)
        {
            var task = failure is null ? Task.CompletedTask : Task.FromException(failure);
            // Check the actual restored interface's task shape, including inherited async disposal.
            if (method.ReturnType == typeof(Task))
                return task;
            if (method.ReturnType == typeof(ValueTask))
                return new ValueTask(task);
            throw new InvalidOperationException($"Unexpected driver async return type: {method}");
        }

        private static Exception Unexpected(MethodInfo method)
            => new InvalidOperationException($"Unexpected driver protocol operation: {method.Name}");
    }

    public class FaultProxy : DispatchProxy
    {
        private Func<MethodInfo, object?[]?, object?> _invoke = null!;
        public static T For<T>(Func<MethodInfo, object?[]?, object?> invoke) where T : class
        {
            var proxy = Create<T, FaultProxy>();
            ((FaultProxy)(object)proxy)._invoke = invoke;
            return proxy;
        }
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => _invoke(targetMethod!, args);
    }
}
