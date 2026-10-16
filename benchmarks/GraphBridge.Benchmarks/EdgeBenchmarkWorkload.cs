using System.Diagnostics;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Neo4j.Driver;

namespace GraphBridge.Benchmarks;

/// <summary>
/// Optional identified-edge read matrix. The coordinator must first verify fixture ownership/resources
/// and serialize this workload with all other database work. No edge-write performance claim is made.
/// </summary>
public sealed class EdgeBenchmarkWorkload
{
    private static readonly string[] Cases = ["driver/full", "driver/scalar", "ef/tracked/full", "ef/untracked/full",
        "ef/identity/full", "ef/tracked/scalar", "ef/untracked/scalar", "ef/identity/scalar"];
    private readonly IDriver _rawDriver;
    private readonly IDriver _driver;
    private readonly BoltProtocolObservation _protocol;
    private readonly BenchmarkOptions _options;
    private readonly string _fixtureId;
    private readonly int[] _edgeCounts;
    private readonly Func<string, object, Task> _saveArtifact;
    private readonly DbContextOptions<EdgeBenchmarkContext> _contextOptions;
    private readonly DriverObservation _observation = new();
    private readonly CompilationObservation _compilation = new();
    private readonly BenchmarkCleanup _cleanup = new();
    private readonly HashSet<string> _ownedRuns = new(StringComparer.Ordinal);
    private readonly List<BenchmarkSample> _samples = [];
    private readonly List<BenchmarkProtocolSample> _protocolSamples = [];
    private readonly List<object> _plans = [];
    private readonly Random _random;
    private string _stage = "edge/prepare";
    private double _modelMilliseconds;
    private bool _started;

    public EdgeBenchmarkWorkload(IDriver rawDriver, BoltProtocolObservation protocol, BenchmarkOptions options,
        string fixtureId, IReadOnlyList<int> edgeCounts, Func<string, object, Task> saveArtifact)
    {
        EdgeBenchmarkData.ValidateCounts(edgeCounts);
        if (!Guid.TryParseExact(fixtureId, "N", out _)) throw new ArgumentException("Generated fixture identity required.", nameof(fixtureId));
        _rawDriver = rawDriver;
        _protocol = protocol;
        _options = options;
        _fixtureId = fixtureId;
        _edgeCounts = edgeCounts.ToArray();
        _saveArtifact = saveArtifact;
        _random = new(options.Seed);
        _driver = ObservedDriverProxy.Wrap(rawDriver, _observation);
        var builder = new DbContextOptionsBuilder<EdgeBenchmarkContext>().UseNeo4j(_driver, "neo4j");
        builder.LogTo((id, _) => id == CoreEventId.QueryCompilationStarting || id == CoreEventId.QueryExecutionPlanned,
            _compilation.OnEvent);
        _contextOptions = builder.Options;
    }

    public async Task<EdgeBenchmarkResult> RunAsync(CancellationToken cancellationToken)
    {
        if (_started) throw new InvalidOperationException("Each edge benchmark workload can run only once.");
        _started = true;
        Exception? primary = null;
        try
        {
            await PrepareAsync(cancellationToken);
            _stage = "edge/model-initialization";
            var modelStart = Stopwatch.GetTimestamp();
            await using (var context = Context()) _ = context.Model;
            _modelMilliseconds = Stopwatch.GetElapsedTime(modelStart).TotalMilliseconds;
            await _saveArtifact("edge-environment.json", new
            {
                EdgeCounts = _edgeCounts, ModelInitializationMilliseconds = _modelMilliseconds,
                Topology = "At most 10 sources and 100 targets; directed endpoint pairs repeat every 100 edges; each physical edge has its own application Guid.",
                IntegrityScope = "Returned identities: mapped endpoint labels, required canonical endpoint/edge keys, type-global bounded-two identity count before filtering, and three observed uniqueness constraints. No whole-graph validation claim.",
                TimingScope = "Includes context/session disposal and per-row acceptance; excludes the EF tracking-state acceptance interval. Compilation spans are actual EF diagnostic events, not cold-server latency.",
                AllocationScope = "Process-wide GC.GetTotalAllocatedBytes(precise:true), including observer/driver/background allocations, excluding the tracking-state acceptance interval.",
                Scope = "Read-only performance matrix. Native relationship creation below is deterministic fixture preparation, not an edge write benchmark."
            });
            foreach (var count in _edgeCounts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var run = Guid.NewGuid().ToString("N");
                _ownedRuns.Add(run);
                await _cleanup.RunWithCleanupAsync(run, async () =>
                {
                    _stage = $"edge/seed/{count}";
                    await SeedAsync(run, count, cancellationToken);
                    await VerifySeedAsync(run, count, cancellationToken);
                    await ReadMatrixAsync(run, count, cancellationToken);
                }, CleanupAsync);
            }
            Require(_protocolSamples.Count == _edgeCounts.Length * Cases.Length
                && _protocolSamples.All(x => x.Measurement.IsValid)
                && _plans.Count == _edgeCounts.Length * Cases.Length * 2
                && _samples.Count == _edgeCounts.Length * Cases.Length * (1 + _options.Warmups + _options.Samples),
                "Edge benchmark evidence does not cover the selected read matrix.");
            await _saveArtifact("edge-summary.json", _samples.Where(x => x.Phase == "warm")
                .GroupBy(x => x.Case).Select(group => new BenchmarkSummary(group.Key,
                    Distribution.From(group.Select(x => x.TotalMilliseconds)),
                    Distribution.From(group.Select(x => (double)x.ProcessAllocatedBytes)))).ToArray());
            _stage = "edge/finalization";
        }
        catch (Exception error) { primary = error; }
        await _cleanup.FinishAsync(primary, () => _ownedRuns.ToArray(), CleanupAsync, PersistAsync,
            () => _saveArtifact("edge-completion.json", new
            {
                Completed = true, EdgeCounts = _edgeCounts, ReadCases = _edgeCounts.Length * Cases.Length,
                ProtocolCases = _protocolSamples.Count, ExplainAndProfilePlans = _plans.Count,
                Scope = "Identified-edge full/scalar reads only; all selected rows verified and owned data cleaned."
            }), report => _saveArtifact("edge-failure.json", report), _stage);
        return new(_edgeCounts, _samples.ToArray(), _protocolSamples.ToArray(), _plans.ToArray(), _modelMilliseconds);
    }

    private Task PersistAsync() => Task.WhenAll(
        _saveArtifact("edge-raw-samples.json", _samples), _saveArtifact("edge-protocol-samples.json", _protocolSamples),
        _saveArtifact("edge-plans.json", _plans));

    private async Task PrepareAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var session = _rawDriver.AsyncSession(o => o.WithDatabase("neo4j"));
        var marker = await session.RunAsync("MATCH (f:GraphBridgeFixture {id: $fixtureId}) RETURN count(f) AS count", new { fixtureId = _fixtureId });
        Require((await marker.SingleAsync())["count"].As<long>() == 1, "Owned Community fixture marker missing.");
        var components = await session.RunAsync("CALL dbms.components() YIELD name, versions, edition WHERE name = 'Neo4j Kernel' RETURN versions[0] AS version, edition");
        var component = await components.SingleAsync();
        Require(component["version"].As<string>() == "2026.09.0"
            && component["edition"].As<string>().Equals("community", StringComparison.OrdinalIgnoreCase), "Pinned Community fixture required.");
        foreach (var command in new[]
        {
            $"CREATE CONSTRAINT gb_benchmark_edge_source_key IF NOT EXISTS FOR (n:{EdgeBenchmarkData.SourceLabel}) REQUIRE n.Id IS UNIQUE",
            $"CREATE CONSTRAINT gb_benchmark_edge_target_key IF NOT EXISTS FOR (n:{EdgeBenchmarkData.TargetLabel}) REQUIRE n.Id IS UNIQUE",
            $"CREATE CONSTRAINT gb_benchmark_identified_edge_key IF NOT EXISTS FOR ()-[r:{EdgeBenchmarkData.RelationshipType}]-() REQUIRE r.Id IS UNIQUE"
        })
            await (await session.RunAsync(command)).ConsumeAsync();
        await (await session.RunAsync("CALL db.awaitIndexes(30)")).ConsumeAsync();
        await VerifyConstraintsAsync(session);
    }

    private async Task ReadMatrixAsync(string run, int count, CancellationToken cancellationToken)
    {
        foreach (var name in Cases)
        {
            _samples.Add(await MeasureAsync(name, run, count, "first-observed", 0, cancellationToken));
            var command = _observation.Shapes.Values.Single(x => x.Text != EdgeBenchmarkData.ConstraintsQuery);
            await ExplainAndProfileAsync(name, count, command, cancellationToken);
        }
        for (var iteration = -_options.Warmups; iteration < _options.Samples; iteration++)
            foreach (var name in Cases.OrderBy(_ => _random.Next()))
                _samples.Add(await MeasureAsync(name, run, count, iteration < 0 ? "warmup" : "warm", iteration, cancellationToken));
        foreach (var name in Cases)
        {
            _stage = $"edge/protocol/{count}/{name}";
            _protocol.Begin();
            var succeeded = false;
            BoltProtocolMeasurement measurement;
            try { _ = await MeasureAsync(name, run, count, "protocol-observation", 0, cancellationToken); succeeded = true; }
            finally { measurement = _protocol.Complete(succeeded); _protocolSamples.Add(new($"edge/read/{count}/{name}", measurement)); }
            _stage = $"edge/protocol/{count}/{name}";
            await _saveArtifact("edge-protocol-samples.json", _protocolSamples);
            Require(measurement.IsValid, "Edge protocol observation rejected its serial exchange contract.");
        }
        await PersistAsync();
    }

    private async Task<BenchmarkSample> MeasureAsync(string name, string run, int count, string phase, int iteration, CancellationToken cancellationToken)
    {
        _stage = $"edge/read/{count}/{name}/{phase}/{iteration}";
        cancellationToken.ThrowIfCancellationRequested();
        _observation.Reset();
        _compilation.Reset();
        var allocated = GC.GetTotalAllocatedBytes(true);
        var start = Stopwatch.GetTimestamp();
        double? first = null;
        double acceptanceMilliseconds = 0;
        long acceptanceBytes = 0, seen = 0, checksum = 0;
        var scalar = name.EndsWith("/scalar", StringComparison.Ordinal);
        void Accept(int ordinal, EdgeBenchmarkEdge? edge)
        {
            first ??= Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            Require(ordinal == seen, "Edge result order differs from the seeded ordinal.");
            if (edge is not null) ValidateExpected(edge, run, ordinal, count);
            seen++;
            checksum += ordinal;
        }
        if (name.StartsWith("driver/", StringComparison.Ordinal))
        {
            await using var session = _driver.AsyncSession(o => o.WithDatabase("neo4j"));
            await VerifyConstraintsAsync(session);
            var cursor = await session.RunAsync(EdgeBenchmarkData.ReadQuery(scalar), new { run });
            while (await cursor.FetchAsync())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var row = EdgeBenchmarkData.Decode(cursor.Current.Values, scalar);
                Accept(row.Ordinal, row.Entity);
            }
            await cursor.ConsumeAsync();
        }
        else
        {
            await using var context = Context();
            IQueryable<EdgeBenchmarkEdge> query = context.Edges.Where(e => e.Run == run).OrderBy(e => e.Ordinal);
            if (name.Contains("/untracked/", StringComparison.Ordinal)) query = query.AsNoTracking();
            if (name.Contains("/identity/", StringComparison.Ordinal)) query = query.AsNoTrackingWithIdentityResolution();
            if (scalar)
                await foreach (var ordinal in query.Select(e => e.Ordinal).AsAsyncEnumerable().WithCancellation(cancellationToken)) Accept(ordinal, null);
            else
                await foreach (var edge in query.AsAsyncEnumerable().WithCancellation(cancellationToken)) Accept(edge.Ordinal, edge);
            var beforeAcceptance = GC.GetTotalAllocatedBytes(true);
            var acceptanceStart = Stopwatch.GetTimestamp();
            Require(context.ChangeTracker.Entries().Count() == (!scalar && name.Contains("/tracked/", StringComparison.Ordinal) ? count : 0),
                "Edge tracking mode state count differs from its EF contract.");
            acceptanceMilliseconds = Stopwatch.GetElapsedTime(acceptanceStart).TotalMilliseconds;
            acceptanceBytes = GC.GetTotalAllocatedBytes(true) - beforeAcceptance;
        }
        var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds - acceptanceMilliseconds;
        var bytes = GC.GetTotalAllocatedBytes(true) - allocated - acceptanceBytes;
        Require(seen == count && checksum == (long)count * (count - 1) / 2, "Edge count/checksum differs from the seed.");
        return new($"edge/read/{count}/{name}", phase, iteration, elapsed, first, _compilation.Milliseconds,
            _compilation.Count, bytes, _observation.Snapshot(), seen, checksum);
    }

    private static async Task VerifyConstraintsAsync(IAsyncQueryRunner runner)
    {
        var cursor = await runner.RunAsync(EdgeBenchmarkData.ConstraintsQuery);
        var rows = await cursor.ToListAsync();
        EdgeBenchmarkSchema.Validate(rows.Select(row => new EdgeBenchmarkConstraint(row["entityType"].As<string>(),
            row["type"].As<string>(), row["labelsOrTypes"].As<List<string>>(), row["properties"].As<List<string>>())));
    }

    private async Task SeedAsync(string run, int count, CancellationToken cancellationToken)
    {
        await using var session = _rawDriver.AsyncSession(o => o.WithDatabase("neo4j"));
        foreach (var source in new[] { true, false })
        {
            var rows = Enumerable.Range(0, source ? EdgeBenchmarkData.SourceCount(count) : EdgeBenchmarkData.TargetCount(count))
                .Select(i => new Dictionary<string, object>
                {
                    ["Id"] = (source ? EdgeBenchmarkData.SourceKey(run, i) : EdgeBenchmarkData.TargetKey(run, i)).ToString("D"), ["Run"] = run
                }).ToArray();
            var cursor = await session.RunAsync($"UNWIND $rows AS row CREATE (n:{(source ? EdgeBenchmarkData.SourceLabel : EdgeBenchmarkData.TargetLabel)}) SET n = row RETURN count(n) AS count", new { rows });
            Require((await cursor.SingleAsync())["count"].As<long>() == rows.Length, "Edge endpoint seed count mismatch.");
        }
        for (var offset = 0; offset < count; offset += 1000)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rows = Enumerable.Range(offset, Math.Min(1000, count - offset)).Select(i =>
            {
                var expected = EdgeBenchmarkData.Expected(run, i, count);
                return new Dictionary<string, object>
                {
                    ["source"] = expected.SourceId.ToString("D"), ["target"] = expected.TargetId.ToString("D"),
                    ["properties"] = new Dictionary<string, object>
                    {
                        ["Id"] = expected.Id.ToString("D"), ["Run"] = run, ["Ordinal"] = expected.Ordinal,
                        ["Name"] = expected.Name, ["Version"] = expected.Version
                    }
                };
            }).ToArray();
            var cursor = await session.RunAsync($"UNWIND $rows AS row MATCH (s:{EdgeBenchmarkData.SourceLabel} {{Id:row.source, Run:$run}}), (t:{EdgeBenchmarkData.TargetLabel} {{Id:row.target, Run:$run}}) CREATE (s)-[r:{EdgeBenchmarkData.RelationshipType}]->(t) SET r = row.properties RETURN count(r) AS count", new { rows, run });
            Require((await cursor.SingleAsync())["count"].As<long>() == rows.Length, "Physical edge seed count mismatch.");
        }
    }

    private async Task VerifySeedAsync(string run, int count, CancellationToken cancellationToken)
    {
        await using var session = _rawDriver.AsyncSession(o => o.WithDatabase("neo4j"));
        await VerifyConstraintsAsync(session);
        var cursor = await session.RunAsync(EdgeBenchmarkData.ReadQuery(false), new { run });
        var seen = 0;
        while (await cursor.FetchAsync())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var row = EdgeBenchmarkData.Decode(cursor.Current.Values, false);
            ValidateExpected(row.Entity!, run, seen, count);
            seen++;
        }
        await cursor.ConsumeAsync();
        Require(seen == count, "Fresh edge verification count mismatch.");
        // This independent physical inventory also rejects hidden/mislabelled owned edges or accidental FK copies.
        var inventory = await session.RunAsync($"MATCH ()-[r:{EdgeBenchmarkData.RelationshipType} {{Run:$run}}]->() RETURN count(r) AS count, count(r.SourceId) AS sourceCopies, count(r.TargetId) AS targetCopies", new { run });
        var observed = await inventory.SingleAsync();
        Require(observed["count"].As<long>() == count && observed["sourceCopies"].As<long>() == 0 && observed["targetCopies"].As<long>() == 0,
            "Owned physical edge inventory or native endpoint storage differs from the fixture contract.");
    }

    private static void ValidateExpected(EdgeBenchmarkEdge edge, string run, int ordinal, int count)
        => Require(edge.Ordinal == ordinal && edge.Id == EdgeBenchmarkData.EdgeKey(run, ordinal)
            && edge.SourceId == EdgeBenchmarkData.SourceKey(run, ordinal % EdgeBenchmarkData.SourceCount(count))
            && edge.TargetId == EdgeBenchmarkData.TargetKey(run, ordinal % EdgeBenchmarkData.TargetCount(count))
            && edge.Run == run && edge.Name == "edge-" + ordinal.ToString(CultureInfo.InvariantCulture) && edge.Version == 1,
            "Edge identity, directed endpoints or payload differs from the deterministic seed.");

    private async Task CleanupAsync(string run)
    {
        Require(_ownedRuns.Contains(run), "Refusing cleanup of an unowned edge run.");
        await using var session = _rawDriver.AsyncSession(o => o.WithDatabase("neo4j"));
        // Match by this run's edge payload, independent of endpoint labels, so interrupted preparation is recoverable.
        await DeleteBatchesAsync(session, $"MATCH ()-[r:{EdgeBenchmarkData.RelationshipType} {{Run:$run}}]->() WITH r LIMIT 1000 DELETE r RETURN count(r) AS count", run);
        foreach (var label in new[] { EdgeBenchmarkData.SourceLabel, EdgeBenchmarkData.TargetLabel })
            await DeleteBatchesAsync(session, $"MATCH (n:{label} {{Run:$run}}) WITH n LIMIT 1000 DELETE n RETURN count(n) AS count", run);
        _ownedRuns.Remove(run);
    }

    private static async Task DeleteBatchesAsync(IAsyncQueryRunner runner, string command, string run)
    {
        for (var batch = 0; batch < 12; batch++)
            if ((await (await runner.RunAsync(command, new { run })).SingleAsync())["count"].As<long>() == 0) return;
        throw new InvalidOperationException("Owned edge cleanup exceeded its bounded maximum.");
    }

    private async Task ExplainAndProfileAsync(string name, int count, CapturedCommand command, CancellationToken cancellationToken)
    {
        _stage = $"edge/plans/{count}/{name}";
        await using var session = _rawDriver.AsyncSession(o => o.WithDatabase("neo4j"));
        foreach (var mode in new[] { "EXPLAIN", "PROFILE" })
        {
            var cursor = await session.RunAsync(mode + " " + command.Text, command.Parameters ?? new { });
            var summary = await BenchmarkPlanCursor.ConsumeAsync(cursor, mode == "PROFILE" ? count : 0, cancellationToken);
            Require(mode == "PROFILE" ? summary.HasProfile : summary.HasPlan, "Expected real edge query plan was absent.");
            _plans.Add(new
            {
                Case = $"edge/read/{count}/{name}", Mode = mode, Command = command.Text,
                Plan = mode == "PROFILE" ? ProfileNode(summary.QueryProfile) : ExplainNode(summary.Plan),
                summary.ResultAvailableAfter, summary.ResultConsumedAfter,
                Scope = "Separate actual-command execution with identical parameters; excluded from timing/allocation and protocol samples."
            });
        }
    }

    private static object ExplainNode(IPlan plan) => new
    { plan.OperatorType, plan.Arguments, plan.Identifiers, Children = plan.Children.Select(ExplainNode).ToArray() };
    private static object ProfileNode(IQueryProfile plan) => new
    {
        plan.OperatorType, plan.Arguments, plan.Identifiers, plan.DbHits, plan.Rows, plan.PageCacheHits,
        plan.PageCacheMisses, plan.PageCacheHitRatio, plan.Time, Children = plan.Children.Select(ProfileNode).ToArray()
    };
    private EdgeBenchmarkContext Context() => new(_contextOptions);
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}

public sealed record EdgeBenchmarkResult(IReadOnlyList<int> EdgeCounts, IReadOnlyList<BenchmarkSample> Samples,
    IReadOnlyList<BenchmarkProtocolSample> ProtocolSamples, IReadOnlyList<object> Plans, double ModelInitializationMilliseconds);

public sealed record EdgeBenchmarkConstraint(string EntityType, string Type, IReadOnlyList<string> LabelsOrTypes, IReadOnlyList<string> Properties);

public static class EdgeBenchmarkSchema
{
    public static void Validate(IEnumerable<EdgeBenchmarkConstraint> constraints)
    {
        var observed = constraints.ToArray();
        foreach (var label in new[] { EdgeBenchmarkData.SourceLabel, EdgeBenchmarkData.TargetLabel })
            if (!observed.Any(c => c.EntityType == "NODE" && c.Type is "NODE_PROPERTY_UNIQUENESS" or "UNIQUENESS" or "NODE_KEY"
                && c.LabelsOrTypes.SequenceEqual([label]) && c.Properties.SequenceEqual(["Id"])))
                throw new InvalidOperationException("Edge benchmark requires both endpoint application-key uniqueness constraints.");
        if (!observed.Any(c => c.EntityType == "RELATIONSHIP"
            && c.Type is "RELATIONSHIP_PROPERTY_UNIQUENESS" or "RELATIONSHIP_UNIQUENESS" or "RELATIONSHIP_KEY"
            && c.LabelsOrTypes.SequenceEqual([EdgeBenchmarkData.RelationshipType]) && c.Properties.SequenceEqual(["Id"])))
            throw new InvalidOperationException("Edge benchmark requires type/application-ID relationship uniqueness.");
    }
}
