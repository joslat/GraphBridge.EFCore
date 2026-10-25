namespace GraphBridge.GraphRag;

/// <summary>Toolkit settings for <c>services.AddGraphRag&lt;TContext&gt;(...)</c>.</summary>
public sealed class GraphRagOptions
{
    public GraphRagChunkerOptions Chunking { get; set; } = new();
    public GraphRagIngestionOptions Ingestion { get; set; } = new();
    /// <summary>The defaults for <see cref="IGraphRagRetriever"/> and <see cref="IGraphRagAnswers"/> calls without options.</summary>
    public GraphRagRetrievalOptions Retrieval { get; set; } = new();
    /// <summary>Bounds for extraction, which runs when the model maps an ontology (ADR 0052, section 7).</summary>
    public GraphRagExtractionOptions Extraction { get; set; } = new();
}

/// <summary>Service keys the toolkit looks up.</summary>
public static class GraphRagServiceKeys
{
    /// <summary>An <c>IChatClient</c> registered under this key is used for extraction; otherwise the container's unkeyed one.</summary>
    public const string ExtractionChatClient = "GraphBridge.GraphRag.Extraction";
}

/// <summary>Ingestion bounds. Each has a default and a maximum; a limit reached is reported in the receipt.</summary>
public sealed class GraphRagIngestionOptions
{
    /// <summary>1 to 256 texts per embedding call.</summary>
    public int EmbeddingBatchSize { get; set; } = 32;
    /// <summary>1 to 8 embedding calls in parallel.</summary>
    public int Lanes { get; set; } = 2;
    /// <summary>1 to 100,000 documents per <c>IngestAsync</c> call; the rest are left out and reported.</summary>
    public int MaxDocuments { get; set; } = 10_000;
    /// <summary>1 KiB to 256 MiB; a larger document fails with <c>document-too-large</c>.</summary>
    public int MaxDocumentBytes { get; set; } = 16 << 20;
    /// <summary>How long a run holds a document before another run may take it over: 1 second to 1 day.</summary>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromMinutes(10);
    /// <summary>Which embedding failures are retried, at most twice. Null: an <see cref="HttpRequestException"/> with status
    /// 408, 429 or 5xx, or a timeout. Return false for everything when the client already retries.</summary>
    public Func<Exception, bool>? IsTransient { get; set; }

    internal void Validate()
    {
        if (EmbeddingBatchSize is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(EmbeddingBatchSize), "EmbeddingBatchSize must be 1 to 256.");
        if (Lanes is < 1 or > 8) throw new ArgumentOutOfRangeException(nameof(Lanes), "Lanes must be 1 to 8.");
        if (MaxDocuments is < 1 or > 100_000) throw new ArgumentOutOfRangeException(nameof(MaxDocuments), "MaxDocuments must be 1 to 100,000.");
        if (MaxDocumentBytes is < 1024 or > 256 << 20) throw new ArgumentOutOfRangeException(nameof(MaxDocumentBytes), "MaxDocumentBytes must be 1 KiB to 256 MiB.");
        if (LeaseDuration < TimeSpan.FromSeconds(1) || LeaseDuration > TimeSpan.FromDays(1))
            throw new ArgumentOutOfRangeException(nameof(LeaseDuration), "LeaseDuration must be 1 second to 1 day.");
    }
}

/// <summary>Ingests sources: one transaction per document, resumable, leased per document (ADR 0052, section 4).</summary>
public interface IGraphRagIngestion
{
    Task<GraphRagIngestionReceipt> IngestAsync(IEnumerable<GraphRagSource> sources, CancellationToken cancellationToken = default);

    /// <summary>
    /// Ingests documents whose chunks were decided elsewhere, such as by the DataIngestion adapter (ADR 0052, section 8), with the
    /// same lease, unchanged check, embeddings, extraction and transaction as files. All documents of one call share a chunker.
    /// </summary>
    Task<GraphRagIngestionReceipt> IngestChunkedAsync(IEnumerable<GraphRagChunkedDocument> documents, CancellationToken cancellationToken = default);
}

/// <summary>What one ingestion call did. <see cref="InputTokens"/> is null when the generator reports no usage;
/// <see cref="Extraction"/> is null when the model maps no ontology.</summary>
public sealed record GraphRagIngestionReceipt(string RunId, IReadOnlyList<GraphRagDocumentOutcome> Documents,
    int ChunksWritten, int EmbeddingCalls, long? InputTokens, TimeSpan Elapsed, IReadOnlyList<GraphRagLimit> LimitsReached,
    GraphRagExtractionSummary? Extraction = null);

/// <summary>One document's outcome. <see cref="ErrorCode"/> is set when it failed, such as <c>invalid-utf8</c>,
/// <c>lease-held</c> or <c>extraction-failed</c>; messages and document text are never part of the outcome.</summary>
public sealed record GraphRagDocumentOutcome(string DocumentId, string SourceUri, GraphRagDocumentStatus Status, string? ErrorCode);

public enum GraphRagDocumentStatus { Ingested, Unchanged, Failed }

/// <summary>A bound that stopped work, with its value.</summary>
public sealed record GraphRagLimit(string Name, int Value);
