using System.Text;
using GraphBridge.EntityFrameworkCore.Migrations;
using GraphBridge.GraphRag;
using GraphBridge.Samples.GraphRagShared;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Neo4j.Driver;
using static GraphBridge.IntegrationTests.GraphRagServeTests;

namespace GraphBridge.IntegrationTests;

// GraphRAG v2, wave 9 (GR-18) on the disposable fixture: adopt mode on a graph of neo4j-graphrag 1.22.0's shape, built here
// with Cypher: Document and Chunk nodes that also carry __KGBuilder__, path and createdAt on documents, text, index and
// embedding on chunks, no IDs, FROM_DOCUMENT and NEXT_CHUNK, and the library's own vector index.
[Collection(Neo4jIntegrationCollection.Name)]
public sealed class GraphRagAdoptionTests(DisposableNeo4jFixture fixture) : IAsyncLifetime
{
    private const int Dimensions = 8;

    private static readonly (string Path, string Text, string[] Chunks)[] Corpus =
    [
        ("engines.md", "The rocket engine burns liquid hydrogen. Its turbopumps spin fast. The nozzle glows white during the burn.",
            ["The rocket engine burns liquid hydrogen.", "Its turbopumps spin fast.", "The nozzle glows white during the burn."]),
        ("crew.md", "The crew of four trained for years. The commander flew the spacecraft by hand.",
            ["The crew of four trained for years.", "The commander flew the spacecraft by hand."]),
        ("notes.md", "", ["Gardeners keep seeds in a cool shed.", "Tomatoes need sun and water."])   // no source file: chunk-grounded
    ];

    private readonly string _suffix = Guid.NewGuid().ToString("N")[..12];
    private readonly List<string> _files = [];
    private readonly GraphRagEmbeddingSet _set = new("graphbridge-sample-hashing", Dimensions);
    private GraphRagLayout Layout => GraphRagLayout.Neo4jGraphRag with
    {
        DocumentLabel = "ADocument_" + _suffix, SectionLabel = "ASection_" + _suffix, ChunkLabel = "AChunk_" + _suffix,
        RunLabel = "ARun_" + _suffix, LeaseLabel = "ALease_" + _suffix, SchemaPrefix = "gba_" + _suffix, VectorIndexName = "pyvec_" + _suffix
    };

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var file in _files) File.Delete(file);
        if (Environment.GetEnvironmentVariable("GRAPHBRIDGE_INTEGRATION") != "1") return;
        await ResetJournal();
        var labels = new[] { Layout.DocumentLabel, Layout.SectionLabel, Layout.ChunkLabel, Layout.RunLabel, Layout.LeaseLabel }.Select(l => "n:" + l);
        await Execute($"MATCH (n)-[r]-() WHERE {string.Join(" OR ", labels)} DELETE r");
        await Execute($"MATCH (n) WHERE {string.Join(" OR ", labels)} DELETE n");
        foreach (var definition in GraphRagSchema.Definitions(Layout, _set))
            await Execute($"DROP {(definition.IsIndex ? "INDEX" : "CONSTRAINT")} `{definition.Name}` IF EXISTS");
    }

    [Neo4jIntegrationFact]
    public async Task A_dry_run_writes_nothing_apply_answers_with_the_right_grounding_and_undo_restores_the_graph()
    {
        await BuildForeignGraphAsync();
        var sources = new Dictionary<string, string>();
        foreach (var (path, text, _) in Corpus.Where(d => d.Text.Length > 0))
        {
            var file = Path.Combine(Path.GetTempPath(), $"adopt-{_suffix}-{path}");
            File.WriteAllText(file, text, new UTF8Encoding(false));
            _files.Add(file);
            sources[path] = file;
        }
        var plan = GraphRagAdoption.Plan(Layout, _set, new GraphRagAdoptionOptions { SourceFiles = sources });
        await using var provider = Services();
        await ResetJournal();
        await using (var scope = provider.CreateAsyncScope())
            await PrepareAsync(scope.ServiceProvider.GetRequiredService<ServeContext>(), plan, "adopt-" + _suffix);
        var before = await SnapshotAsync();

        await using (var scope = provider.CreateAsyncScope())
        {
            var preview = await plan.PreviewAsync(scope.ServiceProvider.GetRequiredService<ServeContext>());
            Assert.Equal((3, 7, 7, 4, 2, 1, 2), (preview.Documents, preview.Chunks, preview.ChunkLinks, preview.NextLinks,
                preview.SourceGroundedDocuments, preview.ChunkGroundedDocuments, preview.ChunkGroundedChunks));
            Assert.True(preview.CanApply, string.Join("; ", preview.Blockers));
        }
        Assert.Equal(before, await SnapshotAsync());

        GraphRagAdoptionReceipt receipt;
        await using (var scope = provider.CreateAsyncScope())
            receipt = await plan.ApplyAsync(scope.ServiceProvider.GetRequiredService<ServeContext>());
        Assert.Equal((3, 7, 2, 1), (receipt.Documents, receipt.Chunks, receipt.SourceGroundedDocuments, receipt.ChunkGroundedDocuments));

        // A second adoption is refused: every node now belongs to a run.
        await using (var scope = provider.CreateAsyncScope())
            Assert.False((await plan.PreviewAsync(scope.ServiceProvider.GetRequiredService<ServeContext>())).CanApply);

        var answers = provider.GetRequiredService<IGraphRagAnswers>();
        var sourced = await answers.AnswerAsync("Which engine burns liquid hydrogen?");
        var citation = Assert.Single(sourced.Citations);
        Assert.Equal(GraphRagGrounding.Source, citation.Grounding);
        Assert.Equal(citation.Quote, Encoding.UTF8.GetString(File.ReadAllBytes(sources["engines.md"]), citation.StartByte, citation.EndByte - citation.StartByte));

        var chunked = await answers.AnswerAsync("Where do gardeners keep seeds?");
        var inChunk = Assert.Single(chunked.Citations);
        Assert.Equal(GraphRagGrounding.Chunk, inChunk.Grounding);
        Assert.Equal(inChunk.Quote, Encoding.UTF8.GetString(Encoding.UTF8.GetBytes("Gardeners keep seeds in a cool shed."), inChunk.StartByte, inChunk.EndByte - inChunk.StartByte));
        Assert.Empty((await provider.GetRequiredService<IGraphRagMaintenance>().CheckAsync()).Findings);

        await using (var scope = provider.CreateAsyncScope())
        {
            // A run that isn't this graph's adoption is refused before anything changes.
            await Assert.ThrowsAsync<InvalidOperationException>(() => plan.UndoAsync(scope.ServiceProvider.GetRequiredService<ServeContext>(), "not-an-adoption-run"));
            var undo = await plan.UndoAsync(scope.ServiceProvider.GetRequiredService<ServeContext>(), receipt.RunId);
            Assert.Equal((3, 7, 11), (undo.Documents, undo.Chunks, undo.Relationships));
        }
        Assert.Equal(before, await SnapshotAsync());
        Assert.Equal(0, await CountAsync($"MATCH (r:{Layout.RunLabel}) RETURN count(r) AS n"));
    }

    [Neo4jIntegrationFact]
    public async Task A_graph_without_its_vector_index_or_with_orphan_chunks_is_not_adopted()
    {
        await BuildForeignGraphAsync(withIndex: false);
        await Execute($"CREATE (:{Layout.ChunkLabel}:__KGBuilder__ {{text: 'an orphan', `index`: 0}})");
        var plan = GraphRagAdoption.Plan(Layout, _set);
        foreach (var step in plan.PrepareSteps.Where(s => s.Schema?.Kind != GraphMigrationSchemaKind.VectorIndex))
            await Execute(GraphMigrationStepRenderer.Render(step).Cypher);
        await using var provider = Services();
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ServeContext>();
        var preview = await plan.PreviewAsync(db);
        Assert.Contains(preview.Blockers, b => b.Contains("no vector index named", StringComparison.Ordinal));
        Assert.Contains(preview.Blockers, b => b.Contains("1 chunks have no FROM_DOCUMENT", StringComparison.Ordinal));
        await Assert.ThrowsAsync<InvalidOperationException>(() => plan.ApplyAsync(db));
    }

    [Neo4jIntegrationFact]
    public async Task Two_documents_with_one_path_or_a_vector_index_of_other_dimensions_are_not_adopted()
    {
        await BuildForeignGraphAsync(withIndex: false);
        await Execute($"CREATE (:{Layout.DocumentLabel}:__KGBuilder__ {{path: 'crew.md', createdAt: '2026-10-26T00:00:00+00:00'}})");
        await Execute($"CREATE VECTOR INDEX `{Layout.VectorIndexName}` FOR (c:{Layout.ChunkLabel}) ON c.embedding "
            + "OPTIONS {indexConfig: {`vector.dimensions`: 4, `vector.similarity_function`: 'cosine'}}");
        await Execute("CALL db.awaitIndexes(60)");
        var plan = GraphRagAdoption.Plan(Layout, _set);
        foreach (var step in plan.PrepareSteps) await Execute(GraphMigrationStepRenderer.Render(step).Cypher);
        await using var provider = Services();
        await using var scope = provider.CreateAsyncScope();
        var preview = await plan.PreviewAsync(scope.ServiceProvider.GetRequiredService<ServeContext>());
        Assert.Contains("two documents have the path 'crew.md'", preview.Blockers);
        Assert.Contains($"the vector index '{Layout.VectorIndexName}' has 4 dimensions; the embedding set has {Dimensions}", preview.Blockers);
        Assert.False(preview.CanApply);
    }

    [Neo4jIntegrationFact]
    public async Task A_document_ingested_again_after_adoption_belongs_to_that_run_and_undo_leaves_it()
    {
        await BuildForeignGraphAsync();
        var file = Path.Combine(Path.GetTempPath(), $"adopt-{_suffix}-engines.md");
        File.WriteAllText(file, Corpus[0].Text, new UTF8Encoding(false));
        _files.Add(file);
        var plan = GraphRagAdoption.Plan(Layout, _set, new GraphRagAdoptionOptions { SourceFiles = new Dictionary<string, string> { ["engines.md"] = file } });
        await using var provider = Services();
        await ResetJournal();
        await using (var scope = provider.CreateAsyncScope())
            await PrepareAsync(scope.ServiceProvider.GetRequiredService<ServeContext>(), plan, "adopt-again-" + _suffix);
        GraphRagAdoptionReceipt adopted;
        await using (var scope = provider.CreateAsyncScope())
            adopted = await plan.ApplyAsync(scope.ServiceProvider.GetRequiredService<ServeContext>());

        // The adopted document's ID is its source file's, so ingesting that file is the same document, written again by this run.
        var ingested = await provider.GetRequiredService<IGraphRagIngestion>().IngestAsync([GraphRagSources.File(file)]);
        var outcome = Assert.Single(ingested.Documents);
        Assert.Equal(GraphRagDocumentStatus.Ingested, outcome.Status);

        await using (var scope = provider.CreateAsyncScope())
            await plan.UndoAsync(scope.ServiceProvider.GetRequiredService<ServeContext>(), adopted.RunId);
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ServeContext>();
            var document = await db.Set<GraphRagDocument>().SingleAsync(d => d.Id == outcome.DocumentId);
            Assert.Equal(ingested.RunId, document.RunId);
            Assert.NotEmpty(await db.Set<GraphRagChunk>().Where(c => c.DocumentId == outcome.DocumentId).ToListAsync());
        }
        // The other two documents are back as neo4j-graphrag wrote them.
        Assert.Equal(2, await CountAsync($"MATCH (d:{Layout.DocumentLabel}) WHERE d.Id IS NULL RETURN count(d) AS n"));
    }

    private async Task BuildForeignGraphAsync(bool withIndex = true)
    {
        var embedder = new HashingEmbedder(Dimensions);
        foreach (var (path, _, chunks) in Corpus)
        {
            var vectors = await embedder.GenerateAsync(chunks);
            await Execute(
                $$"""
                CREATE (d:{{Layout.DocumentLabel}}:__KGBuilder__ {path: $path, createdAt: '2026-10-26T00:00:00+00:00'})
                WITH d UNWIND range(0, size($texts) - 1) AS i
                CREATE (c:{{Layout.ChunkLabel}}:__KGBuilder__ {text: $texts[i], `index`: i, embedding: $vectors[i]})-[:FROM_DOCUMENT]->(d)
                WITH d, collect(c) AS chunks
                UNWIND range(0, size(chunks) - 2) AS i
                WITH chunks[i] AS a, chunks[i + 1] AS b
                CREATE (a)-[:NEXT_CHUNK]->(b)
                """,
                new { path, texts = chunks, vectors = vectors.Select(v => v.Vector.ToArray().Select(x => (double)x).ToArray()).ToArray() });
        }
        if (withIndex)
        {
            await Execute($"CREATE VECTOR INDEX `{Layout.VectorIndexName}` FOR (c:{Layout.ChunkLabel}) ON c.embedding "
                + "OPTIONS {indexConfig: {`vector.dimensions`: 8, `vector.similarity_function`: 'cosine'}}");
            await Execute("CALL db.awaitIndexes(60)");
        }
    }

    // Every node's and relationship's property keys under the run's labels, in a stable order.
    private async Task<string> SnapshotAsync()
    {
        await using var session = fixture.OpenSession();
        var nodes = await (await session.RunAsync(
            $"MATCH (n) WHERE n:{Layout.DocumentLabel} OR n:{Layout.ChunkLabel} RETURN labels(n) AS labels, keys(n) AS keys ORDER BY coalesce(n.path, n.text)")).ToListAsync();
        var edges = await (await session.RunAsync(
            $"MATCH (a:{Layout.ChunkLabel})-[r]->(b) RETURN type(r) AS type, keys(r) AS keys ORDER BY a.text, type(r)")).ToListAsync();
        return string.Join('\n', nodes.Select(r => string.Join(',', r["labels"].As<List<string>>().Order()) + ":" + string.Join(',', r["keys"].As<List<string>>().Order()))
            .Concat(edges.Select(r => r["type"].As<string>() + ":" + string.Join(',', r["keys"].As<List<string>>().Order()))));
    }

    // The provider's migration journal, by its stored names, reset as the other suites reset it.
    private async Task ResetJournal()
    {
        foreach (var label in new[] { "__GraphBridgeMigrationStep", "__GraphBridgeMigration", "__GraphBridgeMigrationControl" })
            await Execute($"MATCH (n:{label}) DELETE n");
        foreach (var name in new[] { "gb_migration_control_key", "gb_migration_history_key", "gb_migration_step_key" })
            await Execute($"DROP CONSTRAINT {name} IF EXISTS");
    }

    // PrepareSteps through the migration engine, as an application runs them: an existing index of the declared name and
    // definition, such as neo4j-graphrag's vector index, counts as done.
    private static async Task PrepareAsync(DbContext db, GraphRagAdoptionPlan plan, string id)
    {
        await db.Database.InitializeGraphMigrationsAsync();
        await db.Database.ApplyGraphMigrationAsync(new GraphMigrationPlan(id, null, plan.PrepareSteps,
            GraphMigrationRollbackPolicy.NoRollback("The test drops its schema at the end.")));
    }

    private ServiceProvider Services()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IChatClient>(new ExtractiveChatClient());
        services.AddSingleton(new ServeModel(Layout, Dimensions, null));
        services.AddDbContext<ServeContext>(o => o.UseNeo4j(fixture.Driver, fixture.Database).ReplaceService<IModelCacheKeyFactory, ServeKey>());
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(new HashingEmbedder(Dimensions));
        services.AddGraphRag<ServeContext>(_set, o => o.Retrieval.NeighbourChunks = 0);
        return services.BuildServiceProvider();
    }

    private async Task Execute(string cypher, object? parameters = null)
    {
        await using var session = fixture.OpenSession();
        await (parameters is null ? await session.RunAsync(cypher) : await session.RunAsync(cypher, parameters)).ConsumeAsync();
    }

    private async Task<int> CountAsync(string cypher)
    {
        await using var session = fixture.OpenSession();
        return (await (await session.RunAsync(cypher)).SingleAsync())["n"].As<int>();
    }
}
