using GraphBridge.GraphRag;
using GraphBridge.GraphRag.Ontologies;
using GraphBridge.Samples.GraphRagShared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GraphBridge.Workshop.Lessons;

public sealed class Lesson34KeepingItTidy : ILesson
{
    public LessonInfo Info { get; } = new("34", 'G', "Keeping the graph tidy", "34-KeepingItTidy", LessonMode.Connected, Core: false,
        Requirement.Guarded, "Merge a character that arrived under two names, then undo it; re-ingest only what changed; remove a document and the things only it mentioned; and check the graph's health.");

    // Written for the workshop; the characters are from the public-domain novel.
    private const string First = "Alice met the White Rabbit near the river. “Oh dear, I shall be late!” said the Rabbit to Alice.";
    private const string Later = "Later, Alice followed the White Rabbit into Wonderland.";

    // A model's replies, written by hand. The second names "the White Rabbit", which extraction keeps apart from "White Rabbit".
    private const string FirstReply = """
        {"entities": [
          {"kind": "Character", "name": "Alice", "mentions": ["Alice"]},
          {"kind": "Character", "name": "White Rabbit", "mentions": ["the White Rabbit", "the Rabbit"]},
          {"kind": "Place", "name": "the river", "mentions": ["the river"]}
        ],
        "relationships": [{"type": "RELATED_TO", "from": "Alice", "fromKind": "Character", "to": "White Rabbit", "toKind": "Character", "kind": "follows"}],
        "quotes": [{"type": "SAYS", "speaker": "White Rabbit", "words": "Oh dear, I shall be late!", "addressee": "Alice"}]}
        """;

    private const string LaterReply = """
        {"entities": [
          {"kind": "Character", "name": "Alice", "mentions": ["Alice"]},
          {"kind": "Character", "name": "the White Rabbit", "mentions": ["the White Rabbit"]},
          {"kind": "Place", "name": "Wonderland", "mentions": ["Wonderland"]}
        ],
        "relationships": [], "quotes": []}
        """;

    public async Task RunAsync(LessonContext lesson, CancellationToken cancellationToken)
    {
        if (!await GraphRagWorkshop.ReadyAsync(lesson, "maintenance", cancellationToken)) return;
        var extractor = new GraphRagWorkshop.WrittenReply(("near the river", FirstReply), ("into Wonderland", LaterReply));
        await using var services = GraphRagWorkshop.Services(lesson, new ExtractiveChatClient(), extractor);
        try
        {
            await GraphRagWorkshop.ProvisionAsync(lesson.Connection!, cancellationToken);
            var receipt = await services.GetRequiredService<IGraphRagIngestion>()
                .IngestAsync([GraphRagSources.Text("first", First), GraphRagSources.Text("later", Later)], cancellationToken);
            var first = receipt.Documents.Single(d => d.SourceUri == "text:first").DocumentId;
            await using var scope = services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<GraphRagWorkshop.RagContext>();
            Task<List<string>> CharactersAsync() => db.Set<Character>().OrderBy(c => c.Name).Select(c => c.Name).ToListAsync(cancellationToken);
            lesson.ExpectSequence("one character under two names", ["Alice", "White Rabbit", "the White Rabbit"], await CharactersAsync());

            lesson.Step("Merge suggestions: the toolkit suggests, you decide");
            lesson.Code("Suggest");
            // workshop:start Suggest
            var suggestions = await GraphRagResolution.SuggestAsync(db, cancellationToken);
            var plan = GraphRagResolution.Plan(suggestions.Where(s => s.Reason == "same-name"));
            var preview = await plan.PreviewAsync(db, cancellationToken);
            // workshop:end Suggest
            lesson.Table(suggestions.Select(s => new { s.Kind, Merge = s.MergeName, Into = s.KeepName, s.Reason }));
            lesson.Value("Would move", $"{preview.Mentions} mention, {preview.Facts} facts, {preview.Quotes} quotes");
            lesson.Check("nothing blocks the plan", preview.CanApply, string.Join("; ", preview.Blockers));

            lesson.Step("Apply, then undo: each merge is a run that records what it changed");
            lesson.Code("Merge");
            // workshop:start Merge
            var merged = await plan.ApplyAsync(db, cancellationToken);
            var afterMerge = await CharactersAsync();
            var undone = await GraphRagMergePlan.UndoAsync(db, merged.RunIds.Single(), cancellationToken);
            // workshop:end Merge
            lesson.ExpectSequence("merged: the name became an alias", ["Alice", "White Rabbit"], afterMerge);
            lesson.Value("Undone", $"{undone.Mentions} mention, {undone.Facts} facts and {undone.Quotes} quotes put back");
            lesson.ExpectSequence("undone: both names are back", ["Alice", "White Rabbit", "the White Rabbit"], await CharactersAsync());

            lesson.Step("Re-ingest only what changed");
            lesson.Code("Changed");
            // workshop:start Changed
            var maintenance = services.GetRequiredService<IGraphRagMaintenance>();
            var changed = await maintenance.ReingestChangedAsync(
                [GraphRagSources.Text("first", First), GraphRagSources.Text("later", Later + " Alice ran.")], cancellationToken);
            // workshop:end Changed
            lesson.Table(changed.Documents.Select(d => new { d.SourceUri, d.Status }));
            lesson.Expect("the unchanged document is skipped", GraphRagDocumentStatus.Unchanged, changed.Documents.Single(d => d.SourceUri == "text:first").Status);
            lesson.Expect("the changed one is replaced", GraphRagDocumentStatus.Ingested, changed.Documents.Single(d => d.SourceUri == "text:later").Status);

            lesson.Step("Remove a document, then the things only it mentioned");
            lesson.Code("Remove");
            // workshop:start Remove
            var removed = await maintenance.RemoveSourceAsync(first, cancellationToken);
            var orphans = await maintenance.RemoveOrphansAsync(cancellationToken);
            var health = await maintenance.CheckAsync(cancellationToken);
            // workshop:end Remove
            lesson.Value("Removed", $"{removed.Documents} document, {removed.Chunks} chunk, {removed.Edges} links; then {orphans.Entities} things no document mentions");
            lesson.ExpectSequence("what the remaining document mentions stays", ["Alice", "the White Rabbit"], await CharactersAsync());
            lesson.Expect("one document left", 1, health.Documents);
            lesson.Check("the health check finds nothing wrong", health.Findings.Count == 0, string.Join("; ", health.Findings));
        }
        finally
        {
            await GraphRagWorkshop.CleanAsync(lesson.Connection!, services);
        }
        lesson.Limit("Suggestions inherit the extraction's mistakes, so a person decides. Re-ingesting can bring a merged name back, and an undo is refused once that happened; merge again instead.");
    }
}
