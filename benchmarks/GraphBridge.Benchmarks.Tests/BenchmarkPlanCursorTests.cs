using System.Reflection;
using Neo4j.Driver;

namespace GraphBridge.Benchmarks.Tests;

public sealed class BenchmarkPlanCursorTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(10_000)]
    public async Task Drains_every_row_and_end_of_stream_before_consuming_summary(int rows)
    {
        var stream = new PlanStream(rows);

        var summary = await BenchmarkPlanCursor.ConsumeAsync(stream.Cursor, rows);

        Assert.Same(stream.Summary, summary);
        Assert.Equal(rows + 1, stream.Fetches);
        Assert.Equal(1, stream.Consumes);
    }

    [Fact]
    public async Task Extra_row_fails_immediately_without_discarding_remaining_rows()
    {
        var stream = new PlanStream(10_000);

        await Assert.ThrowsAsync<InvalidOperationException>(() => BenchmarkPlanCursor.ConsumeAsync(stream.Cursor, 2));

        Assert.Equal(3, stream.Fetches);
        Assert.Equal(0, stream.Consumes);
    }

    [Fact]
    public async Task Short_result_fails_after_end_of_stream_without_summary()
    {
        var stream = new PlanStream(2);

        await Assert.ThrowsAsync<InvalidOperationException>(() => BenchmarkPlanCursor.ConsumeAsync(stream.Cursor, 3));

        Assert.Equal(3, stream.Fetches);
        Assert.Equal(0, stream.Consumes);
    }

    [Fact]
    public async Task Pre_canceled_token_performs_no_cursor_operations()
    {
        var stream = new PlanStream(1);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            BenchmarkPlanCursor.ConsumeAsync(stream.Cursor, 1, cancellation.Token));

        Assert.Equal(0, stream.Fetches);
        Assert.Equal(0, stream.Consumes);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task Cancellation_during_fetch_stops_before_another_fetch_or_summary(int cancelOnFetch)
    {
        using var cancellation = new CancellationTokenSource();
        var stream = new PlanStream(2) { AfterFetch = count => { if (count == cancelOnFetch) cancellation.Cancel(); } };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            BenchmarkPlanCursor.ConsumeAsync(stream.Cursor, 2, cancellation.Token));

        Assert.Equal(cancelOnFetch, stream.Fetches);
        Assert.Equal(0, stream.Consumes);
    }

    [Fact]
    public async Task Fetch_failure_is_preserved_without_summary()
    {
        var failure = new InvalidOperationException("fetch failure");
        var stream = new PlanStream(2) { FetchFailure = failure };

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() => BenchmarkPlanCursor.ConsumeAsync(stream.Cursor, 2));

        Assert.Same(failure, actual);
        Assert.Equal(0, stream.Consumes);
    }

    [Fact]
    public async Task Negative_expected_count_fails_before_transport()
    {
        var stream = new PlanStream(0);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => BenchmarkPlanCursor.ConsumeAsync(stream.Cursor, -1));

        Assert.Equal(0, stream.Fetches);
        Assert.Equal(0, stream.Consumes);
    }

    private sealed class PlanStream
    {
        private readonly int _rows;
        private bool _exhausted;

        public PlanStream(int rows)
        {
            _rows = rows;
            Summary = Proxy.For<IResultSummary>((_, _) => throw new InvalidOperationException("Summary fields are not needed."));
            Cursor = Proxy.For<IResultCursor>((method, _) => method.Name switch
            {
                "FetchAsync" => FetchAsync(),
                "ConsumeAsync" => ConsumeAsync(),
                _ => throw new InvalidOperationException("No records may be read or buffered: " + method.Name)
            });
        }

        public IResultCursor Cursor { get; }
        public IResultSummary Summary { get; }
        public int Fetches { get; private set; }
        public int Consumes { get; private set; }
        public Action<int>? AfterFetch { get; init; }
        public Exception? FetchFailure { get; init; }

        private Task<bool> FetchAsync()
        {
            Fetches++;
            if (FetchFailure is { } failure) return Task.FromException<bool>(failure);
            var available = Fetches <= _rows;
            _exhausted = !available;
            AfterFetch?.Invoke(Fetches);
            return Task.FromResult(available);
        }

        private Task<IResultSummary> ConsumeAsync()
        {
            Consumes++;
            if (!_exhausted)
                throw new InvalidOperationException("Consuming an unread stream discards rows; complete PROFILE statistics are unavailable.");
            return Task.FromResult(Summary);
        }
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
