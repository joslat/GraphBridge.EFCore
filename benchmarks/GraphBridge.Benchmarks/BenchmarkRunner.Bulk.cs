using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Neo4j.Driver;

namespace GraphBridge.Benchmarks;

public sealed partial class BenchmarkRunner
{
    public async Task RunBulkProfileAsync(CancellationToken cancellationToken)
    {
        if (!options.GraphSizes.SequenceEqual([1]) || !options.EdgeSizes.SequenceEqual([1]))
            throw new ArgumentException("Bulk node profiling uses WriteSizes; set graph/edge placeholder sizes to 1.");
        _driver = ObservedDriverProxy.Wrap(rawDriver, _observation);
        var builder = new DbContextOptionsBuilder<BenchmarkContext>().UseNeo4j(_driver, "neo4j");
        builder.LogTo((id, _) => id == CoreEventId.QueryCompilationStarting || id == CoreEventId.QueryExecutionPlanned, _compilation.OnEvent);
        _contextOptions = builder.Options;
        if (Directory.Exists(options.OutputDirectory) && Directory.EnumerateFileSystemEntries(options.OutputDirectory).Any())
            throw new InvalidOperationException("Bulk output must be new or empty.");
        Directory.CreateDirectory(options.OutputDirectory);
        var commands = new List<object>();
        var shapes = new Dictionary<string, string[]>(StringComparer.Ordinal);
        Exception? failure = null;
        try
        {
            await PrepareAsync(cancellationToken);
            var watch = Stopwatch.StartNew();
            await using (var context = Context()) _ = context.Model;
            await SaveJsonAsync("environment.json", new { Environment = _environment, Options = options,
                ModelInitializationMilliseconds = watch.Elapsed.TotalMilliseconds,
                TieredCompilation = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation") ?? "runtime default",
                ProviderBinarySha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(typeof(GraphModelBuilderExtensions).Assembly.Location))),
                Scope = "Nodes only; end-to-end tracked read/save vs bounded EF bulk vs model-specific guarded native batch. No edge throughput claim.",
                ControlBoundary = "Native control validates the key constraint and expected count/version on this seeded fixture; it does not implement all provider mapping/type/snapshot guards.",
                Observation = "Identical public-driver observer in all arms. Protocol debug capture runs in separate untimed probes. Seeding, verification and cleanup excluded." });
            foreach (var size in options.WriteSizes)
            foreach (var operation in BulkProfileMode.Operations)
            {
                for (var iteration = -options.Warmups - 1; iteration < options.Samples; iteration++)
                foreach (var backend in BulkProfileMode.Backends.OrderBy(_ => _random.Next()))
                {
                    _stage = BulkProfileMode.Case(size, operation, backend) + "/" + iteration;
                    cancellationToken.ThrowIfCancellationRequested();
                    var run = OwnRun();
                    await _cleanup.RunWithCleanupAsync(run, async () =>
                    {
                        await SeedAsync(run, size, cancellationToken);
                        _observation.Reset(); _compilation.Reset();
                        var allocatedBefore = GC.GetTotalAllocatedBytes(true);
                        var start = Stopwatch.GetTimestamp();
                        var affected = await MutateBulkProfileAsync(operation, backend, run, size, cancellationToken);
                        var milliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                        var allocated = GC.GetTotalAllocatedBytes(true) - allocatedBefore;
                        Require(affected == size, "Bulk profile affected count mismatch.");
                        var caseName = BulkProfileMode.Case(size, operation, backend);
                        var currentShapes = _observation.Shapes.Keys.ToArray();
                        if (shapes.TryGetValue(caseName, out var initialShapes))
                            Require(initialShapes.SequenceEqual(currentShapes), "Bulk command shapes changed between samples.");
                        else shapes.Add(caseName, currentShapes);
                        _samples.Add(new(BulkProfileMode.Case(size, operation, backend),
                            iteration == -options.Warmups - 1 ? "first-observed" : iteration < 0 ? "warmup" : "warm",
                            iteration, milliseconds, null, _compilation.Milliseconds, _compilation.Count,
                            allocated, _observation.Snapshot(), affected, 0));
                        if (iteration == -options.Warmups - 1)
                            commands.Add(new { Case = BulkProfileMode.Case(size, operation, backend), Cypher = _observation.Shapes.Keys.ToArray() });
                        await VerifyBulkProfileAsync(run, operation, size, cancellationToken);
                    }, CleanupAsync);
                }
                foreach (var backend in BulkProfileMode.Backends)
                {
                    var run = OwnRun();
                    await _cleanup.RunWithCleanupAsync(run, async () =>
                    {
                        await SeedAsync(run, size, cancellationToken);
                        _observation.Reset();
                        await ObserveProtocolAsync(BulkProfileMode.Case(size, operation, backend), async () =>
                        {
                            Require(await MutateBulkProfileAsync(operation, backend, run, size, cancellationToken) == size,
                                "Bulk protocol affected count mismatch.");
                        });
                        await VerifyBulkProfileAsync(run, operation, size, cancellationToken);
                    }, CleanupAsync);
                }
                await SaveJsonAsync("raw-samples.json", _samples);
            }
            BulkProfileMode.Validate(options, _samples, _protocolSamples);
        }
        catch (Exception error) { failure = error; }
        await _cleanup.FinishAsync(failure, () => _ownedRuns.ToArray(), CleanupAsync,
            async () =>
            {
                await SaveJsonAsync("raw-samples.json", _samples);
                await SaveJsonAsync("commands.json", commands);
                await SaveJsonAsync("protocol-samples.json", _protocolSamples);
                await SaveJsonAsync("summary.json", _samples.Where(s => s.Phase == "warm").GroupBy(s => s.Case)
                    .Select(g => new { Case = g.Key, Milliseconds = Distribution.From(g.Select(s => s.TotalMilliseconds)),
                        MeanMilliseconds = g.Average(s => s.TotalMilliseconds),
                        RowsPerSecond = g.First().ResultCount * 1000 / g.Average(s => s.TotalMilliseconds),
                        MeanAllocatedBytes = g.Average(s => s.ProcessAllocatedBytes), Commands = g.First().Commands }));
            },
            () => SaveJsonAsync("bulk-completion.json", new { Completed = true, Cleaned = true, ShapesStable = true, Mode = "bulk-nodes",
                BatchSizes = options.WriteSizes, options.Samples, options.Warmups, SampleCount = _samples.Count,
                ProtocolCount = _protocolSamples.Count }), report => SaveJsonAsync("failure.json", report), _stage);
    }

    private async Task<int> MutateBulkProfileAsync(string operation, string backend, string run, int count, CancellationToken cancellationToken)
    {
        if (backend != "driver-batch")
        {
            await using var context = Context();
            if (backend == "bulk")
                return operation == "delete" ? await context.People.Where(p => p.Run == run).ExecuteDeleteAsync(cancellationToken)
                    : await context.People.Where(p => p.Run == run).ExecuteUpdateAsync(s => s
                        .SetProperty(p => p.Name, "bulk-updated").SetProperty(p => p.Ordinal, p => p.Ordinal + 1), cancellationToken);
            var people = await context.People.Where(p => p.Run == run).ToListAsync(cancellationToken);
            Require(people.Count == count, "Tracked baseline selection differs.");
            if (operation == "delete") context.RemoveRange(people);
            else foreach (var person in people) { person.Name = "bulk-updated"; person.Ordinal++; }
            return await context.SaveChangesAsync(cancellationToken);
        }
        await using var session = _driver.AsyncSession(o => o.WithDatabase("neo4j").WithDefaultAccessMode(AccessMode.Write));
        await using var transaction = await session.BeginTransactionAsync();
        try
        {
            await ValidateConstraintAsync(transaction);
            var mutation = operation == "delete" ? "DELETE n" : "SET n.Name = $name, n.Ordinal = n.Ordinal + 1, n.Version = n.Version + 1";
            var cursor = await transaction.RunAsync($"MATCH (n:{Label}) WHERE n.Run = $run SET n.Id = n.Id WITH n WHERE n.Version = 1 {mutation} RETURN count(n) AS count",
                new { run, name = "bulk-updated" });
            var affected = (await cursor.SingleAsync())["count"].As<int>();
            Require(affected == count, "Native batch version/cardinality mismatch.");
            cancellationToken.ThrowIfCancellationRequested();
            await transaction.CommitAsync();
            return affected;
        }
        catch { try { await transaction.RollbackAsync(); } catch { /* Preserve original failure. */ } throw; }
    }

    private async Task VerifyBulkProfileAsync(string run, string operation, int count, CancellationToken cancellationToken)
    {
        await using var session = rawDriver.AsyncSession(o => o.WithDatabase("neo4j"));
        var cursor = await session.RunAsync(FullRead, new { run });
        var ordinal = 0;
        while (await cursor.FetchAsync())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var row = cursor.Current;
            Require(operation == "update" && row["Id"].As<string>() == Key(run, ordinal).ToString("D")
                && row["Run"].As<string>() == run && row["Ordinal"].As<int>() == ordinal + 1
                && row["Name"].As<string>() == "bulk-updated" && row["Version"].As<long>() == 2,
                "Fresh read differs from the complete expected bulk result.");
            ordinal++;
        }
        Require(ordinal == (operation == "delete" ? 0 : count), "Fresh bulk result count differs.");
        await cursor.ConsumeAsync();
    }
}
