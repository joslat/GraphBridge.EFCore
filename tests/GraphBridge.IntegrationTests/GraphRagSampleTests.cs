using System.Text;
using GraphBridge.EntityFrameworkCore.Migrations;
using GraphBridge.GraphRag;
using GraphBridge.GraphRag.Ontologies;
using GraphBridge.Samples.GraphRagArticles;
using GraphBridge.Samples.GraphRagNovel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Neo4j.Driver;

namespace GraphBridge.IntegrationTests;

// Samples 08 and 09 acceptance (GraphRAG wave 4): each sample's own setup, offline ingestion and the typed LINQ report it
// prints, on the fixture. Offline, sample 08's extraction replays GLM-5.3-Flash's reply recorded on 25 October 2026 (GR-46),
// and sample 09's uses replies written by hand. The same samples with a real model's calls are in GraphBridge.GraphRag.RealModelTests.
[Collection(Neo4jIntegrationCollection.Name)]
public sealed class GraphRagSampleTests(DisposableNeo4jFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => Enabled ? ResetJournal() : Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (!Enabled) return;
        foreach (var definition in GraphRagSchema.Definitions(Novel.Layout, Novel.EmbeddingSet, GraphRagOntologies.Novel)
                     .Concat(GraphRagSchema.Definitions(Articles.Layout, Articles.EmbeddingSet, Articles.Ontology)))
            await Execute($"DROP {(definition.IsIndex ? "INDEX" : "CONSTRAINT")} {definition.Name} IF EXISTS");
        await ResetJournal();
    }

    private static bool Enabled => Environment.GetEnvironmentVariable("GRAPHBRIDGE_INTEGRATION") == "1";

    [Neo4jIntegrationFact]
    public async Task The_novel_sample_extracts_the_opening_of_chapter_one_into_characters_kin_and_speech()
    {
        Assert.Null(Novel.Models);
        await using var services = new ServiceCollection()
            .AddDbContext<NovelContext>(options => options.UseNeo4j(fixture.Driver, fixture.Database))
            .AddNovel()
            .BuildServiceProvider();
        await using var scope = services.CreateAsyncScope();
        var provider = scope.ServiceProvider;
        var db = provider.GetRequiredService<NovelContext>();
        await db.Database.InitializeGraphMigrationsAsync();
        Assert.Equal(Novel.SchemaPlan.Steps.Count, (await db.Database.ApplyGraphMigrationAsync(Novel.SchemaPlan)).CompletedSteps);
        var maintenance = provider.GetRequiredService<IGraphRagMaintenance>();
        try
        {
            await maintenance.ResetAsync(new GraphRagResetScope());
            await maintenance.RemoveOrphansAsync();
            var receipt = await provider.GetRequiredService<IGraphRagIngestion>().IngestAsync([Novel.Source(Novel.Excerpt)]);
            Assert.Equal(GraphRagDocumentStatus.Ingested, Assert.Single(receipt.Documents).Status);
            var extraction = receipt.Extraction!;
            Assert.Equal((1, 5, 4, 3), (extraction.ChatCalls, extraction.Entities, extraction.Facts, extraction.Quotes));
            Assert.Empty(extraction.Pruned);
            Assert.Empty(extraction.Quarantined);

            var summary = await Novel.SummarizeAsync(db);
            Assert.Equal(["Alice", "Alice's sister", "White Rabbit"], summary.Characters.Select(c => c.Name));
            Assert.Empty(summary.Characters[2].Aliases);
            Assert.Equal(2, summary.Characters[2].Mentions);
            Assert.Equal([("Alice", "sibling", "Alice's sister")], summary.Kin);
            // Speech interrupted by narration is one quote per part.
            Assert.Equal([("Alice", "and what is the use of a book"), ("Alice", "without pictures or conversations?"), ("White Rabbit", "Oh dear! Oh dear! I shall be late!")],
                summary.Says);
            Assert.Equal(["rabbit-hole"], summary.Places);
            var chase = await db.Set<Event>().SingleAsync();
            Assert.Equal(("The White Rabbit pops down the rabbit-hole", "I", null), (chase.Name, chase.Chapter, chase.Date));
            Assert.Equal(2, (await db.Set<ParticipatesIn<Character>>().ToListAsync()).Count);
            Assert.Single(await db.Set<TakesPlaceIn>().ToListAsync());

            // The speech's bytes are the file's bytes.
            var bytes = await File.ReadAllBytesAsync(Novel.Excerpt);
            Assert.All(await db.Set<Says>().ToListAsync(), says => Assert.Equal(says.Words, Encoding.UTF8.GetString(bytes, says.StartByte, says.EndByte - says.StartByte)));

            var answer = await provider.GetRequiredService<IGraphRagAnswers>().AnswerAsync("What did the Rabbit take out of its waistcoat-pocket?");
            Assert.Equal(GraphRagAnswerDisposition.Answered, answer.Disposition);
            var citation = Assert.Single(answer.Citations);
            Assert.Equal(citation.Quote, Encoding.UTF8.GetString(bytes, citation.StartByte, citation.EndByte - citation.StartByte));
            Assert.Equal(GraphRagAnswerDisposition.InsufficientEvidence,
                (await provider.GetRequiredService<IGraphRagAnswers>().AnswerAsync("What is the capital of Mongolia?")).Disposition);

            var again = await provider.GetRequiredService<IGraphRagIngestion>().IngestAsync([Novel.Source(Novel.Excerpt)]);
            Assert.Equal(GraphRagDocumentStatus.Unchanged, Assert.Single(again.Documents).Status);
        }
        finally
        {
            await maintenance.ResetAsync(new GraphRagResetScope());
            await maintenance.RemoveOrphansAsync();
            await Execute("MATCH (n:NovelRun) DELETE n");
        }
        Assert.Empty(await db.Set<Character>().ToListAsync());
    }

    [Neo4jIntegrationFact]
    public async Task The_articles_sample_reports_authors_jobs_quotes_and_places_with_the_sources_own_metadata()
    {
        Assert.Null(Articles.Models);
        await using var services = new ServiceCollection()
            .AddDbContext<ArticlesContext>(options => options.UseNeo4j(fixture.Driver, fixture.Database))
            .AddArticles()
            .BuildServiceProvider();
        await using var scope = services.CreateAsyncScope();
        var provider = scope.ServiceProvider;
        var db = provider.GetRequiredService<ArticlesContext>();
        await db.Database.InitializeGraphMigrationsAsync();
        Assert.Equal(Articles.SchemaPlan.Steps.Count, (await db.Database.ApplyGraphMigrationAsync(Articles.SchemaPlan)).CompletedSteps);
        var maintenance = provider.GetRequiredService<IGraphRagMaintenance>();
        try
        {
            await maintenance.ResetAsync(new GraphRagResetScope());
            await maintenance.RemoveOrphansAsync();
            var sources = Articles.Sources(Articles.Folder);
            var receipt = await provider.GetRequiredService<IGraphRagIngestion>().IngestAsync(sources);
            Assert.Equal(2, receipt.Documents.Count);
            Assert.All(receipt.Documents, d => Assert.Equal(GraphRagDocumentStatus.Ingested, d.Status));
            Assert.Empty(receipt.Extraction!.Pruned);
            Assert.Empty(receipt.Extraction.Quarantined);

            var report = await Articles.ReportAsync(db);
            Assert.Equal(
                [new ArticleLine("Larkfield library opens a seed bank", "2026-03-14", "The Larkfield Courier (fictional)", ["Dana Whitfield"], ["Seed sharing"]),
                 new ArticleLine("Seed bank lends its first thousand packets", "2026-06-02", "The Larkfield Courier (fictional)", ["Dana Whitfield"], ["Seed sharing"])],
                report.Articles, new ArticleLineComparer());
            Assert.Equal([new JobLine("Tomas Reyes", "head librarian", "Larkfield Public Library", 2)], report.Jobs);
            Assert.Equal(3, report.Quotes.Count);
            Assert.All(report.Quotes, q => Assert.Equal("Tomas Reyes", q.Speaker));
            Assert.Equal([new PlaceLine("Larkfield Public Library", "Elm Street")], report.Located);

            // One concept across both articles; its alias from the first, mentions from both; the labels carry the prefix.
            var concept = await db.Set<Concept>().SingleAsync();
            Assert.Equal(("community seed bank", "seed bank"), (concept.Name, concept.Aliases));
            Assert.Equal(3, await Count("MATCH (p:NewsPerson) RETURN count(p) AS value"));
            Assert.Equal(4, await Count("MATCH (:NewsPerson {Name: 'Tomas Reyes'})-[m:MENTIONED_IN_NEWS_PERSON]->(:ArticlePassage) RETURN count(m) AS value"));
            Assert.Equal("https://example.org/larkfield-courier/seed-bank-opens",
                (await db.Set<GraphRagDocument>().SingleAsync(d => d.Published == "2026-03-14")).Url);

            // Answers cite the articles' own bytes, and a question they don't cover is refused.
            var answers = provider.GetRequiredService<IGraphRagAnswers>();
            var answer = await answers.AnswerAsync("Who cut the ribbon at the seed bank opening?");
            Assert.Equal(GraphRagAnswerDisposition.Answered, answer.Disposition);
            Assert.All(answer.Citations, c => Assert.Equal(c.Quote, Encoding.UTF8.GetString(
                File.ReadAllBytes(sources.Single(s => s.SourceUri == receipt.Documents.Single(d => d.DocumentId == c.DocumentId).SourceUri).Path!),
                c.StartByte, c.EndByte - c.StartByte)));
            Assert.Contains(answer.Citations, c => c.Quote.Contains("Tomas Reyes cut the ribbon", StringComparison.Ordinal));
            Assert.Equal(GraphRagAnswerDisposition.InsufficientEvidence, (await answers.AnswerAsync("What is the capital of Mongolia?")).Disposition);
        }
        finally
        {
            await maintenance.ResetAsync(new GraphRagResetScope());
            await maintenance.RemoveOrphansAsync();
            await Execute("MATCH (n:ArticleRun) DELETE n");
        }
        Assert.Equal(0, await Count("MATCH (n) WHERE n:NewsPerson OR n:NewsOrganization OR n:NewsPlace OR n:NewsEvent OR n:NewsConcept OR n:NewsTopic RETURN count(n) AS value"));
    }

    [Neo4jIntegrationFact]
    public async Task The_articles_sample_reads_the_NASA_excerpts_and_its_agent_answers_through_the_toolkit_s_tool()
    {
        // GR-58, offline: the shipped NASA excerpts with their own metadata, and the agent command with the stand-in model.
        Assert.Null(Articles.Models);
        await using var services = new ServiceCollection()
            .AddDbContext<ArticlesContext>(options => options.UseNeo4j(fixture.Driver, fixture.Database))
            .AddArticles()
            .BuildServiceProvider();
        await using var scope = services.CreateAsyncScope();
        var provider = scope.ServiceProvider;
        var db = provider.GetRequiredService<ArticlesContext>();
        await db.Database.InitializeGraphMigrationsAsync();
        await db.Database.ApplyGraphMigrationAsync(Articles.SchemaPlan);
        var maintenance = provider.GetRequiredService<IGraphRagMaintenance>();
        try
        {
            await maintenance.ResetAsync(new GraphRagResetScope());
            await maintenance.RemoveOrphansAsync();
            var sources = Articles.Sources(Path.Combine(AppContext.BaseDirectory, "data", "nasa", "articles"));
            var receipt = await provider.GetRequiredService<IGraphRagIngestion>().IngestAsync(sources);
            Assert.Equal(5, receipt.Documents.Count);
            Assert.All(receipt.Documents, d => Assert.Equal(GraphRagDocumentStatus.Ingested, d.Status));

            var report = await Articles.ReportAsync(db);
            Assert.Equal(["2026-04-01", "2026-04-02", "2026-04-06", "2026-04-07", "2026-04-10"], report.Articles.Select(a => a.Published));
            Assert.All(report.Articles, a => Assert.Equal("NASA", a.Publisher));
            Assert.Equal("https://www.nasa.gov/news-release/nasa-welcomes-record-setting-artemis-ii-moonfarers-back-to-earth/",
                (await db.Set<GraphRagDocument>().SingleAsync(d => d.Published == "2026-04-10")).Url);

            var (text, calls) = await Articles.AskAgentAsync(provider, "How far from Earth did the Artemis II astronauts travel when they broke the record?");
            var answer = calls.Single(c => c.Tool == GraphRagTools.AnswerTool);
            Assert.NotEmpty(answer.Evidence);
            var files = receipt.Documents.ToDictionary(d => d.DocumentId, d => File.ReadAllBytes(sources.Single(s => s.SourceUri == d.SourceUri).Path!));
            // The stand-in picks the evidence line sharing most words with the question; whichever it is, it is a span of a release.
            foreach (var span in answer.Evidence)
                Assert.False(string.IsNullOrWhiteSpace(Encoding.UTF8.GetString(files[span.DocumentId!], span.StartByte!.Value, span.EndByte!.Value - span.StartByte.Value)));
            Assert.StartsWith("From the documents: ", text, StringComparison.Ordinal);
        }
        finally
        {
            await maintenance.ResetAsync(new GraphRagResetScope());
            await maintenance.RemoveOrphansAsync();
            await Execute("MATCH (n:ArticleRun) DELETE n");
        }
    }

    private sealed class ArticleLineComparer : IEqualityComparer<ArticleLine>
    {
        public bool Equals(ArticleLine? x, ArticleLine? y)
            => x is not null && y is not null && (x.Title, x.Published, x.Publisher) == (y.Title, y.Published, y.Publisher)
                && x.Authors.SequenceEqual(y.Authors) && x.Topics.SequenceEqual(y.Topics);

        public int GetHashCode(ArticleLine obj) => obj.Title.GetHashCode(StringComparison.Ordinal);
    }

    private async Task Execute(string text)
    {
        await using var session = fixture.OpenSession();
        await (await session.RunAsync(text)).ConsumeAsync();
    }

    private async Task<int> Count(string text)
    {
        await using var session = fixture.OpenSession();
        return (int)(await (await session.RunAsync(text)).SingleAsync())["value"].As<long>();
    }

    private async Task ResetJournal()
    {
        foreach (var label in new[] { GraphMigrationJournal.StepLabel, GraphMigrationJournal.HistoryLabel, GraphMigrationJournal.ControlLabel })
            await Execute($"MATCH (n:{label}) DELETE n");
        foreach (var name in new[] { GraphMigrationJournal.ControlConstraint, GraphMigrationJournal.HistoryConstraint, GraphMigrationJournal.StepConstraint })
            await Execute($"DROP CONSTRAINT {name} IF EXISTS");
    }
}
