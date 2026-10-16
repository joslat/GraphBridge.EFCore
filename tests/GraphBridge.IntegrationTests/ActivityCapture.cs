using System.Collections.Concurrent;
using System.Diagnostics;
using GraphBridge.EntityFrameworkCore.Diagnostics;

namespace GraphBridge.IntegrationTests;

internal sealed class ActivityCapture : IDisposable
{
    private readonly Activity _parent = new Activity("integration-observability").Start();
    private readonly ConcurrentQueue<Activity> _started = new();
    private readonly ActivityListener _listener;
    internal ConcurrentQueue<Activity> Spans { get; } = new();
    internal Activity Parent => _parent;

    internal ActivityCapture()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == GraphBridgeDiagnostics.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> options) => options.Parent.TraceId == _parent.TraceId
                ? ActivitySamplingResult.AllDataAndRecorded : ActivitySamplingResult.None,
            ActivityStarted = span => { if (span.TraceId == _parent.TraceId) _started.Enqueue(span); },
            ActivityStopped = span => { if (span.TraceId == _parent.TraceId) Spans.Enqueue(span); }
        };
        ActivitySource.AddActivityListener(_listener);
    }

    internal void AssertCompleteAndPrivate(params string[] secrets)
    {
        Assert.NotEmpty(_started);
        Assert.Equal(_started.Select(s => s.SpanId).OrderBy(s => s.ToString()), Spans.Select(s => s.SpanId).OrderBy(s => s.ToString()));
        Assert.Equal(Spans.Count, Spans.Select(s => s.SpanId).Distinct().Count());
        foreach (var span in Spans)
        {
            Assert.Null(span.StatusDescription);
            Assert.True(span.Duration >= TimeSpan.Zero);
            foreach (var secret in secrets) Assert.DoesNotContain(secret, string.Join(";", span.TagObjects));
            Assert.All(span.Events, e => Assert.Empty(e.Tags));
            Assert.True(span.ParentSpanId == _parent.SpanId || Spans.Any(s => s.SpanId == span.ParentSpanId));
        }
        Assert.Same(_parent, Activity.Current);
    }

    public void Dispose() { _listener.Dispose(); _parent.Dispose(); }
}
