using System.Diagnostics;
using System.Text.Json;
using GraphBridge.EntityFrameworkCore.Migrations;
using GraphBridge.GraphRag;
using GraphBridge.GraphRag.Ontologies;
using GraphBridge.IntegrationTests;
using GraphBridge.Samples.GraphRagShared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace GraphBridge.GraphRag.InteropTests;

/// <summary>
/// GraphRAG v2, wave 9 (GR-52): GraphBridge and Neo4j's Python neo4j-graphrag 1.22.0 on one disposable database, against the
/// acceptance list of 18 October 2026. Each graph has labels of its own, so the layouts are renamed on both sides. No model
/// is called: both stacks embed with the same word-hashing function.
/// </summary>
[Collection(Neo4jIntegrationCollection.Name)]
public sealed class InteropTests(DisposableNeo4jFixture fixture) : IAsyncLifetime
{
    private const int Dimensions = 64;

    internal static readonly string Suffix = Guid.NewGuid().ToString("N")[..10];
    internal static readonly GraphRagEmbeddingSet Embeddings = new("graphbridge-sample-hashing", Dimensions);
    internal static readonly GraphRagLayout Built = Renamed("I");
    internal static readonly GraphRagLayout Foreign = Renamed("P");
    // The Python graph of the adoption test, with neo4j-graphrag's own vector and full-text indexes named in the layout.
    internal static readonly GraphRagLayout Adopted = Renamed("A") with { VectorIndexName = $"py_vector_a_{Suffix}", FullTextIndexName = $"py_text_a_{Suffix}" };
    // GR-16: GraphBridge's things stored as neo4j-graphrag stores entities, under class labels of this run.
    internal static readonly GraphRagLayout Entities = Renamed("E");
    internal static readonly GraphRagOntology EntityOntology = GraphRagOntologies.Generic.WithLabelPrefix("E" + Suffix).WithNeo4jGraphRagEntities();

    private static readonly (string Path, string Text)[] Corpus =
    [
        ("engines.md", "# Engines\n\nThe rocket engine burns liquid hydrogen and liquid oxygen. Its turbopumps spin very fast.\n\nEngineers fired the engine on a test stand for eight minutes before the flight.\n\nThe nozzle glows white during a long burn.\n"),
        ("crew.md", "# Crew\n\nThe crew of four trained for years at the space centre. The commander flew the spacecraft by hand during a test.\n\nThe pilot watched the displays while the mission specialists photographed the far side of the Moon.\n"),
        ("gardens.md", "# Gardens\n\nTomatoes need sun and water. A lawn mower engine burns petrol, never liquid hydrogen.\n\nThe gardener planted beans along the fence and kept the seeds in a cool shed.\n")
    ];

    // GraphBridge's neo4j-graphrag layout (FROM_DOCUMENT, NEXT_CHUNK; id, index, text, embedding) with labels of this run.
    private static GraphRagLayout Renamed(string tag) => GraphRagLayout.Neo4jGraphRag with
    {
        DocumentLabel = $"{tag}Document_{Suffix}", SectionLabel = $"{tag}Section_{Suffix}", ChunkLabel = $"{tag}Chunk_{Suffix}",
        RunLabel = $"{tag}Run_{Suffix}", LeaseLabel = $"{tag}Lease_{Suffix}", SchemaPrefix = $"gbi{tag.ToLowerInvariant()}_{Suffix}"
    };

    private static object Config(GraphRagLayout layout) => new { document_node_label = layout.DocumentLabel, chunk_node_label = layout.ChunkLabel };

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (!InteropFactAttribute.Enabled) return;
        await ResetJournal();
        foreach (var layout in new[] { Built, Foreign, Adopted, Entities })
        {
            var labels = new[] { layout.DocumentLabel, layout.SectionLabel, layout.ChunkLabel, layout.RunLabel, layout.LeaseLabel }
                .Concat(layout == Entities ? EntityOntology.Classes.Select(c => c.Label) : []).Select(l => "n:" + l);
            await Execute($"MATCH (n)-[r]-() WHERE {string.Join(" OR ", labels)} DELETE r");
            await Execute($"MATCH (n) WHERE {string.Join(" OR ", labels)} DELETE n");
        }
        foreach (var definition in GraphRagSchema.Definitions(Built, Embeddings).Concat(GraphRagSchema.Definitions(Adopted, Embeddings))
                     .Concat(GraphRagSchema.Definitions(Entities, Embeddings, EntityOntology)))
            await Execute($"DROP {(definition.IsIndex ? "INDEX" : "CONSTRAINT")} `{definition.Name}` IF EXISTS");
        foreach (var file in Files) File.Delete(file);
        foreach (var name in new[] { $"py_vector_{Suffix}", $"py_text_{Suffix}" }) await Execute($"DROP INDEX `{name}` IF EXISTS");
    }

    [InteropFact]
    public async Task Both_stacks_embed_a_text_into_the_same_vector()
    {
        string[] texts = ["The rocket engine burns liquid hydrogen.", "Tomatoes need sun!", "", "Über-long hyphenated words_and under_scores 123 ok."];
        var python = await Python.RunAsync("embed", new { dimensions = Dimensions, texts });
        var dotnet = await new HashingEmbedder(Dimensions).GenerateAsync(texts);
        var vectors = python.GetProperty("vectors").EnumerateArray().Select(v => v.EnumerateArray().Select(x => (float)x.GetDouble()).ToArray()).ToArray();
        Assert.Equal(dotnet.Select(e => e.Vector.ToArray()), vectors);
    }

    [InteropFact]
    public async Task Python_reads_a_GraphBridge_graph_with_matching_counts_links_and_top_passages()
    {
        await using var provider = await GraphBridgeGraphAsync();
        var python = Python.Read(await Python.RunAsync("count", new { config = Config(Built) }));
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BuiltContext>();
            var chunks = await db.Set<GraphRagChunk>().CountAsync();
            Assert.Equal((Corpus.Length, chunks, chunks, chunks - Corpus.Length), python);
        }

        // The same vector index, the same question vector: the passages above the fifth score are the same set, with the same scores.
        const string question = "Which engine burns liquid hydrogen?";
        var vector = (await new HashingEmbedder(Dimensions).GenerateAsync([question]))[0].Vector.ToArray();
        var evidence = await provider.GetRequiredService<IGraphRagRetriever>().RetrieveAsync(question,
            new GraphRagRetrievalOptions { Routes = GraphRagRoutes.Vector, CandidatesPerRoute = 5, MaxItems = 5, NeighbourChunks = 0, MaxUtf8Bytes = 65_536 });
        var ours = evidence.Items.Select(i => (Id: i.ChunkId, Score: i.Routes.Single().NativeScore)).ToArray();
        var retrieved = await Python.RunAsync("retrieve", new
        {
            vector_index = Index(Built, GraphMigrationSchemaKind.VectorIndex), fulltext_index = Index(Built, GraphMigrationSchemaKind.FullTextIndex),
            query_text = question, query_vector = vector, top_k = 5
        });
        var theirs = retrieved.GetProperty("vector").EnumerateArray()
            .Select(i => (Id: i.GetProperty("properties").GetProperty("id").GetString()!, Score: i.GetProperty("score").GetDouble())).ToArray();
        Assert.Equal(5, ours.Length);
        Assert.Equal(5, theirs.Length);
        var above = Above(ours);
        Assert.NotEmpty(above);
        Assert.Equal(above.Keys.Order(), Above(theirs).Keys.Order());
        Assert.All(above, p => Assert.Equal(p.Value, Above(theirs)[p.Key], 6));

        // Python's hybrid retriever uses GraphBridge's full-text index too, and finds passages of this graph.
        var hybrid = retrieved.GetProperty("hybrid").EnumerateArray().Select(i => i.GetProperty("properties").GetProperty("id").GetString()).ToArray();
        Assert.NotEmpty(hybrid);
        await using (var scope = provider.CreateAsyncScope())
        {
            var ids = await scope.ServiceProvider.GetRequiredService<BuiltContext>().Set<GraphRagChunk>().Select(c => c.Id).ToListAsync();
            Assert.All(hybrid, id => Assert.Contains(id, ids));
        }
    }

    [InteropFact]
    public async Task Python_reads_the_things_GraphBridge_extracts_through_its_own_entity_labels_and_FROM_CHUNK()
    {
        // GR-16: GraphBridge extracts with a scripted model; neo4j-graphrag counts the entities and its VectorCypherRetriever
        // collects them through FROM_CHUNK, by name, as it does for the entities its own pipeline writes.
        foreach (var step in GraphRagSchema.CreateSteps(Entities, Embeddings, EntityOntology)) await Execute(GraphMigrationStepRenderer.Render(step).Cypher);
        await Execute("CALL db.awaitIndexes(60)");
        await using var provider = Services<EntityContext>(new EntityReplies());
        var receipt = await provider.GetRequiredService<IGraphRagIngestion>().IngestAsync([GraphRagSources.Text("pioneers.md", Pioneers, "text/markdown")]);
        Assert.Equal(GraphRagDocumentStatus.Ingested, Assert.Single(receipt.Documents).Status);

        var vector = (await new HashingEmbedder(Dimensions).GenerateAsync(["Who designed the Analytical Engine?"]))[0].Vector.ToArray();
        var python = await Python.RunAsync("entities", new
        {
            chunk_node_label = Entities.ChunkLabel, vector_index = Index(Entities, GraphMigrationSchemaKind.VectorIndex), query_vector = vector, top_k = 3
        });

        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EntityContext>();
        var mentions = (await db.Set<GraphRagMention<Person>>().ToListAsync()).Select(m => (m.EntityId, m.ChunkId))
            .Concat((await db.Set<GraphRagMention<Place>>().ToListAsync()).Select(m => (m.EntityId, m.ChunkId)))
            .Concat((await db.Set<GraphRagMention<Concept>>().ToListAsync()).Select(m => (m.EntityId, m.ChunkId))).ToArray();
        var names = (await db.Set<Person>().Select(t => new { t.Id, t.Name }).ToListAsync())
            .Concat(await db.Set<Place>().Select(t => new { t.Id, t.Name }).ToListAsync())
            .Concat(await db.Set<Concept>().Select(t => new { t.Id, t.Name }).ToListAsync()).ToDictionary(t => t.Id, t => t.Name);
        Assert.Equal((mentions.Length, mentions.Select(m => m.EntityId).Distinct().Count()),
            (python.GetProperty("mentions").GetInt32(), python.GetProperty("things").GetInt32()));
        Assert.True(mentions.Length >= 4, "The scripted extraction wrote too few mentions to compare.");

        // Each passage neo4j-graphrag retrieves comes with exactly the things GraphBridge extracted from it.
        var retrieved = python.GetProperty("retrieved").EnumerateArray().ToArray();
        Assert.NotEmpty(retrieved);
        foreach (var item in retrieved)
        {
            var id = item.GetProperty("id").GetString()!;
            var expected = mentions.Where(m => m.ChunkId == id).Select(m => names[m.EntityId]).Distinct().Order(StringComparer.Ordinal);
            Assert.Equal(expected, item.GetProperty("entities").EnumerateArray().Select(e => e.GetString()!).Order(StringComparer.Ordinal));
        }
        Assert.Contains(retrieved, item => item.GetProperty("entities").EnumerateArray().Any(e => e.GetString() == "Charles Babbage"));
    }

    private const string Pioneers = "# Pioneers\n\nAda Lovelace wrote the first program for the Analytical Engine while she lived in London.\n\n"
        + "Charles Babbage designed the Analytical Engine and built part of its mill in London.\n\nThe garden behind the house had roses.\n";

    // A scripted model: each name it knows is extracted from the passages that contain it, with the name as its mention.
    private sealed class EntityReplies : IChatClient
    {
        private static readonly (string Kind, string Name)[] Known =
            [("Person", "Ada Lovelace"), ("Person", "Charles Babbage"), ("Concept", "Analytical Engine"), ("Place", "London")];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var passage = messages.ToList()[1].Text;
            var found = Known.Where(k => passage.Contains(k.Name, StringComparison.Ordinal))
                .Select(k => new { kind = k.Kind, name = k.Name, mentions = new[] { k.Name } });
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                JsonSerializer.Serialize(new { entities = found, relationships = Array.Empty<object>(), quotes = Array.Empty<object>() }))));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    [InteropFact]
    public async Task GraphBridge_counts_a_Python_graph_and_its_reset_leaves_that_graph_alone()
    {
        var built = await Python.RunAsync("build", new
        {
            config = Config(Foreign), dimensions = Dimensions, chunk_size = 200, chunk_overlap = 0,
            documents = Corpus.Select(d => new { path = d.Path, text = d.Text }), vector_index = $"py_vector_{Suffix}", fulltext_index = $"py_text_{Suffix}"
        });
        var chunks = built.GetProperty("chunks").GetInt32();
        var python = Python.Read(await Python.RunAsync("count", new { config = Config(Foreign) }));
        Assert.Equal((Corpus.Length, chunks, chunks, chunks - Corpus.Length), python);

        await using var provider = Services<ForeignContext>();
        var health = await provider.GetRequiredService<IGraphRagMaintenance>().CheckAsync();
        Assert.Equal((Corpus.Length, chunks), (health.Documents, health.Chunks));

        // GraphBridge removes only what a GraphBridge run wrote: Python's graph has no run, so the reset refuses and removes nothing.
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetRequiredService<IGraphRagMaintenance>().ResetAsync(new GraphRagResetScope()));
        Assert.Contains("no toolkit run ID; nothing was removed", refused.Message, StringComparison.Ordinal);
        Assert.Equal(python, Python.Read(await Python.RunAsync("count", new { config = Config(Foreign) })));
    }

    [InteropFact]
    public async Task GraphBridge_adopts_a_Python_graph_and_Python_reads_it_the_same_before_and_after_and_after_undo()
    {
        // GR-18 on a graph neo4j-graphrag built, with overlapping passages placed in their source files.
        await Python.RunAsync("build", new
        {
            config = Config(Adopted), dimensions = Dimensions, chunk_size = 120, chunk_overlap = 30,
            documents = Corpus.Select(d => new { path = d.Path, text = d.Text }), vector_index = Adopted.VectorIndexName, fulltext_index = Adopted.FullTextIndexName
        });
        var sources = Corpus.ToDictionary(d => d.Path, d => Write(d.Path, d.Text));
        var plan = GraphRagAdoption.Plan(Adopted, Embeddings, new GraphRagAdoptionOptions { SourceFiles = sources });
        await using var provider = Services<AdoptedContext>(new ExtractiveChatClient());
        await ResetJournal();
        await using (var scope = provider.CreateAsyncScope())
            await PrepareAsync(scope.ServiceProvider.GetRequiredService<AdoptedContext>(), plan, "adopt-" + Suffix);
        const string question = "Which engine burns liquid hydrogen?";
        var vector = (await new HashingEmbedder(Dimensions).GenerateAsync([question]))[0].Vector.ToArray();
        async Task<string> PythonSeesAsync()
        {
            var counts = Python.Read(await Python.RunAsync("count", new { config = Config(Adopted) }));
            var found = await Python.RunAsync("retrieve", new { vector_index = Adopted.VectorIndexName, fulltext_index = Adopted.FullTextIndexName, query_text = question, query_vector = vector, top_k = 3 });
            return counts + " | " + string.Join(" | ", found.GetProperty("vector").EnumerateArray().Select(i => i.GetProperty("properties").GetProperty("text").GetString()));
        }
        var before = await PythonSeesAsync();

        GraphRagAdoptionReceipt receipt;
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AdoptedContext>();
            var preview = await plan.PreviewAsync(db);
            Assert.True(preview.CanApply, string.Join("; ", preview.Blockers));
            Assert.Equal((Corpus.Length, 0), (preview.SourceGroundedDocuments, preview.ChunkGroundedDocuments));
            Assert.Equal(before, await PythonSeesAsync());
            receipt = await plan.ApplyAsync(db);
        }
        Assert.Equal(Corpus.Length, receipt.SourceGroundedDocuments);
        Assert.Equal(before, await PythonSeesAsync());

        var answer = await provider.GetRequiredService<IGraphRagAnswers>().AnswerAsync(question);
        var citation = Assert.Single(answer.Citations);
        Assert.Equal(GraphRagGrounding.Source, citation.Grounding);
        Assert.Equal(citation.Quote, System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(sources["engines.md"]), citation.StartByte, citation.EndByte - citation.StartByte));

        await using (var scope = provider.CreateAsyncScope())
            await plan.UndoAsync(scope.ServiceProvider.GetRequiredService<AdoptedContext>(), receipt.RunId);
        Assert.Equal(before, await PythonSeesAsync());
    }

    private static readonly List<string> Files = [];

    private static string Write(string name, string text)
    {
        var file = Path.Combine(Path.GetTempPath(), $"interop-{Suffix}-{name}");
        File.WriteAllText(file, text, new System.Text.UTF8Encoding(false));
        Files.Add(file);
        return file;
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

    private static Dictionary<string, double> Above((string Id, double Score)[] items)
    {
        var floor = items.Min(i => i.Score);
        return items.Where(i => i.Score > floor + 1e-9).ToDictionary(i => i.Id, i => i.Score);
    }

    private async Task<ServiceProvider> GraphBridgeGraphAsync()
    {
        foreach (var step in GraphRagSchema.CreateSteps(Built, Embeddings)) await Execute(GraphMigrationStepRenderer.Render(step).Cypher);
        await Execute("CALL db.awaitIndexes(60)");
        var provider = Services<BuiltContext>();
        var receipt = await provider.GetRequiredService<IGraphRagIngestion>().IngestAsync(Corpus.Select(d => GraphRagSources.Text(d.Path, d.Text, "text/markdown")));
        Assert.All(receipt.Documents, d => Assert.Equal(GraphRagDocumentStatus.Ingested, d.Status));
        return provider;
    }

    private ServiceProvider Services<TContext>(IChatClient? chat = null) where TContext : DbContext
    {
        var services = new ServiceCollection()
            .AddDbContext<TContext>(o => o.UseNeo4j(fixture.Driver, fixture.Database))
            .AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(new HashingEmbedder(Dimensions));
        if (chat is not null) services.AddSingleton(chat);
        return services.AddGraphRag<TContext>(Embeddings, o => (o.Chunking.MaxUtf8Bytes, o.Retrieval.NeighbourChunks) = (200, 0)).BuildServiceProvider();
    }

    private static string Index(GraphRagLayout layout, GraphMigrationSchemaKind kind) => GraphRagSchema.Definitions(layout, Embeddings).Single(d => d.Kind == kind).Name;

    private async Task Execute(string cypher)
    {
        await using var session = fixture.OpenSession();
        await (await session.RunAsync(cypher)).ConsumeAsync();
    }

    public sealed class BuiltContext(DbContextOptions<BuiltContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.AddGraphRag(Dimensions, o => o.Layout = Built);
    }

    public sealed class ForeignContext(DbContextOptions<ForeignContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.AddGraphRag(Dimensions, o => o.Layout = Foreign);
    }

    public sealed class AdoptedContext(DbContextOptions<AdoptedContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.AddGraphRag(Dimensions, o => o.Layout = Adopted);
    }

    public sealed class EntityContext(DbContextOptions<EntityContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.AddGraphRag(Dimensions, o => (o.Layout, o.Ontology) = (Entities, EntityOntology));
    }
}

/// <summary>Runs only with the disposable fixture and a Python environment that has the pinned neo4j-graphrag.</summary>
public sealed class InteropFactAttribute : FactAttribute
{
    public static bool Enabled => Environment.GetEnvironmentVariable("GRAPHBRIDGE_INTEGRATION") == "1"
        && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GRAPHBRIDGE_INTEROP_PYTHON"));

    public InteropFactAttribute()
    {
        if (!Enabled) Skip = "Set GRAPHBRIDGE_INTEGRATION=1 with the fixture, and GRAPHBRIDGE_INTEROP_PYTHON to the Python of tests/interop/python's environment.";
    }
}

/// <summary>Runs tests/interop/python/interop.py: a JSON request on standard input, a JSON result on standard output.</summary>
internal static class Python
{
    public static async Task<JsonElement> RunAsync(string command, object request)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("GRAPHBRIDGE_INTEROP_PYTHON")!)
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
        };
        start.ArgumentList.Add("-I");
        start.ArgumentList.Add(Repository("tests", "interop", "python", "interop.py"));
        start.ArgumentList.Add(command);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.StandardInput.WriteAsync(JsonSerializer.Serialize(request));
        process.StandardInput.Close();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        try { await process.WaitForExitAsync(timeout.Token); }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
        var (stdout, stderr) = (await output, await error);
        Assert.DoesNotContain(Environment.GetEnvironmentVariable("GRAPHBRIDGE_NEO4J_PASSWORD")!, stdout + stderr);
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"interop.py {command} exited with {process.ExitCode}: {(stderr.Length > 2_000 ? stderr[^2_000..] : stderr)}");
        return JsonDocument.Parse(stdout).RootElement.Clone();
    }

    public static (int Documents, int Chunks, int ChunkLinks, int NextLinks) Read(JsonElement counts)
        => (counts.GetProperty("documents").GetInt32(), counts.GetProperty("chunks").GetInt32(), counts.GetProperty("chunk_links").GetInt32(),
            counts.GetProperty("next_links").GetInt32());

    public static string Repository(params string[] path)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "GraphBridge.sln"))) directory = directory.Parent;
        return Path.Combine([directory?.FullName ?? throw new InvalidOperationException("The repository root was not found."), .. path]);
    }
}
