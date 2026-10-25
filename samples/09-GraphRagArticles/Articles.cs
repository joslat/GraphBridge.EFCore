using System.Text.Json;
using GraphBridge.EntityFrameworkCore.Migrations;
using GraphBridge.GraphRag;
using GraphBridge.GraphRag.Ontologies;
using GraphBridge.Samples.GraphRagShared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace GraphBridge.Samples.GraphRagArticles;

/// <summary>
/// The application's context: the article ontology over articles, sections and passages. Its labels are prefixed
/// (<c>NewsPerson</c>, <c>NewsPlace</c>, ...) so it can share a database with sample 08, which also maps <c>Place</c> and <c>Event</c>.
/// </summary>
public sealed class ArticlesContext(DbContextOptions<ArticlesContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
        => modelBuilder.AddGraphRag(Articles.EmbeddingSet.Dimensions, o => (o.Layout, o.Ontology) = (Articles.Layout, Articles.Ontology));
}

public static class Articles
{
    public static SampleModelSettings? Models { get; } = SampleModelSettings.FromEnvironment();

    public static GraphRagEmbeddingSet EmbeddingSet { get; } = Models is { } models
        ? new(models.EmbeddingModel, models.EmbeddingDimensions)
        : new("graphbridge-sample-hashing", 256);

    public static GraphRagOntology Ontology { get; } = GraphRagOntologies.Article.WithLabelPrefix("News");

    public static GraphRagLayout Layout { get; } = GraphRagLayout.Default with
    {
        DocumentLabel = "Article", SectionLabel = "ArticleSection", ChunkLabel = "ArticlePassage", RunLabel = "ArticleRun",
        LeaseLabel = "ArticleLease", SchemaPrefix = "articles"
    };

    public static GraphMigrationPlan SchemaPlan { get; } = new("graphrag-articles-" + EmbeddingSet.Id, null,
        GraphRagSchema.CreateSteps(Layout, EmbeddingSet, Ontology),
        GraphMigrationRollbackPolicy.NoRollback("Dropping the constraints and indexes is a separate, reviewed migration."));

    /// <summary>The two fictional articles shipped with the sample, written for it.</summary>
    public static string Folder => Path.Combine(AppContext.BaseDirectory, "data", "articles");

    public static string RecordedReplies => Path.Combine(AppContext.BaseDirectory, "data", "article-replies.json");

    public static IServiceCollection AddArticles(this IServiceCollection services)
    {
        if (Models is { } models)
        {
            var client = new OpenAiCompatibleClient(models);
            services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(client).AddSingleton<IChatClient>(client);
        }
        else
        {
            services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(new HashingEmbedder(EmbeddingSet.Dimensions))
                .AddSingleton<IChatClient>(new ExtractiveChatClient())
                .AddKeyedSingleton<IChatClient>(GraphRagServiceKeys.ExtractionChatClient, new RecordedExtractionClient(RecordedReplies));
        }
        return services.AddGraphRag<ArticlesContext>(EmbeddingSet, options => options.Chunking.MaxUtf8Bytes = 4_096);
    }

    /// <summary>
    /// The Markdown files of a folder, with each one's metadata from <c>articles.json</c> next to the folder when it lists the
    /// file: title, published date, publisher and URL are the source's own, stored on the article and never extracted.
    /// </summary>
    public static IReadOnlyList<GraphRagSource> Sources(string folder)
    {
        var index = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar))!, "articles.json");
        var metadata = new Dictionary<string, GraphRagSourceMetadata>(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(index))
        {
            using var json = JsonDocument.Parse(File.ReadAllText(index));
            foreach (var entry in json.RootElement.EnumerateArray())
                metadata[entry.GetProperty("file").GetString()!] = new(Text(entry, "title"), Text(entry, "published"), Text(entry, "publisher"), Text(entry, "url"));
        }
        return GraphRagSources.Folder(folder)
            .Select(source => metadata.TryGetValue(Path.GetFileName(source.Path!), out var found) ? source with { Metadata = found } : source)
            .ToArray();

        static string? Text(JsonElement entry, string name) => entry.TryGetProperty(name, out var value) ? value.GetString() : null;
    }

    /// <summary>
    /// The report the sample prints, all through typed LINQ over the ontology's classes. <c>Organization</c> is spelled out:
    /// the repository's tests also compile sample 02, whose namespace has that name.
    /// </summary>
    public static async Task<ArticlesReport> ReportAsync(ArticlesContext db, CancellationToken cancellationToken = default)
    {
        var people = await db.Set<Person>().Where(p => !p.IsPlaceholder).ToListAsync(cancellationToken);
        var organizations = await db.Set<global::GraphBridge.GraphRag.Ontologies.Organization>().ToListAsync(cancellationToken);
        var names = people.Select(p => (p.Id, p.Name)).Concat(organizations.Select(o => (o.Id, o.Name))).ToDictionary(p => p.Id, p => p.Name);
        string Name(string id) => names.TryGetValue(id, out var name) ? name : "someone";

        var documents = await db.Set<GraphRagDocument>().OrderBy(d => d.Published).ToListAsync(cancellationToken);
        var authored = await db.Set<Authored>().ToListAsync(cancellationToken);
        var topics = (await db.Set<Topic>().ToListAsync(cancellationToken)).ToDictionary(t => t.Id, t => t.Name);
        var about = await db.Set<About>().ToListAsync(cancellationToken);
        var articles = documents.Select(d => new ArticleLine(d.Title ?? d.SourceUri, d.Published, d.Publisher,
            authored.Where(a => a.ToId == d.Id).Select(a => Name(a.FromId)).ToArray(),
            about.Where(a => a.FromId == d.Id).Select(a => topics.GetValueOrDefault(a.ToId, "?")).ToArray())).ToArray();

        var jobs = (await db.Set<WorksFor>().ToListAsync(cancellationToken))
            .Select(w => new JobLine(Name(w.FromId), w.Role, Name(w.ToId), w.DocumentIds.Split('\n').Length)).ToArray();
        var quotes = (await db.Set<Quoted>().ToListAsync(cancellationToken)).OrderBy(q => q.ChunkId).ThenBy(q => q.StartByte)
            .Select(q => new QuoteLine(Name(q.SpeakerId), q.Words)).ToArray();
        var places = (await db.Set<Place>().ToListAsync(cancellationToken)).ToDictionary(p => p.Id, p => p.Name);
        var located = (await db.Set<LocatedIn<global::GraphBridge.GraphRag.Ontologies.Organization>>().ToListAsync(cancellationToken))
            .Select(l => new PlaceLine(Name(l.FromId), places.GetValueOrDefault(l.ToId, "?"))).ToArray();
        return new(articles, jobs, quotes, located);
    }
}

public sealed record ArticleLine(string Title, string? Published, string? Publisher, IReadOnlyList<string> Authors, IReadOnlyList<string> Topics);

/// <summary>A <c>WORKS_FOR</c> fact and how many articles state it.</summary>
public sealed record JobLine(string Person, string? Role, string Organization, int Articles);

public sealed record QuoteLine(string Speaker, string Words);

public sealed record PlaceLine(string Organization, string Place);

public sealed record ArticlesReport(IReadOnlyList<ArticleLine> Articles, IReadOnlyList<JobLine> Jobs, IReadOnlyList<QuoteLine> Quotes,
    IReadOnlyList<PlaceLine> Located);
