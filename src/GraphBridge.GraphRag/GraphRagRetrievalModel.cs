using System.Globalization;
using System.Text.Json.Serialization;

namespace GraphBridge.GraphRag;

/// <summary>Retrieval routes. Each returns chunks from its own index; fusion combines their ranks (ADR 0052, section 4).</summary>
[Flags]
public enum GraphRagRoutes { Vector = 1, FullText = 2 }

/// <summary>Retrieval bounds. Each has a default and a maximum; a limit reached is reported in the evidence.</summary>
public sealed class GraphRagRetrievalOptions
{
    public GraphRagRoutes Routes { get; set; } = GraphRagRoutes.Vector | GraphRagRoutes.FullText;
    /// <summary>1 to 256 chunks per route.</summary>
    public int CandidatesPerRoute { get; set; } = 20;
    /// <summary>1 to 128 evidence items.</summary>
    public int MaxItems { get; set; } = 8;
    /// <summary>64 to 65,536 bytes of UTF-8 evidence text.</summary>
    public int MaxUtf8Bytes { get; set; } = 16_384;
    /// <summary>0 to 4 chunks before and after each item along <c>NEXT_CHUNK</c>, returned as labelled context.</summary>
    public int NeighbourChunks { get; set; } = 1;
    /// <summary>A read-only Cypher query whose rows become labelled context; see <see cref="WithCypherExpansion"/>.</summary>
    public string? CypherExpansion { get; private set; }
    public IReadOnlyDictionary<string, object?>? CypherExpansionParameters { get; private set; }
    /// <summary>Where retrieval may look (ADR 0052, section 12); <see langword="null"/> searches everything.</summary>
    public GraphRagScope? Scope { get; set; }
    /// <summary>0 to 100,000. A scope with at most this many chunks is scored exactly, chunk by chunk; a larger one asks the
    /// vector index for more candidates and keeps those inside it. 10,000 by default.</summary>
    public int ScopeExactChunks { get; set; } = 10_000;
    /// <summary>
    /// Earlier turns of a conversation, oldest first, for <see cref="IGraphRagAnswers"/> (ADR 0052, section 18): with any, the
    /// question is first rewritten into one that stands alone, which is then retrieved and answered. The newest six turns are
    /// used. <see cref="IGraphRagRetriever"/> takes one standalone question and refuses history.
    /// </summary>
    public IReadOnlyList<GraphRagTurn>? History { get; set; }

    /// <summary>
    /// Adds a Cypher expansion, run in a read transaction after retrieval with <c>$chunkIds</c> bound to the evidence's chunk IDs
    /// and <c>$scopeDocumentIds</c> to the scope's document IDs, or null without a scope. It must return columns <c>chunkId</c>
    /// and <c>text</c>; its rows become context, never citations. Values go in parameters. The query is yours, so it keeps to
    /// the scope only if it uses <c>$scopeDocumentIds</c>.
    /// </summary>
    public GraphRagRetrievalOptions WithCypherExpansion(string cypher, IReadOnlyDictionary<string, object?>? parameters = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cypher);
        if (parameters?.ContainsKey("chunkIds") == true) throw new ArgumentException("The parameter 'chunkIds' is reserved for the evidence's chunk IDs.", nameof(parameters));
        if (parameters?.ContainsKey("scopeDocumentIds") == true) throw new ArgumentException("The parameter 'scopeDocumentIds' is reserved for the scope's document IDs.", nameof(parameters));
        var copy = Clone();
        copy.CypherExpansion = cypher;
        copy.CypherExpansionParameters = parameters is null ? null : new Dictionary<string, object?>(parameters, StringComparer.Ordinal);
        return copy;
    }

    internal GraphRagRetrievalOptions Clone() => new()
    {
        Routes = Routes, CandidatesPerRoute = CandidatesPerRoute, MaxItems = MaxItems, MaxUtf8Bytes = MaxUtf8Bytes,
        NeighbourChunks = NeighbourChunks, CypherExpansion = CypherExpansion, CypherExpansionParameters = CypherExpansionParameters,
        Scope = Scope, ScopeExactChunks = ScopeExactChunks, History = History
    };

    internal void Validate()
    {
        if (Routes == 0 || (Routes & ~(GraphRagRoutes.Vector | GraphRagRoutes.FullText)) != 0)
            throw new ArgumentOutOfRangeException(nameof(Routes), "Choose at least one of the Vector and FullText routes.");
        if (CandidatesPerRoute is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(CandidatesPerRoute), "CandidatesPerRoute must be 1 to 256.");
        if (MaxItems is < 1 or > 128) throw new ArgumentOutOfRangeException(nameof(MaxItems), "MaxItems must be 1 to 128.");
        if (MaxUtf8Bytes is < 64 or > 65_536) throw new ArgumentOutOfRangeException(nameof(MaxUtf8Bytes), "MaxUtf8Bytes must be 64 to 65,536.");
        if (NeighbourChunks is < 0 or > 4) throw new ArgumentOutOfRangeException(nameof(NeighbourChunks), "NeighbourChunks must be 0 to 4.");
        if (ScopeExactChunks is < 0 or > 100_000) throw new ArgumentOutOfRangeException(nameof(ScopeExactChunks), "ScopeExactChunks must be 0 to 100,000.");
    }
}

/// <summary>
/// Where retrieval may look (ADR 0052, section 12): whole documents, sections, documents chosen by their metadata, or the
/// documents a read-only Cypher query returns. Every route and every neighbour stays inside the scope, and
/// <see cref="GraphRagEvidence.Scope"/> says how. An empty scope finds nothing.
/// </summary>
public sealed class GraphRagScope
{
    /// <summary>At most this many document or section IDs in a scope. A metadata scope that matches more keeps the first ones by
    /// ID; a Cypher scope keeps the first rows the query returns, one per document or byte range. Both report the limit.</summary>
    public const int MaxDocuments = 10_000;

    private GraphRagScope(GraphRagScopeKind kind, IReadOnlyList<string>? ids = null, GraphRagDocumentFilter? filter = null,
        string? cypher = null, IReadOnlyDictionary<string, object?>? parameters = null)
        => (Kind, Ids, Filter, Cypher, Parameters) = (kind, ids ?? [], filter, cypher, parameters);

    internal GraphRagScopeKind Kind { get; }
    internal IReadOnlyList<string> Ids { get; }
    internal GraphRagDocumentFilter? Filter { get; }
    internal string? Cypher { get; }
    internal IReadOnlyDictionary<string, object?>? Parameters { get; }

    /// <summary>Whole documents, by ID.</summary>
    public static GraphRagScope Documents(params IEnumerable<string> documentIds) => new(GraphRagScopeKind.Documents, Checked(documentIds, nameof(documentIds)));

    /// <summary>Sections, by ID: the chunks that lie wholly within each section's bytes, nested sections included.</summary>
    public static GraphRagScope Sections(params IEnumerable<string> sectionIds) => new(GraphRagScopeKind.Sections, Checked(sectionIds, nameof(sectionIds)));

    /// <summary>The documents whose metadata matches every value <paramref name="filter"/> sets.</summary>
    public static GraphRagScope Matching(GraphRagDocumentFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        if (filter is { Publisher: null, MediaType: null, SourceUriPrefix: null, PublishedFrom: null, PublishedBefore: null })
            throw new ArgumentException("Set at least one value of the filter.", nameof(filter));
        return new(GraphRagScopeKind.Metadata, filter: filter);
    }

    /// <summary>
    /// The documents a read-only Cypher query returns, run in a read transaction before retrieval. It returns a
    /// <c>documentId</c> column, and optionally <c>startByte</c> and <c>endByte</c> to keep to part of a document. Values go in
    /// parameters. Use it for your own properties, such as a tenant.
    /// </summary>
    public static GraphRagScope FromCypher(string cypher, IReadOnlyDictionary<string, object?>? parameters = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cypher);
        return new(GraphRagScopeKind.Cypher, cypher: cypher,
            parameters: parameters is null ? null : new Dictionary<string, object?>(parameters, StringComparer.Ordinal));
    }

    public override string ToString() => Kind switch
    {
        GraphRagScopeKind.Documents => $"documents ({Ids.Count})",
        GraphRagScopeKind.Sections => $"sections ({Ids.Count})",
        GraphRagScopeKind.Metadata => "documents by metadata",
        _ => "documents by Cypher"
    };

    private static string[] Checked(IEnumerable<string> ids, string name)
    {
        ArgumentNullException.ThrowIfNull(ids, name);
        var list = ids.Distinct(StringComparer.Ordinal).ToArray();
        if (list.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("IDs must not be empty.", name);
        if (list.Length > MaxDocuments) throw new ArgumentOutOfRangeException(name, $"A scope takes at most {MaxDocuments:N0} IDs.");
        return list;
    }
}

internal enum GraphRagScopeKind { Documents, Sections, Metadata, Cypher }

/// <summary>Documents by their metadata. Every value set must match. <see cref="PublishedFrom"/> and <see cref="PublishedBefore"/>
/// compare the stored text, so ISO 8601 dates order as dates.</summary>
public sealed record GraphRagDocumentFilter
{
    public string? Publisher { get; init; }
    public string? MediaType { get; init; }
    /// <summary>The source URI starts with this text.</summary>
    public string? SourceUriPrefix { get; init; }
    /// <summary>Published on or after this value.</summary>
    public string? PublishedFrom { get; init; }
    /// <summary>Published before this value.</summary>
    public string? PublishedBefore { get; init; }
}

/// <summary>How a scoped retrieval kept to its scope: the documents and chunks the scope holds, and each route's strategy.</summary>
public sealed record GraphRagScopeTrace(string Scope, int Documents, int Chunks, IReadOnlyList<GraphRagScopeRoute> Routes);

/// <summary>
/// One route under a scope. <c>exact</c>: every chunk in the scope was scored. <c>over-fetch</c>: the index was asked for
/// <see cref="Fetched"/> candidates and those outside the scope were dropped, which can miss a match ranked lower. <see cref="Kept"/>
/// is what the route returned.
/// </summary>
public sealed record GraphRagScopeRoute(GraphRagRoutes Route, string Strategy, int Fetched, int Kept);

/// <summary>neo4j-graphrag's retriever names as presets. Hybrid fuses ranks, not that library's normalised scores.</summary>
public static class GraphRagRetrievers
{
    public static GraphRagRetrievalOptions Vector() => new() { Routes = GraphRagRoutes.Vector };
    public static GraphRagRetrievalOptions Hybrid() => new() { Routes = GraphRagRoutes.Vector | GraphRagRoutes.FullText };
    public static GraphRagRetrievalOptions VectorCypher(string cypher) => Vector().WithCypherExpansion(cypher);
    public static GraphRagRetrievalOptions HybridCypher(string cypher) => Hybrid().WithCypherExpansion(cypher);
}

/// <summary>Finds evidence for a question.</summary>
public interface IGraphRagRetriever
{
    Task<GraphRagEvidence> RetrieveAsync(string question, GraphRagRetrievalOptions? options = null, CancellationToken cancellationToken = default);
}

/// <summary>Evidence: cited items in fused rank order, labelled context that is never cited, and what the budget left out.</summary>
public sealed record GraphRagEvidence(string Question, IReadOnlyList<GraphRagEvidenceItem> Items, IReadOnlyList<GraphRagContextItem> Context,
    int Utf8Bytes, int OmittedItems, IReadOnlyList<GraphRagLimit> LimitsReached, GraphRagProvenance Provenance)
{
    /// <summary>How retrieval kept to <see cref="GraphRagRetrievalOptions.Scope"/>; <see langword="null"/> without a scope.</summary>
    public GraphRagScopeTrace? Scope { get; init; }

    /// <summary>For a follow-up (ADR 0052, section 18): the question asked, and the standalone <see cref="Question"/> it became.
    /// <see langword="null"/> without history.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GraphRagRewrite? Rewrite { get; init; }
}

/// <summary>An earlier turn of a conversation: its question and, when there was one, the answer's text.</summary>
public sealed record GraphRagTurn(string Question, string? Answer = null);

/// <summary>
/// How a follow-up was read: the question <see cref="Asked"/>, the <see cref="Standalone"/> question retrieved and answered, and
/// the turns the model saw. <see cref="Outcome"/> is <c>rewritten</c>, <c>unchanged</c>, or <c>unreadable</c> when the model's
/// reply couldn't be read and the asked question was used.
/// </summary>
public sealed record GraphRagRewrite(string Asked, string Standalone, int Turns, string Outcome);

/// <summary>A chunk, or the budget's cut of one: <see cref="Text"/> is exactly the document's bytes [<see cref="StartByte"/>, <see cref="EndByte"/>).
/// <see cref="Routes"/> traces its rank in each route's list.</summary>
public sealed record GraphRagEvidenceItem(string ChunkId, string DocumentId, string Text, int StartByte, int EndByte,
    double Score, IReadOnlyList<GraphRagRouteRank> Routes)
{
    /// <summary>For a paged document, such as a PDF (ADR 0052, section 14): the pages the item spans, the first holding its first byte.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<GraphRagPage>? Pages { get; init; }

    /// <summary>Whether the span is in the source document or, for an adopted chunk without a source, in the chunk's own text.</summary>
    public GraphRagGrounding Grounding { get; init; }

    /// <summary>Where the document's text came from when it isn't the source's own, such as OCR (ADR 0052, section 21). Null otherwise.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TextOrigin { get; init; }
}

/// <summary>
/// What a span's bytes are (ADR 0052, section 13). <see cref="Source"/>: the source document's, as for every ingested chunk.
/// <see cref="Chunk"/>: the stored chunk's own text, for a chunk adopted from a graph another tool built without its source; a
/// quote is then proven to be in the passage, not where the passage sits in its source.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<GraphRagGrounding>))]
public enum GraphRagGrounding { Source, Chunk }

/// <summary>An item's place in one route: its 1-based rank, the index's score, and the reciprocal-rank score it contributed.</summary>
public sealed record GraphRagRouteRank(GraphRagRoutes Route, int Rank, double NativeScore, double RankScore);

/// <summary>Context next to the evidence, such as a neighbouring chunk or a Cypher expansion row. Never a citation.</summary>
public sealed record GraphRagContextItem(string ChunkId, string Text, string Reason);

/// <summary>Where the evidence came from: the server, the embedding set and the indexes used.</summary>
public sealed record GraphRagProvenance(string ServerVersion, string EmbeddingSetId, IReadOnlyList<string> Indexes, string? Similarity);

/// <summary>A page of a paged document: its number, from 1, and the UTF-8 byte offset where it starts in the document.</summary>
public sealed record GraphRagPage(int Number, int StartByte);

// Page maps (ADR 0052, section 14): stored on the document as comma-separated offsets, one per page in page order.
internal static class GraphRagPages
{
    public static int[] Parse(string starts) => [.. starts.Split(',').Select(s => int.Parse(s, NumberStyles.None, CultureInfo.InvariantCulture))];

    /// <summary>The pages that bytes [start, end) span: the page holding the first byte, and each page that starts inside.</summary>
    public static IReadOnlyList<GraphRagPage> Spanning(int[] starts, int start, int end)
    {
        var pages = new List<GraphRagPage>();
        for (var i = 0; i < starts.Length; i++)
        {
            var next = i + 1 < starts.Length ? starts[i + 1] : int.MaxValue;
            if (next <= start || next <= starts[i]) continue;   // before the span, or a page without text
            if (starts[i] >= end && pages.Count > 0) break;
            pages.Add(new(i + 1, starts[i]));
        }
        return pages;
    }

    /// <summary>The page holding a byte, or null without pages.</summary>
    public static int? At(IReadOnlyList<GraphRagPage>? pages, int offset)
        => pages is not { Count: > 0 } ? null : pages.LastOrDefault(p => p.StartByte <= offset)?.Number ?? pages[0].Number;
}
