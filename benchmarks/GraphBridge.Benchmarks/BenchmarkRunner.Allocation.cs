using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace GraphBridge.Benchmarks;

public sealed partial class BenchmarkRunner
{
    /// <summary>Focused tracing workload; uses the unchanged baseline seed/read/assertion path.</summary>
    public async Task RunAllocationProfileAsync(string backend, CancellationToken cancellationToken)
    {
        if (backend is not ("driver" or "ef") || options.GraphSizes.Length != 1 || options.GraphSizes[0] != 100000)
            throw new ArgumentException("Allocation profiling requires driver/ef and exactly 100000 nodes.");
        _driver = ObservedDriverProxy.Wrap(rawDriver, _observation);
        var builder = new DbContextOptionsBuilder<BenchmarkContext>().UseNeo4j(_driver, "neo4j");
        builder.LogTo((id, _) => id == CoreEventId.QueryCompilationStarting || id == CoreEventId.QueryExecutionPlanned, _compilation.OnEvent);
        _contextOptions = builder.Options;
        if (Directory.Exists(options.OutputDirectory) && Directory.EnumerateFileSystemEntries(options.OutputDirectory).Any())
            throw new InvalidOperationException("Allocation output must be new or empty.");
        Directory.CreateDirectory(options.OutputDirectory);
        Exception? failure = null;
        try
        {
            await PrepareAsync(cancellationToken);
            await using (var context = Context()) _ = context.Model;
            await SaveJsonAsync("environment.json", new { Environment = _environment, Options = options, Backend = backend,
                Scope = "Focused original full-entity read; allocations process-wide; tracing overhead separate from baseline." });
            var run = OwnRun();
            await _cleanup.RunWithCleanupAsync(run, async () =>
            {
                _stage = "allocation-seed";
                await SeedAsync(run, 100000, cancellationToken);
                await VerifyRunAsync(run, 100000, 1, "person-", cancellationToken);
                var name = backend == "driver" ? "driver/full" : "ef/untracked/full";
                _samples.Add(await MeasureReadAsync(name, run, 100000, "first-observed", 0, cancellationToken));
                for (var iteration = -options.Warmups; iteration < options.Samples; iteration++)
                {
                    var phase = iteration < 0 ? "warmup" : "warm";
                    AllocationProfileEvents.Log.SampleBegin(backend, phase, iteration);
                    try { _samples.Add(await MeasureReadAsync(name, run, 100000, phase, iteration, cancellationToken)); }
                    finally { AllocationProfileEvents.Log.SampleEnd(backend, phase, iteration); }
                }
            }, CleanupAsync);
        }
        catch (Exception error) { failure = error; }
        await _cleanup.FinishAsync(failure, () => _ownedRuns.ToArray(), CleanupAsync,
            () => SaveJsonAsync("allocation-samples.json", _samples),
            () => SaveJsonAsync("allocation-completion.json", new { Completed = true, Backend = backend, Rows = 100000,
                options.Samples, options.Warmups, SampleCount = _samples.Count, Cleaned = true,
                Scope = "Full-entity allocation experiment only; no writes/edges/protocol/plan matrix claimed." }),
            report => SaveJsonAsync("failure.json", report), _stage);
    }
}
