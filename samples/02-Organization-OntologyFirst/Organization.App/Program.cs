using System.Text.Json;
using GraphBridge.EntityFrameworkCore.Migrations;
using GraphBridge.Samples.OrganizationDemo;
using Microsoft.EntityFrameworkCore;
using Neo4j.Driver;

if (args.Length != 1 || args[0] is not ("--preview" or "--prepare-schema" or "--run" or "--migrate-demo"))
{
    Console.WriteLine("Choose --preview, --prepare-schema, --run, or --migrate-demo. See the Organization sample README.");
    return 1;
}
if (args[0] == "--preview")
{
    foreach (var command in GraphMigrationJournal.PreviewInitialization()) Console.WriteLine(command + ";");
    foreach (var plan in new[] { OrganizationPlans.Schema(), OrganizationPlans.VersionTwo() })
    {
        Console.WriteLine($"Plan {plan.Id}; checksum {plan.Checksum}; predecessor {plan.PredecessorId ?? "none"}.");
        Console.WriteLine(plan.RollbackPolicy.Reason);
        foreach (var step in plan.Preview().Steps)
        {
            Console.WriteLine($"{step.Id}: {step.Precondition}");
            Console.WriteLine(step.Command.Cypher);
            Console.WriteLine(JsonSerializer.Serialize(step.Command.Parameters));
        }
    }
    return 0;
}

static string Required(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
    ? value : throw new InvalidOperationException($"Missing {name}; use the repository's owned disposable fixture environment.");
var uri = Required("GRAPHBRIDGE_NEO4J_URI");
var database = Required("GRAPHBRIDGE_NEO4J_DATABASE");
var fixtureId = Required("GRAPHBRIDGE_FIXTURE_ID");
if (uri != "bolt://127.0.0.1:17687" || database != "neo4j" || !Guid.TryParseExact(fixtureId, "N", out _))
    throw new InvalidOperationException("This example is restricted to the repository's disposable fixture.");
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };
var cancellationToken = cancellation.Token;
await using var driver = GraphDatabase.Driver(uri, AuthTokens.Basic(Required("GRAPHBRIDGE_NEO4J_USERNAME"), Required("GRAPHBRIDGE_NEO4J_PASSWORD")),
    configure => configure.WithConnectionTimeout(TimeSpan.FromSeconds(10)));
await using (var ownership = OrganizationWorkflow.Context(driver, database))
{
    var markers = await ownership.Database.QueryCypherAsync("MATCH (f:GraphBridgeFixture {id:$id}) RETURN count(f) AS count",
        new Dictionary<string, object?> { ["id"] = fixtureId }, record => record["count"].As<long>(), maxRows: 1, cancellationToken: cancellationToken);
    if (markers.Single() != 1) throw new InvalidOperationException("Fixture marker missing; refusing sample writes.");
}
if (args[0] == "--prepare-schema")
{
    var result = await OrganizationWorkflow.PrepareSchemaAsync(driver, database, cancellationToken);
    Console.WriteLine($"Reviewed sample schema: {result.CompletedSteps} steps; already applied={result.AlreadyApplied}.");
}
else if (args[0] == "--run")
{
    try
    {
        var result = await OrganizationWorkflow.RunAsync(driver, database, cancellationToken);
        Console.WriteLine(result.Cypher);
        Console.WriteLine($"Generated EF entries: added={result.Added}, updated={result.Updated}, deleted={result.Deleted}; native parallel relationships={result.ParallelRelationships}.");
        Console.WriteLine($"Calendar roundtrip={result.CalendarRoundTrip}; reference fixup={result.NavigationFixup}; synthetic join nodes={result.FakeJoinNodes}.");
        Console.WriteLine($"Source compiler v1 fingerprint={result.SourceSchemaFingerprint}; finalized runtime v1 fingerprint={result.TargetSchemaFingerprint}.");
    }
    catch (Exception error)
    {
        foreach (var key in new[] { "OrganizationSample.PersonId", "OrganizationSample.OrganizationId",
                     "OrganizationSample.FirstEmploymentId", "OrganizationSample.SecondEmploymentId" })
            if (error.Data[key] is Guid id) Console.Error.WriteLine($"Inspect this run's {key}: {id:D}");
        if (error.Data.Contains("OrganizationSample.CleanupFailure"))
            Console.Error.WriteLine("Sample cleanup did not complete; inspect the exact run IDs above.");
        throw;
    }
}
else
{
    var sourceMapping = OrganizationWorkflow.GetSchemaFingerprints(driver, database);
    var targetMapping = OrganizationWorkflow.GetSchemaFingerprints(driver, database, versionTwo: true);
    Console.WriteLine($"Source v1 mapping fingerprint={sourceMapping.Target}; target v2 mapping fingerprint={targetMapping.Target}. Both match their compiler artifacts; these are mapping expectations, not database observations.");
    await OrganizationWorkflow.RunMigrationDemoAsync(driver, database, cancellationToken);
    foreach (var line in await OrganizationWorkflow.ReadVersionTwoAsync(driver, database, cancellationToken)) Console.WriteLine(line);
    Console.WriteLine("Migrated sample data and journal are retained. Repeating this mode verifies the already-applied plan without reseeding.");
}
return 0;
