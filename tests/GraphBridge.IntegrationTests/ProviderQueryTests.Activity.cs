using System.Collections.Concurrent;
using System.Diagnostics;
using GraphBridge.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore;

namespace GraphBridge.IntegrationTests;

public sealed partial class ProviderQueryTests
{
    [Neo4jIntegrationFact]
    public async Task Live_query_early_disposal_and_cancellation_close_every_span_and_restore_parent()
    {
        var runId = NewRunId();
        try
        {
            await SeedAsync(runId, Person("private-first", 19), Person("private-second", 21));
            foreach (var cancel in new[] { false, true })
            {
                using var trace = new ActivityCapture();
                using var cancellation = new CancellationTokenSource();
                await using var db = CreateContext();
                var iterator = db.People.Where(p => p.RunId == runId).AsAsyncEnumerable().GetAsyncEnumerator(cancellation.Token);
                try
                {
                    Assert.True(await iterator.MoveNextAsync());
                    Assert.Same(trace.Parent, Activity.Current);
                    Assert.DoesNotContain(trace.Spans, s => s.OperationName == GraphBridgeDiagnostics.QueryActivityName);
                    if (cancel)
                    {
                        cancellation.Cancel();
                        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await iterator.MoveNextAsync());
                    }
                }
                finally { await iterator.DisposeAsync(); }
                var operation = Assert.Single(trace.Spans, s => s.OperationName == GraphBridgeDiagnostics.QueryActivityName);
                var statement = Assert.Single(trace.Spans, s => s.OperationName == GraphBridgeDiagnostics.StatementActivityName);
                Assert.Equal(cancel ? ActivityStatusCode.Error : ActivityStatusCode.Unset, operation.Status);
                Assert.Equal(cancel ? "canceled" : null, statement.GetTagItem("error.type"));
                trace.AssertCompleteAndPrivate(runId, "private-first", "private-second");
            }
        }
        finally { await CleanupAsync(runId); }
    }

    [Neo4jIntegrationFact]
    public async Task Live_materialization_failure_marks_query_and_statement_failed_and_releases_cursor()
    {
        var runId = NewRunId();
        try
        {
            await SeedPropertiesAsync([new Dictionary<string, object?>
            {
                ["Id"] = Guid.NewGuid().ToString("D"), ["RunId"] = runId,
                ["name"] = "private-corrupt", ["age"] = null, ["state"] = 0L
            }]);
            using var trace = new ActivityCapture();
            await using var db = CreateContext();
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.People.Where(p => p.RunId == runId).ToListAsync());
            Assert.Empty(db.ChangeTracker.Entries());
            Assert.Equal(ActivityStatusCode.Error, Assert.Single(trace.Spans, s => s.OperationName == GraphBridgeDiagnostics.QueryActivityName).Status);
            var statement = Assert.Single(trace.Spans, s => s.OperationName == GraphBridgeDiagnostics.StatementActivityName);
            Assert.Equal("operation_error", statement.GetTagItem("error.type"));
            Assert.Equal(GraphBridgeDiagnostics.CommandFailedEventName, statement.Events.Last().Name);
            trace.AssertCompleteAndPrivate(runId, "private-corrupt");
        }
        finally { await CleanupAsync(runId); }
    }

    [Neo4jIntegrationFact]
    public async Task Live_LINQ_command_emits_correlated_value_free_run_span()
    {
        var runId = NewRunId();
        try
        {
            await SeedAsync(runId, Person("private-person-value", 19));
            using var parent = new Activity("integration-query").Start();
            var spans = new ConcurrentQueue<Activity>();
            using var listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == GraphBridgeDiagnostics.ActivitySourceName,
                Sample = (ref ActivityCreationOptions<ActivityContext> options) =>
                    options.Parent.TraceId == parent.TraceId ? ActivitySamplingResult.AllDataAndRecorded : ActivitySamplingResult.None,
                ActivityStopped = activity => { if (activity.TraceId == parent.TraceId) spans.Enqueue(activity); }
            };
            ActivitySource.AddActivityListener(listener);
            await using var db = CreateContext();
            Assert.Equal("private-person-value", Assert.Single(await db.People.Where(p => p.RunId == runId).ToListAsync()).Name);
            Assert.Equal(3, spans.Count);
            var operation = Assert.Single(spans, s => s.OperationName == GraphBridgeDiagnostics.QueryActivityName);
            var statement = Assert.Single(spans, s => s.OperationName == GraphBridgeDiagnostics.StatementActivityName);
            var span = Assert.Single(spans, s => s.OperationName == GraphBridgeDiagnostics.CommandActivityName);
            Assert.Equal(parent.SpanId, operation.ParentSpanId);
            Assert.Equal(operation.SpanId, statement.ParentSpanId);
            Assert.Equal(statement.SpanId, span.ParentSpanId);
            Assert.Equal(GraphBridgeDiagnostics.CommandActivityName, span.OperationName);
            Assert.Equal(ActivityKind.Client, span.Kind);
            Assert.Equal(3, span.TagObjects.Count());
            Assert.DoesNotContain(runId, string.Join(";", span.TagObjects));
            Assert.DoesNotContain("private-person-value", string.Join(";", span.TagObjects));
            Assert.Empty(span.Events);
            Assert.Equal(GraphBridgeDiagnostics.CommandCompletedEventName, statement.Events.Last().Name);
            Assert.Same(parent, Activity.Current);
        }
        finally { await CleanupAsync(runId); }
    }
}
