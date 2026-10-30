using GraphBridge.GraphRag;
using GraphRagStarter;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Neo4j.Driver;

const string usage = "Usage: dotnet run --project App -- setup | ingest [folder] | ask \"question\" | cleanup";
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
    Console.Error.WriteLine("Set the four GRAPHBRIDGE_NEO4J_* variables to a Neo4j 2026.09 or later database you can write to first.");
    return 2;
}

await using var driver = GraphDatabase.Driver(settings[0], AuthTokens.Basic(settings[2], settings[3]));
await using var services = new ServiceCollection()
    .AddDbContext<StarterContext>(options => options.UseNeo4j(driver, settings[1]!))
    .AddStarter()
    .BuildServiceProvider();
await using var scope = services.CreateAsyncScope();
var provider = scope.ServiceProvider;

switch (args[0])
{
    case "setup":
    {
        // The schema is your own reviewed migration: preview it, then apply it. The toolkit never creates schema itself.
        var db = provider.GetRequiredService<StarterContext>();
        foreach (var step in Starter.SchemaPlan.Preview().Steps) Console.WriteLine(step.Command.Cypher);
        await db.Database.InitializeGraphMigrationsAsync();
        // The plan has no predecessor, so it is the database's first migration: one with other migrations refuses it.
        var check = await db.Database.ValidateGraphMigrationAsync(Starter.SchemaPlan);
        if (!check.AlreadyApplied && check.Issues.Count > 0)
        {
            Console.Error.WriteLine($"Not applied: {string.Join(" ", check.Issues)}");
            Console.Error.WriteLine("The schema plan has no predecessor, so it must be the database's first migration. To add it to a database with migrations, give it the last one's ID as its predecessor.");
            return 2;
        }
        var applied = await db.Database.ApplyGraphMigrationAsync(Starter.SchemaPlan);
        Console.WriteLine(applied.AlreadyApplied ? "The schema was already applied." : $"Applied {applied.CompletedSteps} steps; the indexes are ONLINE.");
        return 0;
    }
    case "ingest":
    {
        // The folder's Markdown files; run it again and unchanged files are skipped.
        var folder = args.Length == 2 ? args[1] : Path.Combine(AppContext.BaseDirectory, "docs");
        var receipt = await provider.GetRequiredService<IGraphRagIngestion>().IngestAsync(GraphRagSources.Folder(folder));
        foreach (var group in receipt.Documents.GroupBy(d => d.Status)) Console.WriteLine($"{group.Key}: {group.Count()}");
        foreach (var failed in receipt.Documents.Where(d => d.Status == GraphRagDocumentStatus.Failed)) Console.WriteLine($"  {failed.SourceUri}: {failed.ErrorCode}");
        Console.WriteLine($"{receipt.ChunksWritten} chunks written.");
        return 0;
    }
    case "ask":
    {
        // Every citation is checked against the stored bytes; otherwise the answer is a refusal, with a reason.
        var answer = await provider.GetRequiredService<IGraphRagAnswers>().AnswerAsync(args[1]);
        if (answer.Disposition == GraphRagAnswerDisposition.InsufficientEvidence)
        {
            Console.WriteLine($"Not answered: {answer.Reason}.");
            return 1;
        }
        Console.WriteLine(answer.Text);
        foreach (var citation in answer.Citations)
            Console.WriteLine($"  [bytes {citation.StartByte}-{citation.EndByte}] \"{citation.Quote}\"");
        return 0;
    }
    default:
    {
        var removed = await provider.GetRequiredService<IGraphRagMaintenance>().ResetAsync(new GraphRagResetScope());
        Console.WriteLine($"Removed {removed.Documents} documents and {removed.Chunks} chunks. The schema stays; drop it with a migration.");
        return 0;
    }
}
