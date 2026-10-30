using GraphBridge.GraphRag;
using GraphBridge.Samples.GraphRagArticles;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Neo4j.Driver;

const string usage = "Usage: dotnet run --project samples/09-GraphRagArticles -- setup | fetch folder [--excerpt paragraphs] | ingest [folder | nasa] | report | ask \"question\" [--export answer.json] | chat | graph \"question\" | agent \"question\" | serve [url] | cleanup";
var names = new[] { "GRAPHBRIDGE_NEO4J_URI", "GRAPHBRIDGE_NEO4J_DATABASE", "GRAPHBRIDGE_NEO4J_USERNAME", "GRAPHBRIDGE_NEO4J_PASSWORD" };
var settings = names.Select(Environment.GetEnvironmentVariable).ToArray();
string[] commands = ["setup", "fetch", "ingest", "report", "ask", "chat", "graph", "agent", "serve", "cleanup"];
var fetchArgs = args is ["fetch", _] || args is ["fetch", _, "--excerpt", var count] && int.TryParse(count, out var n) && n > 0;
var askArgs = args is ["ask", _] || args is ["ask", _, "--export", _];
if (args.Length == 0 || !commands.Contains(args[0]) || args[0] == "fetch" && !fetchArgs || args[0] == "ask" && !askArgs
    || args[0] is not ("fetch" or "ask") && args.Length > 2 || args[0] is "agent" or "graph" && args.Length != 2
    || args.Length == 2 && args[0] is not ("fetch" or "ingest" or "ask" or "agent" or "graph" or "serve"))
{
    Console.Error.WriteLine(usage);
    return 2;
}

if (args[0] == "fetch")
{
    // NASA's Artemis II news releases (see Nasa.cs). Needs no database: it writes Markdown and articles.json for ingest.
    var excerpt = args.Length == 4 ? int.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture) : (int?)null;
    foreach (var (release, bytes, sha) in await Nasa.FetchAsync(args[1], excerpt, CancellationToken.None))
        Console.WriteLine($"{release.Release} {release.File}: {bytes.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} bytes, SHA-256 {sha[..16]}");
    Console.WriteLine($"Wrote {Nasa.Releases.Count} releases to {args[1]}, and articles.json next to it. Then: ingest {args[1]}");
    return 0;
}
if (settings.Any(string.IsNullOrWhiteSpace))
{
    Console.Error.WriteLine("Set the four GRAPHBRIDGE_NEO4J_* variables to a disposable Neo4j 2026.09 or later database first.");
    return 2;
}

await using var driver = GraphDatabase.Driver(settings[0], AuthTokens.Basic(settings[2], settings[3]));

if (args[0] == "serve")
{
    // The toolkit's endpoints: POST /graphrag/ask streams the answer as server-sent events, then its checked citations or a
    // refusal; POST /graphrag/search and GET /graphrag/documents/{id} too. The sample listens on localhost only and answers
    // only requests addressed to it: before serving anyone else, add your own authentication and rate limits to the whole
    // app, the toolkit's group, /report and the explorer alike.
    var url = args.Length == 2 ? args[1] : "http://localhost:5179";
    if (!Uri.TryCreate(url, UriKind.Absolute, out var listen) || !listen.IsLoopback)
    {
        Console.Error.WriteLine($"serve listens on this machine only: give a localhost or 127.0.0.1 URL, not {url}.");
        return 2;
    }
    var builder = WebApplication.CreateBuilder();
    builder.Configuration["AllowedHosts"] = "localhost;127.0.0.1;[::1]";
    builder.Services.AddDbContext<ArticlesContext>(options => options.UseNeo4j(driver, settings[1]!)).AddArticles();
    var app = builder.Build();
    app.MapGraphRag<ArticlesContext>("/graphrag");
    app.MapGet("/report", async (ArticlesContext db, CancellationToken cancellationToken) => Results.Ok(await Articles.ReportAsync(db, cancellationToken)));
    // The explorer page (Explorer.cs): ask, watch the answer stream, open a citation's passage and its graph.
    app.MapExplorer();
    Console.WriteLine(Articles.Models is null ? "Offline: stand-in models." : $"Models: {Articles.Models.Describe()}.");
    Console.WriteLine($"Explorer: {url.TrimEnd('/')}/explorer");
    await app.RunAsync(url);
    return 0;
}

var collection = new ServiceCollection()
    .AddDbContext<ArticlesContext>(options => options.UseNeo4j(driver, settings[1]!))
    .AddArticles();
// Text2Cypher is off by default (experimental); only `graph` turns it on.
if (args[0] == "graph") collection.AddGraphRagText2Cypher<ArticlesContext>();
await using var services = collection.BuildServiceProvider();
await using var scope = services.CreateAsyncScope();
var provider = scope.ServiceProvider;
Console.WriteLine(Articles.Models is null ? "Offline: recorded extraction replies cover the two shipped articles only." : $"Models: {Articles.Models.Describe()}.");

switch (args[0])
{
    case "setup":
    {
        var db = provider.GetRequiredService<ArticlesContext>();
        foreach (var step in Articles.SchemaPlan.Preview().Steps) Console.WriteLine(step.Command.Cypher);
        await db.Database.InitializeGraphMigrationsAsync();
        // The plan has no predecessor, so it is the database's first migration: one with other migrations refuses it.
        var check = await db.Database.ValidateGraphMigrationAsync(Articles.SchemaPlan);
        if (!check.AlreadyApplied && check.Issues.Count > 0)
        {
            Console.Error.WriteLine($"Not applied: {string.Join(" ", check.Issues)}");
            Console.Error.WriteLine("The sample's schema is its database's first migration, and it names its embedding model. A database another sample, "
                + "or this one in another mode (offline, replay or a real model), set up refuses it: give each an empty database.");
            return 2;
        }
        var applied = await db.Database.ApplyGraphMigrationAsync(Articles.SchemaPlan);
        Console.WriteLine(applied.AlreadyApplied ? "The schema was already applied." : $"Applied {applied.CompletedSteps} steps; the indexes are ONLINE.");
        return 0;
    }
    case "ingest":
    {
        // "nasa" is the shipped excerpts of NASA's Artemis II releases; any other argument is a folder of Markdown articles.
        var folder = args.Length == 1 ? Articles.Folder : args[1] == "nasa" ? Nasa.Excerpts : args[1];
        var receipt = await provider.GetRequiredService<IGraphRagIngestion>().IngestAsync(Articles.Sources(folder));
        foreach (var group in receipt.Documents.GroupBy(d => d.Status)) Console.WriteLine($"{group.Key}: {group.Count()}");
        foreach (var failed in receipt.Documents.Where(d => d.Status == GraphRagDocumentStatus.Failed)) Console.WriteLine($"  {failed.SourceUri}: {failed.ErrorCode}");
        if (receipt.Extraction is { } extraction)
        {
            Console.WriteLine($"Extraction: {extraction.ChatCalls} calls, {extraction.Entities} things, {extraction.Mentions} mentions, {extraction.Facts} facts, {extraction.Quotes} quotes.");
            foreach (var group in extraction.Pruned.GroupBy(p => p.Reason)) Console.WriteLine($"  pruned ({group.Key}): {group.Count()}");
        }
        return receipt.Documents.Any(d => d.Status == GraphRagDocumentStatus.Failed) ? 1 : 0;
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
        var refused = answer.Disposition == GraphRagAnswerDisposition.InsufficientEvidence;
        if (refused) Console.WriteLine($"Not answered: {answer.Reason}.");
        else
        {
            Console.WriteLine(answer.Text);
            foreach (var citation in answer.Citations) Console.WriteLine($"  [bytes {citation.StartByte}-{citation.EndByte}] \"{citation.Quote}\"");
            // The answer is the model's; only the quotes are the sources' own words (and NASA asks that AI output is never attributed to it).
            Console.WriteLine($"(Written by {Articles.Models?.ChatModel ?? "the offline stand-in"}; the quoted passages are the sources' own words.)");
        }
        if (args.Length == 4)
        {
            // One JSON document, for a refusal too: the answer or refusal, its citations, the evidence and the receipt; FromJson
            // reads it back.
            await File.WriteAllTextAsync(args[3], GraphRagAnswerExport.ToJson(answer));
            Console.WriteLine($"Exported to {args[3]}.");
        }
        return refused ? 1 : 0;
    }
    case "chat":
    {
        // Follow-up questions: each is rewritten with the conversation so far into one that stands alone, then answered.
        // The application keeps the history; the toolkit stores none. An empty line ends the chat.
        var answers = provider.GetRequiredService<IGraphRagAnswers>();
        var history = new List<GraphRagTurn>();
        Console.WriteLine("Ask a question, then follow up. An empty line ends the chat.");
        while (Console.ReadLine() is { Length: > 0 } question)
        {
            var answer = await answers.AnswerAsync(question, new GraphRagRetrievalOptions { History = history.ToArray() });
            if (answer.Evidence.Rewrite is { Outcome: "rewritten" } rewrite) Console.WriteLine($"  (asked as: {rewrite.Standalone})");
            Console.WriteLine(answer.Disposition == GraphRagAnswerDisposition.Answered ? answer.Text : $"Not answered: {answer.Reason}.");
            foreach (var citation in answer.Citations) Console.WriteLine($"  [bytes {citation.StartByte}-{citation.EndByte}] \"{citation.Quote}\"");
            history.Add(new GraphRagTurn(question, answer.Text));
        }
        return 0;
    }
    case "graph":
    {
        // Text2Cypher (experimental): a model writes one read-only query; the answer is its rows, with no citations.
        var result = await provider.GetRequiredService<IGraphRagGraphQuestions>().AskAsync(args[1]);
        if (result.Cypher is { } cypher) Console.WriteLine(cypher);
        if (result.Disposition == GraphRagGraphAnswerDisposition.Refused)
        {
            Console.WriteLine($"Refused: {result.Reason}.");
            return 1;
        }
        Console.WriteLine(string.Join(" | ", result.Columns));
        foreach (var row in result.Rows) Console.WriteLine(string.Join(" | ", result.Columns.Select(c => Show(row[c]))));
        if (result.Rows.Count == 0) Console.WriteLine("(no rows)");
        foreach (var limit in result.LimitsReached) Console.WriteLine($"  (limit reached: {limit.Name} {limit.Value})");
        Console.WriteLine("Read the query: the rows are what it returned, whether or not it asks what the question asks.");
        return 0;
    }
    case "agent":
    {
        // An Agent Framework agent with the toolkit's context provider: it answers through answer_from_documents.
        var (text, calls) = await Articles.AskAgentAsync(provider, args[1]);
        Console.WriteLine(text);
        foreach (var call in calls)
            Console.WriteLine($"  {call.Tool}: " + (call.Evidence.Count == 0 ? "no evidence" : string.Join(", ", call.Evidence.Select(e => $"bytes {e.StartByte}-{e.EndByte}"))));
        Console.WriteLine($"(Written by {Articles.Models?.ChatModel ?? "the offline stand-in"}; the quoted passages are the sources' own words.)");
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

// A row value as text: lists and maps written out, so nested results stay readable.
static string Show(object? value) => value switch
{
    null => "null",
    string text => text,
    System.Collections.IDictionary map => "{" + string.Join(", ", map.Keys.Cast<object>().Select(k => $"{k}: {Show(map[k])}")) + "}",
    System.Collections.IEnumerable list => "[" + string.Join(", ", list.Cast<object?>().Select(Show)) + "]",
    IFormattable formattable => formattable.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
    _ => value.ToString() ?? ""
};
