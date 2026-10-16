using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Neo4j.Driver;

namespace GraphBridge.Benchmarks;

public sealed partial class BenchmarkRunner(BenchmarkOptions options, IDriver rawDriver, string fixtureId, BoltProtocolObservation protocol)
{
    private const string Label = "GraphBridgeBenchmarkPerson";
    private const string Constraint = "gb_benchmark_person_key";
    private const string ConstraintsQuery = "SHOW CONSTRAINTS YIELD type, entityType, labelsOrTypes, properties RETURN type, entityType, labelsOrTypes, properties";
    private const string FullRead = "MATCH (n:GraphBridgeBenchmarkPerson) WHERE n.Run = $run RETURN n.Id AS Id, n.Run AS Run, n.Ordinal AS Ordinal, n.Name AS Name, n.Version AS Version ORDER BY n.Ordinal";
    private const string ScalarRead = "MATCH (n:GraphBridgeBenchmarkPerson) WHERE n.Run = $run RETURN n.Ordinal AS Ordinal ORDER BY n.Ordinal";
    private const string Create = "CREATE (n:GraphBridgeBenchmarkPerson) SET n = $properties RETURN count(n) AS count";
    private const string Lock = "MATCH (n:GraphBridgeBenchmarkPerson {Id: $id}) SET n.Id = n.Id RETURN count(n) AS count";
    private const string Update = "MATCH (n:GraphBridgeBenchmarkPerson {Id: $id}) WHERE coalesce(n.Version = $version, (n.Version IS NULL AND $version IS NULL)) SET n += $changes RETURN count(n) AS count";
    private const string Delete = "MATCH (n:GraphBridgeBenchmarkPerson {Id: $id}) WHERE coalesce(n.Version = $version, (n.Version IS NULL AND $version IS NULL)) DELETE n RETURN count(n) AS count";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly DriverObservation _observation = new();
    private readonly CompilationObservation _compilation = new();
    private readonly List<BenchmarkSample> _samples = [];
    private readonly List<object> _profiles = [];
    private readonly List<object> _explanations = [];
    private readonly List<BenchmarkProtocolSample> _protocolSamples = [];
    private readonly HashSet<string> _ownedRuns = new(StringComparer.Ordinal);
    private readonly BenchmarkCleanup _cleanup = new();
    private readonly Random _random = new(options.Seed);
    private IDriver _driver = null!;
    private DbContextOptions<BenchmarkContext> _contextOptions = null!;
    private object _environment = null!;
    private string _stage = "prepare";

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _driver = ObservedDriverProxy.Wrap(rawDriver, _observation);
        var builder = new DbContextOptionsBuilder<BenchmarkContext>().UseNeo4j(_driver, "neo4j");
        builder.LogTo((id, _) => id == CoreEventId.QueryCompilationStarting || id == CoreEventId.QueryExecutionPlanned,
            _compilation.OnEvent);
        _contextOptions = builder.Options;
        if (Directory.Exists(options.OutputDirectory) && Directory.EnumerateFileSystemEntries(options.OutputDirectory).Any())
            throw new InvalidOperationException("Benchmark output directory must be new or empty; existing evidence is never overwritten.");
        Directory.CreateDirectory(options.OutputDirectory);
        Exception? primaryFailure = null;
        EdgeBenchmarkResult? edgeResult = null;
        try
        {
            await PrepareAsync(cancellationToken);
            _stage = "initialize-model";
            // Model construction deliberately excluded from query compilation/execution timings; reported separately.
            var modelWatch = Stopwatch.StartNew();
            await using (var context = Context()) _ = context.Model;
            var modelInitializationMilliseconds = modelWatch.Elapsed.TotalMilliseconds;
            await SaveJsonAsync("environment.json", new { Environment = _environment, ModelInitializationMilliseconds = modelInitializationMilliseconds, Options = options });
            foreach (var size in options.GraphSizes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var run = OwnRun();
                await _cleanup.RunWithCleanupAsync(run, async () =>
                {
                    _stage = $"seed/{size}";
                    await SeedAsync(run, size, cancellationToken);
                    await VerifyRunAsync(run, size, 1, "person-", cancellationToken);
                    await ReadsAsync(run, size, cancellationToken);
                    foreach (var count in options.WriteSizes)
                        await WritesAsync(size, count, cancellationToken);
                }, CleanupAsync);
            }
            await VerifyRollbackAsync(cancellationToken);
            Require(_protocolSamples.Count == options.GraphSizes.Length * (8 + 6 * options.WriteSizes.Length)
                && _protocolSamples.All(sample => sample.Measurement.IsValid), "Protocol evidence does not cover the selected matrix.");
            Require(_explanations.Count == options.GraphSizes.Length * 13 && _profiles.Count == _explanations.Count,
                "EXPLAIN and PROFILE evidence must cover eight read cases and five representative write steps per graph size.");
            _stage = "persist-summary";
            await SaveJsonAsync("summary.json", _samples.Where(x => x.Phase == "warm")
                .GroupBy(x => x.Case).Select(group => new BenchmarkSummary(group.Key,
                    Distribution.From(group.Select(x => x.TotalMilliseconds)),
                    Distribution.From(group.Select(x => (double)x.ProcessAllocatedBytes)))));
            _stage = "persist-profiles";
            await SaveJsonAsync("profiles.json", _profiles);
            await SaveJsonAsync("explanations.json", _explanations);
            _stage = "edge-workload";
            edgeResult = await new EdgeBenchmarkWorkload(rawDriver, protocol, options, fixtureId,
                options.EdgeSizes, SaveJsonAsync).RunAsync(cancellationToken);
            _stage = "finalization";
        }
        catch (Exception error) { primaryFailure = error; }
        // Cancellation does not skip cleanup or evidence; completion is written only after both succeed.
        await _cleanup.FinishAsync(primaryFailure, () => _ownedRuns.ToArray(), CleanupAsync,
            () => Task.WhenAll(SaveJsonAsync("raw-samples.json", _samples), SaveJsonAsync("protocol-samples.json", _protocolSamples),
                SaveJsonAsync("profiles.json", _profiles), SaveJsonAsync("explanations.json", _explanations)),
            () =>
            {
                var completedEdges = edgeResult ?? throw new InvalidOperationException("Edge workload completion and cleanup are required.");
                return SaveJsonAsync("completion.json", new { Completed = true, FinishedUtc = DateTimeOffset.UtcNow,
                    BoltMetricStatus = "Verified completed serial Bolt batch exchanges in separate untimed executions; not TCP packet counts or RTT latency.",
                    Nodes = new { GraphSizes = options.GraphSizes, WriteSizes = options.WriteSizes,
                        ReadCases = options.GraphSizes.Length * 8, WriteCases = options.GraphSizes.Length * options.WriteSizes.Length * 6,
                        ProtocolCases = _protocolSamples.Count, ExplainCases = _explanations.Count, ProfileCases = _profiles.Count },
                    Edges = new { EdgeCounts = completedEdges.EdgeCounts, ReadCases = completedEdges.EdgeCounts.Count * 8,
                        ProtocolCases = completedEdges.ProtocolSamples.Count, ExplainAndProfilePlans = completedEdges.Plans.Count },
                    Scope = "Independent versioned node full/scalar reads and atomic create/update/delete batches; identified-edge full/scalar reads. Both workloads verified and cleaned. No edge-write, association-edge, native-reference or Include performance claim." });
            },
            report => SaveJsonAsync("failure.json", report), _stage);
    }

    private async Task PrepareAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var session = rawDriver.AsyncSession(o => o.WithDatabase("neo4j"));
        var marker = await session.RunAsync("MATCH (f:GraphBridgeFixture {id: $fixtureId}) RETURN count(f) AS count", new { fixtureId });
        Require((await marker.SingleAsync())["count"].As<long>() == 1, "Owned Community fixture marker missing.");
        var components = await session.RunAsync("CALL dbms.components() YIELD name, versions, edition WHERE name = 'Neo4j Kernel' RETURN versions[0] AS version, edition");
        var server = await components.SingleAsync();
        Require(server["version"].As<string>() == "2026.09.0" && server["edition"].As<string>().Equals("community", StringComparison.OrdinalIgnoreCase),
            "The benchmark requires exact pinned Neo4j 2026.09.0 Community.");
        _environment = new
        {
            StartedUtc = DateTimeOffset.UtcNow, ServerVersion = server["version"].As<string>(), ServerEdition = server["edition"].As<string>(),
            Runtime = RuntimeInformation.FrameworkDescription, OS = RuntimeInformation.OSDescription,
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(), Environment.ProcessorCount,
            Driver = typeof(IDriver).Assembly.GetName().Version?.ToString(), EF = typeof(DbContext).Assembly.GetName().Version?.ToString(),
            Provider = typeof(GraphModelBuilderExtensions).Assembly.GetName().Version?.ToString(),
            GitCommit = Environment.GetEnvironmentVariable("GRAPHBRIDGE_BENCHMARK_COMMIT") ?? "not supplied",
            FixtureImage = Environment.GetEnvironmentVariable("GRAPHBRIDGE_BENCHMARK_IMAGE") ?? "not supplied",
            options.FetchSize, AllocationScope = "GC.GetTotalAllocatedBytes(precise:true), entire benchmark process; includes driver/background work and observer overhead",
            ColdScope = "First-observed query per case in this process; model and driver warmed; actual compilation event count identifies cache reuse. Not cold server/disk/TLS.",
            CpuModel = Environment.GetEnvironmentVariable("GRAPHBRIDGE_BENCHMARK_CPU") ?? "not supplied by guarded runner",
            HostMemory = Environment.GetEnvironmentVariable("GRAPHBRIDGE_BENCHMARK_MEMORY") ?? "not supplied by guarded runner",
            DockerLimits = Environment.GetEnvironmentVariable("GRAPHBRIDGE_BENCHMARK_DOCKER_LIMITS") ?? "not supplied by guarded runner"
        };
        await (await session.RunAsync($"CREATE CONSTRAINT {Constraint} IF NOT EXISTS FOR (n:{Label}) REQUIRE n.Id IS UNIQUE")).ConsumeAsync();
        await ValidateConstraintAsync(session);
        // This fixture-owned constraint is retained like other integration fixture schema; no unrelated schema is dropped.
    }

    private async Task ReadsAsync(string run, int size, CancellationToken cancellationToken)
    {
        var cases = new[] { "driver/full", "driver/scalar", "ef/tracked/full", "ef/untracked/full", "ef/identity/full",
            "ef/tracked/scalar", "ef/untracked/scalar", "ef/identity/scalar" };
        foreach (var name in cases)
        {
            var sample = await MeasureReadAsync(name, run, size, "first-observed", 0, cancellationToken);
            _samples.Add(sample);
            var command = _observation.Shapes.Values.Last();
            await ProfileReadAsync(name, size, command, cancellationToken);
        }
        for (var iteration = -options.Warmups; iteration < options.Samples; iteration++)
        {
            // Reproducible shuffled cases mitigate fixed-order cache/thermal bias. No timing thresholds.
            foreach (var name in cases.OrderBy(_ => _random.Next()))
                _samples.Add(await MeasureReadAsync(name, run, size, iteration < 0 ? "warmup" : "warm", iteration, cancellationToken));
        }
        foreach (var name in cases)
            await ObserveProtocolAsync($"read/{size}/{name}", async () =>
                _ = await MeasureReadAsync(name, run, size, "protocol-observation", 0, cancellationToken));
        await SaveJsonAsync("raw-samples.json", _samples);
    }

    private async Task<BenchmarkSample> MeasureReadAsync(string name, string run, int size, string phase, int iteration, CancellationToken cancellationToken)
    {
        _stage = $"read/{size}/{name}/{phase}/{iteration}";
        cancellationToken.ThrowIfCancellationRequested();
        _observation.Reset();
        _compilation.Reset();
        var allocated = GC.GetTotalAllocatedBytes(true);
        var start = Stopwatch.GetTimestamp();
        double? first = null;
        double acceptanceMilliseconds = 0;
        long acceptanceBytes = 0;
        long count = 0, checksum = 0;
        var scalar = name.EndsWith("/scalar", StringComparison.Ordinal);
        void Accept(int ordinal, BenchmarkPerson? person)
        {
            first ??= Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            Require(ordinal == count, "Read order/value differs from deterministic seed.");
            if (person is not null)
                Require(person.Id == Key(run, ordinal) && person.Run == run && person.Name == "person-" + ordinal.ToString(CultureInfo.InvariantCulture)
                    && person.Version == 1, "Full entity materialization differs from deterministic seed.");
            count++;
            checksum += ordinal;
        }
        if (name.StartsWith("driver/", StringComparison.Ordinal))
        {
            await using var session = _driver.AsyncSession(o => o.WithDatabase("neo4j"));
            var cursor = await session.RunAsync(scalar ? ScalarRead : FullRead, new { run });
            while (await cursor.FetchAsync())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var row = cursor.Current;
                var ordinal = row["Ordinal"].As<int>();
                Accept(ordinal, scalar ? null : new BenchmarkPerson { Id = Guid.ParseExact(row["Id"].As<string>(), "D"),
                    Run = row["Run"].As<string>(), Ordinal = ordinal, Name = row["Name"].As<string>(), Version = row["Version"].As<long>() });
            }
            await cursor.ConsumeAsync();
        }
        else
        {
            await using var context = Context();
            IQueryable<BenchmarkPerson> query = context.People.Where(p => p.Run == run).OrderBy(p => p.Ordinal);
            if (name.Contains("/untracked/", StringComparison.Ordinal)) query = query.AsNoTracking();
            if (name.Contains("/identity/", StringComparison.Ordinal)) query = query.AsNoTrackingWithIdentityResolution();
            if (scalar)
            {
                await foreach (var value in query.Select(p => p.Ordinal).AsAsyncEnumerable().WithCancellation(cancellationToken)) Accept(value, null);
            }
            else
            {
                await foreach (var person in query.AsAsyncEnumerable().WithCancellation(cancellationToken)) Accept(person.Ordinal, person);
            }
            // This acceptance-only state scan is not part of streaming/materialization. The context
            // remains alive for validation; its subsequent disposal is still included in the sample.
            var acceptanceAllocated = GC.GetTotalAllocatedBytes(true);
            var acceptanceStart = Stopwatch.GetTimestamp();
            Require(context.ChangeTracker.Entries().Count() == (!scalar && name.Contains("/tracked/", StringComparison.Ordinal) ? size : 0),
                "EF tracking mode did not preserve its documented state ownership.");
            acceptanceMilliseconds = Stopwatch.GetElapsedTime(acceptanceStart).TotalMilliseconds;
            acceptanceBytes = GC.GetTotalAllocatedBytes(true) - acceptanceAllocated;
        }
        var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds - acceptanceMilliseconds;
        var bytes = GC.GetTotalAllocatedBytes(true) - allocated - acceptanceBytes;
        Require(count == size && checksum == (long)size * (size - 1) / 2, "Read count/checksum mismatch.");
        return new($"read/{size}/{name}", phase, iteration, elapsed, first, _compilation.Milliseconds,
            _compilation.Count, bytes, _observation.Snapshot(), count, checksum);
    }

    private async Task WritesAsync(int graphSize, int count, CancellationToken cancellationToken)
    {
        foreach (var operation in new[] { "create", "update", "delete" })
        {
            for (var iteration = -options.Warmups - 1; iteration < options.Samples; iteration++)
            foreach (var backend in new[] { "driver", "ef" }.OrderBy(_ => _random.Next()))
            {
                _stage = $"write/graph-{graphSize}/batch-{count}/{operation}/{backend}/{iteration}";
                cancellationToken.ThrowIfCancellationRequested();
                var run = OwnRun();
                await _cleanup.RunWithCleanupAsync(run, async () =>
                {
                    if (operation != "create") await SeedAsync(run, count, cancellationToken);
                    var people = Enumerable.Range(0, count).Select(i => Person(run, i)).ToArray();
                    _observation.Reset();
                    _compilation.Reset();
                    var before = GC.GetTotalAllocatedBytes(true);
                    var start = Stopwatch.GetTimestamp();
                    if (backend == "ef") await WriteEfAsync(operation, people, cancellationToken);
                    else await WriteDriverAsync(operation, people, cancellationToken);
                    var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                    var allocated = GC.GetTotalAllocatedBytes(true) - before;
                    var measured = new BenchmarkSample($"write/graph-{graphSize}/batch-{count}/{operation}/{backend}",
                        iteration == -options.Warmups - 1 ? "first-observed" : iteration < 0 ? "warmup" : "warm", iteration,
                        elapsed, null, _compilation.Milliseconds, _compilation.Count, allocated, _observation.Snapshot(), count, 0);
                    await VerifyRunAsync(run, operation == "delete" ? 0 : count, operation == "update" ? 2 : 1,
                        operation == "update" ? "updated-" : "person-", cancellationToken);
                    _samples.Add(measured);
                }, CleanupAsync);
            }
            foreach (var backend in new[] { "driver", "ef" })
            {
                var run = OwnRun();
                await _cleanup.RunWithCleanupAsync(run, async () =>
                {
                    if (operation != "create") await SeedAsync(run, count, cancellationToken);
                    var people = Enumerable.Range(0, count).Select(i => Person(run, i)).ToArray();
                    await ObserveProtocolAsync($"write/graph-{graphSize}/batch-{count}/{operation}/{backend}", async () =>
                    {
                        if (backend == "ef") await WriteEfAsync(operation, people, cancellationToken);
                        else await WriteDriverAsync(operation, people, cancellationToken);
                    });
                    await VerifyRunAsync(run, operation == "delete" ? 0 : count, operation == "update" ? 2 : 1,
                        operation == "update" ? "updated-" : "person-", cancellationToken);
                }, CleanupAsync);
            }
        }
        if (count == options.WriteSizes[0]) await ProfileWritesAsync(graphSize);
        await SaveJsonAsync("raw-samples.json", _samples);
    }

    private async Task WriteEfAsync(string operation, BenchmarkPerson[] people, CancellationToken cancellationToken)
    {
        await using var context = Context();
        if (operation == "create") context.AddRange(people);
        else
        {
            context.AttachRange(people);
            if (operation == "delete") context.RemoveRange(people);
            else foreach (var person in people) person.Name = "updated-" + person.Ordinal.ToString(CultureInfo.InvariantCulture);
        }
        Require(await context.SaveChangesAsync(cancellationToken) == people.Length, "EF affected-entry count mismatch.");
        if (operation != "delete") Require(people.All(p => p.Version == (operation == "create" ? 1 : 2)), "EF generated versions mismatch.");
    }

    private async Task WriteDriverAsync(string operation, BenchmarkPerson[] people, CancellationToken cancellationToken)
    {
        await using var session = _driver.AsyncSession(o => o.WithDatabase("neo4j").WithDefaultAccessMode(AccessMode.Write));
        await using var transaction = await session.BeginTransactionAsync();
        try
        {
            await ValidateConstraintAsync(transaction);
            foreach (var person in people)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (operation != "create") await ExactlyOneAsync(transaction, Lock, new { id = person.Id.ToString("D") });
                await ExactlyOneAsync(transaction, operation == "create" ? Create : operation == "update" ? Update : Delete,
                    Parameters(person, operation));
            }
            cancellationToken.ThrowIfCancellationRequested();
            await transaction.CommitAsync();
            if (operation != "delete") foreach (var person in people) person.Version = operation == "create" ? 1 : 2;
        }
        catch { try { await transaction.RollbackAsync(); } catch { /* Preserve primary failure; no automatic retry. */ } throw; }
    }

    private static object Parameters(BenchmarkPerson person, string operation)
        => operation == "create" ? new { properties = new Dictionary<string, object> { ["Id"] = person.Id.ToString("D"),
            ["Run"] = person.Run, ["Ordinal"] = person.Ordinal, ["Name"] = person.Name, ["Version"] = 1L } }
            : new { id = person.Id.ToString("D"), version = person.Version,
                changes = new Dictionary<string, object> { ["Name"] = "updated-" + person.Ordinal.ToString(CultureInfo.InvariantCulture), ["Version"] = 2L } };

    private static async Task ExactlyOneAsync(IAsyncQueryRunner runner, string text, object parameters)
    {
        var cursor = await runner.RunAsync(text, parameters);
        var rows = await cursor.ToListAsync();
        Require(rows.Count == 1 && rows[0]["count"].As<long>() == 1, "Direct control cardinality/concurrency failure; transaction must roll back.");
    }
    private static async Task ValidateConstraintAsync(IAsyncQueryRunner runner)
    {
        var cursor = await runner.RunAsync(ConstraintsQuery);
        var constraints = await cursor.ToListAsync();
        Require(constraints.Any(row => row["entityType"].As<string>() == "NODE"
            && row["type"].As<string>() is "NODE_PROPERTY_UNIQUENESS" or "UNIQUENESS" or "NODE_KEY"
            && row["labelsOrTypes"].As<List<string>>().SequenceEqual([Label])
            && row["properties"].As<List<string>>().SequenceEqual(["Id"])), "Application Id uniqueness is required by both write arms.");
    }

    private async Task SeedAsync(string run, int count, CancellationToken cancellationToken)
    {
        await using var session = rawDriver.AsyncSession(o => o.WithDatabase("neo4j"));
        for (var offset = 0; offset < count; offset += 1000)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rows = Enumerable.Range(offset, Math.Min(1000, count - offset)).Select(i => new Dictionary<string, object>
                { ["Id"] = Key(run, i).ToString("D"), ["Run"] = run, ["Ordinal"] = i, ["Name"] = "person-" + i.ToString(CultureInfo.InvariantCulture), ["Version"] = 1L }).ToArray();
            var cursor = await session.RunAsync("UNWIND $rows AS row CREATE (n:GraphBridgeBenchmarkPerson) SET n = row RETURN count(n) AS count", new { rows });
            Require((await cursor.SingleAsync())["count"].As<long>() == rows.Length, "Seed count mismatch.");
        }
    }
    private async Task VerifyRunAsync(string run, int count, long version, string prefix, CancellationToken cancellationToken)
    {
        await using var session = rawDriver.AsyncSession(o => o.WithDatabase("neo4j"));
        var cursor = await session.RunAsync(FullRead, new { run });
        var ordinal = 0;
        while (await cursor.FetchAsync())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var row = cursor.Current;
            Require(row["Ordinal"].As<int>() == ordinal && row["Id"].As<string>() == Key(run, ordinal).ToString("D")
                && row["Run"].As<string>() == run && row["Name"].As<string>() == prefix + ordinal.ToString(CultureInfo.InvariantCulture)
                && row["Version"].As<long>() == version, "Fresh direct read disagrees with expected committed graph.");
            ordinal++;
        }
        Require(ordinal == count, "Fresh committed count differs from expected graph.");
        await cursor.ConsumeAsync();
    }
    private async Task CleanupAsync(string run)
    {
        Require(_ownedRuns.Contains(run), "Cannot clean up an unowned benchmark run.");
        await using var session = rawDriver.AsyncSession(o => o.WithDatabase("neo4j"));
        // Delete bounded batches. Plain DELETE intentionally fails if an unexpected relationship exists.
        for (var batch = 0; batch < 102; batch++)
        {
            var cursor = await session.RunAsync("MATCH (n:GraphBridgeBenchmarkPerson {Run: $run}) WITH n LIMIT 1000 DELETE n RETURN count(n) AS count", new { run });
            if ((await cursor.SingleAsync())["count"].As<long>() == 0)
            {
                _ownedRuns.Remove(run);
                return;
            }
        }
        throw new InvalidOperationException("Cleanup exceeded this run's bounded maximum; retained run identity requires inspection.");
    }
    private string OwnRun() { var run = Guid.NewGuid().ToString("N"); _ownedRuns.Add(run); return run; }
    private BenchmarkContext Context() => new(_contextOptions);
    private static Guid Key(string run, int ordinal)
    {
        // Deterministic keys within one random run, preserving 96 random bits and a bounded ordinal suffix.
        return Guid.ParseExact(run[..24] + ordinal.ToString("x8", CultureInfo.InvariantCulture), "N");
    }
    private static BenchmarkPerson Person(string run, int ordinal) => new()
        { Id = Key(run, ordinal), Run = run, Ordinal = ordinal, Name = "person-" + ordinal.ToString(CultureInfo.InvariantCulture), Version = 1 };
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private async Task ObserveProtocolAsync(string name, Func<Task> operation)
    {
        _stage = "protocol/" + name;
        protocol.Begin();
        var succeeded = false;
        BoltProtocolMeasurement measurement;
        try { await operation(); succeeded = true; }
        finally
        {
            measurement = protocol.Complete(succeeded);
            _protocolSamples.Add(new(name, measurement));
        }
        _stage = "protocol/" + name;
        await SaveJsonAsync("protocol-samples.json", _protocolSamples);
        Require(measurement.IsValid, "Protocol observation rejected its serial exchange contract.");
    }
    private Task SaveJsonAsync(string file, object value)
        => File.WriteAllTextAsync(Path.Combine(options.OutputDirectory, file), JsonSerializer.Serialize(value, Json));

    private async Task ProfileReadAsync(string name, int size, CapturedCommand command, CancellationToken cancellationToken)
    {
        await using var session = rawDriver.AsyncSession(o => o.WithDatabase("neo4j"));
        var parameters = command.Parameters ?? new { };
        await ExplainAsync(session, $"read/{size}/{name}", command.Text, parameters,
            "Separate EXPLAIN planning only, excluded from latency/allocations; same actual command and parameters as PROFILE and the observed read.");
        _stage = $"profile/read/{size}/{name}";
        var cursor = await session.RunAsync("PROFILE " + command.Text, parameters);
        var summary = await BenchmarkPlanCursor.ConsumeAsync(cursor, size, cancellationToken);
        Require(summary.HasProfile, "Server did not return a real PROFILE.");
        _profiles.Add(new { Case = $"read/{size}/{name}", Command = command.Text, Plan = ProfileNode(summary.QueryProfile),
            summary.ResultAvailableAfter, summary.ResultConsumedAfter, Scope = "Separate PROFILE execution, excluded from latency/allocations; same actual command and parameters." });
    }
    private async Task ProfileWritesAsync(int graphSize)
    {
        _stage = $"profile/write/{graphSize}";
        var run = OwnRun();
        await _cleanup.RunWithCleanupAsync(run, async () =>
        {
            await using var session = rawDriver.AsyncSession(o => o.WithDatabase("neo4j"));
            await using var transaction = await session.BeginTransactionAsync();
            await _cleanup.RunWithCleanupAsync(run, async () =>
            {
                await ValidateConstraintAsync(transaction);
                var person = Person(run, 0);
                foreach (var step in new[] { "create", "lock", "update", "lock", "delete" })
                {
                    if (step == "delete") person.Version = 2;
                    var text = step switch { "create" => Create, "update" => Update, "delete" => Delete, _ => Lock };
                    var parameters = step == "lock" ? new { id = person.Id.ToString("D") } : Parameters(person, step);
                    await ExplainAsync(transaction, $"write/graph-{graphSize}/representative-single-node/{step}", text, parameters,
                        "Direct control statement EXPLAIN before its PROFILE in the same rolled-back transaction; planning only, not an EF batch plan or measured write sample.");
                    _stage = $"profile/write/{graphSize}/{step}";
                    var cursor = await transaction.RunAsync("PROFILE " + text, parameters);
                    Require((await cursor.SingleAsync())["count"].As<long>() == 1, "PROFILE write affected count mismatch.");
                    var summary = await cursor.ConsumeAsync();
                    Require(summary.HasProfile, "Missing server PROFILE for write.");
                    _profiles.Add(new { Case = $"write/graph-{graphSize}/representative-single-node/{step}", Command = text,
                        Plan = ProfileNode(summary.QueryProfile), summary.ResultAvailableAfter, summary.ResultConsumedAfter,
                        Scope = "Direct control statement PROFILE in rolled-back transaction; not an EF batch PROFILE or measured write sample." });
                }
            }, _ => transaction.RollbackAsync());
            await VerifyRunAsync(run, 0, 1, "person-", CancellationToken.None);
        }, CleanupAsync);
    }
    private async Task ExplainAsync(IAsyncQueryRunner runner, string name, string text, object parameters, string scope)
    {
        _stage = "explain/" + name;
        var cursor = await runner.RunAsync("EXPLAIN " + text, parameters);
        Require(!await cursor.FetchAsync(), "EXPLAIN unexpectedly returned execution rows.");
        var summary = await cursor.ConsumeAsync();
        Require(summary.HasPlan && !summary.HasProfile && !summary.Counters.ContainsUpdates
            && !summary.Counters.ContainsSystemUpdates, "EXPLAIN must return a real plan without executed updates or profile counters.");
        var plan = summary.Plan ?? throw new InvalidOperationException("EXPLAIN omitted its server plan tree.");
        _explanations.Add(new { Case = name, Command = text, Plan = ExplainNode(plan),
            summary.ResultAvailableAfter, summary.ResultConsumedAfter, Scope = scope });
    }
    private static object ExplainNode(IPlan plan) => new
    {
        plan.OperatorType, plan.Arguments, plan.Identifiers,
        Children = plan.Children.Select(ExplainNode).ToArray()
    };
    private static object ProfileNode(IQueryProfile profile) => new
    {
        profile.OperatorType, profile.Arguments, profile.Identifiers, profile.DbHits, profile.Rows,
        profile.PageCacheHits, profile.PageCacheMisses, profile.PageCacheHitRatio, profile.Time,
        Children = profile.Children.Select(ProfileNode).ToArray()
    };
    private async Task VerifyRollbackAsync(CancellationToken cancellationToken)
    {
        foreach (var backend in new[] { "driver", "ef" })
        {
            _stage = $"rollback/{backend}";
            var run = OwnRun();
            await _cleanup.RunWithCleanupAsync(run, async () =>
            {
                await SeedAsync(run, 2, cancellationToken);
                var people = new[] { Person(run, 0), Person(run, 1) };
                people[1].Version = 99; // First mutation succeeds, second deliberately fails original-version check.
                var failed = false;
                try
                {
                    if (backend == "ef") await WriteEfAsync("update", people, cancellationToken);
                    else await WriteDriverAsync("update", people, cancellationToken);
                }
                catch (DbUpdateConcurrencyException) when (backend == "ef") { failed = true; }
                catch (InvalidOperationException error) when (backend == "driver" && error.Message.StartsWith("Direct control cardinality", StringComparison.Ordinal)) { failed = true; }
                Require(failed, "Deliberately stale batch was not rejected.");
                await VerifyRunAsync(run, 2, 1, "person-", cancellationToken);
            }, CleanupAsync);
        }
    }
}

public sealed record BenchmarkProtocolSample(string Case, BoltProtocolMeasurement Measurement);

internal sealed class CompilationObservation
{
    private long _started;
    public int Count { get; private set; }
    public double Milliseconds { get; private set; }
    public void Reset() { _started = 0; Count = 0; Milliseconds = 0; }
    public void OnEvent(EventData data)
    {
        if (data.EventId == CoreEventId.QueryCompilationStarting) { _started = Stopwatch.GetTimestamp(); Count++; }
        else if (data.EventId == CoreEventId.QueryExecutionPlanned && _started != 0)
        {
            Milliseconds += Stopwatch.GetElapsedTime(_started).TotalMilliseconds;
            _started = 0;
        }
    }
}
