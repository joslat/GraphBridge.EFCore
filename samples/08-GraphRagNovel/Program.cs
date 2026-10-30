using GraphBridge.GraphRag;
using GraphBridge.Samples.GraphRagNovel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Neo4j.Driver;

const string usage = "Usage: dotnet run --project samples/08-GraphRagNovel -- setup | fetch | ingest [book | file.txt] | characters | ask \"question\" | merge [apply | undo runId] | cleanup";
var names = new[] { "GRAPHBRIDGE_NEO4J_URI", "GRAPHBRIDGE_NEO4J_DATABASE", "GRAPHBRIDGE_NEO4J_USERNAME", "GRAPHBRIDGE_NEO4J_PASSWORD" };
var settings = names.Select(Environment.GetEnvironmentVariable).ToArray();
string[] commands = ["setup", "fetch", "ingest", "characters", "ask", "merge", "cleanup"];
var mergeArgs = args is ["merge"] or ["merge", "apply"] or ["merge", "undo", _];
if (args.Length == 0 || !commands.Contains(args[0]) || args[0] == "merge" && !mergeArgs || args[0] != "merge" && args.Length > 2
    || args[0] == "ask" && args.Length != 2 || args.Length == 2 && args[0] is not ("ingest" or "ask" or "merge"))
{
    Console.Error.WriteLine(usage);
    return 2;
}
if (args[0] == "fetch")
{
    Console.WriteLine($"Downloading {Novel.BookUrl} ...");
    Console.WriteLine($"Saved the book, without Project Gutenberg's header and footer, to {await Novel.FetchBookAsync(Novel.BookPath)}.");
    Console.WriteLine("Ingest it with: ingest book");
    return 0;
}
if (settings.Any(string.IsNullOrWhiteSpace))
{
    Console.Error.WriteLine("Set the four GRAPHBRIDGE_NEO4J_* variables to a disposable Neo4j 2026.09 or later database first.");
    return 2;
}

await using var driver = GraphDatabase.Driver(settings[0], AuthTokens.Basic(settings[2], settings[3]));
await using var services = new ServiceCollection()
    .AddDbContext<NovelContext>(options => options.UseNeo4j(driver, settings[1]!))
    .AddNovel()
    .BuildServiceProvider();
await using var scope = services.CreateAsyncScope();
var provider = scope.ServiceProvider;
Console.WriteLine(Novel.Models is null ? "Offline: stand-in models; extraction replays the recorded replies for the passages they cover." : $"Models: {Novel.Models.Describe()}.");

switch (args[0])
{
    case "setup":
    {
        var db = provider.GetRequiredService<NovelContext>();
        foreach (var step in Novel.SchemaPlan.Preview().Steps) Console.WriteLine(step.Command.Cypher);
        await db.Database.InitializeGraphMigrationsAsync();
        // The plan has no predecessor, so it is the database's first migration: one with other migrations refuses it.
        var check = await db.Database.ValidateGraphMigrationAsync(Novel.SchemaPlan);
        if (!check.AlreadyApplied && check.Issues.Count > 0)
        {
            Console.Error.WriteLine($"Not applied: {string.Join(" ", check.Issues)}");
            Console.Error.WriteLine("The sample's schema is its database's first migration, and it names its embedding model. A database another sample, "
                + "or this one in another mode (offline, replay or a real model), set up refuses it: give each an empty database.");
            return 2;
        }
        var applied = await db.Database.ApplyGraphMigrationAsync(Novel.SchemaPlan);
        Console.WriteLine(applied.AlreadyApplied ? "The schema was already applied." : $"Applied {applied.CompletedSteps} steps; the indexes are ONLINE.");
        return 0;
    }
    case "ingest":
    {
        var book = args.Length == 1 ? Novel.Excerpt : args[1] == "book" ? Novel.BookPath : args[1];
        if (!File.Exists(book))
        {
            Console.Error.WriteLine(args[1] == "book" ? "The book isn't downloaded yet: run fetch first." : $"There is no file {book}.");
            return 2;
        }
        var receipt = await provider.GetRequiredService<IGraphRagIngestion>().IngestAsync([Novel.Source(book)]);
        var outcome = receipt.Documents.Single();
        Console.WriteLine($"{outcome.Status}{(outcome.ErrorCode is null ? "" : ": " + outcome.ErrorCode)}; {receipt.ChunksWritten} passages written.");
        if (receipt.Extraction is { } extraction)
        {
            Console.WriteLine($"Extraction: {extraction.ChatCalls} calls, {extraction.Entities} things, {extraction.Mentions} mentions, {extraction.Facts} facts, {extraction.Quotes} quotes.");
            foreach (var group in extraction.Pruned.GroupBy(p => p.Reason)) Console.WriteLine($"  pruned ({group.Key}): {group.Count()}");
            if (extraction.Quarantined.Count > 0) Console.WriteLine($"  quarantined replies: {extraction.Quarantined.Count}");
        }
        return outcome.Status == GraphRagDocumentStatus.Failed ? 1 : 0;
    }
    case "characters":
    {
        var summary = await Novel.SummarizeAsync(provider.GetRequiredService<NovelContext>());
        Console.WriteLine("Characters:");
        foreach (var character in summary.Characters)
            Console.WriteLine($"  {character.Name}{(character.Aliases.Count > 0 ? " (" + string.Join(", ", character.Aliases) + ")" : "")}: {character.Mentions} mentions");
        if (summary.Kin.Count > 0) Console.WriteLine("Kin:");
        foreach (var (from, kind, to) in summary.Kin) Console.WriteLine($"  {from} is a {kind} of {to}");
        if (summary.Says.Count > 0) Console.WriteLine("Who says what:");
        foreach (var (speaker, words) in summary.Says) Console.WriteLine($"  {speaker}: \"{words}\"");
        Console.WriteLine("Places: " + string.Join(", ", summary.Places));
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
            Console.WriteLine($"  [bytes {citation.StartByte}-{citation.EndByte}] \"{citation.Quote}\"");
        return 0;
    }
    case "merge":
    {
        // Merge suggestions: one character can arrive under two names ("Caterpillar", "the Caterpillar"). The toolkit suggests,
        // you decide. `merge` only reads; `merge apply` merges the same-name pairs, never the alias ones, which inherit the
        // model's mistakes; each merge is a run that `merge undo` reverses.
        var db = provider.GetRequiredService<NovelContext>();
        if (args.Length == 3)
        {
            var undone = await GraphRagMergePlan.UndoAsync(db, args[2]);
            Console.WriteLine($"Undone: {undone.Mentions} mentions, {undone.Facts} facts and {undone.Quotes} quotes are back.");
            return 0;
        }
        var suggestions = await GraphRagResolution.SuggestAsync(db);
        foreach (var suggestion in suggestions)
            Console.WriteLine($"  {suggestion.Kind}: \"{suggestion.MergeName}\" into \"{suggestion.KeepName}\" ({suggestion.Reason})");
        var plan = GraphRagResolution.Plan(suggestions.Where(s => s.Reason == "same-name"));
        var preview = await plan.PreviewAsync(db);
        Console.WriteLine($"{suggestions.Count} suggestions; the {preview.Merges} same-name merges would move {preview.Mentions} mentions, {preview.Facts} facts and {preview.Quotes} quotes.");
        foreach (var blocker in preview.Blockers) Console.WriteLine($"  blocked: {blocker}");
        if (args.Length == 1 || !preview.CanApply) return preview.CanApply ? 0 : 1;
        GraphRagMergeReceipt receipt;
        try
        {
            receipt = await plan.ApplyAsync(db);
        }
        catch (GraphRagMergeException error)
        {
            // The merges before the failed one are written; each can still be undone.
            Console.WriteLine(error.Message);
            foreach (var run in error.AppliedRunIds.Reverse()) Console.WriteLine($"  undo with: merge undo {run}");
            return 1;
        }
        Console.WriteLine($"Merged: {receipt.Mentions} mentions, {receipt.FactsMoved} facts moved, {receipt.FactsJoined} joined, {receipt.FactsDropped} dropped, {receipt.Quotes} quotes.");
        // Last first: a later merge into the same thing must be undone before an earlier one.
        foreach (var run in receipt.RunIds.Reverse()) Console.WriteLine($"  undo with: merge undo {run}");
        return 0;
    }
    default:
    {
        var maintenance = provider.GetRequiredService<IGraphRagMaintenance>();
        var removed = await maintenance.ResetAsync(new GraphRagResetScope());
        var orphans = await maintenance.RemoveOrphansAsync();
        Console.WriteLine($"Removed {removed.Documents} books, {removed.Chunks} passages, {orphans.Entities} things and {removed.Edges + orphans.Edges} relationships. The schema stays; drop it with a migration.");
        return 0;
    }
}
