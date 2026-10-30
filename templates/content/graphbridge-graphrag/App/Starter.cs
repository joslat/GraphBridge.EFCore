using GraphBridge.EntityFrameworkCore.Migrations;
using GraphBridge.GraphRag;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace GraphRagStarter;

/// <summary>Your EF Core context. <c>AddGraphRag</c> maps documents, sections, chunks, their links, runs and leases.</summary>
public sealed class StarterContext(DbContextOptions<StarterContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.AddGraphRag(Starter.EmbeddingSet.Dimensions);
}

public static class Starter
{
    /// <summary>
    /// The embedding model and its dimensions. The offline stand-in uses 256; for a real model, name it and use its
    /// dimensions, for example <c>new("text-embedding-3-small", 1536)</c>. Changing it is a new set and a new vector index.
    /// </summary>
    public static GraphRagEmbeddingSet EmbeddingSet { get; } = new("starter-hashing", 256);

    /// <summary>The schema as your own reviewed migration plan: key constraints, the vector index and a full-text index.</summary>
    public static GraphMigrationPlan SchemaPlan { get; } = new("graphrag-starter-" + EmbeddingSet.Id, null,
        GraphRagSchema.CreateSteps(GraphRagLayout.Default, EmbeddingSet),
        GraphMigrationRollbackPolicy.NoRollback("Dropping the indexes is a separate, reviewed migration."));

    /// <summary>
    /// The models and the toolkit. Offline stand-ins by default, so the starter runs with no key. To use a real model,
    /// register your <see cref="IChatClient"/> and <see cref="IEmbeddingGenerator{TInput, TEmbedding}"/> here instead, from any
    /// Microsoft.Extensions.AI provider, and change <see cref="EmbeddingSet"/> to match.
    /// </summary>
    public static IServiceCollection AddStarter(this IServiceCollection services)
        => services
            .AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(new HashingEmbedder(EmbeddingSet.Dimensions))
            .AddSingleton<IChatClient>(new ExtractiveChatClient())
            .AddGraphRag<StarterContext>(EmbeddingSet);
}
