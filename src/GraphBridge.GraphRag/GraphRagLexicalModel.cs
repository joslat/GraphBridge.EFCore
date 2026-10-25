namespace GraphBridge.GraphRag;

// ADR 0052, section 2: the lexical layer. Keys are computed by the toolkit, so writes are idempotent. Every node and
// edge carries the RunId of the ingestion run that wrote it. Positions are UTF-8 byte offsets, end exclusive.

/// <summary>A source document. Its text lives in its chunks; the document keeps the content hash and length.</summary>
public sealed class GraphRagDocument
{
    /// <summary>SHA-256 (lower-case hex) of the normalised source URI.</summary>
    public string Id { get; set; } = "";
    public string SourceUri { get; set; } = "";
    public string? Title { get; set; }
    public string MediaType { get; set; } = "";
    /// <summary>SHA-256 (lower-case hex) of the document's UTF-8 bytes as loaded.</summary>
    public string ContentHash { get; set; } = "";
    public long LengthBytes { get; set; }
    /// <summary>From <see cref="GraphRagSourceMetadata"/>: the publication date as given (ISO 8601 recommended), publisher and URL.</summary>
    public string? Published { get; set; }
    public string? Publisher { get; set; }
    public string? Url { get; set; }
    public string RunId { get; set; } = "";
}

/// <summary>A heading and the bytes up to the next heading of the same or a higher level.</summary>
public sealed class GraphRagSection
{
    public string Id { get; set; } = "";
    public string DocumentId { get; set; } = "";
    public string Title { get; set; } = "";
    /// <summary>1 for <c>#</c>, up to 6.</summary>
    public int Level { get; set; }
    public int Order { get; set; }
    public int StartByte { get; set; }
    public int EndByte { get; set; }
    public string RunId { get; set; } = "";
}

/// <summary>A span of a document. <see cref="Text"/> is exactly the document's bytes [<see cref="StartByte"/>, <see cref="EndByte"/>).</summary>
public sealed class GraphRagChunk
{
    public string Id { get; set; } = "";
    public string DocumentId { get; set; } = "";
    public int Order { get; set; }
    public string Text { get; set; } = "";
    public int StartByte { get; set; }
    public int EndByte { get; set; }
    public int? TokenCount { get; set; }
    /// <summary>Mapped with <c>IsGraphVector</c> and the dimensions passed to <c>AddGraphRag</c> (ADR 0054).</summary>
    public float[]? Embedding { get; set; }
    /// <summary>The embedding set that wrote <see cref="Embedding"/>.</summary>
    public string? EmbeddingSetId { get; set; }
    public string RunId { get; set; } = "";
}

/// <summary><c>IN_DOCUMENT</c> (or <c>FROM_DOCUMENT</c>): chunk to document.</summary>
public sealed class GraphRagChunkDocument
{
    public string ChunkId { get; set; } = "";
    public string DocumentId { get; set; } = "";
    public string RunId { get; set; } = "";
}

/// <summary><c>SECTION_OF</c>: section to document.</summary>
public sealed class GraphRagSectionDocument
{
    public string SectionId { get; set; } = "";
    public string DocumentId { get; set; } = "";
    public string RunId { get; set; } = "";
}

/// <summary><c>IN_SECTION</c>: chunk to the innermost section that contains it.</summary>
public sealed class GraphRagChunkSection
{
    public string ChunkId { get; set; } = "";
    public string SectionId { get; set; } = "";
    public string RunId { get; set; } = "";
}

/// <summary><c>NEXT_CHUNK</c>: chunk to the following chunk of the same document.</summary>
public sealed class GraphRagNextChunk
{
    public string ChunkId { get; set; } = "";
    public string NextChunkId { get; set; } = "";
    public string RunId { get; set; } = "";
}

/// <summary><c>SUBSECTION_OF</c>: section to its parent section.</summary>
public sealed class GraphRagSubsection
{
    public string SectionId { get; set; } = "";
    public string ParentSectionId { get; set; } = "";
    public string RunId { get; set; } = "";
}

/// <summary>One call to <c>IngestAsync</c>: when it ran, with which settings, and what happened to each document.</summary>
public sealed class GraphRagIngestionRun
{
    public string Id { get; set; } = "";
    public DateTimeOffset StartedUtc { get; set; }
    public DateTimeOffset? CompletedUtc { get; set; }
    /// <summary><c>Running</c>, <c>Completed</c> or <c>Failed</c>.</summary>
    public string Status { get; set; } = "";
    /// <summary>Hashes the chunker settings and the embedding set; a document is unchanged only under the same fingerprint.</summary>
    public string SettingsFingerprint { get; set; } = "";
    public int DocumentsIngested { get; set; }
    public int DocumentsUnchanged { get; set; }
    public int DocumentsFailed { get; set; }
}

/// <summary>A run's claim on one document while it writes it. Inserted, never upserted; see ADR 0052, section 4.</summary>
public sealed class GraphRagLease
{
    /// <summary>The document ID.</summary>
    public string Key { get; set; } = "";
    public string RunId { get; set; } = "";
    public DateTimeOffset ExpiresUtc { get; set; }
}
