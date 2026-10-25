using System.Text;
using GraphBridge.EntityFrameworkCore.Migrations;
using GraphBridge.GraphRag;
using GraphBridge.Samples.GraphRagShared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Neo4j.Driver;

namespace GraphBridge.Workshop.Lessons;

public sealed class Lesson31CitedAnswers : ILesson
{
    public LessonInfo Info { get; } = new("31", 'G', "From documents to cited answers", "31-CitedAnswers", LessonMode.Mixed, Core: false,
        Requirement.Guarded, "Store two short documents with exact chunk positions, then answer a question with a citation checked against the stored bytes, and refuse one the documents don't answer.");

    // The lesson's own labels and schema names, so it never touches the movie graph.
    public static GraphRagLayout Layout { get; } = GraphRagLayout.Default with
    {
        DocumentLabel = "WorkshopDocument", SectionLabel = "WorkshopSection", ChunkLabel = "WorkshopChunk",
        RunLabel = "WorkshopIngestionRun", LeaseLabel = "WorkshopLease", SchemaPrefix = "workshop_rag"
    };

    private static readonly GraphRagEmbeddingSet EmbeddingSet = new("workshop-hashing", 64);

    // Line breaks are normalised, so positions are the same however the file was checked out.
    private static readonly string Notes = """
        # The Matrix

        The Matrix was released in 1999. Keanu Reeves plays Neo, a programmer who learns that the world is a simulation.

        # Cloud Atlas

        Cloud Atlas tells six stories across five centuries. Tom Hanks and Hugo Weaving each play several roles.
        """.ReplaceLineEndings("\n");

    public async Task RunAsync(LessonContext lesson, CancellationToken cancellationToken)
    {
        lesson.Step("Chunk a document: every chunk is exactly its bytes");
        lesson.Code("Chunk");
        // workshop:start Chunk
        var bytes = Encoding.UTF8.GetBytes(Notes);
        var plan = GraphRagChunkers.Create(new GraphRagChunkerOptions { MaxUtf8Bytes = 160 })
            .Chunk(new GraphRagLoadedDocument("text:notes", "text/markdown", "Notes", bytes));
        // workshop:end Chunk
        lesson.Table(plan.Chunks.Select(c => new
        {
            c.Order, c.StartByte, c.EndByte, Section = c.SectionOrder is { } s ? plan.Sections[s].Title : "",
            Text = Encoding.UTF8.GetString(bytes, c.StartByte, c.EndByte - c.StartByte).Split('\n').First(l => l.Length > 0)
        }));
        lesson.Expect("two sections, one per heading", 2, plan.Sections.Count);
        lesson.Check("no chunk crosses a heading", plan.Chunks.All(c => c.SectionOrder is not null));

        lesson.Step("Check a quote the way answers are checked");
        lesson.Code("Locate");
        // workshop:start Locate
        var chunk = new GraphRagEvidenceItem("c", "d", "Tom Hanks and Hugo Weaving each play several roles.", 0, 52, 1, []);
        var found = GraphRagCitationVerifier.Locate(chunk, "Hugo   Weaving each play...roles");
        var invented = GraphRagCitationVerifier.Locate(chunk, "Hugo Weaving plays one role");
        // workshop:end Locate
        lesson.Expect("whitespace and an elision are forgiven; the citation records the stored text",
            "Hugo Weaving each play several roles", found?.Quote);
        lesson.Check("words that aren't there are never found", invented is null);

        lesson.Step("The schema the toolkit needs, as reviewed steps");
        foreach (var step in GraphRagSchema.CreateSteps(Layout, EmbeddingSet)) lesson.Note(GraphMigrationStepRenderer.Render(step).Cypher.Split('\n')[0]);
        lesson.Note("In an application these steps go in a migration plan of its own (lesson 23); the toolkit never creates schema itself.");

        if (!lesson.NeedsDatabase("ingestion and answers")) return;
        var connection = lesson.Connection!;
        var version = (await WorkshopData.ReadAsync(connection,
            "CALL dbms.components() YIELD name, versions WHERE name = 'Neo4j Kernel' RETURN versions[0] AS version",
            r => r["version"].As<string>(), cancellationToken)).Single();
        if (!Version.TryParse(version.Split('-')[0], out var parsed) || parsed < new Version(2026, 9))
        {
            lesson.Skip($"Neo4j {version} can't run the toolkit's retrieval, which needs Neo4j 2026.09 or later with Cypher 25");
            return;
        }

        await using var services = new ServiceCollection()
            .AddDbContext<RagContext>(o => o.UseNeo4j(lesson.Driver, lesson.Database))
            .AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(new HashingEmbedder(EmbeddingSet.Dimensions))
            .AddSingleton<IChatClient>(new ExtractiveChatClient())
            .AddGraphRag<RagContext>(EmbeddingSet, o => o.Chunking.MaxUtf8Bytes = 160)
            .BuildServiceProvider();
        try
        {
            foreach (var step in GraphRagSchema.CreateSteps(Layout, EmbeddingSet))
                await WorkshopData.ExecuteAsync(connection, GraphMigrationStepRenderer.Render(step).Cypher, cancellationToken);
            foreach (var index in GraphRagSchema.Definitions(Layout, EmbeddingSet).Where(d => d.IsIndex))
                await WorkshopData.ExecuteAsync(connection, "CALL db.awaitIndex($index, 60)", cancellationToken, ("index", index.Name));

            lesson.Step("Ingest: one transaction per document, unchanged documents skipped");
            lesson.Code("Ingest");
            // workshop:start Ingest
            var ingestion = services.GetRequiredService<IGraphRagIngestion>();
            var receipt = await ingestion.IngestAsync([GraphRagSources.Text("notes", Notes, "text/markdown")], cancellationToken);
            var again = await ingestion.IngestAsync([GraphRagSources.Text("notes", Notes, "text/markdown")], cancellationToken);
            // workshop:end Ingest
            lesson.Expect("the document is written", GraphRagDocumentStatus.Ingested, receipt.Documents.Single().Status);
            lesson.Expect("and skipped the second time", GraphRagDocumentStatus.Unchanged, again.Documents.Single().Status);
            lesson.Value("Chunks written", receipt.ChunksWritten);

            lesson.Step("Answer with a citation checked against the stored bytes");
            lesson.Code("Answer");
            // workshop:start Answer
            var answers = services.GetRequiredService<IGraphRagAnswers>();
            var answer = await answers.AnswerAsync("Which roles do Tom Hanks and Hugo Weaving play?", cancellationToken: cancellationToken);
            var refusal = await answers.AnswerAsync("Who directed Jaws?", cancellationToken: cancellationToken);
            // workshop:end Answer
            lesson.Expect("the question is answered", GraphRagAnswerDisposition.Answered, answer.Disposition);
            var citation = answer.Citations.Single();
            lesson.Value("Citation", $"bytes {citation.StartByte}-{citation.EndByte}: \"{citation.Quote}\"");
            lesson.Expect("the quote is exactly the stored bytes at its span", citation.Quote,
                Encoding.UTF8.GetString(bytes, citation.StartByte, citation.EndByte - citation.StartByte));
            lesson.Expect("a question the notes don't answer is refused", GraphRagAnswerDisposition.InsufficientEvidence, refusal.Disposition);
            lesson.Value("Refusal reason", refusal.Reason);
            lesson.Note("The extractive chat client stands in for a model: it quotes the evidence line that shares the most words with the question.");
        }
        finally
        {
            // Remove everything the lesson wrote, even when canceled: documents, chunks, runs, leases and its schema.
            await services.GetRequiredService<IGraphRagMaintenance>().ResetAsync(new GraphRagResetScope(), CancellationToken.None);
            await WorkshopData.ExecuteAsync(connection, $"MATCH (n) WHERE n:{Layout.RunLabel} OR n:{Layout.LeaseLabel} DELETE n", CancellationToken.None);
            foreach (var definition in GraphRagSchema.Definitions(Layout, EmbeddingSet))
                await WorkshopData.ExecuteAsync(connection, $"DROP {(definition.IsIndex ? "INDEX" : "CONSTRAINT")} {definition.Name} IF EXISTS", CancellationToken.None);
        }
        lesson.Limit("The toolkit is experimental (GBRAG001). Citations prove that the stored text says something, not that it is true; see the GraphRAG pages of the docs.");
    }

    public sealed class RagContext(DbContextOptions<RagContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.AddGraphRag(EmbeddingSet.Dimensions, o => o.Layout = Layout);
    }
}
