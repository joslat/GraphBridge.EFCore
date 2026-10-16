using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Neo4j.Driver;
using static GraphBridge.UnitTests.NativeQueryTests;

namespace GraphBridge.UnitTests;

public sealed partial class ExplainQueryTests
{
    [Fact]
    public async Task Owned_explain_snapshots_parameters_never_commits_and_disposes_every_resource()
    {
        var parameters = new Dictionary<string, object?> { ["value"] = "private-before" };
        var script = new Script { OnBegin = () => parameters["value"] = "private-after" };
        await using var db = Context(script);
        var result = await db.Database.ExplainCypherAsync("RETURN $value", parameters);
        Assert.Equal("EXPLAIN RETURN $value", script.Command);
        Assert.Equal("private-before", script.Parameters!["value"]);
        Assert.Matches("^[a-f0-9]{64}$", result.StatementFingerprint);
        Assert.Same(script.Plan, result.Plan);
        Assert.Equal(1, script.Count("transaction.RollbackAsync"));
        Assert.Equal(0, script.Count("transaction.CommitAsync"));
        Assert.Equal(1, script.Count("transaction.DisposeAsync"));
        Assert.Equal(1, script.Count("session.DisposeAsync"));
        Assert.Null(db.Database.CurrentTransaction);
    }

    [Theory]
    [InlineData("rows")]
    [InlineData("no-plan")]
    [InlineData("profile")]
    [InlineData("updates")]
    [InlineData("system-updates")]
    public async Task Execution_evidence_or_absent_plan_is_rejected_and_owned_resources_rolled_back(string response)
    {
        var script = new Script { Response = response };
        await using var db = Context(script);
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.Database.ExplainCypherAsync("RETURN 1", Empty));
        Assert.Equal(0, script.Count("transaction.CommitAsync"));
        Assert.Equal(1, script.Count("transaction.RollbackAsync"));
        Assert.Equal(1, script.Count("transaction.DisposeAsync"));
        Assert.Equal(1, script.Count("session.DisposeAsync"));
    }

    [Fact]
    public async Task Primary_error_survives_cleanup_failures_but_success_does_not_hide_cleanup_failure()
    {
        var primary = new IOException("planning failed");
        var cleanup = new IOException("cleanup failed");
        var failed = new Script { RunFailure = primary, CleanupFailure = cleanup };
        await using (var db = Context(failed))
            Assert.Same(primary, await Assert.ThrowsAsync<IOException>(() => db.Database.ExplainCypherAsync("RETURN 1", Empty)));
        var succeeded = new Script { CleanupFailure = cleanup };
        await using (var db = Context(succeeded))
            Assert.Same(cleanup, await Assert.ThrowsAsync<IOException>(() => db.Database.ExplainCypherAsync("RETURN 1", Empty)));
        Assert.Equal(1, failed.Count("session.DisposeAsync"));
        Assert.Equal(1, succeeded.Count("session.DisposeAsync"));
    }

    [Fact]
    public async Task Borrowed_failure_drains_cursor_without_ending_callers_transaction()
    {
        var script = new Script { Response = "rows" };
        await using var db = Context(script);
        await using var transaction = await db.Database.BeginTransactionAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.Database.ExplainCypherAsync("RETURN 1", Empty));
        Assert.Same(transaction, db.Database.CurrentTransaction);
        Assert.Equal(1, script.Count("cursor.ConsumeAsync"));
        Assert.Equal(0, script.Count("transaction.RollbackAsync"));
        Assert.Equal(0, script.Count("transaction.DisposeAsync"));
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task Invalid_arguments_and_precancellation_never_start_transport()
    {
        var script = new Script();
        await using var db = Context(script);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => db.Database.ExplainCypherAsync("RETURN 1", Empty, 0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => db.Database.ExplainCypherAsync("RETURN 1", Empty, 100_001));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => db.Database.ExplainCypherAsync("RETURN 1", Empty, cancellationToken: canceled.Token));
        Assert.Empty(script.Calls);
    }

    [Theory]
    [InlineData("PROFILE CREATE (n)")]
    [InlineData("  profile/*mode*/ CREATE (n)")]
    [InlineData("EXPLAIN RETURN 1")]
    [InlineData("CYPHER 25 PROFILE CREATE (n)")]
    [InlineData("// preamble\nPROFILE CREATE (n)")]
    [InlineData("/* preamble */ RETURN 1")]
    public async Task Mode_preambles_and_leading_comments_are_rejected_before_transport(string text)
    {
        var script = new Script();
        await using var db = Context(script);
        await Assert.ThrowsAsync<ArgumentException>(() => db.Database.ExplainCypherAsync(text, Empty));
        Assert.Empty(script.Calls);
    }

    private static readonly IReadOnlyDictionary<string, object?> Empty = new Dictionary<string, object?>();
    private static DbContext Context(Script script) => new(new DbContextOptionsBuilder().UseNeo4j(script.Driver, "selected").Options);

    private sealed class Script
    {
        public string Response { get; init; } = "plan";
        public Exception? RunFailure { get; init; }
        public Exception? CleanupFailure { get; init; }
        public Action? OnBegin { get; init; }
        public List<string> Calls { get; } = [];
        public string? Command { get; private set; }
        public IDictionary<string, object>? Parameters { get; private set; }
        public IPlan Plan { get; }
        public IDriver Driver { get; }
        public int Count(string call) => Calls.Count(c => c == call);

        public Script()
        {
            Plan = Proxy.For<IPlan>((method, _) => method.Name switch
            {
                "get_Children" => new List<IPlan>(),
                "get_OperatorType" => "ProduceResults",
                _ => throw new InvalidOperationException(method.Name)
            });
            var counters = Proxy.For<ICounters>((method, _) => method.Name switch
            {
                "get_ContainsUpdates" => Response == "updates",
                "get_ContainsSystemUpdates" => Response == "system-updates",
                _ => throw new InvalidOperationException(method.Name)
            });
            var summary = Proxy.For<IResultSummary>((method, _) => method.Name switch
            {
                "get_HasPlan" => Response != "no-plan",
                "get_HasProfile" => Response == "profile",
                "get_Plan" => Plan,
                "get_Counters" => counters,
                "get_ResultAvailableAfter" or "get_ResultConsumedAfter" => TimeSpan.Zero,
                _ => throw new InvalidOperationException(method.Name)
            });
            var cursor = Proxy.For<IResultCursor>((method, _) =>
            {
                Calls.Add("cursor." + method.Name);
                return method.Name switch
                {
                    "FetchAsync" => Task.FromResult(Response == "rows"),
                    "ConsumeAsync" => Task.FromResult(summary),
                    "get_IsOpen" => true,
                    _ => throw new InvalidOperationException(method.Name)
                };
            });
            var transaction = Proxy.For<IAsyncTransaction>((method, args) =>
            {
                Calls.Add("transaction." + method.Name);
                if (method.Name == "RunAsync")
                {
                    Command = Assert.IsType<string>(args![0]);
                    Parameters = Assert.IsAssignableFrom<IDictionary<string, object>>(args[1]);
                    return RunFailure is null ? Task.FromResult(cursor) : Task.FromException<IResultCursor>(RunFailure);
                }
                return Complete(method, CleanupFailure);
            });
            var session = Proxy.For<IAsyncSession>((method, _) =>
            {
                Calls.Add("session." + method.Name);
                if (method.Name == "BeginTransactionAsync") { OnBegin?.Invoke(); return Task.FromResult(transaction); }
                return Complete(method, CleanupFailure);
            });
            Driver = Proxy.For<IDriver>((method, _) => { Calls.Add("driver." + method.Name); return session; });
        }
        private static object Complete(MethodInfo method, Exception? error)
        {
            var task = error is null ? Task.CompletedTask : Task.FromException(error);
            return method.ReturnType == typeof(ValueTask) ? new ValueTask(task) : task;
        }
    }
}
