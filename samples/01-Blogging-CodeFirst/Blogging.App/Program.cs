using GraphBridge.Samples.Blogging;
using Neo4j.Driver;

if (args is ["--check-schema"])
{
    // This entry point deliberately has no schema creation path. Connection settings still target
    // the repository's disposable fixture; adapt that deployment guard in your own application.
    try
    {
        if (Required("GRAPHBRIDGE_NEO4J_URI") != "bolt://127.0.0.1:17687"
            || Required("GRAPHBRIDGE_NEO4J_DATABASE") != "neo4j")
            throw new InvalidOperationException("Sample connection guard failed.");
        await using var readDriver = GraphDatabase.Driver(Required("GRAPHBRIDGE_NEO4J_URI"),
            AuthTokens.Basic(Required("GRAPHBRIDGE_NEO4J_USERNAME"), Required("GRAPHBRIDGE_NEO4J_PASSWORD")),
            options => options.WithConnectionTimeout(TimeSpan.FromSeconds(10)));
        await using var readContext = new BloggingContext(readDriver, "neo4j");
        using var readCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; readCancellation.Cancel(); };
        return await SchemaPreflight.RunAsync(readContext, Console.Out, readCancellation.Token);
    }
    catch (Exception error) when (error is InvalidOperationException or ArgumentException or Neo4jException)
    {
        Console.WriteLine("{\"formatVersion\":1,\"outcome\":\"error\",\"exitCode\":2,\"code\":\"ConnectionConfigurationFailed\"}");
        return 2;
    }
}
if (args.Contains("--check-schema"))
{
    Console.Error.WriteLine("--check-schema must be used alone; it cannot prepare schema or run writes.");
    return 2;
}
if (!args.Contains("--run") && !args.Contains("--prepare-schema"))
{
    Console.WriteLine("Use --prepare-schema for explicit sample key constraints and --run for EF CRUD, native HAS_POST storage, Include and rollback. See the sample README.");
    return 1;
}

static string Required(string name) => Environment.GetEnvironmentVariable(name)
    ?? throw new InvalidOperationException($"Missing {name}; use eng/scripts/Run-BloggingSample.ps1 with the disposable fixture.");

var uri = Required("GRAPHBRIDGE_NEO4J_URI");
if (uri != "bolt://127.0.0.1:17687" || Required("GRAPHBRIDGE_NEO4J_DATABASE") != "neo4j")
    throw new InvalidOperationException("This demo runs only on the repository's disposable fixture.");
await using var driver = GraphDatabase.Driver(uri, AuthTokens.Basic(
    Required("GRAPHBRIDGE_NEO4J_USERNAME"), Required("GRAPHBRIDGE_NEO4J_PASSWORD")));
await using (var setup = driver.AsyncSession(b => b.WithDatabase("neo4j")))
{
    var marker = await setup.RunAsync("MATCH (f:GraphBridgeFixture {id: $id}) RETURN count(f) AS count",
        new { id = Required("GRAPHBRIDGE_FIXTURE_ID") });
    if ((await marker.SingleAsync())["count"].As<long>() != 1)
        throw new InvalidOperationException("Disposable fixture marker missing; refusing sample writes.");
    if (args.Contains("--prepare-schema"))
    {
        // Deliberate setup only; provider queries and SaveChanges never create schema.
        foreach (var statement in new[]
        {
            "CREATE CONSTRAINT graphbridge_blog_id IF NOT EXISTS FOR (n:GraphBridgeBlog) REQUIRE n.Id IS UNIQUE",
            "CREATE CONSTRAINT graphbridge_post_id IF NOT EXISTS FOR (n:GraphBridgePost) REQUIRE n.Id IS UNIQUE"
        })
            await (await setup.RunAsync(statement)).ConsumeAsync();
        Console.WriteLine("Explicit sample uniqueness constraints prepared.");
    }
}
if (args.Contains("--run"))
{
    using var cancellation = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
    var result = await BloggingWorkflow.RunAsync(driver, "neo4j", cancellation.Token);
    Console.WriteLine(result.Cypher);
    Console.WriteLine($"Storage fingerprints: source={result.SourceFingerprint}; target={result.TargetFingerprint}.");
    Console.WriteLine($"EF entries: added={result.Added}, updated={result.Updated}, deleted={result.Deleted}; blog version={result.Version}; rollback={result.RollbackVerified}; reference fixup={result.ReferenceFixupVerified}; Include={result.IncludeVerified}.");
    Console.WriteLine("Only this invocation's Blog, two Posts and their owned HAS_POST links were removed.");
}
return 0;
