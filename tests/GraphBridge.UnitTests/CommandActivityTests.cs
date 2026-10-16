using System.Collections.Concurrent;
using System.Diagnostics;
using GraphBridge.EntityFrameworkCore.Diagnostics;
using Neo4j.Driver;
using static GraphBridge.UnitTests.NativeQueryTests;

namespace GraphBridge.UnitTests;

public sealed class CommandActivityTests
{
    [Fact]
    public async Task Awaited_command_has_one_parented_span_and_never_captures_command_or_values()
    {
        using var parent = new Activity("unit-command-parent").Start();
        var spans = new ConcurrentQueue<Activity>();
        using var listener = Listen(parent, spans);
        var cursor = Proxy.For<IResultCursor>((method, _) => method.Name == "FetchAsync" ? Task.FromResult(false) : throw new NotSupportedException());
        var pending = new TaskCompletionSource<IResultCursor>(TaskCreationOptions.RunContinuationsAsynchronously);
        var values = new Dictionary<string, object> { ["private-name"] = "private-value" };
        var runner = Proxy.For<IAsyncQueryRunner>((method, args) =>
        {
            Assert.Equal("RunAsync", method.Name);
            Assert.Equal("RETURN 'private-literal', $private-name", args![0]);
            Assert.Same(values, args[1]);
            Assert.Equal(GraphBridgeDiagnostics.CommandActivityName, Activity.Current!.OperationName);
            return pending.Task;
        });
        var operation = GraphBridgeDiagnostics.RunAsync(runner, "RETURN 'private-literal', $private-name", values);
        Assert.Empty(spans);
        Assert.Same(parent, Activity.Current);
        pending.SetResult(cursor);
        var traced = await operation;
        var span = Assert.Single(spans);
        Assert.Equal(GraphBridgeDiagnostics.CommandActivityName, span.OperationName);
        Assert.Equal(ActivityKind.Client, span.Kind);
        Assert.Equal(ActivityStatusCode.Unset, span.Status);
        Assert.Equal("neo4j", span.GetTagItem("db.system.name"));
        Assert.Equal("linq", span.GetTagItem("graphbridge.command.kind"));
        Assert.Equal("run", span.GetTagItem("graphbridge.command.stage"));
        Assert.Equal(3, span.TagObjects.Count());
        Assert.Empty(span.Events);
        Assert.True(span.Duration >= TimeSpan.Zero);
        Assert.Same(parent, Activity.Current);
        Assert.False(await traced.FetchAsync());
        var statement = Assert.Single(spans, item => item.OperationName == GraphBridgeDiagnostics.StatementActivityName);
        Assert.Equal(parent.SpanId, statement.ParentSpanId);
        Assert.Equal(statement.SpanId, span.ParentSpanId);
        Assert.Equal(1, statement.GetTagItem("graphbridge.parameter.count"));
        Assert.Equal(1, statement.GetTagItem("graphbridge.parameter.string.count"));
        Assert.DoesNotContain("private", string.Join(";", statement.TagObjects));
        Assert.Equal(new[] { GraphBridgeDiagnostics.CommandStartingEventName, GraphBridgeDiagnostics.CommandSubmittedEventName,
            GraphBridgeDiagnostics.CommandCompletedEventName }, statement.Events.Select(e => e.Name));
        Assert.Same(parent, Activity.Current);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failure_preserves_exception_and_emits_only_fixed_error_classification(bool cancellation)
    {
        using var parent = new Activity("unit-command-error").Start();
        var spans = new ConcurrentQueue<Activity>();
        using var listener = Listen(parent, spans);
        Exception failure = cancellation ? new OperationCanceledException("secret-credential") : new IOException("secret-credential");
        var runner = Proxy.For<IAsyncQueryRunner>((_, _) => Task.FromException<IResultCursor>(failure));
        var actual = await Record.ExceptionAsync(() => GraphBridgeDiagnostics.RunAsync(runner, "secret-query", null, schema: true));
        Assert.Same(failure, actual);
        Assert.Equal(2, spans.Count);
        var span = Assert.Single(spans, item => item.OperationName == GraphBridgeDiagnostics.CommandActivityName);
        Assert.Equal(ActivityStatusCode.Error, span.Status);
        Assert.Null(span.StatusDescription);
        Assert.Equal(cancellation ? "canceled" : "driver_error", span.GetTagItem("error.type"));
        Assert.Equal("schema_preflight", span.GetTagItem("graphbridge.command.kind"));
        Assert.Equal(4, span.TagObjects.Count());
        Assert.DoesNotContain("secret", string.Join(";", span.TagObjects));
        Assert.Empty(span.Events);
        var statement = Assert.Single(spans, item => item.OperationName == GraphBridgeDiagnostics.StatementActivityName);
        Assert.Equal(ActivityStatusCode.Error, statement.Status);
        Assert.Null(statement.StatusDescription);
        Assert.DoesNotContain("secret", string.Join(";", statement.TagObjects));
        Assert.Equal(GraphBridgeDiagnostics.CommandFailedEventName, statement.Events.Last().Name);
        Assert.Same(parent, Activity.Current);
    }

    [Theory]
    [InlineData(ActivitySamplingResult.None)]
    [InlineData(ActivitySamplingResult.PropagationData)]
    public async Task Sampling_is_respected_without_changing_execution(ActivitySamplingResult sampling)
    {
        using var parent = new Activity("unit-command-sampling").Start();
        var spans = new ConcurrentQueue<Activity>();
        using var listener = Listen(parent, spans, sampling);
        var calls = 0;
        var cursor = Proxy.For<IResultCursor>((method, _) => method.Name == "FetchAsync" ? Task.FromResult(false) : throw new NotSupportedException());
        var runner = Proxy.For<IAsyncQueryRunner>((_, _) => { calls++; return Task.FromResult(cursor); });
        var observed = await GraphBridgeDiagnostics.RunAsync(runner, "RETURN 1", null);
        Assert.False(await observed.FetchAsync());
        Assert.Equal(1, calls);
        if (sampling == ActivitySamplingResult.None) { Assert.Empty(spans); Assert.Same(cursor, observed); }
        else
        {
            Assert.Equal(2, spans.Count);
            Assert.All(spans, span => { Assert.Empty(span.TagObjects); Assert.Empty(span.Events); });
        }
        Assert.Same(parent, Activity.Current);
    }

    [Theory]
    [InlineData("FetchAsync")]
    [InlineData("ConsumeAsync")]
    [InlineData("KeysAsync")]
    [InlineData("PeekAsync")]
    public async Task Cursor_failure_preserves_original_error_and_closes_statement_once(string stage)
    {
        using var parent = new Activity("cursor-failure").Start();
        var spans = new ConcurrentQueue<Activity>();
        using var listener = Listen(parent, spans);
        var failure = new IOException("private-error-payload");
        var summary = Proxy.For<IResultSummary>((_, _) => throw new NotSupportedException());
        var cursor = Proxy.For<IResultCursor>((method, _) => method.Name switch
        {
            "FetchAsync" => Task.FromException<bool>(failure),
            "ConsumeAsync" when stage == "ConsumeAsync" => Task.FromException<IResultSummary>(failure),
            "ConsumeAsync" => Task.FromResult(summary),
            "KeysAsync" => Task.FromException<string[]>(failure),
            "PeekAsync" => Task.FromException<IRecord>(failure),
            _ => throw new NotSupportedException()
        });
        var runner = Proxy.For<IAsyncQueryRunner>((_, _) => Task.FromResult(cursor));
        var observed = await GraphBridgeDiagnostics.RunAsync(runner, "RETURN 'private-literal'", null, kind: GraphCommandKind.Native);
        var actual = await Record.ExceptionAsync(async () =>
        {
            switch (stage)
            {
                case "FetchAsync": await observed.FetchAsync(); break;
                case "ConsumeAsync": await observed.ConsumeAsync(); break;
                case "KeysAsync": await observed.KeysAsync(); break;
                default: await observed.PeekAsync(); break;
            }
        });
        Assert.Same(failure, actual);
        await Record.ExceptionAsync(() => observed.ConsumeAsync());
        Assert.Equal(2, spans.Count);
        var statement = Assert.Single(spans, s => s.OperationName == GraphBridgeDiagnostics.StatementActivityName);
        Assert.Equal(ActivityStatusCode.Error, statement.Status);
        Assert.Equal("driver_error", statement.GetTagItem("error.type"));
        Assert.Null(statement.GetTagItem("graphbridge.statement.fingerprint"));
        Assert.Null(statement.StatusDescription);
        Assert.DoesNotContain("private", string.Join(";", statement.TagObjects));
        Assert.Single(statement.Events, e => e.Name == GraphBridgeDiagnostics.CommandFailedEventName);
        Assert.Same(parent, Activity.Current);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cursor_async_enumeration_restores_parent_and_completes_on_early_disposal(bool early)
    {
        using var parent = new Activity("cursor-enumeration").Start();
        var spans = new ConcurrentQueue<Activity>();
        using var listener = Listen(parent, spans);
        var disposed = 0;
        var record = Proxy.For<IRecord>((_, _) => throw new NotSupportedException());
        async IAsyncEnumerable<IRecord> Stream()
        {
            try
            {
                await Task.Yield();
                Assert.Equal(GraphBridgeDiagnostics.StatementActivityName, Activity.Current!.OperationName);
                yield return record;
                Assert.Equal(GraphBridgeDiagnostics.StatementActivityName, Activity.Current!.OperationName);
            }
            finally { disposed++; }
        }
        var cursor = Proxy.For<IResultCursor>((method, _) => method.Name == "GetAsyncEnumerator"
            ? Stream().GetAsyncEnumerator() : throw new NotSupportedException());
        var runner = Proxy.For<IAsyncQueryRunner>((_, _) => Task.FromResult(cursor));
        var observed = await GraphBridgeDiagnostics.RunAsync(runner, "RETURN 1", null);
        await foreach (var row in observed)
        {
            Assert.Same(record, row);
            Assert.Same(parent, Activity.Current);
            if (early) break;
        }
        Assert.Equal(1, disposed);
        Assert.Equal(2, spans.Count);
        Assert.Same(parent, Activity.Current);
    }

    [Theory]
    [InlineData("get_Current")]
    [InlineData("get_IsOpen")]
    [InlineData("GetAsyncEnumerator")]
    public async Task Synchronous_cursor_failure_does_not_leak_statement_or_change_exception(string stage)
    {
        using var parent = new Activity("sync-cursor-failure").Start();
        var spans = new ConcurrentQueue<Activity>();
        using var listener = Listen(parent, spans);
        var error = new IOException("private-cursor-failure");
        var cursor = Proxy.For<IResultCursor>((_, _) => throw error);
        var runner = Proxy.For<IAsyncQueryRunner>((_, _) => Task.FromResult(cursor));
        var traced = await GraphBridgeDiagnostics.RunAsync(runner, "RETURN 1", null);
        var actual = Record.Exception(() =>
        {
            if (stage == "get_Current") _ = traced.Current;
            else if (stage == "get_IsOpen") _ = traced.IsOpen;
            else _ = traced.GetAsyncEnumerator();
        });
        Assert.Same(error, actual);
        GraphBridgeDiagnostics.CompleteCursor(traced);
        Assert.Equal(2, spans.Count);
        Assert.Equal("driver_error", Assert.Single(spans, s => s.OperationName == GraphBridgeDiagnostics.StatementActivityName).GetTagItem("error.type"));
        Assert.Same(parent, Activity.Current);
    }

    [Fact]
    public async Task Parameter_metadata_uses_bounded_categories_without_formatting_or_enumerating_values()
    {
        using var parent = new Activity("parameter-shape").Start();
        var spans = new ConcurrentQueue<Activity>();
        using var listener = Listen(parent, spans);
        var values = new Dictionary<string, object>
        {
            ["private-null"] = null!, ["private-boolean"] = true, ["private-string"] = "private-value",
            ["private-integer"] = long.MaxValue, ["private-floating"] = double.NaN,
            ["private-map"] = new Dictionary<string, object> { ["private-nested"] = new SecretValue() },
            ["private-list"] = new SecretEnumerable(), ["private-object"] = new SecretValue()
        };
        var cursor = Proxy.For<IResultCursor>((method, _) => method.Name == "FetchAsync" ? Task.FromResult(false) : throw new NotSupportedException());
        var runner = Proxy.For<IAsyncQueryRunner>((_, args) => { Assert.Same(values, args![1]); return Task.FromResult(cursor); });
        var traced = await GraphBridgeDiagnostics.RunAsync(runner, "RETURN 'private-literal'", values, kind: GraphCommandKind.Native);
        Assert.False(await traced.FetchAsync());
        var statement = Assert.Single(spans, s => s.OperationName == GraphBridgeDiagnostics.StatementActivityName);
        Assert.Equal(8, statement.GetTagItem("graphbridge.parameter.count"));
        foreach (var category in new[] { "null", "boolean", "string", "integer", "floating", "map", "collection", "other" })
            Assert.Equal(1, statement.GetTagItem("graphbridge.parameter." + category + ".count"));
        Assert.Equal(11, statement.TagObjects.Count());
        Assert.DoesNotContain("private", string.Join(";", statement.TagObjects));
        Assert.Null(statement.GetTagItem("graphbridge.statement.fingerprint"));
    }

    private sealed class SecretValue
    {
        public override string ToString() => throw new InvalidOperationException("Value formatting is forbidden.");
    }
    private sealed class SecretEnumerable : System.Collections.IEnumerable
    {
        public System.Collections.IEnumerator GetEnumerator() => throw new InvalidOperationException("Value enumeration is forbidden.");
    }

    internal static ActivityListener Listen(Activity parent, ConcurrentQueue<Activity> spans,
        ActivitySamplingResult sampling = ActivitySamplingResult.AllDataAndRecorded)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == GraphBridgeDiagnostics.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> options) =>
                options.Parent.TraceId == parent.TraceId ? sampling : ActivitySamplingResult.None,
            ActivityStopped = activity => { if (activity.TraceId == parent.TraceId) spans.Enqueue(activity); }
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }
}
