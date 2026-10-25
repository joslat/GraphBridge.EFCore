using System.Text;
using System.Text.Json;
using GraphBridge.EntityFrameworkCore.Migrations;
using GraphBridge.GraphRag;
using GraphBridge.GraphRag.AgentFramework;
using GraphBridge.GraphRag.DataIngestion;
using GraphBridge.GraphRag.Evaluation;
using GraphBridge.Samples.GraphRagShared;
using Microsoft.Agents.AI;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DataIngestion;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.ML.Tokenizers;
using Neo4j.Driver;

namespace GraphBridge.IntegrationTests;

// GraphRAG wave 5 acceptance (ADR 0052, section 8) on the disposable fixture: a DataIngestion pipeline, an agent through
// the context provider, the tools over extracted things, and a re-embedding plan. Each test owns its labels and schema prefix.
[Collection(Neo4jIntegrationCollection.Name)]
public sealed class GraphRagServeTests(DisposableNeo4jFixture fixture) : IAsyncLifetime
{
    private const string Markdown = """
        # Graphs — an overview 🌍

        Graphs connect facts. Ünïcödé text keeps its bytes, and a node holds properties that describe it.

        - Edges link nodes.
        - They have types.

        ## Indexes

        A vector index finds similar passages, and a full-text index finds words.
        """;

    private readonly string _suffix = Guid.NewGuid().ToString("N")[..12];
    private readonly List<string> _files = [];
    private readonly GraphRagEmbeddingSet _set = new("fake-hash-8", 8);
    private GraphRagLayout Layout => GraphRagLayout.Default with
    {
        DocumentLabel = "SDocument_" + _suffix, SectionLabel = "SSection_" + _suffix, ChunkLabel = "SChunk_" + _suffix,
        RunLabel = "SRun_" + _suffix, LeaseLabel = "SLease_" + _suffix, SchemaPrefix = "gbs_" + _suffix
    };
    private GraphRagOntology? _ontology;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var file in _files) File.Delete(file);
        if (!Enabled) return;
        var labels = new[] { Layout.DocumentLabel, Layout.SectionLabel, Layout.ChunkLabel, Layout.RunLabel, Layout.LeaseLabel }
            .Concat(_ontology?.Classes.Select(c => c.Label) ?? []).Select(l => "n:" + l);
        await Execute($"MATCH (n)-[r]-() WHERE {string.Join(" OR ", labels)} DELETE r");
        await Execute($"MATCH (n) WHERE {string.Join(" OR ", labels)} DELETE n");
        var set4 = new GraphRagEmbeddingSet("fake-hash-4", 4);
        foreach (var definition in GraphRagSchema.Definitions(Layout, _set, _ontology).Concat(GraphRagSchema.Definitions(Layout with { ChunkEmbeddingProperty = "EmbeddingNext" }, set4)))
            await Execute($"DROP {(definition.IsIndex ? "INDEX" : "CONSTRAINT")} {definition.Name} IF EXISTS");
    }

    private static bool Enabled => Environment.GetEnvironmentVariable("GRAPHBRIDGE_INTEGRATION") == "1";

    [Neo4jIntegrationFact]
    public async Task DataIngestion_pipelines_write_positioned_chunks_with_a_header_chunker_and_with_the_span_chunker()
    {
        await ProvisionAsync(Layout, _set);
        await using var provider = Services(Layout, _set, null, new HashGenerator(8));
        var tokenizer = TiktokenTokenizer.CreateForModel("gpt-4o");
        foreach (var (name, chunker) in new (string, IngestionChunker<string>)[]
                 {
                     ("header", new HeaderChunker(new IngestionChunkerOptions(tokenizer) { MaxTokensPerChunk = 30 })),
                     ("span", new GraphRagSpanChunker(new GraphRagChunkerOptions { MaxUtf8Bytes = 96 }))
                 })
        {
            var file = WriteFile(name);
            var writer = new GraphBridgeChunkWriter(provider.GetRequiredService<IGraphRagIngestion>(), new GraphBridgeChunkWriterOptions { ChunkerName = "HeaderChunker;30" });
            using (var pipeline = new IngestionPipeline<string>(new MarkdownReader(), chunker, writer))
                await foreach (var result in pipeline.ProcessAsync([new FileInfo(file)]))
                    Assert.True(result.Succeeded, result.Exception?.ToString());
            var receipt = Assert.Single(writer.Receipts);
            var outcome = Assert.Single(receipt.Documents);
            Assert.Equal(GraphRagDocumentStatus.Ingested, outcome.Status);
            Assert.True(receipt.ChunksWritten > 1, name);

            // The stored text is the reader's Markdown, and every chunk is exactly its span of it.
            var text = GraphRagIngestionText.Of(await new MarkdownReader().ReadAsync(new FileInfo(file)));
            var bytes = Encoding.UTF8.GetBytes(text);
            await using var db = Context(Layout, _set, null);
            var chunks = await db.Set<GraphRagChunk>().Where(c => c.DocumentId == outcome.DocumentId).OrderBy(c => c.Order).ToListAsync();
            Assert.Equal(receipt.ChunksWritten, chunks.Count);
            foreach (var chunk in chunks) Assert.Equal(Encoding.UTF8.GetString(bytes, chunk.StartByte, chunk.EndByte - chunk.StartByte), chunk.Text);
            Assert.Equal("Graphs — an overview 🌍", (await db.Set<GraphRagDocument>().SingleAsync(d => d.Id == outcome.DocumentId)).Title);

            // The same file again: skipped as unchanged.
            using (var again = new IngestionPipeline<string>(new MarkdownReader(), chunker, writer))
                await foreach (var result in again.ProcessAsync([new FileInfo(file)])) Assert.True(result.Succeeded);
            Assert.Equal(GraphRagDocumentStatus.Unchanged, writer.Receipts[^1].Documents.Single().Status);
        }
    }

    [Neo4jIntegrationFact]
    public async Task An_agent_answers_through_the_context_provider_with_citations_of_stored_bytes_and_refuses_what_the_documents_dont_say()
    {
        await ProvisionAsync(Layout, _set);
        await using var provider = Services(Layout, _set, null, new HashGenerator(8), new ExtractiveChatClient());
        var receipt = await provider.GetRequiredService<IGraphRagIngestion>().IngestAsync([GraphRagSources.Text("guide", Markdown, "text/markdown")]);
        Assert.Equal(GraphRagDocumentStatus.Ingested, Assert.Single(receipt.Documents).Status);
        var trail = new GraphRagToolTrail();
        var context = GraphRagContextProvider.Create<ServeContext>(provider, new GraphRagToolOptions { Trail = trail });

        var agent = new ChatClientAgent(new AgentChat("Which index finds similar passages?"), new ChatClientAgentOptions { AIContextProviders = [context] });
        var response = await agent.RunAsync("Which index finds similar passages?");
        var answer = trail.Calls.Single(c => c.Tool == GraphRagTools.AnswerTool);
        var citation = Assert.Single(answer.Evidence);
        var stored = Encoding.UTF8.GetBytes(Markdown.ReplaceLineEndings("\n"));
        var quote = Encoding.UTF8.GetString(stored, citation.StartByte!.Value, citation.EndByte!.Value - citation.StartByte.Value);
        Assert.Contains("vector index finds similar passages", quote);
        Assert.Contains("similar passages", response.Text);

        var refusing = new ChatClientAgent(new AgentChat("What is the capital of Mongolia?"), new ChatClientAgentOptions { AIContextProviders = [context] });
        var refusal = await refusing.RunAsync("What is the capital of Mongolia?");
        Assert.Contains("\"answered\":false", refusal.Text.Replace(" ", ""), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(trail.Calls.Last(c => c.Tool == GraphRagTools.AnswerTool).Evidence);
    }

    [Neo4jIntegrationFact]
    public async Task The_tools_find_extracted_things_by_name_and_list_their_facts_with_the_documents_that_state_them()
    {
        _ontology = GraphRagOntologies.Novel.WithLabelPrefix("S" + _suffix);
        await ProvisionAsync(Layout, _set, _ontology);
        var services = new ServiceCollection().AddKeyedSingleton<IChatClient>(GraphRagServiceKeys.ExtractionChatClient, new GraphRagExtractionTests.Replies());
        await using var provider = Services(Layout, _set, _ontology, new HashGenerator(8), services: services);
        var receipt = await provider.GetRequiredService<IGraphRagIngestion>().IngestAsync(
            [GraphRagSources.Text("first", GraphRagExtractionTests.First), GraphRagSources.Text("second", GraphRagExtractionTests.Second)]);
        Assert.All(receipt.Documents, d => Assert.Equal(GraphRagDocumentStatus.Ingested, d.Status));

        var tools = GraphRagTools.Create<ServeContext>(provider).ToDictionary(t => t.Name);
        var things = Read<GraphRagToolThing[]>(await tools[GraphRagTools.FindThingsTool].InvokeAsync(new AIFunctionArguments { ["name"] = "Rabbit", ["kind"] = "Character" }));
        var rabbit = Assert.Single(things);
        Assert.Equal(("White Rabbit", "Character", ["the Rabbit"]), (rabbit.Name, rabbit.Kind, rabbit.Aliases.ToArray()), new RabbitComparer());
        var facts = Read<GraphRagToolFact[]>(await tools[GraphRagTools.RelatedFactsTool].InvokeAsync(new AIFunctionArguments { ["thingId"] = rabbit.Id }));
        var fact = Assert.Single(facts);
        Assert.Equal(("RELATED_TO", "Alice", "White Rabbit", 2), (fact.Type, fact.FromName, fact.ToName, fact.DocumentIds.Count));
    }

    [Neo4jIntegrationFact]
    public async Task A_reembedding_plan_previews_prepares_backfills_cuts_over_and_cleans_up()
    {
        await ProvisionAsync(Layout, _set);
        await using (var before = Services(Layout, _set, null, new HashGenerator(8)))
            Assert.Equal(GraphRagDocumentStatus.Ingested, (await before.GetRequiredService<IGraphRagIngestion>()
                .IngestAsync([GraphRagSources.Text("guide", Markdown, "text/markdown")])).Documents.Single().Status);
        var to = new GraphRagEmbeddingSet("fake-hash-4", 4);
        var plan = GraphRagReembedding.Plan(Layout, _set, to, "EmbeddingNext");
        var chunkCount = 0;

        await using (var db = Context(Layout, _set, null))
        {
            var preview = await plan.PreviewAsync(db);
            chunkCount = preview.Chunks;
            Assert.True(preview.Chunks > 1);
            Assert.Equal((0, preview.Chunks), (preview.Backfilled, preview.Remaining));
            foreach (var step in plan.PrepareSteps) await Execute(GraphMigrationStepRenderer.Render(step).Cypher);
            await Execute("CALL db.awaitIndex($index, 60)", new { index = to.VectorIndexNameFor(Layout.SchemaPrefix) });
            Assert.Equal(2, await plan.BackfillAsync(db, new HashGenerator(4), batchSize: 2, maxChunks: 2));
            Assert.Equal(preview.Chunks - 2, await plan.BackfillAsync(db, new HashGenerator(4), batchSize: 2));
            Assert.Equal(0, (await plan.PreviewAsync(db)).Remaining);
            // Completing before cut-over would mark the vectors the old model still searches.
            await Assert.ThrowsAsync<InvalidOperationException>(() => plan.CompleteAsync(db));
        }

        await using var after = Services(plan.NewLayout, to, null, new HashGenerator(4));
        await using (var db = Context(plan.NewLayout, to, null))
        {
            Assert.True(await plan.CompleteAsync(db) > 1);
            var evidence = await after.GetRequiredService<IGraphRagRetriever>().RetrieveAsync("vector index similar passages", GraphRagRetrievers.Vector());
            Assert.NotEmpty(evidence.Items);
            Assert.Equal(to.Id, evidence.Provenance.EmbeddingSetId);
            var health = await after.GetRequiredService<IGraphRagMaintenance>().CheckAsync();
            Assert.DoesNotContain("mixed-embedding-sets", health.Findings);
            Assert.DoesNotContain("missing-embedding", health.Findings);

            foreach (var step in plan.CleanupSteps) await Execute(GraphMigrationStepRenderer.Render(step).Cypher);
            Assert.Equal(chunkCount, await plan.CleanupAsync(db));
            Assert.Equal(0, await Count($"MATCH (c:{Layout.ChunkLabel}) WHERE c.Embedding IS NOT NULL RETURN count(c) AS value"));
            Assert.Equal(0, await Count($"SHOW INDEXES YIELD name WHERE name = '{_set.VectorIndexNameFor(Layout.SchemaPrefix)}' RETURN count(*) AS value"));
        }
    }

    [Neo4jIntegrationFact]
    public async Task The_evaluation_harness_scores_the_public_corpus_through_the_real_pipeline()
    {
        await ProvisionAsync(Layout, _set);
        var activities = new System.Collections.Concurrent.ConcurrentQueue<System.Diagnostics.Activity>();
        using var listener = new System.Diagnostics.ActivityListener
        {
            ShouldListenTo = source => source.Name == GraphRagTracing.ActivitySourceName,
            Sample = (ref System.Diagnostics.ActivityCreationOptions<System.Diagnostics.ActivityContext> _) => System.Diagnostics.ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activities.Enqueue
        };
        System.Diagnostics.ActivitySource.AddActivityListener(listener);
        await using var provider = Services(Layout, _set, null, new HashGenerator(8), new GraphRagExtractiveChatClient(), maxUtf8Bytes: 2_048);
        var (corpus, cases) = GraphRagEvaluator.AliceCorpus();
        var receipt = await provider.GetRequiredService<IGraphRagIngestion>().IngestAsync([GraphRagSources.File(corpus)]);
        Assert.Equal(GraphRagDocumentStatus.Ingested, Assert.Single(receipt.Documents).Status);
        var report = await GraphRagEvaluator.RunAsync(provider.GetRequiredService<IGraphRagAnswers>(), cases);
        Assert.True(report.Passed, string.Join("; ", report.Failures.Select(f => $"{f.Case}: {f.Metric.Explanation}")));
        Assert.Equal(1.0, report.Score("graphrag_citation_validity"));
        Assert.All(report.Cases, c => Assert.NotNull(c.Answer.Receipt));

        // GR-39: every stage traced, with IDs and counts, and never the path or the text.
        var mine = activities.Where(a => a.GetTagItem("graphrag.run_id") as string == receipt.RunId || a.Parent is not null || a.OperationName is GraphRagTracing.RetrieveActivity or GraphRagTracing.AnswerActivity).ToArray();
        Assert.Subset(mine.Select(a => a.OperationName).ToHashSet(), new HashSet<string> { GraphRagTracing.IngestActivity, GraphRagTracing.DocumentActivity,
            GraphRagTracing.EmbedActivity, GraphRagTracing.WriteActivity, GraphRagTracing.RetrieveActivity, GraphRagTracing.AnswerActivity });
        var ingest = mine.Single(a => a.OperationName == GraphRagTracing.IngestActivity && a.GetTagItem("graphrag.run_id") as string == receipt.RunId);
        Assert.Equal((1, receipt.ChunksWritten), (ingest.GetTagItem("graphrag.documents_ingested"), ingest.GetTagItem("graphrag.chunks_written")));
        Assert.DoesNotContain(mine.SelectMany(a => a.TagObjects), t => t.Value is string text
            && (text.Contains("Rabbit", StringComparison.Ordinal) || text.Contains(Path.GetFileName(corpus), StringComparison.Ordinal)));
    }

    private string WriteFile(string name)
    {
        var path = Path.Combine(Path.GetTempPath(), $"graphrag-{name}-{_suffix}.md");
        File.WriteAllText(path, Markdown.ReplaceLineEndings("\n"));
        _files.Add(path);
        return path;
    }

    private async Task ProvisionAsync(GraphRagLayout layout, GraphRagEmbeddingSet set, GraphRagOntology? ontology = null)
    {
        foreach (var step in GraphRagSchema.CreateSteps(layout, set, ontology)) await Execute(GraphMigrationStepRenderer.Render(step).Cypher);
        foreach (var definition in GraphRagSchema.Definitions(layout, set, ontology).Where(d => d.IsIndex))
            await Execute("CALL db.awaitIndex($index, 60)", new { index = definition.Name });
    }

    private ServiceProvider Services(GraphRagLayout layout, GraphRagEmbeddingSet set, GraphRagOntology? ontology, IEmbeddingGenerator<string, Embedding<float>> generator,
        IChatClient? chat = null, IServiceCollection? services = null, int maxUtf8Bytes = 96)
    {
        services ??= new ServiceCollection();
        if (chat is not null) services.AddSingleton(chat);
        services.AddSingleton(new ServeModel(layout, set.Dimensions, ontology));
        services.AddDbContext<ServeContext>(o => o.UseNeo4j(fixture.Driver, fixture.Database).ReplaceService<IModelCacheKeyFactory, ServeKey>());
        services.AddSingleton(generator);
        services.AddGraphRag<ServeContext>(set, o => o.Chunking.MaxUtf8Bytes = maxUtf8Bytes);
        return services.BuildServiceProvider();
    }

    private ServeContext Context(GraphRagLayout layout, GraphRagEmbeddingSet set, GraphRagOntology? ontology)
        => new(new DbContextOptionsBuilder<ServeContext>().UseNeo4j(fixture.Driver, fixture.Database).ReplaceService<IModelCacheKeyFactory, ServeKey>().Options,
            new ServeModel(layout, set.Dimensions, ontology));

    private static T Read<T>(object? result) => ((JsonElement)result!).Deserialize<T>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    private async Task Execute(string text, object? parameters = null)
    {
        await using var session = fixture.OpenSession();
        await (parameters is null ? await session.RunAsync(text) : await session.RunAsync(text, parameters)).ConsumeAsync();
    }

    private async Task<int> Count(string text)
    {
        await using var session = fixture.OpenSession();
        return (int)(await (await session.RunAsync(text)).SingleAsync())["value"].As<long>();
    }

    public sealed record ServeModel(GraphRagLayout Layout, int Dimensions, GraphRagOntology? Ontology);

    public sealed class ServeContext(DbContextOptions<ServeContext> options, ServeModel model) : DbContext(options)
    {
        public ServeModel Shape { get; } = model;
        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.AddGraphRag(Shape.Dimensions, o => (o.Layout, o.Ontology) = (Shape.Layout, Shape.Ontology));
    }

    public sealed class ServeKey : IModelCacheKeyFactory
    {
        public object Create(DbContext context, bool designTime) => (context.GetType(), ((ServeContext)context).Shape, designTime);
    }

    private sealed class RabbitComparer : IEqualityComparer<(string, string, string[])>
    {
        public bool Equals((string, string, string[]) x, (string, string, string[]) y) => x.Item1 == y.Item1 && x.Item2 == y.Item2 && x.Item3.SequenceEqual(y.Item3);
        public int GetHashCode((string, string, string[]) obj) => obj.Item1.GetHashCode(StringComparison.Ordinal);
    }

    // A minimal Markdown reader for the pipeline: headings and blank-line separated blocks as elements, as Markdown readers give them.
    private sealed class MarkdownReader : IngestionDocumentReader
    {
        public override async Task<IngestionDocument> ReadAsync(Stream source, string identifier, string mediaType, CancellationToken cancellationToken = default)
        {
            var text = await new StreamReader(source, Encoding.UTF8).ReadToEndAsync(cancellationToken);
            var document = new IngestionDocument(identifier);
            IngestionDocumentSection? section = null;
            foreach (var block in text.ReplaceLineEndings("\n").Split("\n\n").Select(b => b.Trim('\n')).Where(b => b.Length > 0))
            {
                if (block.StartsWith('#') || section is null)
                {
                    section = new IngestionDocumentSection();
                    document.Sections.Add(section);
                }
                section.Elements.Add(block.StartsWith('#')
                    ? new IngestionDocumentHeader(block) { Level = block.TakeWhile(c => c == '#').Count(), Text = block.TrimStart('#').Trim() }
                    : new IngestionDocumentParagraph(block) { Text = block });
            }
            return document;
        }
    }

    // A chat model that calls answer_from_documents once, then repeats what the tool returned.
    private sealed class AgentChat(string question) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var result = messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().LastOrDefault();
            return Task.FromResult(result is null
                ? new ChatResponse(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("call-1", GraphRagTools.AnswerTool, new Dictionary<string, object?> { ["question"] = question })]))
                : new ChatResponse(new ChatMessage(ChatRole.Assistant, "From the documents: " + result.Result)));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class HashGenerator(int dimensions) : IEmbeddingGenerator<string, Embedding<float>>
    {
        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values, EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(values.Select(text =>
                new Embedding<float>(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text)).Take(dimensions).Select(b => 1f + b).ToArray()))));

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
