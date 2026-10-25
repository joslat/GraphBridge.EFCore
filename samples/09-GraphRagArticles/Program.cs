using GraphBridge.GraphRag;
using GraphBridge.Samples.GraphRagArticles;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Neo4j.Driver;

const string usage = "Usage: dotnet run --project samples/09-GraphRagArticles -- setup | ingest [folder] | report | ask \"question\" | serve [url] | cleanup";
var names = new[] { "GRAPHBRIDGE_NEO4J_URI", "GRAPHBRIDGE_NEO4J_DATABASE", "GRAPHBRIDGE_NEO4J_USERNAME", "GRAPHBRIDGE_NEO4J_PASSWORD" };
var settings = names.Select(Environment.GetEnvironmentVariable).ToArray();
string[] commands = ["setup", "ingest", "report", "ask", "serve", "cleanup"];
if (args.Length is 0 or > 2 || !commands.Contains(args[0]) || args[0] == "ask" && args.Length != 2
    || args.Length == 2 && args[0] is not ("ingest" or "ask" or "serve"))
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

if (args[0] == "serve")
{
    // The endpoint stays inside the sample: one GET that answers with checked citations, one that reports the graph.
    var builder = WebApplication.CreateBuilder();
    builder.Services.AddDbContext<ArticlesContext>(options => options.UseNeo4j(driver, settings[1]!)).AddArticles();
    var app = builder.Build();
    app.MapGet("/ask", async (string q, IGraphRagAnswers answers, CancellationToken cancellationToken) =>
    {
        var answer = await answers.AnswerAsync(q, cancellationToken: cancellationToken);
        return Results.Ok(new
        {
            answered = answer.Disposition == GraphRagAnswerDisposition.Answered, answer.Text, answer.Reason,
            citations = answer.Citations.Select(c => new { c.DocumentId, c.StartByte, c.EndByte, c.Quote })
        });
    });
    app.MapGet("/report", async (ArticlesContext db, CancellationToken cancellationToken) => Results.Ok(await Articles.ReportAsync(db, cancellationToken)));
    await app.RunAsync(args.Length == 2 ? args[1] : "http://localhost:5179");
    return 0;
}

await using var services = new ServiceCollection()
    .AddDbContext<ArticlesContext>(options => options.UseNeo4j(driver, settings[1]!))
    .AddArticles()
    .BuildServiceProvider();
await using var scope = services.CreateAsyncScope();
var provider = scope.ServiceProvider;
Console.WriteLine(Articles.Models is null ? "Offline: recorded extraction replies cover the two shipped articles only." : $"Models: {Articles.Models.ChatModel} and {Articles.Models.EmbeddingModel}.");

switch (args[0])
{
    case "setup":
    {
        var db = provider.GetRequiredService<ArticlesContext>();
        foreach (var step in Articles.SchemaPlan.Preview().Steps) Console.WriteLine(step.Command.Cypher);
        await db.Database.InitializeGraphMigrationsAsync();
        var applied = await db.Database.ApplyGraphMigrationAsync(Articles.SchemaPlan);
        Console.WriteLine(applied.AlreadyApplied ? "The schema was already applied." : $"Applied {applied.CompletedSteps} steps; the indexes are ONLINE.");
        return 0;
    }
    case "ingest":
    {
        var receipt = await provider.GetRequiredService<IGraphRagIngestion>().IngestAsync(Articles.Sources(args.Length == 2 ? args[1] : Articles.Folder));
        foreach (var group in receipt.Documents.GroupBy(d => d.Status)) Console.WriteLine($"{group.Key}: {group.Count()}");
        foreach (var failed in receipt.Documents.Where(d => d.Status == GraphRagDocumentStatus.Failed)) Console.WriteLine($"  {failed.SourceUri}: {failed.ErrorCode}");
        if (receipt.Extraction is { } extraction)
        {
            Console.WriteLine($"Extraction: {extraction.ChatCalls} calls, {extraction.Entities} things, {extraction.Mentions} mentions, {extraction.Facts} facts, {extraction.Quotes} quotes.");
            foreach (var group in extraction.Pruned.GroupBy(p => p.Reason)) Console.WriteLine($"  pruned ({group.Key}): {group.Count()}");
        }
        return 0;
    }
    case "report":
    {
        var report = await Articles.ReportAsync(provider.GetRequiredService<ArticlesContext>());
        foreach (var article in report.Articles)
            Console.WriteLine($"{article.Published} {article.Title} ({article.Publisher}) by {string.Join(", ", article.Authors)}; about {string.Join(", ", article.Topics)}");
        foreach (var job in report.Jobs) Console.WriteLine($"{job.Person} works for {job.Organization} as {job.Role ?? "?"} (stated in {job.Articles} articles)");
        foreach (var line in report.Located) Console.WriteLine($"{line.Organization} is in {line.Place}");
        foreach (var quote in report.Quotes) Console.WriteLine($"{quote.Speaker}: \"{quote.Words}\"");
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
        foreach (var citation in answer.Citations) Console.WriteLine($"  [bytes {citation.StartByte}-{citation.EndByte}] \"{citation.Quote}\"");
        return 0;
    }
    default:
    {
        var maintenance = provider.GetRequiredService<IGraphRagMaintenance>();
        var removed = await maintenance.ResetAsync(new GraphRagResetScope());
        var orphans = await maintenance.RemoveOrphansAsync();
        Console.WriteLine($"Removed {removed.Documents} articles, {removed.Chunks} passages, {orphans.Entities} things and {removed.Edges + orphans.Edges} relationships. The schema stays; drop it with a migration.");
        return 0;
    }
}
