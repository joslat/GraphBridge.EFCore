using GraphBridge.EntityFrameworkCore.Migrations;
using GraphBridge.GraphRag;
using GraphBridge.GraphRag.Pdf;
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
    /// <summary>The models named by <c>AI_INFERENCE_PROVIDER</c>, or the recorded ones when replaying; null runs offline.</summary>
    public static SampleModelSettings? Models { get; } = SampleModelSettings.FromEnvironment(Recordings);

    /// <summary>Real model calls recorded by the real-model tests on <see cref="RecordedDocs"/>: replayed with <c>GRAPHBRIDGE_AI_MODE=replay</c>.</summary>
    public static string Recordings => Path.Combine(AppContext.BaseDirectory, "data", "quickstart-recordings.json");

    /// <summary>The copy of three documentation pages that the recorded calls were made on.</summary>
    public static string RecordedDocs => Path.Combine(AppContext.BaseDirectory, "data", "docs");

    /// <summary>A folder's Markdown and PDF files. The shipped copy is named by its address in the repository (<see cref="SampleData"/>).</summary>
    public static IReadOnlyList<GraphRagSource> Sources(string folder)
        => [.. SampleData.Folder(folder, "07-GraphRagQuickStart"), .. SampleData.Folder(folder, "07-GraphRagQuickStart", "*.pdf")];

    /// <summary>The embedding model's set, named after it with its dimensions; offline, the stand-in embedder's.</summary>
    public static GraphRagEmbeddingSet EmbeddingSet { get; } = Models is { } models
        ? new(models.EmbeddingModel, models.EmbeddingDimensions)
        : new("graphbridge-sample-hashing", 256);

    /// <summary>The schema as the application's own reviewed migration plan, one per embedding set.</summary>
    public static GraphMigrationPlan SchemaPlan { get; } = new("graphrag-quickstart-" + EmbeddingSet.Id, null,
        GraphRagSchema.CreateSteps(GraphRagLayout.Default, EmbeddingSet),
        GraphMigrationRollbackPolicy.NoRollback("Dropping the indexes is a separate, reviewed migration."));

    /// <summary>
    /// Everything the sample needs. With no model settings it runs offline, with no key: the stand-ins are in
    /// <c>samples/GraphRagShared</c>.
    /// </summary>
    public static IServiceCollection AddQuickStart(this IServiceCollection services)
    {
        if (Models is { } models)
        {
            var (chat, embeddings) = models.CreateClients();
            services.AddSingleton(embeddings).AddSingleton(chat);
        }
        else
        {
            services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(new HashingEmbedder(EmbeddingSet.Dimensions))
                .AddSingleton<IChatClient>(new ExtractiveChatClient());
        }
        // PDFs load through their text layer, page by page, so citations name their pages (GraphBridge.GraphRag.Pdf).
        return services.AddGraphRag<QuickStartContext>(EmbeddingSet, options => options.Chunking.MaxUtf8Bytes = 1_024).AddGraphRagPdf();
    }
}
