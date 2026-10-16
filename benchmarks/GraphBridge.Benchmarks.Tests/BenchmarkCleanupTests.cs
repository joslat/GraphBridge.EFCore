using System.Text.Json;
using Neo4j.Driver;

namespace GraphBridge.Benchmarks.Tests;

public sealed class BenchmarkCleanupTests
{
    private const string FirstRun = "11111111111111111111111111111111";
    private const string SecondRun = "22222222222222222222222222222222";

    [Theory]
    [InlineData("Neo.DatabaseError.Statement.ExecutionFailed", "Neo.DatabaseError.Statement.ExecutionFailed")]
    [InlineData("private token or query", null)]
    public async Task Failure_report_preserves_bounded_server_code_but_never_message(string code, string? expected)
    {
        var error = new DatabaseException(code, "private token or query");
        BenchmarkFailureReport? report = null;
        var actual = await Assert.ThrowsAsync<DatabaseException>(() => new BenchmarkCleanup().FinishAsync(error,
            () => Array.Empty<string>(), _ => Task.CompletedTask, () => Task.CompletedTask, () => Task.CompletedTask,
            value => { report = value; return Task.CompletedTask; }));
        Assert.Same(error, actual);
        Assert.NotNull(report);
        Assert.Equal(expected, report.Neo4jErrorCode);
        Assert.DoesNotContain("private", JsonSerializer.Serialize(report));
    }

    [Fact]
    public async Task Workload_failure_survives_nested_cleanup_failures_and_all_finalization_attempts()
    {
        var cleanup = new BenchmarkCleanup();
        var primary = new InvalidOperationException("secret workload payload");
        var remaining = new HashSet<string> { FirstRun, SecondRun };
        var calls = new List<string>();
        BenchmarkFailureReport? report = null;
        Task Delete(string run)
        {
            calls.Add(run);
            if (run == FirstRun) throw new IOException("secret cleanup payload");
            remaining.Remove(run);
            return Task.CompletedTask;
        }
        var nested = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            cleanup.RunWithCleanupAsync(FirstRun, () => Task.FromException(primary), Delete));
        Assert.Same(primary, nested);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => cleanup.FinishAsync(nested,
            () => remaining.ToArray(), Delete,
            () => { calls.Add("raw"); return Task.CompletedTask; },
            () => { calls.Add("completion"); return Task.CompletedTask; },
            value => { report = value; calls.Add("failure"); return Task.CompletedTask; }, "read/100/ef/tracked/full"));

        Assert.Same(primary, error);
        Assert.Equal(2, calls.Count(x => x == FirstRun));
        Assert.Contains(SecondRun, calls);
        Assert.Equal(["raw", "failure"], calls.TakeLast(2));
        Assert.DoesNotContain("completion", calls);
        Assert.NotNull(report);
        Assert.Equal([FirstRun], report.RemainingRunIds);
        Assert.Equal("read/100/ef/tracked/full", report.Stage);
        Assert.Equal(typeof(InvalidOperationException).FullName, report.PrimaryExceptionType);
        Assert.Equal(2, report.CleanupFailures.Count);
        Assert.DoesNotContain("secret", JsonSerializer.Serialize(report));
    }

    [Fact]
    public async Task Raw_and_failure_artifact_errors_do_not_replace_primary_or_skip_owned_cleanup()
    {
        var cleanup = new BenchmarkCleanup();
        var primary = new OperationCanceledException("private cancellation details");
        var calls = new List<string>();
        BenchmarkFailureReport? report = null;
        var error = await Assert.ThrowsAsync<OperationCanceledException>(() => cleanup.FinishAsync(primary,
            () => new[] { FirstRun, SecondRun },
            run => { calls.Add(run); return Task.CompletedTask; },
            () => { calls.Add("raw"); throw new IOException("private filesystem path"); },
            () => { calls.Add("completion"); return Task.CompletedTask; },
            value => { report = value; calls.Add("failure"); throw new UnauthorizedAccessException("private path"); }));
        Assert.Same(primary, error);
        Assert.Equal([FirstRun, SecondRun, "raw", "failure"], calls);
        Assert.NotNull(report);
        Assert.Equal([typeof(IOException).FullName!], report.ArtifactExceptionTypes);
        Assert.DoesNotContain("private", JsonSerializer.Serialize(report));
    }

    [Fact]
    public async Task First_cleanup_failure_is_rethrown_after_remaining_cleanup_and_raw_persistence()
    {
        var cleanup = new BenchmarkCleanup();
        var first = new IOException("unpublished");
        var calls = new List<string>();
        var error = await Assert.ThrowsAsync<IOException>(() => cleanup.FinishAsync(null,
            () => new[] { FirstRun, SecondRun },
            run => { calls.Add(run); throw run == FirstRun ? first : new InvalidOperationException(); },
            () => { calls.Add("raw"); return Task.CompletedTask; },
            () => { calls.Add("completion"); return Task.CompletedTask; },
            _ => { calls.Add("failure"); return Task.CompletedTask; }));
        Assert.Same(first, error);
        Assert.Equal([FirstRun, SecondRun, "raw", "failure"], calls);
    }

    [Fact]
    public async Task Completion_is_persisted_only_after_cleanup_and_raw_evidence_succeed()
    {
        var calls = new List<string>();
        await new BenchmarkCleanup().FinishAsync(null, () => new[] { FirstRun },
            run => { calls.Add(run); return Task.CompletedTask; },
            () => { calls.Add("raw"); return Task.CompletedTask; },
            () => { calls.Add("completion"); return Task.CompletedTask; },
            _ => { calls.Add("failure"); return Task.CompletedTask; });
        Assert.Equal([FirstRun, "raw", "completion"], calls);
    }

    [Fact]
    public async Task Raw_persistence_failure_prevents_completion_even_when_workload_and_cleanup_succeeded()
    {
        var failure = new IOException("unpublished path");
        var calls = new List<string>();
        var error = await Assert.ThrowsAsync<IOException>(() => new BenchmarkCleanup().FinishAsync(null,
            () => Array.Empty<string>(), _ => Task.CompletedTask,
            () => { calls.Add("raw"); throw failure; },
            () => { calls.Add("completion"); return Task.CompletedTask; },
            _ => { calls.Add("failure"); return Task.CompletedTask; }));
        Assert.Same(failure, error);
        Assert.Equal(["raw", "failure"], calls);
    }
}
