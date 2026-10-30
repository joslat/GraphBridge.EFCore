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
    /// <summary>How failed embedding and extraction calls are retried (ADR 0052, section 11).</summary>
    public GraphRagRetryOptions Retry { get; set; } = new();
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

    /// <summary>
    /// What <see cref="IngestAsync"/> would do with these sources, without a model call or a write (ADR 0052, section 11): each
    /// document loaded, checked against what's stored and chunked; the embedding and extraction calls that would follow; and
    /// their input tokens, counted with the registered <see cref="IGraphRagTokenCounter"/> or estimated without one.
    /// </summary>
    Task<GraphRagIngestionPreview> PreviewAsync(IEnumerable<GraphRagSource> sources, CancellationToken cancellationToken = default);
}

/// <summary>
/// A cost preview of one ingestion call. Calls and tokens count only the documents that would be ingested. Extraction calls
/// are one per chunk, plus one more for each reply that comes back unreadable. Tokens are input tokens:
/// <see cref="TokensCounted"/> is true when an <see cref="IGraphRagTokenCounter"/> counted them, and false when they're
/// estimated at four bytes of UTF-8 per token. Both add a few tokens per message for the chat format, and neither counts the
/// JSON schema, which some providers bill as input. Output tokens depend on the model, and a reasoning model's include its
/// reasoning, so they aren't estimated.
/// </summary>
public sealed record GraphRagIngestionPreview(IReadOnlyList<GraphRagDocumentPreview> Documents, int Chunks, int EmbeddingCalls, int ExtractionCalls,
    long EmbeddingTokens, long ExtractionInputTokens, bool TokensCounted, IReadOnlyList<GraphRagLimit> LimitsReached);

/// <summary>One document in a preview: whether it would be ingested, is unchanged, or would fail (with the same
/// <see cref="ErrorCode"/> as ingestion), its size, and its chunks when it would be ingested.</summary>
public sealed record GraphRagDocumentPreview(string DocumentId, string SourceUri, GraphRagPreviewStatus Status, string? ErrorCode, int Bytes, int Chunks);

public enum GraphRagPreviewStatus { WouldIngest, Unchanged, WouldFail }

/// <summary>What one ingestion call did. <see cref="InputTokens"/> is null when the generator reports no usage;
/// <see cref="Extraction"/> is null when the model maps no ontology; <see cref="Retries"/> counts the embedding and extraction
/// calls that were retried, and the time spent waiting.</summary>
public sealed record GraphRagIngestionReceipt(string RunId, IReadOnlyList<GraphRagDocumentOutcome> Documents,
    int ChunksWritten, int EmbeddingCalls, long? InputTokens, TimeSpan Elapsed, IReadOnlyList<GraphRagLimit> LimitsReached,
    GraphRagExtractionSummary? Extraction = null, GraphRagRetrySummary? Retries = null);

/// <summary>One document's outcome. <see cref="ErrorCode"/> is set when it failed, such as <c>invalid-utf8</c>,
/// <c>lease-held</c>, <c>embedding-failed</c>, <c>extraction-failed</c> or <c>rate-limited</c>; messages and document text are
/// never part of the outcome.</summary>
public sealed record GraphRagDocumentOutcome(string DocumentId, string SourceUri, GraphRagDocumentStatus Status, string? ErrorCode);

public enum GraphRagDocumentStatus { Ingested, Unchanged, Failed }

/// <summary>A bound that stopped work, with its value.</summary>
public sealed record GraphRagLimit(string Name, int Value);
