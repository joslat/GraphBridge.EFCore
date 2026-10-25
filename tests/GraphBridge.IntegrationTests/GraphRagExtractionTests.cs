using System.Text;
using GraphBridge.EntityFrameworkCore.Migrations;
using GraphBridge.GraphRag;
using GraphBridge.GraphRag.Ontologies;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Neo4j.Driver;

namespace GraphBridge.IntegrationTests;

// GraphRAG wave 4 acceptance (ADR 0052, section 7) on the disposable fixture, with recorded replies instead of a model.
// Each test prefixes the ontology's labels and owns its layout labels and schema prefix.
[Collection(Neo4jIntegrationCollection.Name)]
public sealed class GraphRagExtractionTests(DisposableNeo4jFixture fixture) : IAsyncLifetime
{
    // Written for these tests; the characters are from the public-domain novel, the sentences are not.
    internal const string First = "Alice met the White Rabbit near the river. “I’m late!” said the Rabbit to Alice. The man waved.";
    internal const string Second = "Later, Alice followed the Rabbit into Wonderland. The man laughed.";

    internal const string FirstReply = """
        {"entities": [
          {"kind": "Character", "name": "Alice", "aliases": ["Miss Alice"], "description": "A curious girl.", "mentions": ["Alice"]},
          {"kind": "Character", "name": "White Rabbit", "aliases": ["the Rabbit"], "mentions": ["the White Rabbit", "the Rabbit"]},
          {"kind": "Character", "name": "the man", "mentions": ["The man"]},
          {"kind": "Place", "name": "the river", "mentions": ["the river"]}
        ],
        "relationships": [
          {"type": "RELATED_TO", "from": "Alice", "fromKind": "Character", "to": "White Rabbit", "toKind": "Character", "kind": "friend"}
        ],
        "quotes": [{"type": "SAYS", "speaker": "the Rabbit", "words": "I'm late!", "addressee": "Alice"}]}
        """;

    internal const string SecondReply = """
        {"entities": [
          {"kind": "Character", "name": "Alice", "aliases": ["Alice Liddell"], "mentions": ["Alice"]},
          {"kind": "Character", "name": "White Rabbit", "mentions": ["the Rabbit"]},
          {"kind": "Place", "name": "Wonderland", "mentions": ["Wonderland"]},
          {"kind": "Character", "name": "The Man", "mentions": ["The man"]}
        ],
        "relationships": [
          {"type": "RELATED_TO", "from": "Alice", "fromKind": "Character", "to": "the Rabbit", "toKind": "Character", "kind": "rival"}
        ],
        "quotes": []}
        """;

    private readonly string _suffix = Guid.NewGuid().ToString("N")[..12];
    private readonly GraphRagEmbeddingSet _set = new("fake-hash-8", 8);
    private GraphRagOntology Ontology => _ontology ??= GraphRagOntologies.Novel.WithLabelPrefix("X" + _suffix);
    private GraphRagOntology? _ontology;
    private GraphRagLayout Layout => GraphRagLayout.Default with
    {
        DocumentLabel = "XDocument_" + _suffix, SectionLabel = "XSection_" + _suffix, ChunkLabel = "XChunk_" + _suffix,
        RunLabel = "XRun_" + _suffix, LeaseLabel = "XLease_" + _suffix, SchemaPrefix = "gbx_" + _suffix
    };

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (!Enabled) return;
        var labels = string.Join(" OR ", new[] { Layout.DocumentLabel, Layout.SectionLabel, Layout.ChunkLabel, Layout.RunLabel, Layout.LeaseLabel }
            .Concat(Ontology.Classes.Select(c => c.Label)).Select(l => "n:" + l));
        await Execute($"MATCH (n)-[r]-() WHERE {labels} DELETE r");
        await Execute($"MATCH (n) WHERE {labels} DELETE n");
        foreach (var definition in GraphRagSchema.Definitions(Layout, _set, Ontology))
            await Execute($"DROP {(definition.IsIndex ? "INDEX" : "CONSTRAINT")} {definition.Name} IF EXISTS");
    }

    private static bool Enabled => Environment.GetEnvironmentVariable("GRAPHBRIDGE_INTEGRATION") == "1";

    [Neo4jIntegrationFact]
    public async Task Extraction_writes_typed_things_mentions_facts_and_quotes_that_LINQ_reads_and_a_rerun_writes_nothing_new()
    {
        await ProvisionAsync();
        await using var provider = Services(new Replies());
        var receipt = await provider.GetRequiredService<IGraphRagIngestion>().IngestAsync(Sources());
        Assert.All(receipt.Documents, d => Assert.Equal(GraphRagDocumentStatus.Ingested, d.Status));
        // Counts are per document: both documents write Alice, the White Rabbit and the fact.
        var extraction = receipt.Extraction!;
        Assert.Equal((2, 8, 9, 2, 1), (extraction.ChatCalls, extraction.Entities, extraction.Mentions, extraction.Facts, extraction.Quotes));
        var (first, second) = (receipt.Documents[0].DocumentId, receipt.Documents[1].DocumentId);

        await using var db = Context();
        Assert.Equal(["Alice", "White Rabbit"], await db.Set<Character>().Where(c => !c.IsPlaceholder).OrderBy(c => c.Name).Select(c => c.Name).ToListAsync());
        Assert.Equal(2, await db.Set<Character>().CountAsync(c => c.IsPlaceholder));
        Assert.Equal(["Wonderland", "the river"], (await db.Set<Place>().Select(p => p.Name).ToListAsync()).Order(StringComparer.Ordinal));
        var alice = await db.Set<Character>().SingleAsync(c => c.Name == "Alice");
        var rabbit = await db.Set<Character>().SingleAsync(c => c.Name == "White Rabbit");
        // Aliases unioned across documents; the first document's description kept.
        Assert.Equal(("Alice Liddell\nMiss Alice", "A curious girl."), (alice.Aliases, alice.Description));

        var fact = await db.Set<RelatedTo<Character, Character>>().SingleAsync();
        Assert.Equal((alice.Id, rabbit.Id, "friend"), (fact.FromId, fact.ToId, fact.Kind));
        Assert.Equal(string.Join('\n', new[] { first, second }.Order(StringComparer.Ordinal)), fact.DocumentIds);

        var mentions = await db.Set<GraphRagMention<Character>>().Where(m => m.EntityId == alice.Id).ToListAsync();
        Assert.Equal(2, mentions.Count);
        var source = Encoding.UTF8.GetBytes(First);
        foreach (var mention in await db.Set<GraphRagMention<Character>>().Where(m => m.EntityId == rabbit.Id).ToListAsync())
        {
            var chunk = await db.Set<GraphRagChunk>().SingleAsync(c => c.Id == mention.ChunkId);
            if (chunk.DocumentId == first) Assert.Equal(mention.Quote, Encoding.UTF8.GetString(source, mention.StartByte, mention.EndByte - mention.StartByte));
        }
        var says = await db.Set<Says>().SingleAsync();
        Assert.Equal(("I’m late!", "Alice", rabbit.Id), (says.Words, says.Addressee, says.SpeakerId));
        Assert.Equal(says.Words, Encoding.UTF8.GetString(source, says.StartByte, says.EndByte - says.StartByte));

        var before = await Count(CountAll());
        var again = await provider.GetRequiredService<IGraphRagIngestion>().IngestAsync(Sources());
        Assert.All(again.Documents, d => Assert.Equal(GraphRagDocumentStatus.Unchanged, d.Status));
        Assert.Equal((0, 0), (again.Extraction!.ChatCalls, again.Extraction.Entities));
        Assert.Equal(before, await Count(CountAll()));

        var health = await provider.GetRequiredService<IGraphRagMaintenance>().CheckAsync();
        Assert.DoesNotContain("orphan-entities", health.Findings);
        Assert.DoesNotContain("stale-facts", health.Findings);
        Assert.DoesNotContain("schema-missing", health.Findings);
    }

    [Neo4jIntegrationFact]
    public async Task Removing_a_document_drops_its_mentions_and_quotes_takes_it_off_facts_and_leaves_orphans_to_clean_up()
    {
        await ProvisionAsync();
        await using var provider = Services(new Replies());
        var receipt = await provider.GetRequiredService<IGraphRagIngestion>().IngestAsync(Sources());
        var (first, second) = (receipt.Documents[0].DocumentId, receipt.Documents[1].DocumentId);
        var maintenance = provider.GetRequiredService<IGraphRagMaintenance>();

        var removed = await maintenance.RemoveSourceAsync(first);
        Assert.Equal(1, removed.Documents);
        await using (var db = Context())
        {
            Assert.Equal(second, (await db.Set<RelatedTo<Character, Character>>().SingleAsync()).DocumentIds);
            Assert.Empty(await db.Set<Says>().ToListAsync());
            var alice = await db.Set<Character>().SingleAsync(c => c.Name == "Alice");
            Assert.Single(await db.Set<GraphRagMention<Character>>().Where(m => m.EntityId == alice.Id).ToListAsync());
        }
        // The river and the first document's placeholder lost their only mentions.
        var health = await maintenance.CheckAsync();
        Assert.Equal(2, health.OrphanEntities);
        Assert.Contains("orphan-entities", health.Findings);
        var orphans = await maintenance.RemoveOrphansAsync();
        Assert.Equal(2, orphans.Entities);
        Assert.DoesNotContain("orphan-entities", (await maintenance.CheckAsync()).Findings);

        await maintenance.RemoveSourceAsync(second);
        Assert.Equal(4, (await maintenance.RemoveOrphansAsync()).Entities);
        await using (var db = Context())
        {
            Assert.Empty(await db.Set<RelatedTo<Character, Character>>().ToListAsync());
            Assert.Equal(0, await db.Set<Character>().CountAsync());
            Assert.Equal(0, await db.Set<Place>().CountAsync());
        }
        Assert.Equal(0, await Count(CountAll()));
    }

    [Neo4jIntegrationFact]
    public async Task A_failed_extraction_writes_nothing_of_the_document_and_the_next_run_retries_it()
    {
        await ProvisionAsync();
        await using (var failing = Services(new Replies { Fail = true }))
        {
            var receipt = await failing.GetRequiredService<IGraphRagIngestion>().IngestAsync(Sources().Take(1));
            var outcome = Assert.Single(receipt.Documents);
            Assert.Equal((GraphRagDocumentStatus.Failed, "extraction-failed"), (outcome.Status, outcome.ErrorCode));
            await using var db = Context();
            Assert.Equal(0, await db.Set<GraphRagDocument>().CountAsync());
            Assert.Equal(0, await db.Set<GraphRagChunk>().CountAsync());
            Assert.Equal(0, await db.Set<Character>().CountAsync());
        }
        await using var provider = Services(new Replies());
        var retried = await provider.GetRequiredService<IGraphRagIngestion>().IngestAsync(Sources().Take(1));
        Assert.Equal(GraphRagDocumentStatus.Ingested, Assert.Single(retried.Documents).Status);
    }

    private static IEnumerable<GraphRagSource> Sources() => [GraphRagSources.Text("first", First), GraphRagSources.Text("second", Second)];

    private string CountAll()
    {
        var labels = string.Join(" OR ", new[] { Layout.DocumentLabel, Layout.SectionLabel, Layout.ChunkLabel }.Concat(Ontology.Classes.Select(c => c.Label)).Select(l => "n:" + l));
        return $"MATCH (n) WHERE {labels} OPTIONAL MATCH (n)-[r]->() RETURN count(DISTINCT n) + count(r) AS value";
    }

    private async Task ProvisionAsync()
    {
        foreach (var step in GraphRagSchema.CreateSteps(Layout, _set, Ontology))
            await Execute(GraphMigrationStepRenderer.Render(step).Cypher);
        foreach (var index in new[] { _set.VectorIndexNameFor(Layout.SchemaPrefix), GraphRagSchema.FullTextIndexName(Layout), GraphRagSchema.EntityNamesIndexName(Layout) })
            await Execute("CALL db.awaitIndex($index, 60)", new { index });
    }

    private ServiceProvider Services(IChatClient chat)
    {
        var services = new ServiceCollection();
        services.AddKeyedSingleton(GraphRagServiceKeys.ExtractionChatClient, chat);
        services.AddDbContext<RagContext>(o => o.UseNeo4j(fixture.Driver, fixture.Database).ReplaceService<IModelCacheKeyFactory, OntologyKey>());
        services.AddSingleton(Layout);
        services.AddSingleton(Ontology);
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(new HashGenerator());
        services.AddGraphRag<RagContext>(_set, o => o.Chunking.MaxUtf8Bytes = 4096);
        return services.BuildServiceProvider();
    }

    private RagContext Context() => new(new DbContextOptionsBuilder<RagContext>().UseNeo4j(fixture.Driver, fixture.Database)
        .ReplaceService<IModelCacheKeyFactory, OntologyKey>().Options, Layout, Ontology);

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

    public sealed class RagContext(DbContextOptions<RagContext> options, GraphRagLayout layout, GraphRagOntology ontology) : DbContext(options)
    {
        public GraphRagLayout Layout { get; } = layout;
        public GraphRagOntology Ontology { get; } = ontology;
        protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.AddGraphRag(8, o => (o.Layout, o.Ontology) = (Layout, Ontology));
    }

    public sealed class OntologyKey : IModelCacheKeyFactory
    {
        public object Create(DbContext context, bool designTime) => (context.GetType(), ((RagContext)context).Layout, ((RagContext)context).Ontology, designTime);
    }

    // Recorded replies by passage, as a model would give them; Fail stands for a client that refuses every call.
    internal sealed class Replies : IChatClient
    {
        public bool Fail { get; init; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            if (Fail) throw new InvalidOperationException("The model refused the request.");
            var passage = messages.Last().Text;
            var reply = passage.Contains("near the river", StringComparison.Ordinal) ? FirstReply : SecondReply;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, reply)));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class HashGenerator : IEmbeddingGenerator<string, Embedding<float>>
    {
        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values, EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(values.Select(text =>
                new Embedding<float>(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text)).Take(8).Select(b => 1f + b).ToArray()))));

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
