using System.IO.Compression;
using System.Text;
using GraphBridge.EntityFrameworkCore.Migrations;
using GraphBridge.GraphRag.DataIngestion;
using GraphBridge.IntegrationTests;
using GraphBridge.Samples.GraphRagShared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DataIngestion;
using Microsoft.Extensions.DependencyInjection;

namespace GraphBridge.GraphRag.InteropTests;

/// <summary>
/// GR-26, the Office-document recipe (docs/graphrag/AGENTS.md): Word files read by Microsoft's markitdown tool through
/// DataIngestion's <see cref="MarkItDownReader"/>, written by <see cref="GraphBridgeChunkWriter"/> with the text's origin, then
/// retrieved and answered with citations of the converted text. Opt-in: it needs the tool (tests/interop/markitdown).
/// </summary>
[Collection(Neo4jIntegrationCollection.Name)]
public sealed class MarkItDownRecipeTests(DisposableNeo4jFixture fixture) : IAsyncLifetime
{
    private const int Dimensions = 64;
    private const string Origin = "markitdown 0.1.9";
    private static readonly GraphRagEmbeddingSet Embeddings = new("graphbridge-sample-hashing", Dimensions);
    private static readonly string Suffix = Guid.NewGuid().ToString("N")[..10];
    internal static readonly GraphRagLayout Office = GraphRagLayout.Default with
    {
        DocumentLabel = $"ODocument_{Suffix}", SectionLabel = $"OSection_{Suffix}", ChunkLabel = $"OChunk_{Suffix}",
        RunLabel = $"ORun_{Suffix}", LeaseLabel = $"OLease_{Suffix}", SchemaPrefix = $"gbo_{Suffix}"
    };
    private readonly string _folder = Directory.CreateTempSubdirectory("graphbridge-markitdown-").FullName;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        Directory.Delete(_folder, recursive: true);
        if (!MarkItDownFactAttribute.Enabled) return;
        var labels = new[] { Office.DocumentLabel, Office.SectionLabel, Office.ChunkLabel, Office.RunLabel, Office.LeaseLabel }.Select(l => "n:" + l);
        await Execute($"MATCH (n)-[r]-() WHERE {string.Join(" OR ", labels)} DELETE r");
        await Execute($"MATCH (n) WHERE {string.Join(" OR ", labels)} DELETE n");
        foreach (var definition in GraphRagSchema.Definitions(Office, Embeddings))
            await Execute($"DROP {(definition.IsIndex ? "INDEX" : "CONSTRAINT")} `{definition.Name}` IF EXISTS");
    }

    [MarkItDownFact]
    public async Task Word_files_read_by_markitdown_are_ingested_retrieved_and_cited_as_derived_text()
    {
        foreach (var step in GraphRagSchema.CreateSteps(Office, Embeddings)) await Execute(GraphMigrationStepRenderer.Render(step).Cypher);
        await Execute("CALL db.awaitIndexes(60)");
        WriteDocx("engines.docx", "Engines", "The rocket engine burns liquid hydrogen and liquid oxygen.",
            "Engineers fired the engine on a test stand for eight minutes before the flight.");
        WriteDocx("gardens.docx", "Gardens", "Tomatoes need sun and water.", "The gardener planted beans along the fence.");

        await using var provider = new ServiceCollection()
            .AddDbContext<OfficeContext>(o => o.UseNeo4j(fixture.Driver, fixture.Database))
            .AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(new HashingEmbedder(Dimensions))
            .AddSingleton<IChatClient>(new ExtractiveChatClient())
            .AddGraphRag<OfficeContext>(Embeddings, o => o.Retrieval.NeighbourChunks = 0)
            .BuildServiceProvider();
        var reader = new MarkItDownReader(new FileInfo(MarkItDownFactAttribute.Executable!));
        var writer = new GraphBridgeChunkWriter(provider.GetRequiredService<IGraphRagIngestion>(), new GraphBridgeChunkWriterOptions { TextOrigin = Origin });
        using (var pipeline = new IngestionPipeline<string>(reader, new GraphRagSpanChunker(), writer))
            await foreach (var result in pipeline.ProcessAsync(new DirectoryInfo(_folder), "*.docx"))
                Assert.True(result.Succeeded, result.Exception?.ToString());
        Assert.Equal(2, writer.Receipts.Count);
        Assert.All(writer.Receipts, r => Assert.Equal(GraphRagDocumentStatus.Ingested, Assert.Single(r.Documents).Status));

        // The stored text is markitdown's Markdown, with the document's origin.
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OfficeContext>();
            var documents = await db.Set<GraphRagDocument>().ToListAsync();
            Assert.All(documents, d => Assert.Equal(Origin, d.TextOrigin));
            var engines = documents.Single(d => d.Title is "Engines" or "engines");
            var text = string.Concat((await db.Set<GraphRagChunk>().Where(c => c.DocumentId == engines.Id).OrderBy(c => c.Order).ToListAsync()).Select(c => c.Text));
            Assert.StartsWith("# Engines", text, StringComparison.Ordinal);
            Assert.Contains("The rocket engine burns liquid hydrogen and liquid oxygen.", text, StringComparison.Ordinal);
        }

        var answer = await provider.GetRequiredService<IGraphRagAnswers>().AnswerAsync("What does the rocket engine burn?");
        Assert.Equal(GraphRagAnswerDisposition.Answered, answer.Disposition);
        Assert.All(answer.Evidence.Items, i => Assert.Equal(Origin, i.TextOrigin));
        var citation = Assert.Single(answer.Citations);
        Assert.Equal(Origin, citation.TextOrigin);
        Assert.Contains("liquid hydrogen", citation.Quote, StringComparison.Ordinal);
    }

    // A Word file with a heading and paragraphs, built here so the test needs no downloaded document.
    private void WriteDocx(string name, string heading, params string[] paragraphs)
    {
        const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        static string Escape(string text) => System.Security.SecurityElement.Escape(text);
        var body = $"<w:p><w:pPr><w:pStyle w:val=\"Heading1\"/></w:pPr><w:r><w:t>{Escape(heading)}</w:t></w:r></w:p>"
            + string.Concat(paragraphs.Select(p => $"<w:p><w:r><w:t>{Escape(p)}</w:t></w:r></w:p>"));
        var parts = new Dictionary<string, string>
        {
            ["[Content_Types].xml"] = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/><Override PartName=\"/word/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml\"/></Types>",
            ["_rels/.rels"] = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/></Relationships>",
            ["word/_rels/document.xml.rels"] = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/></Relationships>",
            ["word/styles.xml"] = $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:styles xmlns:w=\"{W}\"><w:style w:type=\"paragraph\" w:styleId=\"Heading1\"><w:name w:val=\"heading 1\"/></w:style></w:styles>",
            ["word/document.xml"] = $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:document xmlns:w=\"{W}\"><w:body>{body}</w:body></w:document>"
        };
        using var zip = ZipFile.Open(Path.Combine(_folder, name), ZipArchiveMode.Create);
        foreach (var (path, text) in parts)
        {
            using var stream = zip.CreateEntry(path).Open();
            stream.Write(Encoding.UTF8.GetBytes(text));
        }
    }

    private async Task Execute(string cypher)
    {
        await using var session = fixture.OpenSession();
        await (await session.RunAsync(cypher)).ConsumeAsync();
    }

    public sealed class OfficeContext(DbContextOptions<OfficeContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.AddGraphRag(Dimensions, o => o.Layout = Office);
    }
}

/// <summary>Runs with the disposable fixture and GRAPHBRIDGE_MARKITDOWN set to the markitdown executable (tests/interop/markitdown/README.md).</summary>
public sealed class MarkItDownFactAttribute : FactAttribute
{
    public MarkItDownFactAttribute()
    {
        if (!Enabled) Skip = "Set GRAPHBRIDGE_INTEGRATION=1 with the fixture, and GRAPHBRIDGE_MARKITDOWN to the markitdown executable of tests/interop/markitdown's environment.";
    }

    public static string? Executable => Environment.GetEnvironmentVariable("GRAPHBRIDGE_MARKITDOWN");
    public static bool Enabled => Environment.GetEnvironmentVariable("GRAPHBRIDGE_INTEGRATION") == "1" && File.Exists(Executable);
}
