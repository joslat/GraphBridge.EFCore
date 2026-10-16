using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Neo4j.Driver;

namespace GraphBridge.UnitTests;

/// <summary>Client protocol/lifecycle tests, without simulated graph storage or server outcome claims.</summary>
public sealed partial class NativeQueryTests
{
    [Fact]
    public async Task Autonomous_query_snapshots_parameters_buffers_readonly_results_and_commits_once()
    {
        var script = new NativeScript();
        await using var context = Context(script);
        var parameters = new Dictionary<string, object?> { ["value"] = "before", ["optional"] = null };
        script.OnBegin = () => parameters["value"] = "after";

        var rows = await context.Database.QueryCypherAsync("RETURN $value AS value", parameters, r => r["value"].As<long>());

        Assert.Equal(new[] { 1L, 2L }, rows);
        Assert.Equal("before", script.Parameters!["value"]);
        Assert.Null(script.Parameters["optional"]);
        Assert.Throws<NotSupportedException>(() => ((IList<long>)rows).Add(3));
        Assert.Empty(context.ChangeTracker.Entries());
        Assert.Null(context.Database.CurrentTransaction);
        script.AssertOwnership(commit: 1, rollback: 0);
    }

    [Fact]
    public async Task Projector_error_preserved_when_cursor_and_transaction_cleanup_also_fail()
    {
        var primary = new FormatException("projector failure");
        var script = new NativeScript
        {
            ConsumeFailure = new IOException("consume failure"),
            RollbackFailure = new IOException("rollback failure"),
            DisposeFailure = new IOException("dispose failure")
        };
        await using var context = Context(script);

        var error = await Assert.ThrowsAsync<FormatException>(() => context.Database.QueryCypherAsync<long>(
            "RETURN 1", Empty, _ => throw primary));

        Assert.Same(primary, error);
        Assert.Null(context.Database.CurrentTransaction);
        script.AssertOwnership(commit: 0, rollback: 1);
        Assert.Equal(1, script.Count("cursor.ConsumeAsync"));
    }

    [Fact]
    public async Task Borrowed_projector_error_drains_cursor_but_preserves_caller_transaction()
    {
        var script = new NativeScript();
        await using var context = Context(script);
        await using var transaction = await context.Database.BeginTransactionAsync();
        await Assert.ThrowsAsync<FormatException>(() => context.Database.QueryCypherAsync<long>(
            "RETURN 1", Empty, _ => throw new FormatException()));
        Assert.Same(transaction, context.Database.CurrentTransaction);
        Assert.Equal(1, script.Count("driver.AsyncSession"));
        Assert.Equal(1, script.Count("cursor.ConsumeAsync"));
        Assert.Equal(0, script.Count("transaction.CommitAsync"));
        Assert.Equal(0, script.Count("transaction.RollbackAsync"));
        Assert.Equal(0, script.Count("session.DisposeAsync"));
        await transaction.RollbackAsync();
        script.AssertOwnership(commit: 0, rollback: 1);
    }

    [Fact]
    public async Task Row_limit_rolls_back_owned_transaction_without_returning_partial_results()
    {
        var script = new NativeScript();
        await using var context = Context(script);
        var projected = 0;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => context.Database.QueryCypherAsync(
            "RETURN 1", Empty, r => { projected++; return r["value"]; }, maxRows: 1));
        Assert.Contains("maxRows", error.Message);
        Assert.Equal(1, projected);
        script.AssertOwnership(commit: 0, rollback: 1);
        Assert.Null(context.Database.CurrentTransaction);
    }

    [Fact]
    public async Task Cancellation_during_projection_is_observed_and_rolls_back_before_commit()
    {
        using var cancellation = new CancellationTokenSource();
        var script = new NativeScript();
        await using var context = Context(script);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => context.Database.QueryCypherAsync(
            "RETURN 1", Empty, r => { cancellation.Cancel(); return r["value"]; }, cancellationToken: cancellation.Token));
        script.AssertOwnership(commit: 0, rollback: 1);
        Assert.Equal(1, script.Count("cursor.ConsumeAsync"));
    }

    [Fact]
    public async Task Cancellation_after_commit_success_does_not_turn_success_into_a_cancellation_error()
    {
        using var cancellation = new CancellationTokenSource();
        var script = new NativeScript { OnCommit = cancellation.Cancel };
        await using var context = Context(script);
        Assert.Equal(2, (await context.Database.QueryCypherAsync("RETURN 1", Empty,
            r => r["value"], cancellationToken: cancellation.Token)).Count);
        Assert.True(cancellation.IsCancellationRequested);
        script.AssertOwnership(commit: 1, rollback: 0);
    }

    [Fact]
    public async Task Commit_response_failure_is_preserved_and_never_retried()
    {
        var primary = new IOException("commit response unavailable; server outcome unknown");
        var script = new NativeScript { CommitFailure = primary, DisposeFailure = new IOException("cleanup") };
        await using var context = Context(script);
        Assert.Same(primary, await Assert.ThrowsAsync<IOException>(() => context.Database.QueryCypherAsync(
            "RETURN 1", Empty, r => r["value"])));
        script.AssertOwnership(commit: 1, rollback: 0);
        Assert.Null(context.Database.CurrentTransaction);
    }

    [Fact]
    public async Task Overlapping_operations_are_rejected_by_EF_concurrency_detector()
    {
        var release = new TaskCompletionSource<IResultCursor>(TaskCreationOptions.RunContinuationsAsynchronously);
        var script = new NativeScript { PendingRun = release.Task };
        await using var context = Context(script);
        var first = context.Database.QueryCypherAsync("RETURN 1", Empty, r => r["value"]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => context.Database.QueryCypherAsync("RETURN 2", Empty, r => r["value"]));
        release.SetResult(script.Cursor);
        Assert.Equal(2, (await first).Count);
        Assert.Equal(1, script.Count("transaction.RunAsync"));
        script.AssertOwnership(commit: 1, rollback: 0);
    }

    [Fact]
    public async Task Recursive_projector_query_is_rejected_before_borrowing_outer_owned_transaction()
    {
        var script = new NativeScript();
        await using var context = Context(script);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => context.Database.QueryCypherAsync(
            "RETURN 1", Empty, _ => context.Database.QueryCypherAsync(
                "RETURN 2", Empty, r => r["value"]).GetAwaiter().GetResult()));
        Assert.Contains("recursive", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, script.Count("transaction.RunAsync"));
        Assert.Null(context.Database.CurrentTransaction);
        script.AssertOwnership(commit: 0, rollback: 1);
    }

    [Fact]
    public async Task Validation_and_precancellation_do_not_open_driver_resources()
    {
        var script = new NativeScript();
        await using var context = Context(script);
        await Assert.ThrowsAsync<ArgumentException>(() => context.Database.QueryCypherAsync(" ", Empty, r => r));
        await Assert.ThrowsAsync<ArgumentNullException>(() => context.Database.QueryCypherAsync("RETURN 1", null!, r => r));
        await Assert.ThrowsAsync<ArgumentNullException>(() => context.Database.QueryCypherAsync<long>("RETURN 1", Empty, null!));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => context.Database.QueryCypherAsync("RETURN 1", Empty, r => r, maxRows: 0));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => context.Database.QueryCypherAsync(
            "RETURN 1", Empty, r => r, cancellationToken: cancellation.Token));
        Assert.Empty(script.Calls);
    }

    [Fact]
    public async Task Unconfigured_and_disposed_contexts_cannot_execute_native_queries()
    {
        await using var missing = new DbContext(new DbContextOptionsBuilder().Options);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => missing.Database.QueryCypherAsync("RETURN 1", Empty, r => r));
        Assert.Contains("provider", error.Message, StringComparison.OrdinalIgnoreCase);
        var script = new NativeScript();
        var disposed = Context(script);
        await disposed.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => disposed.Database.QueryCypherAsync("RETURN 1", Empty, r => r));
        Assert.Empty(script.Calls);
    }

    private static readonly IReadOnlyDictionary<string, object?> Empty = new Dictionary<string, object?>();
    private static DbContext Context(NativeScript script) => new(new DbContextOptionsBuilder().UseNeo4j(script.Driver).Options);

    private sealed class NativeScript
    {
        public List<string> Calls { get; } = [];
        public Action? OnBegin { get; set; }
        public Action? OnCommit { get; init; }
        public Exception? ConsumeFailure { get; init; }
        public Exception? RollbackFailure { get; init; }
        public Exception? CommitFailure { get; init; }
        public Exception? DisposeFailure { get; init; }
        public Task<IResultCursor>? PendingRun { get; init; }
        public IDictionary<string, object>? Parameters { get; private set; }
        public string? Command { get; private set; }
        public IDriver Driver { get; }
        public IResultCursor Cursor { get; }
        private int _index = -1;
        private bool _open = true;

        public NativeScript()
        {
            Cursor = Proxy.For<IResultCursor>((method, _) =>
            {
                Calls.Add("cursor." + method.Name);
                return method.Name switch
                {
                    "FetchAsync" => Task.FromResult(++_index < 2),
                    "get_Current" => Record((long)_index + 1),
                    "get_IsOpen" => _open,
                    "ConsumeAsync" => Consume(),
                    _ => throw Unexpected(method)
                };
            });
            var transaction = Proxy.For<IAsyncTransaction>((method, args) =>
            {
                Calls.Add("transaction." + method.Name);
                switch (method.Name)
                {
                    case "RunAsync":
                        Command = Assert.IsType<string>(args![0]);
                        Parameters = Assert.IsAssignableFrom<IDictionary<string, object>>(args![1]);
                        return PendingRun ?? Task.FromResult(Cursor);
                    case "CommitAsync": OnCommit?.Invoke(); return Completion(method, CommitFailure);
                    case "RollbackAsync": return Completion(method, RollbackFailure);
                    case "DisposeAsync": return Completion(method, DisposeFailure);
                    default: throw Unexpected(method);
                }
            });
            var session = Proxy.For<IAsyncSession>((method, _) =>
            {
                Calls.Add("session." + method.Name);
                if (method.Name == "BeginTransactionAsync") { OnBegin?.Invoke(); return Task.FromResult(transaction); }
                if (method.Name == "DisposeAsync") return Completion(method, DisposeFailure);
                throw Unexpected(method);
            });
            Driver = Proxy.For<IDriver>((method, _) =>
            {
                Calls.Add("driver." + method.Name);
                return method.Name == "AsyncSession" ? session : throw Unexpected(method);
            });
        }

        public int Count(string name) => Calls.Count(call => call == name);
        public void AssertOwnership(int commit, int rollback)
        {
            Assert.Equal(1, Count("driver.AsyncSession"));
            Assert.Equal(1, Count("session.BeginTransactionAsync"));
            Assert.Equal(commit, Count("transaction.CommitAsync"));
            Assert.Equal(rollback, Count("transaction.RollbackAsync"));
            Assert.Equal(1, Count("transaction.DisposeAsync"));
            Assert.Equal(1, Count("session.DisposeAsync"));
        }
        private Task<IResultSummary> Consume()
        {
            _open = false;
            return ConsumeFailure is null ? Task.FromResult<IResultSummary>(null!) : Task.FromException<IResultSummary>(ConsumeFailure);
        }
        private static IRecord Record(long value) => Proxy.For<IRecord>((method, _) => method.Name switch
        {
            "get_Item" => value,
            "get_Values" => new Dictionary<string, object> { ["c0"] = value },
            _ => throw Unexpected(method)
        });
        private static object Completion(MethodInfo method, Exception? failure)
        {
            var task = failure is null ? Task.CompletedTask : Task.FromException(failure);
            return method.ReturnType == typeof(ValueTask) ? new ValueTask(task) : task;
        }
        private static Exception Unexpected(MethodInfo method) => new InvalidOperationException("Unexpected driver operation: " + method.Name);
    }

    public class Proxy : DispatchProxy
    {
        private Func<MethodInfo, object?[]?, object?> _invoke = null!;
        public static T For<T>(Func<MethodInfo, object?[]?, object?> invoke) where T : class
        {
            var proxy = Create<T, Proxy>();
            ((Proxy)(object)proxy)._invoke = invoke;
            return proxy;
        }
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => _invoke(targetMethod!, args);
    }
}
