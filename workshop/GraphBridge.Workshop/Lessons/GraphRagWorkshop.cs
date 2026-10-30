using GraphBridge.EntityFrameworkCore.Migrations;
using GraphBridge.GraphRag;
using GraphBridge.GraphRag.Ontologies;
using GraphBridge.Samples.GraphRagShared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Neo4j.Driver;

namespace GraphBridge.Workshop.Lessons;

/// <summary>
/// What lessons 32 to 35 share: lesson 31's labels, the novel ontology under labels of the workshop's own, offline stand-ins for
/// the models, the schema as reviewed steps, and a clean-up that removes everything they wrote, even when a lesson fails.
/// </summary>
public static class GraphRagWorkshop
{
    public static GraphRagLayout Layout => Lesson31CitedAnswers.Layout;

    public static GraphRagEmbeddingSet EmbeddingSet { get; } = new("workshop-hashing", 64);

    // The prefix keeps the things apart from the movie graph's Person and from any other application's labels.
    public static GraphRagOntology Ontology { get; } = GraphRagOntologies.Novel.WithLabelPrefix("Workshop");

    /// <summary>An extraction reply with nothing in it, for lessons about passages rather than things.</summary>
    public const string NothingExtracted = """{"entities": [], "relationships": [], "quotes": []}""";

    /// <summary>The labels these lessons write, which the safety guard allows.</summary>
    public static IEnumerable<string> Labels => Ontology.Classes.Select(c => c.Label);

    /// <summary>A database is needed, and Neo4j 2026.09 or later for the toolkit's retrieval; otherwise the lesson stops here.</summary>
    public static async Task<bool> ReadyAsync(LessonContext lesson, string what, CancellationToken cancellationToken)
    {
        if (!lesson.NeedsDatabase(what)) return false;
        var version = (await WorkshopData.ReadAsync(lesson.Connection!,
            "CALL dbms.components() YIELD name, versions WHERE name = 'Neo4j Kernel' RETURN versions[0] AS version",
            r => r["version"].As<string>(), cancellationToken)).Single();
        if (Version.TryParse(version.Split('-')[0], out var parsed) && parsed >= new Version(2026, 9)) return true;
        lesson.Skip($"Neo4j {version} can't run the toolkit's retrieval, which needs Neo4j 2026.09 or later with Cypher 25");
        return false;
    }

    /// <summary>The toolkit with stand-ins: hashed embeddings, an answer model and, when given, an extraction model.</summary>
    public static ServiceProvider Services(LessonContext lesson, IChatClient answers, IChatClient? extraction = null,
        Action<IServiceCollection>? more = null)
    {
        var services = new ServiceCollection()
            .AddDbContext<RagContext>(o => o.UseNeo4j(lesson.Driver, lesson.Database))
            .AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(new HashingEmbedder(EmbeddingSet.Dimensions))
            .AddSingleton(answers);
        if (extraction is not null) services.AddKeyedSingleton(GraphRagServiceKeys.ExtractionChatClient, extraction);
        services.AddGraphRag<RagContext>(EmbeddingSet, o => o.Chunking.MaxUtf8Bytes = 400);
        more?.Invoke(services);
        return services.BuildServiceProvider();
    }

    /// <summary>The schema the lessons need, as the reviewed steps an application puts in a migration (lesson 23).</summary>
    public static async Task ProvisionAsync(WorkshopConnection connection, CancellationToken cancellationToken)
    {
        foreach (var step in GraphRagSchema.CreateSteps(Layout, EmbeddingSet, Ontology))
            await WorkshopData.ExecuteAsync(connection, GraphMigrationStepRenderer.Render(step).Cypher, cancellationToken);
        foreach (var index in GraphRagSchema.Definitions(Layout, EmbeddingSet, Ontology).Where(d => d.IsIndex))
            await WorkshopData.ExecuteAsync(connection, "CALL db.awaitIndex($index, 60)", cancellationToken, ("index", index.Name));
    }

    /// <summary>Removes the documents, things, runs, leases and schema the lesson wrote: relationships first, never DETACH DELETE.</summary>
    public static async Task CleanAsync(WorkshopConnection connection, ServiceProvider services)
    {
        var none = CancellationToken.None;
        await services.GetRequiredService<IGraphRagMaintenance>().ResetAsync(new GraphRagResetScope(), none);
        await services.GetRequiredService<IGraphRagMaintenance>().RemoveOrphansAsync(none);
        var things = string.Join(" OR ", Labels.Select(l => "n:" + l));
        await WorkshopData.ExecuteAsync(connection, $"MATCH (n)-[r]-() WHERE {things} DELETE r", none);
        await WorkshopData.ExecuteAsync(connection, $"MATCH (n) WHERE {things} OR n:{Layout.RunLabel} OR n:{Layout.LeaseLabel} DELETE n", none);
        foreach (var definition in GraphRagSchema.Definitions(Layout, EmbeddingSet, Ontology))
            await WorkshopData.ExecuteAsync(connection, $"DROP {(definition.IsIndex ? "INDEX" : "CONSTRAINT")} {definition.Name} IF EXISTS", none);
    }

    public sealed class RagContext(DbContextOptions<RagContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.AddGraphRag(EmbeddingSet.Dimensions, o => (o.Layout, o.Ontology) = (Layout, Ontology));
    }

    /// <summary>
    /// A model stand-in that gives written replies, as a recorded model would: the first reply whose text the request's passage
    /// contains, or the one reply given without a match.
    /// </summary>
    public sealed class WrittenReply(params (string Match, string Reply)[] replies) : IChatClient
    {
        public WrittenReply(string reply) : this(("", reply)) { }

        public int Calls { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            var passage = messages.Last().Text;
            var reply = replies.FirstOrDefault(r => passage.Contains(r.Match, StringComparison.Ordinal)).Reply ?? NothingExtracted;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, reply)));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException("The written reply comes in one response.");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
