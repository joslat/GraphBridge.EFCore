using GraphBridge.EntityFrameworkCore.Migrations;
using GraphBridge.GraphRag;
using GraphBridge.Samples.GraphRagShared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace GraphBridge.Samples.GraphRag;

/// <summary>The application's context: its own entities would sit next to the GraphRAG lexical layer.</summary>
public sealed class QuickStartContext(DbContextOptions<QuickStartContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.AddGraphRag(QuickStart.EmbeddingSet.Dimensions);
}

public static class QuickStart
{
    /// <summary>The offline embedder's set. A real model would name itself here, with its own dimensions.</summary>
    public static GraphRagEmbeddingSet EmbeddingSet { get; } = new("graphbridge-sample-hashing", 256);

    /// <summary>The schema as the application's own reviewed migration plan.</summary>
    public static GraphMigrationPlan SchemaPlan { get; } = new("graphrag-quickstart-001", null,
        GraphRagSchema.CreateSteps(GraphRagLayout.Default, EmbeddingSet),
        GraphMigrationRollbackPolicy.NoRollback("Dropping the indexes is a separate, reviewed migration."));

    /// <summary>
    /// Everything the sample needs, offline: no model, no key. The stand-ins are in <c>samples/GraphRagShared</c>; register a real
    /// IEmbeddingGenerator and IChatClient instead to use models.
    /// </summary>
    public static IServiceCollection AddQuickStart(this IServiceCollection services)
        => services
            .AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(new HashingEmbedder(EmbeddingSet.Dimensions))
            .AddSingleton<IChatClient>(new ExtractiveChatClient())
            .AddGraphRag<QuickStartContext>(EmbeddingSet, options => options.Chunking.MaxUtf8Bytes = 1_024);
}
