using System.Collections.Concurrent;
using System.Diagnostics;
using GraphBridge.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore;

namespace GraphBridge.UnitTests;

public sealed partial class NativeQueryTests
{
    [Theory]
    [InlineData("success")]
    [InlineData("projector")]
    [InlineData("canceled")]
    [InlineData("commit")]
    [InlineData("cleanup")]
    [InlineData("projector_cleanup")]
    public async Task Native_traces_correlate_complete_lifecycle_and_preserve_failure_identity(string scenario)
    {
        using var parent = new Activity("native-parent").Start();
        var spans = new ConcurrentQueue<Activity>();
        using var listener = CommandActivityTests.Listen(parent, spans);
        using var cancel = new CancellationTokenSource();
        var error = new IOException("private-primary-error");
        var script = new NativeScript
        {
            CommitFailure = scenario == "commit" ? error : null,
            DisposeFailure = scenario == "cleanup" ? error : null,
            ConsumeFailure = scenario == "projector_cleanup" ? new IOException("private-secondary-cleanup") : null
        };
        await using var db = new DbContext(new DbContextOptionsBuilder().UseNeo4j(script.Driver, "private-database")
            .EnableSensitiveDataLogging().Options);
        var actual = await Record.ExceptionAsync(() => db.Database.QueryCypherAsync("RETURN 'private-literal'",
            new Dictionary<string, object?> { ["private-key"] = "private-value" }, row =>
            {
                Assert.Equal(GraphBridgeDiagnostics.NativeQueryActivityName, Activity.Current!.OperationName);
                if (scenario is "projector" or "projector_cleanup") throw error;
                if (scenario == "canceled") cancel.Cancel();
                return row["value"];
            }, cancellationToken: cancel.Token));
        if (scenario == "success") Assert.Null(actual);
        else if (scenario == "canceled") Assert.IsAssignableFrom<OperationCanceledException>(actual);
        else Assert.Same(error, actual);

        Assert.Equal(5, spans.Count);
        var root = Assert.Single(spans, s => s.OperationName == GraphBridgeDiagnostics.NativeQueryActivityName);
        var statement = Assert.Single(spans, s => s.OperationName == GraphBridgeDiagnostics.StatementActivityName);
        var run = Assert.Single(spans, s => s.OperationName == GraphBridgeDiagnostics.CommandActivityName);
        Assert.Equal(parent.SpanId, root.ParentSpanId);
        Assert.Equal(root.SpanId, statement.ParentSpanId);
        Assert.Equal(statement.SpanId, run.ParentSpanId);
        Assert.Equal(scenario == "success" ? ActivityStatusCode.Unset : ActivityStatusCode.Error, root.Status);
        Assert.Null(statement.GetTagItem("graphbridge.statement.fingerprint"));
        if (scenario is "projector" or "projector_cleanup" or "canceled")
        {
            Assert.Equal(ActivityStatusCode.Error, statement.Status);
            Assert.Equal(scenario == "canceled" ? "canceled" : "operation_error", statement.GetTagItem("error.type"));
            Assert.Single(spans, s => s.OperationName == GraphBridgeDiagnostics.RollbackActivityName);
            script.AssertOwnership(commit: 0, rollback: 1);
        }
        else
        {
            var commit = Assert.Single(spans, s => s.OperationName == GraphBridgeDiagnostics.CommitActivityName);
            Assert.Equal(root.SpanId, commit.ParentSpanId);
            Assert.Equal(scenario == "commit" ? "unconfirmed" : "acknowledged", commit.GetTagItem("graphbridge.commit.outcome"));
            Assert.Equal(scenario == "commit" ? ActivityStatusCode.Error : ActivityStatusCode.Unset, commit.Status);
            script.AssertOwnership(commit: 1, rollback: 0);
        }
        Assert.All(spans, span =>
        {
            Assert.DoesNotContain("private", string.Join(";", span.TagObjects));
            Assert.Null(span.StatusDescription);
            Assert.All(span.Events, e => Assert.Empty(e.Tags));
            Assert.Equal(parent.TraceId, span.TraceId);
        });
        Assert.Same(parent, Activity.Current);
    }

    [Fact]
    public async Task Precancellation_records_operation_without_starting_driver_or_statement()
    {
        using var parent = new Activity("native-precancel").Start();
        var spans = new ConcurrentQueue<Activity>();
        using var listener = CommandActivityTests.Listen(parent, spans);
        var script = new NativeScript();
        await using var db = Context(script);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => db.Database.QueryCypherAsync(
            "RETURN 1", Empty, r => r, cancellationToken: new CancellationToken(true)));
        var root = Assert.Single(spans);
        Assert.Equal(GraphBridgeDiagnostics.NativeQueryActivityName, root.OperationName);
        Assert.Equal("canceled", root.GetTagItem("error.type"));
        Assert.Empty(script.Calls);
        Assert.Same(parent, Activity.Current);
    }

    [Theory]
    [InlineData(ActivitySamplingResult.None)]
    [InlineData(ActivitySamplingResult.PropagationData)]
    [InlineData(ActivitySamplingResult.AllData)]
    public async Task Native_operation_and_transaction_spans_respect_sampling(ActivitySamplingResult sampling)
    {
        using var parent = new Activity("native-sampling").Start();
        var spans = new ConcurrentQueue<Activity>();
        using var listener = CommandActivityTests.Listen(parent, spans, sampling);
        var script = new NativeScript();
        await using var db = Context(script);
        Assert.Equal(2, (await db.Database.QueryCypherAsync("RETURN 1", Empty, r => r)).Count);
        script.AssertOwnership(commit: 1, rollback: 0);
        if (sampling == ActivitySamplingResult.None) Assert.Empty(spans);
        else
        {
            Assert.Equal(5, spans.Count);
            Assert.All(spans, s => Assert.False(s.Recorded));
            if (sampling == ActivitySamplingResult.PropagationData)
                Assert.All(spans, s => { Assert.Empty(s.TagObjects); Assert.Empty(s.Events); });
            else Assert.NotEmpty(Assert.Single(spans, s => s.OperationName == GraphBridgeDiagnostics.StatementActivityName).Events);
        }
        Assert.Same(parent, Activity.Current);
    }

    [Fact]
    public async Task Concurrent_contexts_keep_separate_trace_parents_across_pending_driver_calls()
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = 0;
        await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => Task.Run(async () =>
        {
            using var parent = new Activity("parallel-native").Start();
            var spans = new ConcurrentQueue<Activity>();
            using var listener = CommandActivityTests.Listen(parent, spans);
            var pending = new TaskCompletionSource<Neo4j.Driver.IResultCursor>(TaskCreationOptions.RunContinuationsAsynchronously);
            var script = new NativeScript { PendingRun = pending.Task };
            await using var db = Context(script);
            var query = db.Database.QueryCypherAsync("RETURN 1", Empty, r => r);
            if (Interlocked.Increment(ref entered) == 3) ready.SetResult();
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Same(parent, Activity.Current);
            pending.SetResult(script.Cursor);
            Assert.Equal(2, (await query).Count);
            Assert.Equal(5, spans.Count);
            Assert.All(spans, s => Assert.Equal(parent.TraceId, s.TraceId));
            var root = Assert.Single(spans, s => s.OperationName == GraphBridgeDiagnostics.NativeQueryActivityName);
            Assert.Equal(parent.SpanId, root.ParentSpanId);
            Assert.Same(parent, Activity.Current);
        })));
    }
}
