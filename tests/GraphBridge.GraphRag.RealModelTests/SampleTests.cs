using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Net.ServerSentEvents;
using System.Text;
using System.Text.Json;
using GraphBridge.EntityFrameworkCore.Migrations;
using GraphBridge.GraphRag.AspNetCore;
using GraphBridge.GraphRag.Ontologies;
using GraphBridge.IntegrationTests;
using GraphBridge.Samples.GraphRag;
using GraphBridge.Samples.GraphRagArticles;
using GraphBridge.Samples.GraphRagNovel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace GraphBridge.GraphRag.RealModelTests;

// GraphRAG v2, waves 7 and 8 (GR-46, GR-58): samples 07 to 09 as written, with a real model's calls. Recorded with AI_INFERENCE_PROVIDER set;
// otherwise replayed offline at zero calls. The checks hold for any model: what a model says varies, the trust contract doesn't.
[Collection(Neo4jIntegrationCollection.Name)]
public sealed class SampleTests(DisposableNeo4jFixture fixture, ITestOutputHelper output) : IAsyncLifetime
{
    public Task InitializeAsync() => Enabled ? ResetJournal() : Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (!Enabled) return;
        foreach (var definition in GraphRagSchema.Definitions(GraphRagLayout.Default, QuickStart.EmbeddingSet)
                     .Concat(GraphRagSchema.Definitions(Novel.Layout, Novel.EmbeddingSet, GraphRagOntologies.Novel))
                     .Concat(GraphRagSchema.Definitions(Articles.Layout, Articles.EmbeddingSet, Articles.Ontology)))
            await Execute($"DROP {(definition.IsIndex ? "INDEX" : "CONSTRAINT")} {definition.Name} IF EXISTS");
        await ResetJournal();
    }

    private static bool Enabled => Environment.GetEnvironmentVariable("GRAPHBRIDGE_INTEGRATION") == "1";

    [Neo4jIntegrationFact]
    public async Task The_quick_start_answers_from_its_pages_with_checked_citations_and_refuses_an_unrelated_question()
    {
        Assert.NotNull(QuickStart.Models);
        await using var services = new ServiceCollection()
            .AddDbContext<QuickStartContext>(options => options.UseNeo4j(fixture.Driver, fixture.Database))
            .AddQuickStart()
            .Record(QuickStart.Models, ModelProfile.Repository("samples", "07-GraphRagQuickStart", "data", "quickstart-recordings.json"))
            .BuildServiceProvider();
        await using var scope = services.CreateAsyncScope();
        var provider = scope.ServiceProvider;
        var db = provider.GetRequiredService<QuickStartContext>();
        await db.Database.InitializeGraphMigrationsAsync();
        await db.Database.ApplyGraphMigrationAsync(QuickStart.SchemaPlan);
        var maintenance = provider.GetRequiredService<IGraphRagMaintenance>();
        try
        {
            await maintenance.ResetAsync(new GraphRagResetScope());
            await Execute("MATCH (n:IngestionRun) DELETE n");
            var sources = QuickStart.Sources(QuickStart.RecordedDocs);
            var receipt = await provider.GetRequiredService<IGraphRagIngestion>().IngestAsync(sources);
            Assert.Equal(3, receipt.Documents.Count);
            Assert.All(receipt.Documents, d => Assert.Equal(GraphRagDocumentStatus.Ingested, d.Status));
            output.WriteLine($"Ingested {receipt.ChunksWritten} chunks: {receipt.EmbeddingCalls} embedding calls, {receipt.InputTokens} input tokens, {receipt.Retries}.");

            var answers = provider.GetRequiredService<IGraphRagAnswers>();
            var answer = await answers.AnswerAsync("How does a migration create vector and full-text indexes?");
            Report("indexes", answer);
            Assert.Equal(GraphRagAnswerDisposition.Answered, answer.Disposition);
            Assert.NotEmpty(answer.Citations);
            foreach (var citation in answer.Citations)
            {
                var document = receipt.Documents.Single(d => d.DocumentId == citation.DocumentId);
                var bytes = await File.ReadAllBytesAsync(sources.Single(s => s.SourceUri == document.SourceUri).Path!);
                Assert.Equal(citation.Quote, Encoding.UTF8.GetString(bytes, citation.StartByte, citation.EndByte - citation.StartByte));
            }

            var unrelated = await answers.AnswerAsync("What is the capital of Mongolia?");
            Report("unrelated", unrelated);
            Assert.Equal(GraphRagAnswerDisposition.InsufficientEvidence, unrelated.Disposition);
            Assert.Null(unrelated.Text);

            var again = await provider.GetRequiredService<IGraphRagIngestion>().IngestAsync(QuickStart.Sources(QuickStart.RecordedDocs));
            Assert.All(again.Documents, d => Assert.Equal(GraphRagDocumentStatus.Unchanged, d.Status));
            Assert.Equal(0, again.EmbeddingCalls);
        }
        finally
        {
            await maintenance.ResetAsync(new GraphRagResetScope());
            await Execute("MATCH (n:IngestionRun) DELETE n");
        }
        output.WriteLine($"Real model calls in this process so far: {ModelProfile.Calls}.");
    }

    [Neo4jIntegrationFact]
    public async Task The_novel_sample_extracts_the_opening_of_chapter_one_with_every_span_the_files_own_bytes()
    {
        Assert.NotNull(Novel.Models);
        await using var services = new ServiceCollection()
            .AddDbContext<NovelContext>(options => options.UseNeo4j(fixture.Driver, fixture.Database))
            .AddNovel()
            .Record(Novel.Models, ModelProfile.Repository("samples", "08-GraphRagNovel", "data", "novel-recordings.json"))
            .BuildServiceProvider();
        await using var scope = services.CreateAsyncScope();
        var provider = scope.ServiceProvider;
        var db = provider.GetRequiredService<NovelContext>();
        await db.Database.InitializeGraphMigrationsAsync();
        await db.Database.ApplyGraphMigrationAsync(Novel.SchemaPlan);
        var maintenance = provider.GetRequiredService<IGraphRagMaintenance>();
        try
        {
            await maintenance.ResetAsync(new GraphRagResetScope());
            await maintenance.RemoveOrphansAsync();
            await Execute("MATCH (n:NovelRun) DELETE n");
            var receipt = await provider.GetRequiredService<IGraphRagIngestion>().IngestAsync([Novel.Source(Novel.Excerpt)]);
            Assert.Equal(GraphRagDocumentStatus.Ingested, Assert.Single(receipt.Documents).Status);
            var extraction = receipt.Extraction!;
            output.WriteLine($"Extraction: {extraction.ChatCalls} calls for {extraction.Chunks} passages, {extraction.InputTokens} input and "
                + $"{extraction.OutputTokens} output tokens; {extraction.Entities} things, {extraction.Mentions} mentions, {extraction.Facts} facts, "
                + $"{extraction.Quotes} quotes; repaired {extraction.Repaired}, quarantined {extraction.Quarantined.Count}.");
            foreach (var pruned in extraction.Pruned) output.WriteLine($"  pruned {pruned.Item} {pruned.Label} '{pruned.Name}': {pruned.Reason}");
            Assert.Empty(extraction.Quarantined);

            var summary = await Novel.SummarizeAsync(db);
            output.WriteLine("Characters: " + string.Join("; ", summary.Characters.Select(c => $"{c.Name} ({string.Join(", ", c.Aliases)}) x{c.Mentions}")));
            output.WriteLine("Says: " + string.Join("; ", summary.Says.Select(s => $"{s.Speaker}: {s.Words}")));
            output.WriteLine("Kin: " + string.Join("; ", summary.Kin) + "; places: " + string.Join(", ", summary.Places));
            Assert.Contains(summary.Characters, c => c.Name == "Alice");
            Assert.Contains(summary.Characters, c => c.Name.Contains("Rabbit", StringComparison.Ordinal));
            Assert.Contains(summary.Says, s => s.Speaker.Contains("Rabbit", StringComparison.Ordinal) && s.Words.Contains("Oh dear", StringComparison.Ordinal));

            // Every mention and quote is the file's own bytes at its span.
            var bytes = await File.ReadAllBytesAsync(Novel.Excerpt);
            var spans = (await db.Set<GraphRagMention<Character>>().ToListAsync()).Select(m => (m.StartByte, m.EndByte, m.Quote))
                .Concat((await db.Set<GraphRagMention<Place>>().ToListAsync()).Select(m => (m.StartByte, m.EndByte, m.Quote)))
                .Concat((await db.Set<Says>().ToListAsync()).Select(s => (s.StartByte, s.EndByte, Quote: s.Words))).ToArray();
            Assert.NotEmpty(spans);
            Assert.All(spans, s => Assert.Equal(s.Quote, Encoding.UTF8.GetString(bytes, s.StartByte, s.EndByte - s.StartByte)));

            var answers = provider.GetRequiredService<IGraphRagAnswers>();
            var answer = await answers.AnswerAsync("What did the Rabbit take out of its waistcoat-pocket?");
            Report("waistcoat", answer);
            Assert.Equal(GraphRagAnswerDisposition.Answered, answer.Disposition);
            Assert.Contains("watch", answer.Text!, StringComparison.OrdinalIgnoreCase);
            Assert.All(answer.Citations, c => Assert.Equal(c.Quote, Encoding.UTF8.GetString(bytes, c.StartByte, c.EndByte - c.StartByte)));

            var unrelated = await answers.AnswerAsync("What is the capital of Mongolia?");
            Report("unrelated", unrelated);
            Assert.Equal(GraphRagAnswerDisposition.InsufficientEvidence, unrelated.Disposition);

            var again = await provider.GetRequiredService<IGraphRagIngestion>().IngestAsync([Novel.Source(Novel.Excerpt)]);
            Assert.Equal(GraphRagDocumentStatus.Unchanged, Assert.Single(again.Documents).Status);
            Assert.Equal(0, again.Extraction!.ChatCalls);
        }
        finally
        {
            await maintenance.ResetAsync(new GraphRagResetScope());
            await maintenance.RemoveOrphansAsync();
            await Execute("MATCH (n:NovelRun) DELETE n");
        }
        Assert.Empty(await db.Set<Character>().ToListAsync());
        output.WriteLine($"Real model calls in this process so far: {ModelProfile.Calls}.");
    }

    [Neo4jIntegrationFact]
    public async Task The_articles_sample_reads_NASA_s_Artemis_II_releases_with_every_span_and_citation_the_files_own_bytes()
    {
        // GR-58: five NASA news releases (excerpts shipped with the sample), the article ontology, and answers streamed and not.
        Assert.NotNull(Articles.Models);
        await using var services = new ServiceCollection()
            .AddDbContext<ArticlesContext>(options => options.UseNeo4j(fixture.Driver, fixture.Database))
            .AddArticles()
            .Record(Articles.Models, ModelProfile.Repository("samples", "09-GraphRagArticles", "data", "article-recordings.json"))
            .BuildServiceProvider();
        await using var scope = services.CreateAsyncScope();
        var provider = scope.ServiceProvider;
        var db = provider.GetRequiredService<ArticlesContext>();
        await db.Database.InitializeGraphMigrationsAsync();
        await db.Database.ApplyGraphMigrationAsync(Articles.SchemaPlan);
        var maintenance = provider.GetRequiredService<IGraphRagMaintenance>();
        var folder = Path.Combine(AppContext.BaseDirectory, "data", "nasa", "articles");
        try
        {
            await maintenance.ResetAsync(new GraphRagResetScope());
            await maintenance.RemoveOrphansAsync();
            await Execute("MATCH (n:ArticleRun) DELETE n");
            var sources = Articles.Sources(folder);
            var receipt = await provider.GetRequiredService<IGraphRagIngestion>().IngestAsync(sources);
            Assert.Equal(5, receipt.Documents.Count);
            Assert.All(receipt.Documents, d => Assert.Equal(GraphRagDocumentStatus.Ingested, d.Status));
            var extraction = receipt.Extraction!;
            output.WriteLine($"Ingested {receipt.ChunksWritten} passages: {receipt.EmbeddingCalls} embedding calls. Extraction: {extraction.ChatCalls} calls, "
                + $"{extraction.InputTokens} input and {extraction.OutputTokens} output tokens; {extraction.Entities} things, {extraction.Mentions} mentions, "
                + $"{extraction.Facts} facts, {extraction.Quotes} quotes; repaired {extraction.Repaired}, quarantined {extraction.Quarantined.Count}.");
            foreach (var pruned in extraction.Pruned) output.WriteLine($"  pruned {pruned.Item} {pruned.Label} '{pruned.Name}': {pruned.Reason}");
            Assert.Empty(extraction.Quarantined);

            var report = await Articles.ReportAsync(db);
            foreach (var article in report.Articles) output.WriteLine($"{article.Published} {article.Title} ({article.Publisher}); about {string.Join(", ", article.Topics)}");
            foreach (var job in report.Jobs) output.WriteLine($"{job.Person} works for {job.Organization} as {job.Role ?? "?"} ({job.Articles} articles)");
            foreach (var quote in report.Quotes) output.WriteLine($"{quote.Speaker}: \"{quote.Words}\"");
            Assert.All(report.Articles, a => Assert.Equal(("NASA", true), (a.Publisher, a.Published?.StartsWith("2026-04-", StringComparison.Ordinal))));

            // Replayed, the recorded run gives sample 09's figures exactly; a fresh run is the model's own.
            if (!ModelProfile.Records)
            {
                Assert.Equal((5, 93, 154, 112, 7), (extraction.ChatCalls, extraction.Entities, extraction.Mentions, extraction.Facts, extraction.Quotes));
                Assert.Equal(((long?)8_988, (long?)143_981), (extraction.InputTokens, extraction.OutputTokens));
                Assert.Equal(["Entity ungrounded 1", "Mention quote-not-found 1", "Relationship endpoint-not-found 1", "Relationship pattern-not-allowed 2"],
                    extraction.Pruned.GroupBy(p => $"{p.Item} {p.Reason}").Select(g => $"{g.Key} {g.Count()}").Order(StringComparer.Ordinal));
                foreach (var astronaut in new[] { "Reid Wiseman", "Victor Glover", "Christina Koch", "Jeremy Hansen" })
                    Assert.Contains(report.Jobs, j => j.Person == astronaut && j.Articles == 4);
                Assert.Equal(["Dr. Lori Glaze", "Dr. Nicky Fox", "Jared Isaacman"], report.Quotes.Select(q => q.Speaker).Distinct().Order(StringComparer.Ordinal));
            }

            // Every mention and quote is its file's own bytes at its span.
            var documents = receipt.Documents.ToDictionary(d => d.DocumentId, d => File.ReadAllBytes(sources.Single(s => s.SourceUri == d.SourceUri).Path!));
            var passages = await db.Set<GraphRagChunk>().Select(c => new { c.Id, c.DocumentId }).ToDictionaryAsync(c => c.Id, c => c.DocumentId);
            var spans = (await db.Set<GraphRagMention<Person>>().ToListAsync()).Select(m => (m.ChunkId, m.StartByte, m.EndByte, m.Quote))
                .Concat((await db.Set<GraphRagMention<global::GraphBridge.GraphRag.Ontologies.Organization>>().ToListAsync()).Select(m => (m.ChunkId, m.StartByte, m.EndByte, m.Quote)))
                .Concat((await db.Set<GraphRagMention<Place>>().ToListAsync()).Select(m => (m.ChunkId, m.StartByte, m.EndByte, m.Quote)))
                .Concat((await db.Set<GraphRagMention<Event>>().ToListAsync()).Select(m => (m.ChunkId, m.StartByte, m.EndByte, m.Quote)))
                .Concat((await db.Set<GraphRagMention<Concept>>().ToListAsync()).Select(m => (m.ChunkId, m.StartByte, m.EndByte, m.Quote)))
                .Concat((await db.Set<GraphRagMention<Topic>>().ToListAsync()).Select(m => (m.ChunkId, m.StartByte, m.EndByte, m.Quote)))
                .Concat((await db.Set<Quoted>().ToListAsync()).Select(q => (q.ChunkId, q.StartByte, q.EndByte, Quote: q.Words))).ToArray();
            Assert.NotEmpty(spans);
            Assert.All(spans, s => Assert.Equal(s.Quote, Encoding.UTF8.GetString(documents[passages[s.ChunkId]], s.StartByte, s.EndByte - s.StartByte)));

            var answers = provider.GetRequiredService<IGraphRagAnswers>();
            foreach (var (name, question) in new[]
                     {
                         ("distance", "How far from Earth did the Artemis II crew travel, and whose record did they break?"),
                         ("crew", "Who are the four Artemis II astronauts?"),
                         ("splashdown", "When and where did Artemis II splash down?")
                     })
            {
                var answer = await answers.AnswerAsync(question);
                Report(name, answer);
                Assert.Equal(GraphRagAnswerDisposition.Answered, answer.Disposition);
                Assert.All(answer.Citations, c => Assert.Equal(c.Quote, Encoding.UTF8.GetString(documents[c.DocumentId], c.StartByte, c.EndByte - c.StartByte)));
            }

            // Streamed, the same request gives the same checked answer.
            var streamed = new List<GraphRagAnswerUpdate>();
            await foreach (var update in answers.AnswerStreamingAsync("Who are the four Artemis II astronauts?")) streamed.Add(update);
            var plain = await answers.AnswerAsync("Who are the four Artemis II astronauts?");
            Assert.Contains(streamed, u => u.Kind == GraphRagAnswerUpdateKind.Delta);
            Assert.Equal((plain.Text, plain.Citations.Count), (streamed[^1].Answer!.Text, streamed[^1].Answer!.Citations.Count));

            var unrelated = await answers.AnswerAsync("What is the capital of Mongolia?");
            Report("unrelated", unrelated);
            Assert.Equal(GraphRagAnswerDisposition.InsufficientEvidence, unrelated.Disposition);

            // The agent decides which tools to call; what it states rests on answer_from_documents' checked citations.
            var (said, calls) = await Articles.AskAgentAsync(provider, "How far from Earth did the Artemis II crew travel?");
            output.WriteLine($"[agent] {said}");
            foreach (var call in calls) output.WriteLine($"  {call.Tool}({string.Join(", ", call.Arguments.Select(a => $"{a.Key}: {a.Value}"))}): {call.Evidence.Count} spans");
            Assert.Contains(calls, c => c.Tool == GraphRagTools.AnswerTool && c.Evidence.Count > 0);
            Assert.All(calls.SelectMany(c => c.Evidence).Where(e => e.DocumentId is not null), e => Assert.True(e.EndByte <= documents[e.DocumentId!].Length));
            if (!ModelProfile.Records) Assert.Contains(calls, c => c.Tool == GraphRagTools.SearchTool);   // the recorded agent chose to search too

            // The explorer (GR-56): a cited passage, its mentions as the release's bytes, and facts between its own things.
            var opened = new List<(GraphRagCitation Citation, ExplorerPassage Passage)>();
            foreach (var citation in plain.Citations)
            {
                var found = (await Explorer.PassageAsync(provider, citation.ChunkId))!;
                var release = documents[found.DocumentId];
                Assert.Equal(found.Text, Encoding.UTF8.GetString(release, found.StartByte, found.EndByte - found.StartByte));
                Assert.All(found.Things.SelectMany(t => t.Mentions), m =>
                {
                    Assert.True(m.StartByte >= found.StartByte && m.EndByte <= found.EndByte);
                    Assert.Equal(m.Quote, Encoding.UTF8.GetString(release, m.StartByte, m.EndByte - m.StartByte));
                });
                var ends = found.Things.Select(t => t.Id).Append(found.DocumentId).ToHashSet();
                Assert.All(found.Facts, f => Assert.True(ends.Contains(f.FromId) && ends.Contains(f.ToId)));
                output.WriteLine($"[explorer] {found.Things.Count} things, {found.Facts.Count} facts, {found.Quotes.Count} quotes in passage {found.ChunkId[..8]}.");
                opened.Add((citation, found));
            }
            // The model extracted little from some releases (Liftoff gave one event), so at least one cited passage has people and facts.
            var (cited, passage) = opened.First(p => p.Passage.Things.Any(t => t.Kind == "person") && p.Passage.Facts.Count > 0);
            Assert.Null(await Explorer.PassageAsync(provider, "no-such-passage"));

            // The page and its route, served with the toolkit's endpoints. When recording, the app's clients record too: they load the
            // recordings after every call above was saved, and nothing below calls a model through the first provider.
            {
                var builder = WebApplication.CreateSlimBuilder();
                builder.WebHost.UseUrls("http://127.0.0.1:0");
                builder.Logging.ClearProviders();
                builder.Services.AddDbContext<ArticlesContext>(o => o.UseNeo4j(fixture.Driver, fixture.Database)).AddArticles()
                    .Record(Articles.Models, ModelProfile.Repository("samples", "09-GraphRagArticles", "data", "article-recordings.json"));
                await using var app = builder.Build();
                app.MapGraphRag<ArticlesContext>("/graphrag");
                app.MapExplorer();
                await app.StartAsync();
                using var http = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
                using (var page = await http.GetAsync("/explorer"))
                {
                    Assert.Equal("text/html", page.Content.Headers.ContentType?.MediaType);
                    Assert.Contains("<title>GraphRAG explorer</title>", await page.Content.ReadAsStringAsync(), StringComparison.Ordinal);
                }
                var served = (await http.GetFromJsonAsync<ExplorerPassage>($"/explorer/passages/{Uri.EscapeDataString(cited.ChunkId)}"))!;
                Assert.Equal((passage.Text, passage.Things.Count, passage.Facts.Count), (served.Text, served.Things.Count, served.Facts.Count));
                Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/explorer/passages/no-such-passage")).StatusCode);
                using var asked = await http.PostAsJsonAsync("/graphrag/ask", new { question = "Who are the four Artemis II astronauts?" });
                var stream = Events(await asked.Content.ReadAsStringAsync());
                Assert.Equal("verified", stream[^1].EventType);
                if (!ModelProfile.Records) Assert.Contains(cited.ChunkId, stream[^1].Data, StringComparison.Ordinal);

                // A follow-up as the explorer asks it (GR-57): the conversation so far, with the checked answer, then a question that
                // needs it. The docs' screenshot (docs/images/graphrag-explorer.png) replays this request.
                var crew = JsonSerializer.Deserialize<GraphRagVerifiedEvent>(stream[^1].Data, JsonSerializerOptions.Web)!;
                using var followed = await http.PostAsJsonAsync("/graphrag/ask", new
                {
                    question = "How far from Earth did they travel?",
                    history = new[] { new { question = "Who are the four Artemis II astronauts?", answer = crew.Answer } }
                });
                var events = Events(await followed.Content.ReadAsStringAsync());
                var rewrite = JsonDocument.Parse(events[0].Data).RootElement.GetProperty("rewrite");
                Assert.Equal(("retrieved", "rewritten"), (events[0].EventType, rewrite.GetProperty("outcome").GetString()));
                Assert.Equal("verified", events[^1].EventType);
                var distance = JsonSerializer.Deserialize<GraphRagVerifiedEvent>(events[^1].Data, JsonSerializerOptions.Web)!;
                output.WriteLine($"[follow-up] asked as \"{rewrite.GetProperty("standalone").GetString()}\": {distance.Answer}");
                Assert.NotEmpty(distance.Citations);
                Assert.All(distance.Citations, c => Assert.Equal(c.Quote, Encoding.UTF8.GetString(documents[c.DocumentId], c.StartByte, c.EndByte - c.StartByte)));
            }

            var again = await provider.GetRequiredService<IGraphRagIngestion>().IngestAsync(Articles.Sources(folder));
            Assert.All(again.Documents, d => Assert.Equal(GraphRagDocumentStatus.Unchanged, d.Status));
            Assert.Equal(0, again.Extraction!.ChatCalls);
        }
        finally
        {
            await maintenance.ResetAsync(new GraphRagResetScope());
            await maintenance.RemoveOrphansAsync();
            await Execute("MATCH (n:ArticleRun) DELETE n");
        }
        output.WriteLine($"Real model calls in this process so far: {ModelProfile.Calls}.");
    }

    [Neo4jIntegrationFact]
    public async Task Text2Cypher_answers_graph_questions_on_samples_08_and_09_checked_against_the_graph()
    {
        // GR-15: about 15 questions; each answer's rows are checked against the same graph read through typed LINQ.
        Assert.NotNull(Articles.Models);
        Assert.NotNull(Novel.Models);
        var results = new List<(string Question, GraphRagGraphAnswer Answer, bool Correct)>();
        await using (var articles = new ServiceCollection()
            .AddDbContext<ArticlesContext>(options => options.UseNeo4j(fixture.Driver, fixture.Database))
            .AddArticles()
            .Record(Articles.Models, ModelProfile.Repository("samples", "09-GraphRagArticles", "data", "article-recordings.json"))
            .AddGraphRagText2Cypher<ArticlesContext>()
            .BuildServiceProvider())
        {
            await using var scope = articles.CreateAsyncScope();
            var provider = scope.ServiceProvider;
            var db = provider.GetRequiredService<ArticlesContext>();
            await db.Database.InitializeGraphMigrationsAsync();
            await db.Database.ApplyGraphMigrationAsync(Articles.SchemaPlan);
            var maintenance = provider.GetRequiredService<IGraphRagMaintenance>();
            await maintenance.ResetAsync(new GraphRagResetScope());
            await maintenance.RemoveOrphansAsync();
            await Execute("MATCH (n:ArticleRun) DELETE n");
            var receipt = await provider.GetRequiredService<IGraphRagIngestion>().IngestAsync(Articles.Sources(Path.Combine(AppContext.BaseDirectory, "data", "nasa", "articles")));
            Assert.All(receipt.Documents, d => Assert.Equal(GraphRagDocumentStatus.Ingested, d.Status));

            // The truth, read through typed LINQ.
            var people = await db.Set<Person>().ToDictionaryAsync(p => p.Id, p => p.Name);
            var organizations = await db.Set<global::GraphBridge.GraphRag.Ontologies.Organization>().ToDictionaryAsync(o => o.Id, o => o.Name);
            var places = await db.Set<Place>().ToDictionaryAsync(p => p.Id, p => p.Name);
            var topics = await db.Set<Topic>().ToDictionaryAsync(t => t.Id, t => t.Name);
            var documents = await db.Set<GraphRagDocument>().ToListAsync();
            var jobs = await db.Set<WorksFor>().ToListAsync();
            var quotes = await db.Set<Quoted>().ToListAsync();
            var located = await db.Set<LocatedIn<global::GraphBridge.GraphRag.Ontologies.Organization>>().ToListAsync();
            var about = await db.Set<About>().ToListAsync();
            string Id(Dictionary<string, string> names, string name) => names.Single(n => n.Value == name).Key;
            var nasa = Id(organizations, "NASA");
            var isaacman = Id(people, "Jared Isaacman");
            var cases = new (string Question, string[] Expected)[]
            {
                ("How many news releases are there?", [documents.Count.ToString(CultureInfo.InvariantCulture)]),
                ("Which people work for NASA?", [.. jobs.Where(j => j.ToId == nasa).Select(j => people[j.FromId]).Distinct()]),
                ("Which organization does Jeremy Hansen work for?", [.. jobs.Where(j => j.FromId == Id(people, "Jeremy Hansen")).Select(j => organizations[j.ToId]).Distinct()]),
                ("Who is quoted in the releases?", [.. quotes.Select(q => people[q.SpeakerId]).Distinct()]),
                ("How many quotations are attributed to Jared Isaacman?", [quotes.Count(q => q.SpeakerId == isaacman).ToString(CultureInfo.InvariantCulture)]),
                ("Where is Johnson Space Center located?", [.. located.Where(l => l.FromId == Id(organizations, "Johnson Space Center")).Select(l => places[l.ToId])]),
                ("What is the title of the release published on 2026-04-06?", [documents.Single(d => d.Published == "2026-04-06").Title!]),
                ("Which topics is the release published on 2026-04-02 about?", [.. about.Where(a => a.FromId == documents.Single(d => d.Published == "2026-04-02").Id).Select(a => topics[a.ToId])]),
                ("How many releases state that Reid Wiseman works for NASA?", [jobs.Single(j => j.FromId == Id(people, "Reid Wiseman") && j.ToId == nasa).DocumentIds.Split('\n').Length.ToString(CultureInfo.InvariantCulture)]),
                ("What is the capital of Mongolia?", [])
            };
            var graph = provider.GetRequiredService<IGraphRagGraphQuestions>();
            foreach (var (question, expected) in cases) results.Add(await AskAsync(graph, question, expected));
            await maintenance.ResetAsync(new GraphRagResetScope());
            await maintenance.RemoveOrphansAsync();
        }

        // Each sample applies its schema as a migration of its own, so the journal starts again between them.
        await ResetJournal();
        await using (var novel = new ServiceCollection()
            .AddDbContext<NovelContext>(options => options.UseNeo4j(fixture.Driver, fixture.Database))
            .AddNovel()
            .Record(Novel.Models, ModelProfile.Repository("samples", "08-GraphRagNovel", "data", "novel-recordings.json"))
            .AddGraphRagText2Cypher<NovelContext>()
            .BuildServiceProvider())
        {
            await using var scope = novel.CreateAsyncScope();
            var provider = scope.ServiceProvider;
            var db = provider.GetRequiredService<NovelContext>();
            await db.Database.InitializeGraphMigrationsAsync();
            await db.Database.ApplyGraphMigrationAsync(Novel.SchemaPlan);
            var maintenance = provider.GetRequiredService<IGraphRagMaintenance>();
            await maintenance.ResetAsync(new GraphRagResetScope());
            await maintenance.RemoveOrphansAsync();
            await Execute("MATCH (n:NovelRun) DELETE n");
            Assert.Equal(GraphRagDocumentStatus.Ingested, Assert.Single((await provider.GetRequiredService<IGraphRagIngestion>().IngestAsync([Novel.Source(Novel.Excerpt)])).Documents).Status);

            var characters = await db.Set<Character>().Where(c => !c.IsPlaceholder).ToDictionaryAsync(c => c.Id, c => c.Name);
            var says = await db.Set<Says>().ToListAsync();
            var places = await db.Set<Place>().Select(p => p.Name).ToListAsync();
            var cases = new (string Question, string[] Expected)[]
            {
                ("Which named characters appear in the story?", [.. characters.Values]),
                ("How many named characters are there?", [characters.Count.ToString(CultureInfo.InvariantCulture)]),
                ("Which characters speak?", [.. says.Select(s => s.SpeakerId).Where(characters.ContainsKey).Select(id => characters[id]).Distinct()]),
                ("Which places are mentioned?", [.. places])
            };
            var graph = provider.GetRequiredService<IGraphRagGraphQuestions>();
            foreach (var (question, expected) in cases) results.Add(await AskAsync(graph, question, expected));
            await maintenance.ResetAsync(new GraphRagResetScope());
            await maintenance.RemoveOrphansAsync();
        }

        var correct = results.Count(r => r.Correct);
        output.WriteLine($"Text2Cypher: {correct} of {results.Count} correct; {results.Count(r => r.Answer.Disposition == GraphRagGraphAnswerDisposition.Refused)} refused.");
        // Replayed, the recorded run's score; a fresh run is the model's own.
        if (!ModelProfile.Records) Assert.Equal(RecordedText2CypherCorrect, correct);
        output.WriteLine($"Real model calls in this process so far: {ModelProfile.Calls}.");
    }

    // The recorded run's score (GR-15), asserted when replaying.
    private const int RecordedText2CypherCorrect = 12;

    // Correct when every expected value is a cell of the rows (numbers as written), or, with nothing expected, when the model
    // declined or the query found nothing.
    private async Task<(string, GraphRagGraphAnswer, bool)> AskAsync(IGraphRagGraphQuestions graph, string question, string[] expected)
    {
        var answer = await graph.AskAsync(question);
        var cells = answer.Rows.SelectMany(r => r.Values).SelectMany(Cells).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var correct = expected.Length == 0
            ? answer.Disposition == GraphRagGraphAnswerDisposition.Refused && answer.Reason == "model-declined" || answer.Disposition == GraphRagGraphAnswerDisposition.Answered && answer.Rows.Count == 0
            : answer.Disposition == GraphRagGraphAnswerDisposition.Answered && expected.All(cells.Contains);
        output.WriteLine($"[{(correct ? "ok" : "miss")}] {question} -> {answer.Disposition} {answer.Reason} {answer.Cypher}");
        output.WriteLine($"   rows {answer.Rows.Count}: {string.Join(" | ", answer.Rows.Take(6).Select(r => string.Join(", ", r.Select(c => $"{c.Key}={string.Join("/", Cells(c.Value))}"))))}; expected: {string.Join(", ", expected)}");
        return (question, answer, correct);

        static IEnumerable<string> Cells(object? value) => value switch
        {
            null => [],
            string text => [text],
            IEnumerable<object?> list => list.SelectMany(Cells),
            IDictionary<string, object?> map => map.Values.SelectMany(Cells),
            IFormattable number => [number.ToString(null, CultureInfo.InvariantCulture)],
            _ => [value.ToString() ?? ""]
        };
    }

    private static List<SseItem<string>> Events(string text)
        => [.. SseParser.Create(new MemoryStream(Encoding.UTF8.GetBytes(text))).Enumerate()];

    private void Report(string name, GraphRagAnswer answer)
    {
        output.WriteLine($"[{name}] {answer.Disposition} {answer.Reason}: {answer.Text}");
        foreach (var citation in answer.Citations) output.WriteLine($"  cites bytes {citation.StartByte}-{citation.EndByte}: \"{citation.Quote}\"");
        if (answer.Receipt is { } receipt) output.WriteLine($"  receipt: {receipt.ChatCalls} call, {receipt.InputTokens} in, {receipt.OutputTokens} out, {receipt.Answer.TotalSeconds:0.0} s");
    }

    private async Task Execute(string text)
    {
        await using var session = fixture.OpenSession();
        await (await session.RunAsync(text)).ConsumeAsync();
    }

    // The provider's migration journal, by its stored names (internal to the provider), reset as the integration suite resets it.
    private async Task ResetJournal()
    {
        foreach (var label in new[] { "__GraphBridgeMigrationStep", "__GraphBridgeMigration", "__GraphBridgeMigrationControl" })
            await Execute($"MATCH (n:{label}) DELETE n");
        foreach (var name in new[] { "gb_migration_control_key", "gb_migration_history_key", "gb_migration_step_key" })
            await Execute($"DROP CONSTRAINT {name} IF EXISTS");
    }
}
