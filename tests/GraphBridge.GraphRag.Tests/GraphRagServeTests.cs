using System.Text;
using System.Text.Json;
using GraphBridge.EntityFrameworkCore.Migrations;
using GraphBridge.GraphRag.AgentFramework;
using GraphBridge.GraphRag.DataIngestion;
using Microsoft.Agents.AI;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DataIngestion;
using Microsoft.Extensions.DependencyInjection;
using Neo4j.Driver;

namespace GraphBridge.GraphRag.Tests;

/// <summary>Wave 5 (GR-13, GR-41, GR-42, GR-37), offline: agent tools, chunked documents, span recovery, the context provider and the re-embedding plan.</summary>
public sealed class GraphRagServeTests
{
    private const string Text = "# Graphs\n\nGraphs connect facts. Ünïcödé 🌍.\n\nA node holds properties.";

    [Fact]
    public async Task The_document_tools_answer_with_checked_citations_search_with_spans_and_record_a_trail()
    {
        var trail = new GraphRagToolTrail();
        await using var services = Services<ToolsContext>();
        var tools = GraphRagTools.Create<ToolsContext>(services, new GraphRagToolOptions { Trail = trail });
        Assert.Equal([GraphRagTools.AnswerTool, GraphRagTools.SearchTool], tools.Select(t => t.Name));
        Assert.Contains("question", tools[0].JsonSchema.GetRawText());

        var answer = Read<GraphRagToolAnswer>(await tools[0].InvokeAsync(new AIFunctionArguments { ["question"] = "What do graphs connect?" }));
        Assert.True(answer.Answered);
        Assert.Equal(("doc-1", "chunk-1", 10, 32, "Graphs connect facts."), (answer.Citations[0].DocumentId, answer.Citations[0].ChunkId,
            answer.Citations[0].StartByte, answer.Citations[0].EndByte, answer.Citations[0].Quote));

        var passages = Read<GraphRagToolPassages>(await tools[1].InvokeAsync(new AIFunctionArguments { ["query"] = "graphs", ["limit"] = 99 }));
        Assert.Equal(("E1", "chunk-1", 10, 60), (passages.Passages[0].Evidence, passages.Passages[0].ChunkId, passages.Passages[0].StartByte, passages.Passages[0].EndByte));
        Assert.Equal(20, services.GetRequiredService<Fakes>().LastMaxItems);

        Assert.Equal([GraphRagTools.AnswerTool, GraphRagTools.SearchTool], trail.Calls.Select(c => c.Tool));
        Assert.Equal("What do graphs connect?", trail.Calls[0].Arguments["question"]);
        Assert.Equal(new GraphRagToolEvidence("doc-1", "chunk-1", 10, 32, null), trail.Calls[0].Evidence.Single());
        Assert.Equal("20", trail.Calls[1].Arguments["limit"]);

        // With an ontology the tools over extracted things join them.
        await using var withOntology = Services<ToolsOntologyContext>();
        Assert.Equal([GraphRagTools.AnswerTool, GraphRagTools.SearchTool, GraphRagTools.FindThingsTool, GraphRagTools.RelatedFactsTool],
            GraphRagTools.Create<ToolsOntologyContext>(withOntology).Select(t => t.Name));
    }

    [Fact]
    public async Task An_agent_answers_through_the_context_provider_with_the_toolkits_checked_citations()
    {
        await using var services = Services<ToolsContext>();
        var provider = GraphRagContextProvider.Create<ToolsContext>(services);
        var chat = new AgentChat("What do graphs connect?");
        var agent = new ChatClientAgent(chat, new ChatClientAgentOptions { AIContextProviders = [provider] });
        var response = await agent.RunAsync("What do graphs connect?");
        Assert.Contains("Graphs connect facts.", response.Text);
        Assert.Contains(GraphRagTools.AnswerTool, chat.ToolNames);
        Assert.Contains("answer_from_documents", chat.Instructions);
        Assert.Equal(1, services.GetRequiredService<Fakes>().Answers);
        Assert.Throws<ArgumentException>(() => new GraphRagContextProvider([]));
    }

    [Fact]
    public void A_chunked_document_turns_character_spans_into_byte_spans_and_finds_its_sections()
    {
        var spans = new[] { (10, 43), (45, 69) };
        var document = GraphRagChunkedDocument.Create(GraphRagSources.Text("guide", Text, "text/markdown"), Text, spans, "test-chunker", "Graphs");
        var bytes = Encoding.UTF8.GetBytes(Text);
        Assert.Equal(bytes, document.Utf8.ToArray());
        Assert.Equal(["Graphs connect facts. Ünïcödé 🌍.", "A node holds properties."],
            document.Plan.Chunks.Select(c => Encoding.UTF8.GetString(bytes, c.StartByte, c.EndByte - c.StartByte)));
        Assert.Equal(("Graphs", 1), (document.Plan.Sections.Single().Title, document.Plan.Sections.Single().Level));
        Assert.All(document.Plan.Chunks, c => Assert.Equal(0, c.SectionOrder));
        Assert.Equal("test-chunker", document.Plan.Fingerprint);

        var source = GraphRagSources.Text("guide", Text, "text/markdown");
        Assert.Throws<ArgumentException>(() => GraphRagChunkedDocument.Create(source, Text, [(10, 999)], "c"));
        Assert.Throws<ArgumentException>(() => GraphRagChunkedDocument.Create(source, Text, [(45, 69), (10, 43)], "c"));
        Assert.Throws<ArgumentException>(() => GraphRagChunkedDocument.Create(source, Text, [(10, 41)], "c"));   // ends inside the emoji
        Assert.Throws<ArgumentException>(() => GraphRagChunkedDocument.Create(source, Text, [], "c"));
    }

    [Fact]
    public async Task Pipeline_chunks_are_placed_byte_exact_or_with_whitespace_normalised_and_an_unplaceable_one_fails_its_document()
    {
        var document = Document();
        Assert.Equal(Text, GraphRagIngestionText.Of(document));
        Assert.Equal("Graphs", GraphRagIngestionText.TitleOf(document));
        // A header chunker's shape: the heading path, a line break, then the elements joined by single line breaks (spike d).
        IngestionChunk<string>[] chunks =
        [
            new("# Graphs\nGraphs connect facts. Ünïcödé 🌍.", document, "# Graphs"),
            new("# Graphs\nGraphs connect facts. Ünïcödé 🌍.\nA node holds properties.", document, "# Graphs")
        ];
        var spans = SpanRecovery.Place(Text, chunks, document.Identifier);
        Assert.Equal(["Graphs connect facts. Ünïcödé 🌍.", "Graphs connect facts. Ünïcödé 🌍.\n\nA node holds properties."], spans.Select(s => Text[s.Start..s.End]));
        var error = Assert.Throws<GraphRagChunkPlacementException>(() => SpanRecovery.Place(Text, [new IngestionChunk<string>("Not in the text.", document)], document.Identifier));
        Assert.Equal(("position-not-recovered", 0), (error.ErrorCode, error.Chunk));

        var ingestion = new CapturingIngestion();
        var writer = new GraphBridgeChunkWriter(ingestion, new GraphBridgeChunkWriterOptions { ChunkerName = "HeaderChunker;30" });
        await writer.WriteAsync(chunks.ToAsyncEnumerable());
        var written = Assert.Single(ingestion.Documents);
        Assert.Equal(("text:guide.md", "Graphs", GraphBridgeChunkWriter.Version + ";HeaderChunker;30"), (written.Source.SourceUri, written.Title, written.Plan.Fingerprint));
        Assert.Equal(2, written.Plan.Chunks.Count);
        Assert.Single(writer.Receipts);

        ingestion.Fail = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => writer.WriteAsync(chunks.ToAsyncEnumerable()));
    }

    [Fact]
    public async Task The_span_chunker_records_each_chunks_exact_span_and_the_writer_uses_it()
    {
        var document = Document();
        var chunker = new GraphRagSpanChunker(new GraphRagChunkerOptions { MaxUtf8Bytes = 64, OverlapParagraphs = 0 });
        var chunks = await chunker.ProcessAsync(document).ToListAsync();
        Assert.True(chunks.Count >= 2);
        foreach (var chunk in chunks)
        {
            var (start, end) = ((int)chunk.Metadata[GraphRagIngestionText.StartKey], (int)chunk.Metadata[GraphRagIngestionText.EndKey]);
            Assert.Equal(chunk.Content, Text[start..end]);
            Assert.Equal(chunker.Fingerprint, chunk.Metadata[GraphRagIngestionText.ChunkerKey]);
        }
        var ingestion = new CapturingIngestion();
        await new GraphBridgeChunkWriter(ingestion).WriteAsync(chunks.ToAsyncEnumerable());
        Assert.Equal(chunker.Fingerprint, Assert.Single(ingestion.Documents).Plan.Fingerprint);
    }

    [Fact]
    public void A_reembedding_plan_stages_the_new_set_on_its_own_property_and_index()
    {
        var from = new GraphRagEmbeddingSet("small", 8);
        var to = new GraphRagEmbeddingSet("large", 16);
        var plan = GraphRagReembedding.Plan(GraphRagLayout.Default, from, to, "EmbeddingNext");
        Assert.Equal("EmbeddingNext", plan.NewLayout.ChunkEmbeddingProperty);
        var prepare = GraphMigrationStepRenderer.Render(Assert.Single(plan.PrepareSteps)).Cypher;
        Assert.Contains($"CREATE VECTOR INDEX `{to.VectorIndexName}`", prepare);
        Assert.Contains("(n.`EmbeddingNext`)", prepare);
        Assert.Contains("`vector.dimensions`: 16", prepare);
        Assert.StartsWith($"DROP INDEX `{from.VectorIndexName}`", GraphMigrationStepRenderer.Render(Assert.Single(plan.CleanupSteps)).Cypher);
        // After cut-over, the schema of the new layout and set is exactly the old one plus the prepared index.
        Assert.Contains(GraphRagSchema.Definitions(plan.NewLayout, to), d => d.Name == to.VectorIndexName && d.Properties.Single() == "EmbeddingNext");

        Assert.Throws<ArgumentException>(() => GraphRagReembedding.Plan(GraphRagLayout.Default, from, from, "EmbeddingNext"));
        Assert.Throws<ArgumentException>(() => GraphRagReembedding.Plan(GraphRagLayout.Default, from, to, "Text"));
        Assert.Throws<ArgumentException>(() => GraphRagReembedding.Plan(GraphRagLayout.Default, from, to, "Embedding Next"));
    }

    private static IngestionDocument Document()
    {
        var document = new IngestionDocument("guide.md");
        var section = new IngestionDocumentSection();
        section.Elements.Add(new IngestionDocumentHeader("# Graphs") { Level = 1 });
        section.Elements.Add(new IngestionDocumentParagraph("Graphs connect facts. Ünïcödé 🌍."));
        section.Elements.Add(new IngestionDocumentParagraph("A node holds properties."));
        document.Sections.Add(section);
        return document;
    }

    private static T Read<T>(object? result)
        => result is JsonElement json ? json.Deserialize<T>(new JsonSerializerOptions(JsonSerializerDefaults.Web))! : (T)result!;

    private static ServiceProvider Services<TContext>() where TContext : DbContext
    {
        var fakes = new Fakes();
        return new ServiceCollection()
            .AddDbContext<TContext>(o => o.UseNeo4j(GraphDatabase.Driver("neo4j://localhost:7687", AuthTokens.None), "neo4j"))
            .AddSingleton(fakes).AddSingleton<IGraphRagAnswers>(fakes).AddSingleton<IGraphRagRetriever>(fakes)
            .BuildServiceProvider();
    }

    public sealed class ToolsContext(DbContextOptions<ToolsContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.AddGraphRag(8);
    }

    public sealed class ToolsOntologyContext(DbContextOptions<ToolsOntologyContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.AddGraphRag(8, o => o.Ontology = GraphRagOntologies.Novel);
    }

    internal sealed class Fakes : IGraphRagAnswers, IGraphRagRetriever
    {
        private static readonly GraphRagEvidenceItem Item = new("chunk-1", "doc-1", "Graphs connect facts. Ünïcödé 🌍.", 10, 60, 1, []);
        public int Answers;
        public int LastMaxItems;

        public Task<GraphRagAnswer> AnswerAsync(string question, GraphRagRetrievalOptions? options = null, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Answers);
            var evidence = Evidence(question);
            return Task.FromResult(new GraphRagAnswer(GraphRagAnswerDisposition.Answered, "Graphs connect facts.",
                [new GraphRagCitation("doc-1", "chunk-1", 10, 32, "Graphs connect facts.")], null, evidence));
        }

        public Task<GraphRagEvidence> RetrieveAsync(string question, GraphRagRetrievalOptions? options = null, CancellationToken cancellationToken = default)
        {
            LastMaxItems = options?.MaxItems ?? 0;
            return Task.FromResult(Evidence(question));
        }

        private static GraphRagEvidence Evidence(string question) => new(question, [Item], [], 40, 0, [], new GraphRagProvenance("2026.09.0", "set", [], "COSINE"));
    }

    private sealed class CapturingIngestion : IGraphRagIngestion
    {
        public List<GraphRagChunkedDocument> Documents { get; } = [];
        public bool Fail { get; set; }

        public Task<GraphRagIngestionReceipt> IngestAsync(IEnumerable<GraphRagSource> sources, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<GraphRagIngestionReceipt> IngestChunkedAsync(IEnumerable<GraphRagChunkedDocument> documents, CancellationToken cancellationToken = default)
        {
            var list = documents.ToList();
            Documents.AddRange(list);
            var outcomes = list.Select(d => new GraphRagDocumentOutcome("id", d.Source.SourceUri, Fail ? GraphRagDocumentStatus.Failed : GraphRagDocumentStatus.Ingested,
                Fail ? "write-failed" : null)).ToArray();
            return Task.FromResult(new GraphRagIngestionReceipt("run", outcomes, 2, 1, null, TimeSpan.Zero, []));
        }
    }

    // A chat model that calls answer_from_documents once, then repeats what the tool returned.
    private sealed class AgentChat(string question) : IChatClient
    {
        public List<string> ToolNames { get; } = [];
        public string Instructions { get; private set; } = "";

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var list = messages.ToList();
            ToolNames.AddRange(options?.Tools?.Select(t => t.Name) ?? []);
            Instructions += (options?.Instructions ?? "") + string.Join("\n", list.Where(m => m.Role == ChatRole.System).Select(m => m.Text));
            var result = list.SelectMany(m => m.Contents).OfType<FunctionResultContent>().LastOrDefault();
            if (result is null)
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                    [new FunctionCallContent("call-1", GraphRagTools.AnswerTool, new Dictionary<string, object?> { ["question"] = question })])));
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "From the documents: " + result.Result)));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
