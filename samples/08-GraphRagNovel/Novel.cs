using GraphBridge.EntityFrameworkCore.Migrations;
using GraphBridge.GraphRag;
using GraphBridge.GraphRag.Ontologies;
using GraphBridge.Samples.GraphRagShared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace GraphBridge.Samples.GraphRagNovel;

/// <summary>The application's context: the novel ontology's characters, places, groups and events over books, chapters and passages.</summary>
public sealed class NovelContext(DbContextOptions<NovelContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
        => modelBuilder.AddGraphRag(Novel.EmbeddingSet.Dimensions, o => (o.Layout, o.Ontology) = (Novel.Layout, GraphRagOntologies.Novel));
}

public static class Novel
{
    /// <summary>Project Gutenberg's plain-text UTF-8 edition of Alice's Adventures in Wonderland (eBook #11), public domain in the USA.</summary>
    public const string BookUrl = "https://www.gutenberg.org/cache/epub/11/pg11.txt";

    /// <summary>The models from <c>GRAPHBRIDGE_AI_*</c>; null runs offline.</summary>
    public static SampleModelSettings? Models { get; } = SampleModelSettings.FromEnvironment();

    public static GraphRagEmbeddingSet EmbeddingSet { get; } = Models is { } models
        ? new(models.EmbeddingModel, models.EmbeddingDimensions)
        : new("graphbridge-sample-hashing", 256);

    /// <summary>Labels of the sample's own, so it can share a database with samples 07 and 09.</summary>
    public static GraphRagLayout Layout { get; } = GraphRagLayout.Default with
    {
        DocumentLabel = "Book", SectionLabel = "Chapter", ChunkLabel = "Passage", RunLabel = "NovelRun", LeaseLabel = "NovelLease", SchemaPrefix = "novel"
    };

    /// <summary>The schema as the application's own reviewed migration plan, one per embedding set.</summary>
    public static GraphMigrationPlan SchemaPlan { get; } = new("graphrag-novel-" + EmbeddingSet.Id, null,
        GraphRagSchema.CreateSteps(Layout, EmbeddingSet, GraphRagOntologies.Novel),
        GraphMigrationRollbackPolicy.NoRollback("Dropping the constraints and indexes is a separate, reviewed migration."));

    /// <summary>The opening of chapter I, shipped with the sample, with its recorded extraction replies.</summary>
    public static string Excerpt => Path.Combine(AppContext.BaseDirectory, "data", "chapter-1-opening.txt");

    public static string RecordedReplies => Path.Combine(AppContext.BaseDirectory, "data", "novel-replies.json");

    /// <summary>Where <c>fetch</c> saves the whole book: next to the build output, never in the repository.</summary>
    public static string BookPath => Path.Combine(AppContext.BaseDirectory, "data", "alice.txt");

    /// <summary>
    /// The toolkit with the models from the environment, or offline: a hashing embedder, an extractive chat client for answers,
    /// and recorded replies for extraction, which cover the shipped excerpt only.
    /// </summary>
    public static IServiceCollection AddNovel(this IServiceCollection services)
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
        return services.AddGraphRag<NovelContext>(EmbeddingSet, options => options.Chunking.MaxUtf8Bytes = 4_096);
    }

    /// <summary>Downloads the book once and keeps only the text between Project Gutenberg's start and end markers.</summary>
    public static async Task<string> FetchBookAsync(string path, CancellationToken cancellationToken = default)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        var raw = await http.GetStringAsync(BookUrl, cancellationToken).ConfigureAwait(false);
        var text = BookText(raw);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await File.WriteAllTextAsync(path, text, cancellationToken).ConfigureAwait(false);
        return path;
    }

    /// <summary>The book without the licence header and footer that Project Gutenberg adds around it.</summary>
    public static string BookText(string raw)
    {
        var start = raw.IndexOf("*** START OF", StringComparison.Ordinal);
        var end = raw.IndexOf("*** END OF", StringComparison.Ordinal);
        if (start < 0 || end < start) throw new InvalidOperationException("The download has no Project Gutenberg start and end markers.");
        var body = raw.IndexOf('\n', start) + 1;
        return raw[body..end].Trim().ReplaceLineEndings("\n") + "\n";
    }

    /// <summary>What the sample prints: characters with their mentions, kin, and who says what, all through typed LINQ.</summary>
    public static async Task<NovelSummary> SummarizeAsync(NovelContext db, CancellationToken cancellationToken = default)
    {
        var characters = await db.Set<Character>().Where(c => !c.IsPlaceholder).OrderBy(c => c.Name).ToListAsync(cancellationToken);
        var mentions = (await db.Set<GraphRagMention<Character>>().ToListAsync(cancellationToken))
            .CountBy(m => m.EntityId).ToDictionary(p => p.Key, p => p.Value);
        var names = characters.ToDictionary(c => c.Id, c => c.Name);
        string Name(string id) => names.TryGetValue(id, out var name) ? name : "someone";
        var kin = await db.Set<KinOf>().ToListAsync(cancellationToken);
        var says = (await db.Set<Says>().ToListAsync(cancellationToken)).OrderBy(s => s.StartByte).ToList();
        var places = await db.Set<Place>().OrderBy(p => p.Name).Select(p => p.Name).ToListAsync(cancellationToken);
        return new(
            characters.Select(c => new NovelCharacter(c.Name, c.Aliases?.Split('\n') ?? [], mentions.GetValueOrDefault(c.Id))).ToArray(),
            kin.Select(k => (Name(k.FromId), k.Kind ?? "kin", Name(k.ToId))).ToArray(),
            says.Select(s => (Name(s.SpeakerId), s.Words)).ToArray(),
            places);
    }
}

public sealed record NovelCharacter(string Name, IReadOnlyList<string> Aliases, int Mentions);

public sealed record NovelSummary(IReadOnlyList<NovelCharacter> Characters, IReadOnlyList<(string From, string Kind, string To)> Kin,
    IReadOnlyList<(string Speaker, string Words)> Says, IReadOnlyList<string> Places);
