using GraphBridge.GraphRag;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers the GraphRAG toolkit (ADR 0052, section 3).</summary>
public static class GraphRagServiceCollectionExtensions
{
    /// <summary>
    /// Registers loading, chunking, ingestion, retrieval, answers and maintenance for <typeparamref name="TContext"/>, whose model calls
    /// <c>AddGraphRag</c> with the set's dimensions. Uses the <see cref="IEmbeddingGenerator{TInput, TEmbedding}"/> and, for
    /// answers, the <see cref="IChatClient"/> registered in the container, and an <see cref="IGraphRagTokenCounter"/> if one is
    /// registered. When the model maps an ontology, ingestion also extracts, through an <see cref="IChatClient"/> keyed
    /// <see cref="GraphRagServiceKeys.ExtractionChatClient"/> or the unkeyed one, and an <see cref="IGraphRagExtractionVerifier"/> if one is
    /// registered. Each document is written, and each retrieval read, through a new scope's context.
    /// </summary>
    public static IServiceCollection AddGraphRag<TContext>(this IServiceCollection services, GraphRagEmbeddingSet embeddingSet,
        Action<GraphRagOptions>? configure = null) where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(embeddingSet);
        embeddingSet.Validate();
        var options = new GraphRagOptions();
        configure?.Invoke(options);
        options.Chunking.Validate();
        options.Ingestion.Validate();
        options.Retrieval.Validate();
        options.Extraction.Validate();
        services.TryAddSingleton(embeddingSet);
        services.TryAddSingleton(options);
        services.AddSingleton<IGraphRagDocumentLoader>(_ => new GraphRagTextLoader(options.Ingestion.MaxDocumentBytes));
        services.TryAddSingleton<IGraphRagChunker>(provider => new GraphRagChunker(options.Chunking, provider.GetService<IGraphRagTokenCounter>()));
        services.TryAddTransient<IGraphRagIngestion>(provider => new GraphRagIngestion<TContext>(
            provider.GetRequiredService<IServiceScopeFactory>(), embeddingSet, options,
            provider.GetServices<IGraphRagDocumentLoader>(), provider.GetRequiredService<IGraphRagChunker>(),
            provider.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>(), provider.GetService<TimeProvider>(),
            () => provider.GetKeyedService<IChatClient>(GraphRagServiceKeys.ExtractionChatClient) ?? provider.GetService<IChatClient>(),
            provider.GetService<IGraphRagExtractionVerifier>()));
        services.TryAddTransient<IGraphRagRetriever>(provider => new GraphRagRetriever<TContext>(provider.GetRequiredService<IServiceScopeFactory>(),
            embeddingSet, options, provider.GetService<IEmbeddingGenerator<string, Embedding<float>>>()));
        services.TryAddTransient<IGraphRagAnswers>(provider => new GraphRagAnswerer(provider.GetRequiredService<IGraphRagRetriever>(),
            () => provider.GetService<IChatClient>()));
        services.TryAddTransient<IGraphRagMaintenance>(provider => new GraphRagMaintenance<TContext>(provider.GetRequiredService<IServiceScopeFactory>(),
            embeddingSet, provider.GetRequiredService<IGraphRagIngestion>(), provider.GetService<TimeProvider>()));
        return services;
    }
}
