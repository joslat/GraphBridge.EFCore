namespace GraphBridge.Benchmarks;

public static class BulkProfileMode
{
    public static bool Extract(ref string[] arguments)
    {
        var count = arguments.Count(a => a == "--bulk");
        if (count > 1) throw new ArgumentException("Select bulk profiling once.");
        arguments = arguments.Where(a => a != "--bulk").ToArray();
        return count == 1;
    }

    public static string Case(int size, string operation, string backend) => $"bulk/batch-{size}/{operation}/{backend}";
    public static readonly IReadOnlyList<string> Operations = Array.AsReadOnly(new[] { "update", "delete" });
    public static readonly IReadOnlyList<string> Backends = Array.AsReadOnly(new[] { "tracked", "bulk", "driver-batch" });

    public static void Validate(BenchmarkOptions options, IReadOnlyList<BenchmarkSample> samples, IReadOnlyList<BenchmarkProtocolSample> protocols)
    {
        var cases = options.WriteSizes.SelectMany(size => Operations.SelectMany(operation => Backends.Select(backend =>
            (Name: Case(size, operation, backend), Size: size, Backend: backend)))).ToArray();
        if (samples.Count != cases.Length * (1 + options.Warmups + options.Samples) || protocols.Count != cases.Length)
            throw new InvalidOperationException("Bulk evidence does not cover the selected matrix.");
        foreach (var item in cases)
        {
            var rows = samples.Where(s => s.Case == item.Name).OrderBy(s => s.Iteration).ToArray();
            var expected = Enumerable.Range(-options.Warmups - 1, options.Warmups + 1 + options.Samples).ToArray();
            if (!rows.Select(r => r.Iteration).SequenceEqual(expected)) throw new InvalidOperationException("Bulk sample iterations are incomplete or duplicated.");
            foreach (var row in rows)
            {
                var phase = row.Iteration == -options.Warmups - 1 ? "first-observed" : row.Iteration < 0 ? "warmup" : "warm";
                var runs = item.Backend switch { "tracked" => 2L * item.Size + 2, "bulk" => 4, _ => 2 };
                if (row.Phase != phase || row.ResultCount != item.Size || !double.IsFinite(row.TotalMilliseconds)
                    || row.TotalMilliseconds <= 0 || row.ProcessAllocatedBytes < 0 || row.Commands.RunAsync != runs
                    || row.Commands.Begin != 1 || row.Commands.Commit != 1 || row.Commands.Rollback != 0
                    || (row.Phase == "warm" && row.CompilationCount != 0))
                    throw new InvalidOperationException("Bulk sample count, transaction, compilation or timing contract differs.");
            }
            var matching = protocols.Where(p => p.Case == item.Name).ToArray();
            if (matching.Length != 1 || !matching[0].Measurement.IsValid || matching[0].Measurement.CompletedSerialExchanges is not > 0)
                throw new InvalidOperationException("Bulk protocol evidence is invalid, missing or duplicated.");
        }
    }
}
