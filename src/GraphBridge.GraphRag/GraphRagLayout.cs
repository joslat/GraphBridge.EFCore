namespace GraphBridge.GraphRag;

/// <summary>
/// The labels, relationship types and stored property names of the lexical layer (ADR 0052). Start from
/// <see cref="Default"/> or <see cref="Neo4jGraphRag"/> and change names with <c>with</c>; every name must match
/// <c>[A-Za-z_][A-Za-z0-9_]*</c>. GraphBridge maps a label to one entity type, so an application that already maps
/// <c>Document</c> or <c>Chunk</c> picks other labels here.
/// </summary>
public sealed record GraphRagLayout
{
    public string DocumentLabel { get; init; } = "Document";
    public string SectionLabel { get; init; } = "Section";
    public string ChunkLabel { get; init; } = "Chunk";
    public string RunLabel { get; init; } = "IngestionRun";
    public string LeaseLabel { get; init; } = "GraphRagLease";

    /// <summary>Chunk to document.</summary>
    public string ChunkDocumentType { get; init; } = "IN_DOCUMENT";
    /// <summary>Section to document.</summary>
    public string SectionDocumentType { get; init; } = "SECTION_OF";
    /// <summary>Chunk to section.</summary>
    public string ChunkSectionType { get; init; } = "IN_SECTION";
    /// <summary>Chunk to the next chunk.</summary>
    public string NextChunkType { get; init; } = "NEXT_CHUNK";
    /// <summary>Section to its parent section.</summary>
    public string SubsectionType { get; init; } = "SUBSECTION_OF";

    public string ChunkIdProperty { get; init; } = nameof(GraphRagChunk.Id);
    public string ChunkOrderProperty { get; init; } = nameof(GraphRagChunk.Order);
    public string ChunkTextProperty { get; init; } = nameof(GraphRagChunk.Text);
    public string ChunkEmbeddingProperty { get; init; } = nameof(GraphRagChunk.Embedding);

    /// <summary>Prefix of the constraint and index names <see cref="GraphRagSchema"/> declares.</summary>
    public string SchemaPrefix { get; init; } = "graphrag";

    /// <summary>Labels <c>Document</c>, <c>Section</c>, <c>Chunk</c>, <c>IngestionRun</c>, <c>GraphRagLease</c>; CLR property names.</summary>
    public static GraphRagLayout Default { get; } = new();

    /// <summary>
    /// neo4j-graphrag's names for the document and chunk layer: <c>(:Chunk)-[:FROM_DOCUMENT]->(:Document)</c>,
    /// <c>(:Chunk)-[:NEXT_CHUNK]->(:Chunk)</c> and chunk properties <c>id</c>, <c>index</c>, <c>text</c>, <c>embedding</c>.
    /// Sections keep the default names, which that library ignores.
    /// </summary>
    public static GraphRagLayout Neo4jGraphRag { get; } = new()
    {
        ChunkDocumentType = "FROM_DOCUMENT",
        ChunkIdProperty = "id",
        ChunkOrderProperty = "index",
        ChunkTextProperty = "text",
        ChunkEmbeddingProperty = "embedding"
    };

    internal void Validate()
    {
        foreach (var (name, value) in Names())
            if (string.IsNullOrEmpty(value) || !IsInitial(value[0]) || value.Any(c => !IsInitial(c) && c is not (>= '0' and <= '9')))
                throw new ArgumentException($"GraphRagLayout.{name} must match [A-Za-z_][A-Za-z0-9_]*.");
        var labels = new[] { DocumentLabel, SectionLabel, ChunkLabel, RunLabel, LeaseLabel };
        if (labels.Distinct(StringComparer.Ordinal).Count() != labels.Length)
            throw new ArgumentException("GraphRagLayout labels must be distinct.");
        var types = new[] { ChunkDocumentType, SectionDocumentType, ChunkSectionType, NextChunkType, SubsectionType };
        if (types.Distinct(StringComparer.Ordinal).Count() != types.Length)
            throw new ArgumentException("GraphRagLayout relationship types must be distinct.");
    }

    private IEnumerable<(string Name, string Value)> Names() =>
    [
        (nameof(DocumentLabel), DocumentLabel), (nameof(SectionLabel), SectionLabel), (nameof(ChunkLabel), ChunkLabel),
        (nameof(RunLabel), RunLabel), (nameof(LeaseLabel), LeaseLabel), (nameof(ChunkDocumentType), ChunkDocumentType),
        (nameof(SectionDocumentType), SectionDocumentType), (nameof(ChunkSectionType), ChunkSectionType),
        (nameof(NextChunkType), NextChunkType), (nameof(SubsectionType), SubsectionType),
        (nameof(ChunkIdProperty), ChunkIdProperty), (nameof(ChunkOrderProperty), ChunkOrderProperty),
        (nameof(ChunkTextProperty), ChunkTextProperty), (nameof(ChunkEmbeddingProperty), ChunkEmbeddingProperty),
        (nameof(SchemaPrefix), SchemaPrefix)
    ];

    private static bool IsInitial(char c) => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or '_';
}

/// <summary>Options for <c>modelBuilder.AddGraphRag(...)</c>.</summary>
public sealed class GraphRagModelOptions
{
    public GraphRagLayout Layout { get; set; } = GraphRagLayout.Default;
    /// <summary>The classes, facts and quotes extraction writes (ADR 0052, section 7); null maps the lexical layer only and
    /// ingests without extraction.</summary>
    public GraphRagOntology? Ontology { get; set; }
}
