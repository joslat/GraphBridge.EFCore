using System.Runtime.CompilerServices;
using Neo4j.Driver;

namespace GraphBridge.Benchmarks.Tests;

public sealed class BoltProtocolObservationTests
{
    private const string Connection = "[bolt-42] ";

    [Fact]
    public void One_run_pull_batch_and_many_records_is_one_completed_exchange_not_one_exchange_per_message_or_row()
    {
        var observer = new BoltProtocolObservation();
        observer.Begin();
        RunPull(observer, records: 37);
        var result = observer.Complete(operationSucceeded: true);
        Assert.True(result.IsValid, result.InvalidReason);
        Assert.Equal(1L, result.CompletedSerialExchanges);
        Assert.Equal(1L, result.Flushes);
        Assert.Equal(2L, result.RequestMessages);
        Assert.Equal(2L, result.SuccessResponses);
        Assert.Equal(37L, result.RecordResponses);
        Assert.Equal(1L, result.RequestsByKind["Run"]);
        Assert.Equal(1L, result.RequestsByKind["Pull"]);
    }

    [Fact]
    public void Explicit_transaction_and_followup_fetch_batches_are_counted_only_after_each_batch_drains()
    {
        var observer = new BoltProtocolObservation();
        observer.Begin();
        Request(observer, "BeginMessage"); Flush(observer); Response(observer, "SuccessMessage");
        RunPull(observer, records: 2);
        Request(observer, "PullMessage"); Flush(observer);
        Response(observer, "RecordMessage"); Response(observer, "SuccessMessage");
        Request(observer, "CommitMessage"); Flush(observer); Response(observer, "SuccessMessage");
        var result = observer.Complete(true);
        Assert.True(result.IsValid, result.InvalidReason);
        Assert.Equal(4L, result.CompletedSerialExchanges);
        Assert.Equal(5L, result.RequestMessages);
        Assert.Equal(5L, result.SuccessResponses);
        Assert.Equal(3L, result.RecordResponses);
        Assert.Equal(2L, result.RequestsByKind["Pull"]);
    }

    [Fact]
    public void Observation_is_disabled_for_warmup_timed_passes_and_after_complete()
    {
        var observer = new BoltProtocolObservation();
        Assert.False(observer.IsDebugEnabled());
        Assert.False(observer.IsTraceEnabled());
        observer.Debug(Connection + "C: {0}", new Poison());
        observer.Trace(Connection + "C: {0}", new Poison());
        observer.Begin();
        Assert.True(observer.IsDebugEnabled());
        Assert.True(observer.IsTraceEnabled());
        RunPull(observer, 0);
        Assert.True(observer.Complete(true).IsValid);
        Assert.False(observer.IsDebugEnabled());
        Assert.False(observer.IsTraceEnabled());
        observer.Begin();
        RunPull(observer, 1);
        Assert.Equal(1L, observer.Complete(true).RecordResponses);
    }

    [Fact]
    public void Duplicate_begin_throws_but_complete_without_active_scope_reports_invalid_status()
    {
        var observer = new BoltProtocolObservation();
        Assert.False(observer.Complete(true).IsValid);
        observer.Begin();
        Assert.Throws<InvalidOperationException>(observer.Begin);
        var empty = observer.Complete(true);
        Assert.False(empty.IsValid);
        Assert.Null(empty.CompletedSerialExchanges);
    }

    [Fact]
    public void Arguments_are_never_formatted_and_exception_messages_never_enter_results()
    {
        var observer = new BoltProtocolObservation();
        observer.Begin();
        Request(observer, "RunWithMetadataMessage"); Request(observer, "PullMessage");
        // The real driver's hex string is ignored too; this sentinel fails immediately if the observer formats it.
        observer.Trace(Connection + "C: {0}", new Poison());
        Response(observer, "SuccessMessage"); Response(observer, "RecordMessage"); Response(observer, "SuccessMessage");
        Assert.True(observer.Complete(true).IsValid);
        observer.Begin();
        observer.Warn(new InvalidOperationException("secret-auth-data"), "secret-query-text {0}", new Poison());
        var invalid = observer.Complete(true);
        Assert.False(invalid.IsValid);
        Assert.DoesNotContain("secret", invalid.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Unknown_objects_cannot_spoof_driver_messages_by_ToString()
    {
        var observer = new BoltProtocolObservation();
        observer.Begin();
        observer.Debug(Connection + "C: {0}", new Poison());
        Assert.False(observer.Complete(true).IsValid);
        observer.Begin();
        Request(observer, "RunWithMetadataMessage"); Flush(observer);
        observer.Debug(Connection + "S: {0}", new Poison());
        Assert.False(observer.Complete(true).IsValid);
    }

    [Theory]
    [InlineData("overlapping-request")]
    [InlineData("overlapping-flush")]
    [InlineData("response-before-flush")]
    [InlineData("unmatched-response")]
    [InlineData("unflushed-request")]
    [InlineData("undrained-response")]
    [InlineData("empty-flush")]
    [InlineData("record-for-run")]
    [InlineData("second-connection")]
    [InlineData("missing-prefix")]
    [InlineData("handshake")]
    [InlineData("unknown-trace")]
    [InlineData("hello")]
    [InlineData("route")]
    [InlineData("warning")]
    [InlineData("error")]
    public void Ambiguous_incomplete_or_unsupported_scopes_never_publish_an_exchange_count(string scenario)
    {
        var observer = new BoltProtocolObservation();
        observer.Begin();
        switch (scenario)
        {
            case "overlapping-request": Request(observer, "RunWithMetadataMessage"); Flush(observer); Request(observer, "PullMessage"); break;
            case "overlapping-flush": Request(observer, "RunWithMetadataMessage"); Flush(observer); Flush(observer); break;
            case "response-before-flush": Request(observer, "RunWithMetadataMessage"); Response(observer, "SuccessMessage"); break;
            case "unmatched-response": Response(observer, "SuccessMessage"); break;
            case "unflushed-request": Request(observer, "RunWithMetadataMessage"); break;
            case "undrained-response": Request(observer, "RunWithMetadataMessage"); Flush(observer); break;
            case "empty-flush": Flush(observer); break;
            case "record-for-run": Request(observer, "RunWithMetadataMessage"); Flush(observer); Response(observer, "RecordMessage"); break;
            case "second-connection":
                RunPull(observer, 0);
                observer.Debug("[bolt-43] C: {0}", Message("RunWithMetadataMessage"));
                break;
            case "missing-prefix": observer.Debug("C: {0}", Message("RunWithMetadataMessage")); break;
            case "handshake": observer.Debug(Connection + "C: [HANDSHAKE] {0}", new Poison()); break;
            case "unknown-trace": observer.Trace(Connection + "S: {0}", new Poison()); break;
            case "hello": Request(observer, "HelloMessage"); break;
            case "route": Request(observer, "RouteMessage"); break;
            case "warning": observer.Warn(new Exception(), "ignored"); break;
            case "error": observer.Error(new Exception(), "ignored"); break;
        }
        var result = observer.Complete(true);
        Assert.False(result.IsValid);
        Assert.NotNull(result.InvalidReason);
        Assert.Null(result.CompletedSerialExchanges);
    }

    [Fact]
    public void Failure_and_ignored_each_match_one_request_but_failed_operation_is_not_a_valid_benchmark_measurement()
    {
        var observer = new BoltProtocolObservation();
        observer.Begin();
        Request(observer, "RunWithMetadataMessage"); Request(observer, "PullMessage"); Flush(observer);
        Response(observer, "FailureMessage"); Response(observer, "IgnoredMessage");
        var result = observer.Complete(operationSucceeded: false);
        Assert.False(result.IsValid);
        Assert.Null(result.CompletedSerialExchanges);
        Assert.Equal(1L, result.FailureResponses);
        Assert.Equal(1L, result.IgnoredResponses);
        Assert.Equal(2L, result.RequestMessages);
    }

    [Fact]
    public void Snapshots_are_immutable_and_next_scope_does_not_mutate_prior_counts()
    {
        var observer = new BoltProtocolObservation();
        observer.Begin(); RunPull(observer, 0); var first = observer.Complete(true);
        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, long>)first.RequestsByKind).Add("fake", 99));
        observer.Begin();
        Request(observer, "ResetMessage"); Flush(observer); Response(observer, "SuccessMessage");
        var second = observer.Complete(true);
        Assert.Equal(1L, first.RequestsByKind["Run"]);
        Assert.Equal(0L, first.RequestsByKind["Reset"]);
        Assert.Equal(0L, second.RequestsByKind["Run"]);
        Assert.Equal(1L, second.RequestsByKind["Reset"]);
    }

    [Fact]
    public void Request_batch_growth_is_bounded_and_cannot_publish_partial_exchange_counts()
    {
        var observer = new BoltProtocolObservation();
        observer.Begin();
        var request = Message("RunWithMetadataMessage");
        for (var index = 0; index <= 65_536; index++) observer.Debug(Connection + "C: {0}", request);
        Assert.False(observer.IsTraceEnabled());
        var result = observer.Complete(true);
        Assert.False(result.IsValid);
        Assert.Null(result.CompletedSerialExchanges);
        Assert.Equal(65_536L, result.RequestMessages);
    }

    private static void RunPull(BoltProtocolObservation observer, int records)
    {
        Request(observer, "RunWithMetadataMessage"); Request(observer, "PullMessage"); Flush(observer);
        Response(observer, "SuccessMessage");
        for (var index = 0; index < records; index++) Response(observer, "RecordMessage");
        Response(observer, "SuccessMessage");
    }
    private static void Request(BoltProtocolObservation observer, string type) => observer.Debug(Connection + "C: {0}", Message(type));
    private static void Response(BoltProtocolObservation observer, string type) => observer.Debug(Connection + "S: {0}", Message(type));
    private static void Flush(BoltProtocolObservation observer) => observer.Trace(Connection + "C: {0}", new Poison());
    private static object Message(string type)
        // Real pinned types, no constructors or payloads; payload access/formatting would fail on uninitialized messages.
        => RuntimeHelpers.GetUninitializedObject(typeof(IDriver).Assembly.GetType("Neo4j.Driver.Internal.Messaging." + type, throwOnError: true)!);
    private sealed class Poison
    {
        public override string ToString() => throw new InvalidOperationException("The observer must never format an argument.");
    }
}
