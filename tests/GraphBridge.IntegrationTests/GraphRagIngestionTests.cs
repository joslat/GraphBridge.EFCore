using System.Text;
using GraphBridge.EntityFrameworkCore.Migrations;
using GraphBridge.GraphRag;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Neo4j.Driver;

namespace GraphBridge.IntegrationTests;

// GraphRAG wave 1 acceptance (ADR 0052) on the disposable fixture. Each test owns its labels and schema prefix.
[Collection(Neo4jIntegrationCollection.Name)]
public sealed partial class GraphRagIngestionTests(DisposableNeo4jFixture fixture) : IAsyncLifetime
{
    private const string Markdown = """
        # Graphs — an overview 🌍

        Graphs connect facts. Ünïcödé text keeps its bytes.

        ## Nodes

        A node holds properties.
        It can span lines.

        ## Edges

        Edges link nodes. 🔗 They have types.

        # Appendix

        The end.
        """;

    private readonly string _suffix = Guid.NewGuid().ToString("N")[..12];
    private readonly GraphRagEmbeddingSet _set = new("fake-hash-8", 8);
    private GraphRagLayout Layout => GraphRagLayout.Default with
    {
        DocumentLabel = "RagDocument_" + _suffix, SectionLabel = "RagSection_" + _suffix, ChunkLabel = "RagChunk_" + _suffix,
        RunLabel = "RagRun_" + _suffix, LeaseLabel = "RagLease_" + _suffix, SchemaPrefix = "gbrag_" + _suffix
    };

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (!Enabled) return;
        var labels = string.Join(" OR ", new[] { Layout.DocumentLabel, Layout.SectionLabel, Layout.ChunkLabel, Layout.RunLabel, Layout.LeaseLabel }.Select(l => "n:" + l));
        await Execute($"MATCH (n)-[r]-() WHERE {labels} DELETE r");
        await Execute($"MATCH (n) WHERE {labels} DELETE n");
        foreach (var definition in GraphRagSchema.Definitions(Layout, _set))
            await Execute($"DROP {(definition.IsIndex ? "INDEX" : "CONSTRAINT")} {definition.Name} IF EXISTS");
    }

    private static bool Enabled => Environment.GetEnvironmentVariable("GRAPHBRIDGE_INTEGRATION") == "1";

    [Neo4jIntegrationFact]
    public async Task The_schema_previews_then_applies_through_the_application_migration_and_reports_ONLINE()
    {
        await ResetJournal();
        try
        {
            var plan = new GraphMigrationPlan("graphrag_" + _suffix, null, GraphRagSchema.CreateSteps(Layout, _set),
                GraphMigrationRollbackPolicy.NoRollback("Disposable fixture; the test drops what it created."));
            var preview = plan.Preview();
            Assert.Equal(7, preview.Steps.Count);
            Assert.Contains(preview.Steps, s => s.Command.Cypher.StartsWith("CREATE VECTOR INDEX", StringComparison.Ordinal));
            await using var db = Context();
            await db.Database.InitializeGraphMigrationsAsync();
            Assert.True((await db.Database.ValidateGraphMigrationAsync(plan)).CanApply);
            Assert.Equal(7, (await db.Database.ApplyGraphMigrationAsync(plan)).CompletedSteps);
            foreach (var index in new[] { _set.VectorIndexNameFor(Layout.SchemaPrefix), GraphRagSchema.FullTextIndexName(Layout) })
                Assert.Equal("ONLINE", await Text("SHOW INDEXES YIELD name, state WHERE name = $name RETURN state AS value", new { name = index }));
            Assert.Equal(5L, await Count($"SHOW CONSTRAINTS YIELD name WHERE name STARTS WITH 'gbrag_{_suffix}' RETURN count(*) AS value"));
        }
        finally { await ResetJournal(); }
    }

    [Neo4jIntegrationFact]
    public async Task Chunk_positions_equal_the_source_bytes_and_every_link_carries_the_run()
    {
        await ProvisionAsync();
        await using var provider = Services(new HashGenerator());
        var receipt = await provider.GetRequiredService<IGraphRagIngestion>().IngestAsync([GraphRagSources.Text("guide", Markdown, "text/markdown")]);
        var outcome = Assert.Single(receipt.Documents);
        Assert.Equal(GraphRagDocumentStatus.Ingested, outcome.Status);
        var source = Encoding.UTF8.GetBytes(Markdown.ReplaceLineEndings("\n"));

        await using var db = Context();
        var chunks = await db.Set<GraphRagChunk>().Where(c => c.DocumentId == outcome.DocumentId).OrderBy(c => c.Order).ToListAsync();
        Assert.Equal(receipt.ChunksWritten, chunks.Count);
        Assert.True(chunks.Count > 1);
        var covered = new bool[source.Length];
        foreach (var chunk in chunks)
        {
            Assert.Equal(Encoding.UTF8.GetString(source, chunk.StartByte, chunk.EndByte - chunk.StartByte), chunk.Text);
            Assert.Equal(HashGenerator.Vector(chunk.Text), chunk.Embedding);
            Assert.Equal(_set.Id, chunk.EmbeddingSetId);
            for (var i = chunk.StartByte; i < chunk.EndByte; i++) covered[i] = true;
        }
        Assert.All(covered, Assert.True);
        var document = await db.Set<GraphRagDocument>().SingleAsync(d => d.Id == outcome.DocumentId);
        Assert.Equal((long)source.Length, document.LengthBytes);
        Assert.Equal("Graphs — an overview 🌍", document.Title);
        Assert.Equal(4, await db.Set<GraphRagSection>().CountAsync(s => s.DocumentId == outcome.DocumentId));

        Assert.Equal(chunks.Count, await Count($"MATCH (:{Layout.ChunkLabel})-[r:IN_DOCUMENT]->(:{Layout.DocumentLabel}) RETURN count(r) AS value"));
        Assert.Equal(chunks.Count - 1, await Count($"MATCH (:{Layout.ChunkLabel})-[r:NEXT_CHUNK]->(:{Layout.ChunkLabel}) RETURN count(r) AS value"));
        Assert.Equal(chunks.Count, await Count($"MATCH (:{Layout.ChunkLabel})-[r:IN_SECTION]->(:{Layout.SectionLabel}) RETURN count(r) AS value"));
        Assert.Equal(4, await Count($"MATCH (:{Layout.SectionLabel})-[r:SECTION_OF]->(:{Layout.DocumentLabel}) RETURN count(r) AS value"));
        Assert.Equal(2, await Count($"MATCH (:{Layout.SectionLabel})-[r:SUBSECTION_OF]->(:{Layout.SectionLabel}) RETURN count(r) AS value"));
        Assert.Equal(0, await Count($"MATCH (n)-[r]->() WHERE (n:{Layout.ChunkLabel} OR n:{Layout.SectionLabel}) AND r.RunId <> $run RETURN count(r) AS value", new { run = receipt.RunId }));
        var run = await db.Set<GraphRagIngestionRun>().SingleAsync(r => r.Id == receipt.RunId);
        Assert.Equal(("Completed", 1, 0, 0), (run.Status, run.DocumentsIngested, run.DocumentsUnchanged, run.DocumentsFailed));
        Assert.Equal(0, await db.Set<GraphRagLease>().CountAsync());
    }

    [Neo4jIntegrationFact]
    public async Task A_rerun_skips_unchanged_documents_and_a_changed_one_replaces_its_chunks()
    {
        await ProvisionAsync();
        await using var provider = Services(new HashGenerator());
        var ingestion = provider.GetRequiredService<IGraphRagIngestion>();
        var first = await ingestion.IngestAsync([GraphRagSources.Text("guide", Markdown, "text/markdown")]);
        var again = await ingestion.IngestAsync([GraphRagSources.Text("guide", Markdown, "text/markdown")]);
        Assert.Equal(GraphRagDocumentStatus.Unchanged, Assert.Single(again.Documents).Status);
        Assert.Equal(0, again.EmbeddingCalls);

        var edited = await ingestion.IngestAsync([GraphRagSources.Text("guide", Markdown + "\n\nA new closing line.", "text/markdown")]);
        Assert.Equal(GraphRagDocumentStatus.Ingested, Assert.Single(edited.Documents).Status);
        await using var db = Context();
        var documentId = first.Documents[0].DocumentId;
        Assert.Equal(edited.ChunksWritten, await db.Set<GraphRagChunk>().CountAsync(c => c.DocumentId == documentId));
        Assert.Equal(0, await db.Set<GraphRagChunk>().CountAsync(c => c.RunId == first.RunId));
        Assert.Equal(edited.ChunksWritten - 1, await Count($"MATCH (:{Layout.ChunkLabel})-[r:NEXT_CHUNK]->(:{Layout.ChunkLabel}) RETURN count(r) AS value"));
        Assert.Contains("A new closing line.", (await db.Set<GraphRagChunk>().Where(c => c.DocumentId == documentId).OrderBy(c => c.Order).ToListAsync())[^1].Text);
    }

    [Neo4jIntegrationFact]
    public async Task A_killed_run_resumes_without_duplicates_and_a_live_lease_keeps_a_second_run_out()
    {
        await ProvisionAsync();
        var sources = new[] { "alpha", "beta", "gamma" }.Select(name => GraphRagSources.Text(name, $"# {name}\n\nThe {name} document has its own text.", "text/markdown")).ToArray();
        using var kill = new CancellationTokenSource();
        await using (var provider = Services(new HashGenerator { OnText = text => { if (text.Contains("beta", StringComparison.Ordinal)) kill.Cancel(); } }))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.GetRequiredService<IGraphRagIngestion>().IngestAsync(sources, kill.Token));

        await using (var db = Context())
        {
            Assert.Equal(1, await db.Set<GraphRagDocument>().CountAsync());
            Assert.Equal("Failed", (await db.Set<GraphRagIngestionRun>().SingleAsync()).Status);
        }
        // A crashed process leaves its lease behind; once it has expired, the next run takes it over.
        var gamma = GraphRagIngestion<RagContext>.Hash(sources[2].SourceUri);
        await Execute($"CREATE (:{Layout.LeaseLabel} {{Key: $key, RunId: 'crashed', ExpiresUtc: datetime('2000-01-01T00:00:00Z')}})", new { key = gamma });

        await using (var provider = Services(new HashGenerator()))
        {
            var resumed = await provider.GetRequiredService<IGraphRagIngestion>().IngestAsync(sources);
            Assert.Equal([GraphRagDocumentStatus.Unchanged, GraphRagDocumentStatus.Ingested, GraphRagDocumentStatus.Ingested], resumed.Documents.Select(d => d.Status));
        }
        await using (var db = Context())
        {
            foreach (var group in (await db.Set<GraphRagChunk>().Select(c => new { c.DocumentId, c.Order }).ToListAsync()).GroupBy(c => c.DocumentId))
                Assert.Equal(group.Count(), group.Select(c => c.Order).Distinct().Count());
            Assert.Equal(3, await db.Set<GraphRagDocument>().CountAsync());
            Assert.Equal(0, await db.Set<GraphRagLease>().CountAsync());
        }

        // A lease that hasn't expired keeps a second run out of that document.
        var delta = GraphRagSources.Text("delta", "# delta\n\nHeld by another run.", "text/markdown");
        await Execute($"CREATE (:{Layout.LeaseLabel} {{Key: $key, RunId: 'other', ExpiresUtc: datetime('2999-01-01T00:00:00Z')}})",
            new { key = GraphRagIngestion<RagContext>.Hash(delta.SourceUri) });
        await using (var provider = Services(new HashGenerator()))
        {
            var held = Assert.Single((await provider.GetRequiredService<IGraphRagIngestion>().IngestAsync([delta])).Documents);
            Assert.Equal((GraphRagDocumentStatus.Failed, "lease-held"), (held.Status, held.ErrorCode));
        }
    }

    [Neo4jIntegrationFact]
    public async Task A_document_that_takes_longer_than_its_lease_keeps_it_renewed_and_a_second_run_stays_out()
    {
        // GraphRAG v2, wave 7 (GR-63): the whole book's extraction outlasted the 10-minute lease, and its write was refused.
        await ProvisionAsync();
        var source = GraphRagSources.Text("slow", "# slow\n\nA document whose embedding takes longer than its lease.", "text/markdown");
        using var started = new SemaphoreSlim(0);
        await using var slow = Services(new HashGenerator { Delay = TimeSpan.FromSeconds(4), OnText = _ => started.Release() }, TimeSpan.FromSeconds(1.5));
        var first = slow.GetRequiredService<IGraphRagIngestion>().IngestAsync([source]);
        await started.WaitAsync(TimeSpan.FromSeconds(30));
        await Task.Delay(TimeSpan.FromSeconds(2.5));

        // Past the first lease's original expiry: still held, because it was renewed.
        await using (var second = Services(new HashGenerator(), TimeSpan.FromSeconds(1.5)))
        {
            var held = Assert.Single((await second.GetRequiredService<IGraphRagIngestion>().IngestAsync([source])).Documents);
            Assert.Equal((GraphRagDocumentStatus.Failed, "lease-held"), (held.Status, held.ErrorCode));
        }
        Assert.Equal(GraphRagDocumentStatus.Ingested, Assert.Single((await first).Documents).Status);
        await using var db = Context();
        Assert.Equal(0, await db.Set<GraphRagLease>().CountAsync());
    }

    [Neo4jIntegrationFact]
    public async Task Typed_vector_search_finds_a_written_chunk()
    {
        await ProvisionAsync();
        await using var provider = Services(new HashGenerator());
        await provider.GetRequiredService<IGraphRagIngestion>().IngestAsync([GraphRagSources.Text("guide", Markdown, "text/markdown")]);
        await using var db = Context();
        var target = (await db.Set<GraphRagChunk>().OrderBy(c => c.Order).ToListAsync())[1];
        var hits = await db.Database.QueryVectorNodesAsync(_set.VectorIndexNameFor(Layout.SchemaPrefix),
            HashGenerator.Vector(target.Text).Select(v => (double)v).ToArray(), node => node["Id"].As<string>(), limit: 1);
        Assert.Equal(target.Id, Assert.Single(hits.Results).Value);
        var words = await db.Database.QueryFullTextNodesAsync(GraphRagSchema.FullTextIndexName(Layout), "properties", node => node["Id"].As<string>(), limit: 5);
        Assert.NotEmpty(words.Results);
    }

    // The schema from GraphRagSchema, created from the migration steps' own DDL; the journaled path is the first test.
    private async Task ProvisionAsync()
    {
        foreach (var step in GraphRagSchema.CreateSteps(Layout, _set))
            await Execute(GraphMigrationStepRenderer.Render(step).Cypher);
        foreach (var index in new[] { _set.VectorIndexNameFor(Layout.SchemaPrefix), GraphRagSchema.FullTextIndexName(Layout) })
            await Execute("CALL db.awaitIndex($index, 60)", new { index });
    }

    private ServiceProvider Services(HashGenerator generator, IChatClient? chat = null) => Services(generator, null, chat);

    private ServiceProvider Services(HashGenerator generator, TimeSpan? leaseDuration, IChatClient? chat = null)
    {
        var services = new ServiceCollection();
        if (chat is not null) services.AddSingleton(chat);
        services.AddSingleton(Layout);
        services.AddDbContext<RagContext>(o => o.UseNeo4j(fixture.Driver, fixture.Database).ReplaceService<IModelCacheKeyFactory, LayoutKey>());
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(generator);
        services.AddGraphRag<RagContext>(_set, o =>
        {
            o.Chunking.MaxUtf8Bytes = 96;
            if (leaseDuration is { } lease) o.Ingestion.LeaseDuration = lease;
        });
        return services.BuildServiceProvider();
    }

    private RagContext Context() => new(new DbContextOptionsBuilder<RagContext>().UseNeo4j(fixture.Driver, fixture.Database)
        .ReplaceService<IModelCacheKeyFactory, LayoutKey>().Options, Layout);

    private async Task Execute(string text, object? parameters = null)
    {
        await using var session = fixture.OpenSession();
        await (parameters is null ? await session.RunAsync(text) : await session.RunAsync(text, parameters)).ConsumeAsync();
    }

    private async Task<int> Count(string text, object? parameters = null)
    {
        await using var session = fixture.OpenSession();
        return (int)(await (await session.RunAsync(text, parameters ?? new { })).SingleAsync())["value"].As<long>();
    }

    private async Task<string> Text(string text, object parameters)
    {
        await using var session = fixture.OpenSession();
        return (await (await session.RunAsync(text, parameters)).SingleAsync())["value"].As<string>();
    }

    private async Task ResetJournal()
    {
        // Only the fixture's reserved migration artifacts, as ProviderMigrationTests does.
        foreach (var label in new[] { GraphMigrationJournal.StepLabel, GraphMigrationJournal.HistoryLabel, GraphMigrationJournal.ControlLabel })
            await Execute($"MATCH (n:{label}) DELETE n");
        foreach (var name in new[] { GraphMigrationJournal.ControlConstraint, GraphMigrationJournal.HistoryConstraint, GraphMigrationJournal.StepConstraint })
            await Execute($"DROP CONSTRAINT {name} IF EXISTS");
    }

    public sealed class RagContext(DbContextOptions<RagContext> options, GraphRagLayout layout) : DbContext(options)
    {
        public GraphRagLayout Layout { get; } = layout;
        protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.AddGraphRag(8, o => o.Layout = Layout);
    }

    public sealed class LayoutKey : IModelCacheKeyFactory
    {
        public object Create(DbContext context, bool designTime) => (context.GetType(), ((RagContext)context).Layout, designTime);
    }

    // A deterministic stand-in for an embedding model: no network, no key.
    private sealed class HashGenerator : IEmbeddingGenerator<string, Embedding<float>>
    {
        public Action<string>? OnText { get; init; }
        public TimeSpan Delay { get; init; }

        public static float[] Vector(string text)
        {
            var hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text));
            return [.. hash.Take(8).Select(b => 1f + b)];
        }

        public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values, EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var texts = values.ToArray();
            foreach (var text in texts) OnText?.Invoke(text);
            cancellationToken.ThrowIfCancellationRequested();
            if (Delay > TimeSpan.Zero) await Task.Delay(Delay, cancellationToken);
            return new GeneratedEmbeddings<Embedding<float>>(texts.Select(t => new Embedding<float>(Vector(t))));
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
