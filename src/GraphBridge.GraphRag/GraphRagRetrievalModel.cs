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

    /// <summary>
    /// Adds a Cypher expansion, run in a read transaction after retrieval with <c>$chunkIds</c> bound to the evidence's chunk IDs.
    /// It must return columns <c>chunkId</c> and <c>text</c>; its rows become context, never citations. Values go in parameters.
    /// </summary>
    public GraphRagRetrievalOptions WithCypherExpansion(string cypher, IReadOnlyDictionary<string, object?>? parameters = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cypher);
        if (parameters?.ContainsKey("chunkIds") == true) throw new ArgumentException("The parameter 'chunkIds' is reserved for the evidence's chunk IDs.", nameof(parameters));
        var copy = Clone();
        copy.CypherExpansion = cypher;
        copy.CypherExpansionParameters = parameters is null ? null : new Dictionary<string, object?>(parameters, StringComparer.Ordinal);
        return copy;
    }

    internal GraphRagRetrievalOptions Clone() => new()
    {
        Routes = Routes, CandidatesPerRoute = CandidatesPerRoute, MaxItems = MaxItems, MaxUtf8Bytes = MaxUtf8Bytes,
        NeighbourChunks = NeighbourChunks, CypherExpansion = CypherExpansion, CypherExpansionParameters = CypherExpansionParameters
    };

    internal void Validate()
    {
        if (Routes == 0 || (Routes & ~(GraphRagRoutes.Vector | GraphRagRoutes.FullText)) != 0)
            throw new ArgumentOutOfRangeException(nameof(Routes), "Choose at least one of the Vector and FullText routes.");
        if (CandidatesPerRoute is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(CandidatesPerRoute), "CandidatesPerRoute must be 1 to 256.");
        if (MaxItems is < 1 or > 128) throw new ArgumentOutOfRangeException(nameof(MaxItems), "MaxItems must be 1 to 128.");
        if (MaxUtf8Bytes is < 64 or > 65_536) throw new ArgumentOutOfRangeException(nameof(MaxUtf8Bytes), "MaxUtf8Bytes must be 64 to 65,536.");
        if (NeighbourChunks is < 0 or > 4) throw new ArgumentOutOfRangeException(nameof(NeighbourChunks), "NeighbourChunks must be 0 to 4.");
    }
}

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
    int Utf8Bytes, int OmittedItems, IReadOnlyList<GraphRagLimit> LimitsReached, GraphRagProvenance Provenance);

/// <summary>A chunk, or the budget's cut of one: <see cref="Text"/> is exactly the document's bytes [<see cref="StartByte"/>, <see cref="EndByte"/>).
/// <see cref="Routes"/> traces its rank in each route's list.</summary>
public sealed record GraphRagEvidenceItem(string ChunkId, string DocumentId, string Text, int StartByte, int EndByte,
    double Score, IReadOnlyList<GraphRagRouteRank> Routes);

/// <summary>An item's place in one route: its 1-based rank, the index's score, and the reciprocal-rank score it contributed.</summary>
public sealed record GraphRagRouteRank(GraphRagRoutes Route, int Rank, double NativeScore, double RankScore);

/// <summary>Context next to the evidence, such as a neighbouring chunk or a Cypher expansion row. Never a citation.</summary>
public sealed record GraphRagContextItem(string ChunkId, string Text, string Reason);

/// <summary>Where the evidence came from: the server, the embedding set and the indexes used.</summary>
public sealed record GraphRagProvenance(string ServerVersion, string EmbeddingSetId, IReadOnlyList<string> Indexes, string? Similarity);
