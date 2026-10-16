namespace GraphBridge.Benchmarks;

public sealed record Distribution(int Count, double Minimum, double P50, double P95, double P99, double Maximum)
{
    // Empirical nearest-rank percentiles. A small sample's p99 is often its maximum; no interpolation or SLO claim.
    public static Distribution From(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        if (sorted.Length == 0 || sorted.Any(x => !double.IsFinite(x) || x < 0))
            throw new ArgumentException("Samples must be nonempty, finite and nonnegative.", nameof(values));
        double Percentile(double fraction) => sorted[Math.Max(0, (int)Math.Ceiling(fraction * sorted.Length) - 1)];
        return new(sorted.Length, sorted[0], Percentile(.50), Percentile(.95), Percentile(.99), sorted[^1]);
    }
}

public sealed record CommandCounts(long RunAsync, long FetchAsync, long ConsumeAsync, long Begin, long Commit, long Rollback);
public sealed record BenchmarkSample(string Case, string Phase, int Iteration, double TotalMilliseconds,
    double? FirstRowMilliseconds, double CompilationMilliseconds, int CompilationCount,
    long ProcessAllocatedBytes, CommandCounts Commands, long ResultCount, long Checksum)
{
    // Observable phase boundaries, not a claim of pure transport or isolated materializer CPU.
    public double NonCompilationMilliseconds => Math.Max(0, TotalMilliseconds - CompilationMilliseconds);
    public double? AfterFirstRowMilliseconds => FirstRowMilliseconds is { } first ? Math.Max(0, TotalMilliseconds - first) : null;
}
public sealed record BenchmarkSummary(string Case, Distribution Milliseconds, Distribution ProcessAllocatedBytes);
