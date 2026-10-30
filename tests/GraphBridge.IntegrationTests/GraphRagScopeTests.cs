using GraphBridge.EntityFrameworkCore.Migrations;
using GraphBridge.GraphRag;
using GraphBridge.Samples.GraphRagShared;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using static GraphBridge.IntegrationTests.GraphRagServeTests;

namespace GraphBridge.IntegrationTests;

// GraphRAG v2, wave 8 (GR-11) on the disposable fixture: scoped retrieval keeps every item, neighbour and expansion row
// inside its scope, for each kind of scope and both strategies, and says how in the evidence.
[Collection(Neo4jIntegrationCollection.Name)]
public sealed class GraphRagScopeTests(DisposableNeo4jFixture fixture) : IAsyncLifetime
{
    private const string Gazette = """
        # Rockets

        The rocket engine burns liquid hydrogen and liquid oxygen. Its turbopumps spin fast.

        ## Crew

        The crew of four trained for years. The commander flew the engine burn by hand.

        ## Landing

        The capsule splashed down in the ocean after the engine burn.
        """;

    private const string Orbital = """
        # Engines

        This engine burns liquid hydrogen too, and its nozzle glows white during the burn.

        ## Tests

        Engineers fired the engine on a stand for eight minutes.
        """;

    private const string Archive = """
        # Gardens

        Tomatoes need sun. The engine of a lawn mower burns petrol, not liquid hydrogen.
        """;

    private const string Question = "Which engine burns liquid hydrogen?";

    private readonly string _suffix = Guid.NewGuid().ToString("N")[..12];
    private readonly GraphRagEmbeddingSet _set = new("fake-hash-8", 8);
    private GraphRagLayout Layout => GraphRagLayout.Default with
    {
        DocumentLabel = "KDocument_" + _suffix, SectionLabel = "KSection_" + _suffix, ChunkLabel = "KChunk_" + _suffix,
        RunLabel = "KRun_" + _suffix, LeaseLabel = "KLease_" + _suffix, SchemaPrefix = "gbk_" + _suffix
    };

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (!Enabled) return;
        var labels = new[] { Layout.DocumentLabel, Layout.SectionLabel, Layout.ChunkLabel, Layout.RunLabel, Layout.LeaseLabel }.Select(l => "n:" + l);
        await Execute($"MATCH (n)-[r]-() WHERE {string.Join(" OR ", labels)} DELETE r");
        await Execute($"MATCH (n) WHERE {string.Join(" OR ", labels)} DELETE n");
        foreach (var definition in GraphRagSchema.Definitions(Layout, _set))
            await Execute($"DROP {(definition.IsIndex ? "INDEX" : "CONSTRAINT")} {definition.Name} IF EXISTS");
    }

    private static bool Enabled => Environment.GetEnvironmentVariable("GRAPHBRIDGE_INTEGRATION") == "1";

    [Neo4jIntegrationFact]
    public async Task Every_kind_of_scope_keeps_items_and_neighbours_inside_it_with_exact_scoring_and_with_over_fetch()
    {
        var (provider, ids) = await IngestAsync();
        await using var _ = provider;
        var retriever = provider.GetRequiredService<IGraphRagRetriever>();
        string crew;
        await using (var db = Context()) crew = await db.Set<GraphRagSection>().Where(s => s.DocumentId == ids.Gazette && s.Title == "Crew").Select(s => s.Id).SingleAsync();

        // Without a scope, both documents about hydrogen engines are found, so the scopes below are what keep one out.
        var everything = await retriever.RetrieveAsync(Question, new GraphRagRetrievalOptions { NeighbourChunks = 2 });
        Assert.Contains(everything.Items, i => i.DocumentId == ids.Gazette);
        Assert.Contains(everything.Items, i => i.DocumentId == ids.Orbital);
        Assert.Null(everything.Scope);

        var scopes = new (GraphRagScope Scope, string[] Documents, bool CrewOnly)[]
        {
            (GraphRagScope.Documents(ids.Gazette), [ids.Gazette], false),
            (GraphRagScope.Sections(crew), [ids.Gazette], true),
            (GraphRagScope.Matching(new GraphRagDocumentFilter { Publisher = "Lunar Gazette", PublishedFrom = "2026-01-01" }), [ids.Gazette], false),
            (GraphRagScope.FromCypher($"MATCH (d:{Layout.DocumentLabel}) WHERE d.Publisher = $publisher RETURN d.Id AS documentId",
                new Dictionary<string, object?> { ["publisher"] = "Orbital News" }), [ids.Orbital], false)
        };
        foreach (var exactLimit in new[] { 10_000, 0 })
        {
            foreach (var (scope, documents, crewOnly) in scopes)
            {
                var evidence = await retriever.RetrieveAsync(Question, new GraphRagRetrievalOptions { NeighbourChunks = 2, Scope = scope, ScopeExactChunks = exactLimit });
                Assert.NotEmpty(evidence.Items);
                var spans = await SpansAsync(evidence.Items.Select(i => i.ChunkId).Concat(evidence.Context.Select(c => c.ChunkId)));
                Assert.All(spans, s => Assert.Contains(s.DocumentId, documents));
                if (crewOnly)
                {
                    await using var db = Context();
                    var section = await db.Set<GraphRagSection>().SingleAsync(s => s.Id == crew);
                    Assert.All(spans, s => Assert.True(s.StartByte >= section.StartByte && s.EndByte <= section.EndByte, $"{s.Id} lies outside the section."));
                }
                var trace = Assert.IsType<GraphRagScopeTrace>(evidence.Scope);
                Assert.Equal((scope.ToString(), 1), (trace.Scope, trace.Documents));
                Assert.Equal(exactLimit == 0 ? "over-fetch" : "exact", trace.Routes.Single(r => r.Route == GraphRagRoutes.Vector).Strategy);
                Assert.Equal("over-fetch", trace.Routes.Single(r => r.Route == GraphRagRoutes.FullText).Strategy);
            }
        }

        // The endpoints' document read keeps to the same scopes: a document outside one isn't visible, and a section lists only its chunks.
        await using (var db = Context())
        {
            Assert.Null(await GraphRagScopeCheck.DocumentAsync(db, GraphRagScope.Documents(ids.Gazette), ids.Orbital, default));
            var inCrew = Assert.IsType<Func<int, int, bool>>(await GraphRagScopeCheck.DocumentAsync(db, GraphRagScope.Sections(crew), ids.Gazette, default));
            var section = await db.Set<GraphRagSection>().SingleAsync(s => s.Id == crew);
            var chunks = await db.Set<GraphRagChunk>().AsNoTracking().Where(c => c.DocumentId == ids.Gazette).ToListAsync();
            Assert.Contains(chunks, c => inCrew(c.StartByte, c.EndByte));
            Assert.All(chunks.Where(c => inCrew(c.StartByte, c.EndByte)), c => Assert.True(c.StartByte >= section.StartByte && c.EndByte <= section.EndByte));
        }
    }

    [Neo4jIntegrationFact]
    public async Task An_empty_scope_finds_nothing_and_the_answer_is_refused_without_a_model_call()
    {
        var (provider, _) = await IngestAsync();
        await using var __ = provider;
        var retriever = provider.GetRequiredService<IGraphRagRetriever>();
        foreach (var scope in new[] { GraphRagScope.Documents(), GraphRagScope.Documents("no-such-document"), GraphRagScope.Sections("no-such-section") })
        {
            var evidence = await retriever.RetrieveAsync(Question, new GraphRagRetrievalOptions { Scope = scope });
            Assert.Empty(evidence.Items);
            Assert.Empty(evidence.Context);
            Assert.Equal(0, evidence.Scope!.Chunks);
        }
        var chat = provider.GetRequiredService<CountingChat>();
        var answer = await provider.GetRequiredService<IGraphRagAnswers>().AnswerAsync(Question, new GraphRagRetrievalOptions { Scope = GraphRagScope.Documents() });
        Assert.Equal(("no-evidence", 0), (answer.Reason, chat.Calls));
    }

    [Neo4jIntegrationFact]
    public async Task A_cypher_expansion_gets_the_scope_s_documents_to_keep_its_rows_inside()
    {
        var (provider, ids) = await IngestAsync();
        await using var _ = provider;
        // One evidence item leaves the document's other chunks for the expansion to return.
        var options = new GraphRagRetrievalOptions { MaxItems = 1, NeighbourChunks = 0, Scope = GraphRagScope.Documents(ids.Orbital) }.WithCypherExpansion(
            $"MATCH (c:{Layout.ChunkLabel}) WHERE c.DocumentId IN $scopeDocumentIds AND NOT c.Id IN $chunkIds RETURN c.Id AS chunkId, c.Text AS text ORDER BY c.Order LIMIT 5");
        var evidence = await provider.GetRequiredService<IGraphRagRetriever>().RetrieveAsync(Question, options);
        Assert.NotEmpty(evidence.Context);
        Assert.All(await SpansAsync(evidence.Context.Select(c => c.ChunkId)), s => Assert.Equal(ids.Orbital, s.DocumentId));
    }

    [Neo4jIntegrationFact]
    public async Task A_bound_reached_is_named_in_the_evidence()
    {
        var (provider, _) = await IngestAsync();
        await using var _ = provider;
        // Thirty-odd passages that are the question itself, and a scope of passages that share no word with it.
        var receipt = await provider.GetRequiredService<IGraphRagIngestion>().IngestAsync(
        [
            GraphRagSources.Text("echo", string.Join("\n\n", Enumerable.Repeat(Question, 60))),
            GraphRagSources.Text("garden", string.Join("\n\n", Enumerable.Range(1, 9).Select(i => $"Row {i} of tomatoes needs sun and water.")))
        ]);
        var garden = receipt.Documents.Single(d => d.SourceUri == "text:garden").DocumentId;
        var retriever = provider.GetRequiredService<IGraphRagRetriever>();

        // Over-fetch: the index's best candidates are all outside the scope, so the routes find fewer than they were asked for.
        var scoped = await retriever.RetrieveAsync(Question, new GraphRagRetrievalOptions
        {
            CandidatesPerRoute = 1, NeighbourChunks = 0, ScopeExactChunks = 0, Scope = GraphRagScope.Documents(garden)
        });
        Assert.Contains(scoped.LimitsReached, l => l.Name == "VectorScopeOverFetch");
        Assert.Contains(scoped.LimitsReached, l => l.Name == "FullTextScopeOverFetch");

        // A Cypher scope of more than 10,000 documents keeps 10,000.
        var wide = await retriever.RetrieveAsync(Question, new GraphRagRetrievalOptions
        {
            Scope = GraphRagScope.FromCypher("UNWIND range(1, 10001) AS i RETURN toString(i) AS documentId")
        });
        Assert.Contains(new GraphRagLimit("ScopeDocuments", GraphRagScope.MaxDocuments), wide.LimitsReached);

        // An expansion's rows stop at 256.
        var expanded = await retriever.RetrieveAsync(Question, new GraphRagRetrievalOptions { MaxItems = 1, NeighbourChunks = 0 }
            .WithCypherExpansion("UNWIND range(1, 300) AS i RETURN $chunkIds[0] AS chunkId, 'row ' + toString(i) AS text"));
        Assert.Contains(new GraphRagLimit("CypherExpansionRows", 256), expanded.LimitsReached);
        Assert.Equal(256, expanded.Context.Count(c => c.Reason == "cypher expansion"));

    }

    private async Task<(ServiceProvider Provider, (string Gazette, string Orbital, string Archive) Ids)> IngestAsync()
    {
        foreach (var step in GraphRagSchema.CreateSteps(Layout, _set)) await Execute(GraphMigrationStepRenderer.Render(step).Cypher);
        foreach (var definition in GraphRagSchema.Definitions(Layout, _set).Where(d => d.IsIndex))
            await Execute("CALL db.awaitIndex($index, 60)", new { index = definition.Name });
        var services = new ServiceCollection();
        services.AddSingleton<CountingChat>().AddSingleton<IChatClient>(p => p.GetRequiredService<CountingChat>());
        services.AddSingleton(new ServeModel(Layout, _set.Dimensions, null));
        services.AddDbContext<ServeContext>(o => o.UseNeo4j(fixture.Driver, fixture.Database).ReplaceService<IModelCacheKeyFactory, ServeKey>());
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(new HashingEmbedder(8));
        services.AddGraphRag<ServeContext>(_set, o => o.Chunking.MaxUtf8Bytes = 96);
        var provider = services.BuildServiceProvider();
        var receipt = await provider.GetRequiredService<IGraphRagIngestion>().IngestAsync(
        [
            GraphRagSources.Text("gazette", Gazette, "text/markdown") with { Metadata = new GraphRagSourceMetadata(Publisher: "Lunar Gazette", Published: "2026-04-01") },
            GraphRagSources.Text("orbital", Orbital, "text/markdown") with { Metadata = new GraphRagSourceMetadata(Publisher: "Orbital News", Published: "2026-04-06") },
            GraphRagSources.Text("archive", Archive, "text/markdown") with { Metadata = new GraphRagSourceMetadata(Publisher: "Lunar Gazette", Published: "2025-12-01") }
        ]);
        Assert.All(receipt.Documents, d => Assert.Equal(GraphRagDocumentStatus.Ingested, d.Status));
        string Id(string name) => receipt.Documents.Single(d => d.SourceUri.EndsWith(name, StringComparison.Ordinal)).DocumentId;
        return (provider, (Id("gazette"), Id("orbital"), Id("archive")));
    }

    private async Task<IReadOnlyList<(string Id, string DocumentId, int StartByte, int EndByte)>> SpansAsync(IEnumerable<string> chunkIds)
    {
        var ids = chunkIds.Distinct().ToArray();
        await using var db = Context();
        var spans = await db.Set<GraphRagChunk>().Where(c => ids.Contains(c.Id)).Select(c => new { c.Id, c.DocumentId, c.StartByte, c.EndByte }).ToListAsync();
        Assert.Equal(ids.Length, spans.Count);
        return spans.Select(s => (s.Id, s.DocumentId, s.StartByte, s.EndByte)).ToArray();
    }

    private ServeContext Context()
        => new(new DbContextOptionsBuilder<ServeContext>().UseNeo4j(fixture.Driver, fixture.Database).ReplaceService<IModelCacheKeyFactory, ServeKey>().Options,
            new ServeModel(Layout, _set.Dimensions, null));

    private async Task Execute(string text, object? parameters = null)
    {
        await using var session = fixture.OpenSession();
        await (parameters is null ? await session.RunAsync(text) : await session.RunAsync(text, parameters)).ConsumeAsync();
    }

    // Counts model calls; it is never asked anything in these tests.
    private sealed class CountingChat : IChatClient
    {
        public int Calls;

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "{}")));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
