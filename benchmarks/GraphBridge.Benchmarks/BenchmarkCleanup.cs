using System.Runtime.ExceptionServices;
using System.Text.RegularExpressions;
using Neo4j.Driver;

namespace GraphBridge.Benchmarks;

/// <summary>Best-effort owned cleanup and evidence persistence without replacing the workload failure.</summary>
public sealed class BenchmarkCleanup
{
    private readonly List<BenchmarkCleanupFailure> _cleanupFailures = [];

    public async Task RunWithCleanupAsync(string run, Func<Task> workload, Func<string, Task> cleanup)
    {
        Exception? primary = null;
        try { await workload(); }
        catch (Exception error) { primary = error; }
        var cleanupError = await AttemptCleanupAsync(run, cleanup);
        if ((primary ?? cleanupError) is { } failure) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    public async Task FinishAsync(Exception? primary, Func<IReadOnlyCollection<string>> remainingRuns,
        Func<string, Task> cleanup, Func<Task> persistRaw, Func<Task> persistCompletion,
        Func<BenchmarkFailureReport, Task> persistFailure, string stage = "finalization")
    {
        Exception? firstFailure = primary;
        foreach (var run in remainingRuns().ToArray())
        {
            var error = await AttemptCleanupAsync(run, cleanup);
            firstFailure ??= error;
        }
        var artifactFailures = new List<string>();
        try { await persistRaw(); }
        catch (Exception error) { firstFailure ??= error; artifactFailures.Add(error.GetType().FullName!); }
        if (firstFailure is null)
        {
            try { await persistCompletion(); }
            catch (Exception error) { firstFailure = error; artifactFailures.Add(error.GetType().FullName!); }
        }
        if (firstFailure is not null)
        {
            // Only locally generated run identifiers and exception types leave this boundary.
            var report = new BenchmarkFailureReport(stage, firstFailure.GetType().FullName!,
                remainingRuns().Order(StringComparer.Ordinal).ToArray(), _cleanupFailures.ToArray(), artifactFailures,
                ServerCode(firstFailure));
            try { await persistFailure(report); }
            catch { /* Evidence failure must not replace the workload/cleanup/persistence exception. */ }
            ExceptionDispatchInfo.Capture(firstFailure).Throw();
        }
    }

    private async Task<Exception?> AttemptCleanupAsync(string run, Func<string, Task> cleanup)
    {
        try { await cleanup(run); return null; }
        catch (Exception error)
        {
            _cleanupFailures.Add(new(run, error.GetType().FullName!));
            return error;
        }
    }

    private static string? ServerCode(Exception error)
        => error is Neo4jException { Code: { Length: <= 180 } code }
            && Regex.IsMatch(code, @"\ANeo\.(ClientError|TransientError|DatabaseError)\.[A-Za-z][A-Za-z0-9]{0,63}\.[A-Za-z][A-Za-z0-9]{0,63}\z",
                RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)
            ? code : null;
}

public sealed record BenchmarkCleanupFailure(string Run, string ExceptionType);
public sealed record BenchmarkFailureReport(string Stage, string PrimaryExceptionType, IReadOnlyList<string> RemainingRunIds,
    IReadOnlyList<BenchmarkCleanupFailure> CleanupFailures, IReadOnlyList<string> ArtifactExceptionTypes, string? Neo4jErrorCode = null);
