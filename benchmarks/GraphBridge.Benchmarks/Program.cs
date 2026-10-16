using GraphBridge.Benchmarks;
using Neo4j.Driver;

if (args.Length == 1 && args[0] == "--query-preparation")
{
    QueryPreparationProfile.Run();
    return 0;
}

if (args.Length == 1 && args[0] == "--help")
{
    Console.WriteLine("Optional owned-fixture benchmark. Use the guarded repository runner; this executable never starts Docker.");
    Console.WriteLine("--sizes=1,100,10000,100000 --edge-sizes=1,100,10000 --writes=1,100,1000 --samples=20 --warmups=3 --fetch-size=1000 --seed=1729 --output=PATH");
    Console.WriteLine("--bulk selects the separate node update/delete comparison; use --sizes=1 --edge-sizes=1 and --writes for its batch sizes.");
    return 0;
}

try
{
    var bulk = BulkProfileMode.Extract(ref args);
    var allocationBackend = AllocationProfileMode.Extract(ref args);
    if (bulk && allocationBackend is not null) throw new ArgumentException("Bulk and allocation profiles are separate experiments.");
    var options = BenchmarkOptions.Parse(args);
    static string Required(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
        ? value : throw new InvalidOperationException("Missing guarded benchmark setting: " + name);
    if (Required("GRAPHBRIDGE_BENCHMARKS") != "1" || Required("GRAPHBRIDGE_BENCHMARK_GUARDED") != "1"
        || Required("GRAPHBRIDGE_INTEGRATION") != "1")
        throw new InvalidOperationException("Explicit benchmark opt-in and the repository's supervised resource/ownership runner are required.");
    var uri = new Uri(Required("GRAPHBRIDGE_NEO4J_URI"), UriKind.Absolute);
    if (uri.AbsoluteUri != "bolt://127.0.0.1:17687/" || Required("GRAPHBRIDGE_NEO4J_DATABASE") != "neo4j")
        throw new InvalidOperationException("Only the owned Community fixture at bolt://127.0.0.1:17687/neo4j is accepted.");
    var fixtureId = Required("GRAPHBRIDGE_FIXTURE_ID");
    if (!Guid.TryParseExact(fixtureId, "N", out _)
        || Required("GRAPHBRIDGE_COMPOSE_PROJECT") != "graphbridge-fixture-" + fixtureId)
        throw new InvalidOperationException("Fixture marker and Compose identity must match the generated owned fixture.");
    using var cancellation = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
    var protocol = new BoltProtocolObservation();
    await using var driver = GraphDatabase.Driver(uri,
        AuthTokens.Basic(Required("GRAPHBRIDGE_NEO4J_USERNAME"), Required("GRAPHBRIDGE_NEO4J_PASSWORD")),
        configuration => configuration.WithConnectionTimeout(TimeSpan.FromSeconds(10))
            .WithMaxConnectionPoolSize(1).WithFetchSize(options.FetchSize).WithLogger(protocol));
    var runner = new BenchmarkRunner(options, driver, fixtureId, protocol);
    if (bulk) await runner.RunBulkProfileAsync(cancellation.Token);
    else if (allocationBackend is null) await runner.RunAsync(cancellation.Token);
    else await runner.RunAllocationProfileAsync(allocationBackend, cancellation.Token);
    Console.WriteLine("Benchmark evidence written to " + options.OutputDirectory);
    Console.WriteLine(allocationBackend is null
        ? "Serial Bolt batch exchanges are recorded separately from timings; no performance target is claimed."
        : "Focused allocation experiment only; no complete benchmark matrix or throughput target is claimed.");
    return 0;
}
catch (Exception error)
{
    // Driver exception messages/query payloads can contain values; detailed success evidence is separate.
    Console.Error.WriteLine("Benchmark failed (" + error.GetType().Name + "); no acceptance claimed. Check the guarded runner and retained artifacts.");
    return 1;
}
