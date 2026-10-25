using GraphBridge.GraphRag;
using GraphBridge.Samples.GraphRag;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Neo4j.Driver;

const string usage = "Usage: dotnet run --project samples/07-GraphRagQuickStart -- setup | ingest [folder] | ask \"question\" | cleanup";
var names = new[] { "GRAPHBRIDGE_NEO4J_URI", "GRAPHBRIDGE_NEO4J_DATABASE", "GRAPHBRIDGE_NEO4J_USERNAME", "GRAPHBRIDGE_NEO4J_PASSWORD" };
var settings = names.Select(Environment.GetEnvironmentVariable).ToArray();
if (args.Length is 0 or > 2 || args[0] is not ("setup" or "ingest" or "ask" or "cleanup") || args[0] == "ask" && args.Length != 2
    || args.Length == 2 && args[0] is not ("ingest" or "ask"))
{
    Console.Error.WriteLine(usage);
    return 2;
}
if (settings.Any(string.IsNullOrWhiteSpace))
{
    Console.Error.WriteLine("Set the four GRAPHBRIDGE_NEO4J_* variables to a disposable Neo4j 2026.09 or later database first.");
    return 2;
}

await using var driver = GraphDatabase.Driver(settings[0], AuthTokens.Basic(settings[2], settings[3]));
await using var services = new ServiceCollection()
    .AddDbContext<QuickStartContext>(options => options.UseNeo4j(driver, settings[1]!))
    .AddQuickStart()
    .BuildServiceProvider();
await using var scope = services.CreateAsyncScope();
var provider = scope.ServiceProvider;

switch (args[0])
{
    case "setup":
    {
        // The schema is a reviewed migration of the application's own: preview it, then apply it.
        var db = provider.GetRequiredService<QuickStartContext>();
        foreach (var step in QuickStart.SchemaPlan.Preview().Steps) Console.WriteLine(step.Command.Cypher);
        await db.Database.InitializeGraphMigrationsAsync();
        var applied = await db.Database.ApplyGraphMigrationAsync(QuickStart.SchemaPlan);
        Console.WriteLine(applied.AlreadyApplied ? "The schema was already applied." : $"Applied {applied.CompletedSteps} steps; the indexes are ONLINE.");
        return 0;
    }
    case "ingest":
    {
        var folder = args.Length == 2 ? args[1] : Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "docs");
        var receipt = await provider.GetRequiredService<IGraphRagIngestion>().IngestAsync(GraphRagSources.Folder(folder));
        foreach (var group in receipt.Documents.GroupBy(d => d.Status)) Console.WriteLine($"{group.Key}: {group.Count()}");
        foreach (var failed in receipt.Documents.Where(d => d.Status == GraphRagDocumentStatus.Failed)) Console.WriteLine($"  {failed.SourceUri}: {failed.ErrorCode}");
        Console.WriteLine($"{receipt.ChunksWritten} chunks written in {receipt.Elapsed.TotalSeconds:0.0} s.");
        return 0;
    }
    case "ask":
    {
        var answer = await provider.GetRequiredService<IGraphRagAnswers>().AnswerAsync(args[1]);
        if (answer.Disposition == GraphRagAnswerDisposition.InsufficientEvidence)
        {
            Console.WriteLine($"Not answered: {answer.Reason}.");
            return 1;
        }
        Console.WriteLine(answer.Text);
        foreach (var citation in answer.Citations)
            Console.WriteLine($"  [{citation.DocumentId[..8]} bytes {citation.StartByte}-{citation.EndByte}] \"{citation.Quote}\"");
        return 0;
    }
    default:
    {
        var removed = await provider.GetRequiredService<IGraphRagMaintenance>().ResetAsync(new GraphRagResetScope());
        Console.WriteLine($"Removed {removed.Documents} documents, {removed.Chunks} chunks and {removed.Edges} links. The schema stays; drop it with a migration.");
        return 0;
    }
}
