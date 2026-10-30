using System.Text;
using GraphBridge.GraphRag.Evaluation;
using GraphBridge.GraphRag.Ontologies;
using GraphBridge.IntegrationTests;
using GraphBridge.Samples.GraphRagNovel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Neo4j.Driver;
using Xunit.Abstractions;

namespace GraphBridge.GraphRag.RealModelTests;

/// <summary>A test on the whole book: it needs the fixture and the book, which is fetched once and never committed.</summary>
public sealed class BookFactAttribute : FactAttribute
{
    public BookFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("GRAPHBRIDGE_INTEGRATION") != "1") Skip = "Live disposable Neo4j fixture required; set GRAPHBRIDGE_INTEGRATION=1.";
        else if (WholeBookTests.Book is null) Skip = "Needs the whole book: run sample 08's fetch, then copy it to .artifacts/books/alice.txt or set GRAPHBRIDGE_ALICE_BOOK.";
    }
}

// GraphRAG v2, wave 7 (GR-49, GR-50, GR-47, GR-63, GR-48): the whole of Alice's Adventures in Wonderland with a real model,
// recorded, then replayed at zero calls wherever the book has been fetched. What the model extracts is reported, not asserted;
// the trust contract and the cost preview's accuracy are asserted.
[Collection(Neo4jIntegrationCollection.Name)]
public sealed class WholeBookTests(DisposableNeo4jFixture fixture, ITestOutputHelper output) : IAsyncLifetime
{
    internal static string? Book => new[] { Environment.GetEnvironmentVariable("GRAPHBRIDGE_ALICE_BOOK"), ModelProfile.Repository(".artifacts", "books", "alice.txt") }
        .FirstOrDefault(File.Exists);

    private static string Recordings => ModelProfile.Repository("samples", "08-GraphRagNovel", "data", "novel-recordings.json");

    // Another suite may leave the migration journal behind, and the novel's plan starts a chain of its own.
    public async Task InitializeAsync()
    {
        if (Environment.GetEnvironmentVariable("GRAPHBRIDGE_INTEGRATION") != "1") return;
        foreach (var label in new[] { "__GraphBridgeMigrationStep", "__GraphBridgeMigration", "__GraphBridgeMigrationControl" })
            await Execute($"MATCH (n:{label}) DELETE n");
        foreach (var name in new[] { "gb_migration_control_key", "gb_migration_history_key", "gb_migration_step_key" })
            await Execute($"DROP CONSTRAINT {name} IF EXISTS");
    }

    public async Task DisposeAsync()
    {
        if (Environment.GetEnvironmentVariable("GRAPHBRIDGE_INTEGRATION") != "1") return;
        foreach (var definition in GraphRagSchema.Definitions(Novel.Layout, Novel.EmbeddingSet, GraphRagOntologies.Novel))
            await Execute($"DROP {(definition.IsIndex ? "INDEX" : "CONSTRAINT")} {definition.Name} IF EXISTS");
        foreach (var label in new[] { "__GraphBridgeMigrationStep", "__GraphBridgeMigration", "__GraphBridgeMigrationControl" })
            await Execute($"MATCH (n:{label}) DELETE n");
    }

    [BookFact]
    public async Task The_whole_book_extracts_with_every_failure_explained_and_the_preview_within_a_fifth_of_the_receipt()
    {
        await using var services = Services();
        await using var scope = services.CreateAsyncScope();
        var provider = scope.ServiceProvider;
        var (db, maintenance, ingestion) = (provider.GetRequiredService<NovelContext>(), provider.GetRequiredService<IGraphRagMaintenance>(), provider.GetRequiredService<IGraphRagIngestion>());
        await PrepareAsync(db, maintenance);
        var text = await File.ReadAllTextAsync(Book!);
        var folder = Directory.CreateTempSubdirectory("graphbridge-alice-").FullName;
        try
        {
            // Stage 1, chapter I alone, under a stop rule: stop before the rest if extraction fails on more than one passage in four.
            var chapterOne = Path.Combine(folder, "alice-chapter-1.txt");
            var start = text.IndexOf("\nCHAPTER I.\n", StringComparison.Ordinal);
            await File.WriteAllTextAsync(chapterOne, text[(start + 1)..text.IndexOf("\nCHAPTER II.\n", start, StringComparison.Ordinal)]);
            var first = await ingestion.IngestAsync([GraphRagSources.File(chapterOne)]);
            Report("Chapter I", first);
            Assert.Equal(GraphRagDocumentStatus.Ingested, Assert.Single(first.Documents).Status);
            Assert.True(first.Extraction!.Quarantined.Count * 4 <= first.Extraction.Chunks, "Stop rule: more than one passage in four failed on chapter I.");
            await maintenance.RemoveSourceAsync(first.Documents[0].DocumentId);
            await maintenance.RemoveOrphansAsync();

            // Stage 2, the whole book, previewed first.
            var source = GraphRagSources.File(Book!, new Uri(Novel.BookUrl));
            var preview = await ingestion.PreviewAsync([source]);
            output.WriteLine($"Preview: {preview.Chunks} passages, {preview.EmbeddingCalls} embedding calls ({preview.EmbeddingTokens} tokens), "
                + $"{preview.ExtractionCalls} extraction calls ({preview.ExtractionInputTokens} input tokens, {(preview.TokensCounted ? "counted" : "estimated")}).");
            var receipt = await ingestion.IngestAsync([source]);
            Report("Book", receipt);
            Assert.Equal(GraphRagDocumentStatus.Ingested, Assert.Single(receipt.Documents).Status);
            var extraction = receipt.Extraction!;

            // Every failure has a reason: quarantined replies carry an error code, pruned items a reason.
            Assert.All(extraction.Quarantined, q => Assert.Contains(q.Error, new[] { "empty-reply", "invalid-json", "truncated", "schema-mismatch" }));
            Assert.All(extraction.Pruned, p => Assert.False(string.IsNullOrEmpty(p.Reason)));

            // Replayed, the recorded run gives sample 08's figures exactly; a fresh run is the model's own.
            if (!ModelProfile.Records)
            {
                Assert.Equal((41, 150, 545, 130, 844, 0), (extraction.ChatCalls, extraction.Entities, extraction.Mentions, extraction.Facts, extraction.Quotes, extraction.Quarantined.Count));
                Assert.Equal(((long?)60_583, (long?)733_516, (long?)652_802), (extraction.InputTokens, extraction.OutputTokens, extraction.ReasoningTokens));
                Assert.Equal(new[] { "Mention quote-not-found 9", "Quote endpoint-not-found 1", "Quote limit 13", "Quote quote-not-found 5", "Relationship endpoint-not-found 16",
                    "Relationship pattern-not-allowed 66", "Entity ungrounded 8" }.Order(StringComparer.Ordinal),
                    extraction.Pruned.GroupBy(p => $"{p.Item} {p.Reason}").Select(g => $"{g.Key} {g.Count()}").Order(StringComparer.Ordinal));
            }

            // The preview: the same passages and embedding calls; extraction calls and input tokens within a fifth of the receipt.
            Assert.Equal((preview.Chunks, preview.EmbeddingCalls), (receipt.ChunksWritten, receipt.EmbeddingCalls));
            Within("extraction calls", preview.ExtractionCalls, extraction.ChatCalls);
            Within("extraction input tokens", preview.ExtractionInputTokens, extraction.InputTokens!.Value);
            Within("embedding tokens", preview.EmbeddingTokens, receipt.InputTokens!.Value);

            // The graph: who's in it, who says what, and names that may be one character (for GR-55).
            var summary = await Novel.SummarizeAsync(db);
            output.WriteLine($"{summary.Characters.Count} characters, {summary.Places.Count} places, {summary.Kin.Count} kin facts, {summary.Says.Count} quotes.");
            foreach (var character in summary.Characters.OrderByDescending(c => c.Mentions).Take(40))
                output.WriteLine($"  {character.Name}{(character.Aliases.Count > 0 ? " (" + string.Join(", ", character.Aliases) + ")" : "")}: {character.Mentions}");
            foreach (var speaker in summary.Says.GroupBy(s => s.Speaker).OrderByDescending(g => g.Count()).Take(12))
                output.WriteLine($"  says: {speaker.Key} x{speaker.Count()}");
            foreach (var (a, b) in Duplicates(summary.Characters.Select(c => c.Name).ToArray()))
                output.WriteLine($"  maybe one character: '{a}' and '{b}'");
            string[] cast = ["Alice", "White Rabbit", "Caterpillar", "Duchess", "Cheshire Cat", "Hatter", "March Hare", "Dormouse", "Queen of Hearts", "Mock Turtle", "Gryphon", "King of Hearts"];
            var found = cast.Where(name => summary.Characters.Any(c => c.Name.Contains(name, StringComparison.OrdinalIgnoreCase)
                || c.Aliases.Any(a => a.Contains(name, StringComparison.OrdinalIgnoreCase)))).ToArray();
            output.WriteLine($"Known cast found: {found.Length} of {cast.Length}: {string.Join(", ", found)}.");
            Assert.True(found.Length >= cast.Length - 2, "Too few of the book's main characters were extracted.");
            if (!ModelProfile.Records)
            {
                Assert.Equal(cast, found);
                Assert.Equal((92, 25, 9, 844), (summary.Characters.Count, summary.Places.Count, summary.Kin.Count, summary.Says.Count));
            }

            // Every mention and quote is the book's own bytes at its span.
            var bytes = await File.ReadAllBytesAsync(Book!);
            var spans = (await db.Set<GraphRagMention<Character>>().ToListAsync()).Select(m => (m.StartByte, m.EndByte, m.Quote))
                .Concat((await db.Set<Says>().ToListAsync()).Select(s => (s.StartByte, s.EndByte, Quote: s.Words))).ToArray();
            Assert.All(spans, s => Assert.Equal(s.Quote, Encoding.UTF8.GetString(bytes, s.StartByte, s.EndByte - s.StartByte)));
            output.WriteLine($"{spans.Length} character mentions and quotes checked against the book's bytes.");

            // GR-55: merge suggestions on the real graph. Merging every one keeps each mention and quote the book's bytes,
            // and undoing every merge, last first, leaves every node, relationship and property as it was.
            var suggestions = await GraphRagResolution.SuggestAsync(db);
            foreach (var suggestion in suggestions) output.WriteLine($"  suggested: '{suggestion.MergeName}' into '{suggestion.KeepName}' ({suggestion.Kind}, {suggestion.Reason})");
            // Replayed, the recorded book gives the docs' figure exactly; a fresh run is the model's own.
            if (!ModelProfile.Records) Assert.Equal(27, suggestions.Count);
            var before = await SnapshotAsync();
            var merged = await GraphRagResolution.Plan(suggestions).ApplyAsync(db);
            output.WriteLine($"Merged {merged.RunIds.Count}: {merged.Mentions} mentions, {merged.Quotes} quotes; facts {merged.FactsMoved} moved, {merged.FactsJoined} joined, {merged.FactsDropped} dropped.");
            Assert.All((await db.Set<GraphRagMention<Character>>().ToListAsync()).Select(m => (m.StartByte, m.EndByte, m.Quote))
                    .Concat((await db.Set<Says>().ToListAsync()).Select(s => (s.StartByte, s.EndByte, Quote: s.Words))),
                s => Assert.Equal(s.Quote, Encoding.UTF8.GetString(bytes, s.StartByte, s.EndByte - s.StartByte)));
            Assert.Empty(await GraphRagResolution.SuggestAsync(db));
            foreach (var run in merged.RunIds.Reverse()) await GraphRagMergePlan.UndoAsync(db, run);
            Assert.Equal(before, await SnapshotAsync());
            db.ChangeTracker.Clear();

            // Ask about later chapters.
            var answers = provider.GetRequiredService<IGraphRagAnswers>();
            foreach (var question in new[] { "What did the Queen's croquet game use as balls and mallets?", "Who stole the tarts, according to the verses read at the trial?" })
            {
                var answer = await answers.AnswerAsync(question);
                output.WriteLine($"[{question}] {answer.Disposition} {answer.Reason}: {answer.Text}");
                foreach (var citation in answer.Citations) output.WriteLine($"  cites {citation.StartByte}-{citation.EndByte}: \"{citation.Quote}\"");
                Assert.Equal(GraphRagAnswerDisposition.Answered, answer.Disposition);
                Assert.All(answer.Citations, c => Assert.Equal(c.Quote, Encoding.UTF8.GetString(bytes, c.StartByte, c.EndByte - c.StartByte)));
            }
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
        output.WriteLine($"Real model calls in this process so far: {ModelProfile.Calls}.");
    }

    [BookFact]
    public async Task The_evaluation_corpus_scores_the_whole_book_with_every_citation_the_books_own_bytes()
    {
        await using var services = Services();
        await using var scope = services.CreateAsyncScope();
        var provider = scope.ServiceProvider;
        var db = provider.GetRequiredService<NovelContext>();
        await db.Database.InitializeGraphMigrationsAsync();
        await db.Database.ApplyGraphMigrationAsync(Novel.SchemaPlan);
        // Unchanged when the other test ingested it; recorded or replayed otherwise.
        var receipt = await provider.GetRequiredService<IGraphRagIngestion>().IngestAsync([GraphRagSources.File(Book!, new Uri(Novel.BookUrl))]);
        var outcome = Assert.Single(receipt.Documents);
        Assert.True(outcome.Status != GraphRagDocumentStatus.Failed, $"The book failed to ingest: {outcome.ErrorCode}; retries {receipt.Retries}.");

        var (cases, followUps) = (GraphRagEvaluator.AliceBookCases(), GraphRagEvaluator.AliceBookFollowUps());
        // Every required phrase is in the book, as the coverage metric reads it, so a miss is retrieval's and not the label's.
        static string Collapse(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var book = Collapse(await File.ReadAllTextAsync(Book!));
        Assert.All(cases.Concat(followUps).SelectMany(c => c.RequiredEvidence), phrase => Assert.Contains(Collapse(phrase), book, StringComparison.OrdinalIgnoreCase));
        var report = await GraphRagEvaluator.RunAsync(provider.GetRequiredService<IGraphRagAnswers>(), cases);
        foreach (var result in report.Cases)
        {
            var failed = result.Metrics.Where(m => m.Measured && !m.Passed).Select(m => m.MetricName.Replace("graphrag_", "", StringComparison.Ordinal));
            output.WriteLine($"{result.Case.Id,-16} {(result.Case.Answerable ? "answerable" : "unanswerable"),-12} {result.Answer.Disposition,-20} {result.Answer.Reason,-18} "
                + $"{result.Answer.Citations.Count} citations {string.Join(" ", failed)}");
        }
        var (validity, abstention, coverage) = (report.Score("graphrag_citation_validity"), report.Score("graphrag_abstention"), report.Score("graphrag_retrieval_coverage"));
        output.WriteLine($"Citation validity {validity:P0}, abstention {abstention:P0}, retrieval coverage {coverage:P0}, over {cases.Count} cases.");
        var answered = report.Cases.Count(c => c.Answer.Disposition == GraphRagAnswerDisposition.Answered);
        output.WriteLine($"Answered {answered}, refused {cases.Count - answered}: "
            + string.Join(", ", report.Cases.Where(c => c.Answer.Reason is not null).GroupBy(c => c.Answer.Reason).Select(g => $"{g.Key} {g.Count()}")));

        // The trust contract holds whatever the model does: every citation is the stored bytes at its span.
        Assert.Equal(1.0, validity);
        var bytes = await File.ReadAllBytesAsync(Book!);
        Assert.All(report.Cases.SelectMany(c => c.Answer.Citations), c => Assert.Equal(c.Quote, Encoding.UTF8.GetString(bytes, c.StartByte, c.EndByte - c.StartByte)));

        // GR-57: follow-ups name what they ask about only through their history. Each is retrieved bare first, which costs an
        // embedding only, then answered with its history, which rewrites it.
        bool Covers(GraphRagEvidence evidence, GraphRagEvaluationCase label)
            => label.RequiredEvidence.All(phrase => evidence.Items.Any(i => Collapse(i.Text).Contains(Collapse(phrase), StringComparison.OrdinalIgnoreCase)));
        var retriever = provider.GetRequiredService<IGraphRagRetriever>();
        var bare = 0;
        foreach (var followUp in followUps.Where(c => c.Answerable))
            if (Covers(await retriever.RetrieveAsync(followUp.Question), followUp)) bare++;
        var followed = await GraphRagEvaluator.RunAsync(provider.GetRequiredService<IGraphRagAnswers>(), followUps);
        foreach (var result in followed.Cases)
            output.WriteLine($"{result.Case.Id,-16} {result.Answer.Evidence.Rewrite?.Outcome,-10} \"{result.Answer.Evidence.Question}\" {result.Answer.Disposition} {result.Answer.Reason} "
                + string.Join(" ", result.Metrics.Where(m => m.Measured && !m.Passed).Select(m => m.MetricName.Replace("graphrag_", "", StringComparison.Ordinal))));
        var followUpScores = (Coverage: followed.Score("graphrag_retrieval_coverage"), Abstention: followed.Score("graphrag_abstention"), Bare: bare);
        output.WriteLine($"Follow-ups: retrieval coverage {followUpScores.Coverage:P0} and abstention {followUpScores.Abstention:P0} with history; "
            + $"{bare} of {followUps.Count(c => c.Answerable)} answerable follow-ups found their passage without it.");
        Assert.Equal(1.0, followed.Score("graphrag_citation_validity"));
        Assert.All(followed.Cases, c => Assert.NotNull(c.Answer.Evidence.Rewrite));
        if (!ModelProfile.Records) Assert.Equal(RecordedFollowUps, followUpScores);
        output.WriteLine($"Real model calls in this process so far: {ModelProfile.Calls}.");
    }

    // The recorded follow-up run (GR-57), asserted when replaying.
    private static readonly (double? Coverage, double? Abstention, int Bare) RecordedFollowUps = (1.0, 1.0, 1);

    [BookFact]
    public async Task A_book_that_takes_many_leases_to_extract_is_still_written()
    {
        // The live whole-book runs failed at the write after 14 and 25 minutes; this replays the same calls slowly, at zero cost.
        await using var services = new ServiceCollection()
            .AddDbContext<NovelContext>(options => options.UseNeo4j(fixture.Driver, fixture.Database))
            .AddNovel()
            .Record(Novel.Models, Recordings)
            .BuildServiceProvider();
        var recorded = services.GetRequiredService<IChatClient>();
        await using var slow = new ServiceCollection()
            .AddDbContext<NovelContext>(options => options.UseNeo4j(fixture.Driver, fixture.Database))
            .AddNovel()
            .AddSingleton<IChatClient>(new Slow(recorded, TimeSpan.FromSeconds(15)))
            .BuildServiceProvider();
        slow.GetRequiredService<GraphRagOptions>().Ingestion.LeaseDuration = TimeSpan.FromSeconds(20);
        await using var scope = slow.CreateAsyncScope();
        var provider = scope.ServiceProvider;
        await PrepareAsync(provider.GetRequiredService<NovelContext>(), provider.GetRequiredService<IGraphRagMaintenance>());
        var receipt = await provider.GetRequiredService<IGraphRagIngestion>().IngestAsync([GraphRagSources.File(Book!, new Uri(Novel.BookUrl))]);
        var outcome = Assert.Single(receipt.Documents);
        output.WriteLine($"{outcome.Status} {outcome.ErrorCode} after {receipt.Elapsed.TotalMinutes:0.0} min; retries {receipt.Retries}.");
        Assert.True(outcome.Status == GraphRagDocumentStatus.Ingested, $"{outcome.Status}: {outcome.ErrorCode}");
    }

    [BookFact]
    public async Task The_whole_book_ingested_again_over_its_earlier_graph_replaces_it()
    {
        await using var services = Services();
        await using var scope = services.CreateAsyncScope();
        var provider = scope.ServiceProvider;
        var (db, ingestion) = (provider.GetRequiredService<NovelContext>(), provider.GetRequiredService<IGraphRagIngestion>());
        await PrepareAsync(db, provider.GetRequiredService<IGraphRagMaintenance>());
        Assert.Equal(GraphRagDocumentStatus.Ingested, Assert.Single((await ingestion.IngestAsync([GraphRagSources.File(Book!, new Uri(Novel.BookUrl))])).Documents).Status);
        // As if the stored graph came from other settings, such as earlier extraction instructions.
        await Execute("MATCH (r:NovelRun) SET r.SettingsFingerprint = 'earlier'");
        var again = await ingestion.IngestAsync([GraphRagSources.File(Book!, new Uri(Novel.BookUrl))]);
        var outcome = Assert.Single(again.Documents);
        output.WriteLine($"{outcome.Status} {outcome.ErrorCode} after {again.Elapsed.TotalSeconds:0} s.");
        Assert.True(outcome.Status == GraphRagDocumentStatus.Ingested, $"{outcome.Status}: {outcome.ErrorCode}");
    }

    private sealed class Slow(IChatClient inner, TimeSpan delay) : DelegatingChatClient(inner)
    {
        public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            await Task.Delay(delay, cancellationToken);
            return await base.GetResponseAsync(messages, options, cancellationToken);
        }
    }

    private ServiceProvider Services() => new ServiceCollection()
        .AddDbContext<NovelContext>(options => options.UseNeo4j(fixture.Driver, fixture.Database))
        .AddNovel()
        .Record(Novel.Models, Recordings)
        .BuildServiceProvider();

    private async Task PrepareAsync(NovelContext db, IGraphRagMaintenance maintenance)
    {
        await db.Database.InitializeGraphMigrationsAsync();
        await db.Database.ApplyGraphMigrationAsync(Novel.SchemaPlan);
        await maintenance.ResetAsync(new GraphRagResetScope());
        await maintenance.RemoveOrphansAsync();
        await Execute("MATCH (n:NovelRun) DELETE n");
    }

    private void Report(string name, GraphRagIngestionReceipt receipt)
    {
        var x = receipt.Extraction!;
        output.WriteLine($"{name}: {receipt.ChunksWritten} passages in {receipt.Elapsed.TotalMinutes:0.0} min; {receipt.EmbeddingCalls} embedding calls, {receipt.InputTokens} tokens; "
            + $"extraction {x.ChatCalls} calls, {x.InputTokens} input, {x.OutputTokens} output ({x.ReasoningTokens} reasoning) tokens; "
            + $"{x.Entities} things, {x.Mentions} mentions, {x.Facts} facts, {x.Quotes} quotes; repaired {x.Repaired}, quarantined {x.Quarantined.Count} "
            + $"({(x.Chunks == 0 ? 0 : 100.0 * x.Quarantined.Count / x.Chunks):0.0}% of passages); retries {receipt.Retries}.");
        foreach (var group in x.Pruned.GroupBy(p => (p.Item, p.Reason)).OrderByDescending(g => g.Count()))
            output.WriteLine($"  pruned {group.Key.Item} {group.Key.Reason}: {group.Count()}");
        foreach (var quarantined in x.Quarantined) output.WriteLine($"  quarantined {quarantined.ChunkId[..8]}: {quarantined.Error}");
    }

    private void Within(string name, long preview, long actual)
    {
        var off = actual == 0 ? 0 : Math.Abs(preview - actual) / (double)actual;
        output.WriteLine($"Preview vs receipt, {name}: {preview} vs {actual} ({off:P1} off).");
        Assert.True(off <= 0.2, $"The preview's {name} are more than 20% off the receipt.");
    }

    // Names where one contains the other, such as "Hatter" and "the Hatter": candidates for a reviewed merge, never automatic.
    private static IEnumerable<(string, string)> Duplicates(string[] names)
        => from a in names
           from b in names
           where string.CompareOrdinal(a, b) < 0 && (a.Contains(b, StringComparison.OrdinalIgnoreCase) || b.Contains(a, StringComparison.OrdinalIgnoreCase))
           select (a, b);

    // Every node of the novel's classes and every relationship from them, with all their properties, in a stable order.
    private async Task<string[]> SnapshotAsync()
    {
        var labels = string.Join(" OR ", GraphRagOntologies.Novel.Classes.Select(c => "n:" + c.Label));
        await using var session = fixture.OpenSession();
        var nodes = await (await session.RunAsync($"MATCH (n) WHERE {labels} RETURN labels(n) AS labels, properties(n) AS properties")).ToListAsync();
        var edges = await (await session.RunAsync($"MATCH (n)-[r]->(o) WHERE {labels} RETURN n.Id AS source, type(r) AS type, properties(r) AS properties, o.Id AS target")).ToListAsync();
        static string Text(IReadOnlyDictionary<string, object> properties) => string.Join(", ", properties.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key}={p.Value}"));
        return [.. nodes.Select(r => string.Join(':', r["labels"].As<List<string>>().Order(StringComparer.Ordinal)) + " " + Text(r["properties"].As<IReadOnlyDictionary<string, object>>()))
            .Concat(edges.Select(r => $"{r["source"].As<string>()} {r["type"].As<string>()} {r["target"].As<string>()} {Text(r["properties"].As<IReadOnlyDictionary<string, object>>())}"))
            .Order(StringComparer.Ordinal)];
    }

    private async Task Execute(string text)
    {
        await using var session = fixture.OpenSession();
        await (await session.RunAsync(text)).ConsumeAsync();
    }
}
