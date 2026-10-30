using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using GraphBridge.EntityFrameworkCore.Migrations;

namespace GraphBridge.GraphRag;

/// <summary>
/// The embedding model that writes chunk vectors (ADR 0052). One set is active at a time: a vector index covers one label
/// and property, and Neo4j allows one vector index per schema. Changing the set means re-embedding and replacing the index.
/// </summary>
/// <param name="Model">The embedding model's name, as the generator reports it or as you choose to record it.</param>
/// <param name="Dimensions">1 to 4,096; equal to the dimensions passed to <c>AddGraphRag</c>.</param>
/// <param name="Normalize">Scale each vector to unit length before it is stored.</param>
public sealed record GraphRagEmbeddingSet(string Model, int Dimensions, GraphVectorSimilarity Similarity = GraphVectorSimilarity.Cosine,
    bool Normalize = false, string? ModelRevision = null)
{
    /// <summary>Hashes the model, revision, dimensions, similarity and normalisation, so a different set never shares an ID.</summary>
    public string Id => Hash(string.Join('\u001f', Model, ModelRevision ?? "", Dimensions.ToString(CultureInfo.InvariantCulture),
        Similarity.ToString(), Normalize ? "normalized" : "raw"))[..16];

    /// <summary>The vector index name <see cref="GraphRagSchema"/> declares for this set, with the default schema prefix.</summary>
    public string VectorIndexName => VectorIndexNameFor("graphrag");

    internal string VectorIndexNameFor(string prefix) => $"{prefix}_chunk_embedding_{Id}";

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(Model)) throw new ArgumentException("An embedding set needs a model name.");
        if (Dimensions is < 1 or > 4096) throw new ArgumentOutOfRangeException(nameof(Dimensions), "Embedding dimensions must be 1 to 4,096.");
        if (!Enum.IsDefined(Similarity)) throw new ArgumentOutOfRangeException(nameof(Similarity));
    }

    internal static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
}

/// <summary>
/// The schema the toolkit needs, as typed migration definitions and steps (ADR 0052, section 4; ADR 0053). The application
/// puts the steps in a migration plan of its own, with its own ID and predecessor, then reviews and applies it like any
/// other plan. The toolkit creates no schema itself.
/// </summary>
public static class GraphRagSchema
{
    /// <summary>
    /// Uniqueness constraints on every key, the chunk vector index and the chunk text full-text index. With an ontology, also a
    /// uniqueness constraint on each class's ID and on each mention and quote type's key, and one full-text index over every
    /// class's name and aliases (ADR 0052, section 7). Reads nothing.
    /// </summary>
    public static IReadOnlyList<GraphMigrationSchemaDefinition> Definitions(GraphRagLayout layout, GraphRagEmbeddingSet embeddingSet,
        GraphRagOntology? ontology = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(embeddingSet);
        layout.Validate();
        embeddingSet.Validate();
        var prefix = layout.SchemaPrefix;
        var definitions = new List<GraphMigrationSchemaDefinition>
        {
            Unique($"{prefix}_document_key", layout.DocumentLabel, nameof(GraphRagDocument.Id)),
            Unique($"{prefix}_section_key", layout.SectionLabel, nameof(GraphRagSection.Id)),
            Unique($"{prefix}_chunk_key", layout.ChunkLabel, layout.ChunkIdProperty),
            Unique($"{prefix}_run_key", layout.RunLabel, nameof(GraphRagIngestionRun.Id)),
            Unique($"{prefix}_lease_key", layout.LeaseLabel, nameof(GraphRagLease.Key)),
            GraphMigrationSchemaDefinition.VectorIndex(layout.VectorIndexFor(embeddingSet), "NODE", layout.ChunkLabel,
                layout.ChunkEmbeddingProperty, embeddingSet.Dimensions, embeddingSet.Similarity),
            GraphMigrationSchemaDefinition.FullTextIndex(FullTextIndexName(layout), "NODE", [layout.ChunkLabel], [layout.ChunkTextProperty])
        };
        if (ontology is not null)
        {
            // Constraints stay ahead of the indexes, so the steps create them first.
            var index = definitions.FindLastIndex(d => !d.IsIndex) + 1;
            var mentionTypes = new HashSet<string>(StringComparer.Ordinal);
            foreach (var type in ontology.Classes)
            {
                var name = GraphRagOntologyBuilder.UpperSnake(type.Label).ToLowerInvariant();
                definitions.Insert(index++, Unique($"{prefix}_{name}_key", type.Label, nameof(Ontologies.GraphRagEntity.Id)));
                // A mention type every class shares (FROM_CHUNK) has one key constraint (ADR 0056).
                if (mentionTypes.Add(type.MentionType))
                    definitions.Insert(index++, UniqueRelationship($"{prefix}_{type.MentionType.ToLowerInvariant()}_key", type.MentionType));
            }
            foreach (var quote in ontology.Quotes)
                definitions.Insert(index++, UniqueRelationship($"{prefix}_{quote.Type.ToLowerInvariant()}_key", quote.Type));
            definitions.Add(GraphMigrationSchemaDefinition.FullTextIndex(EntityNamesIndexName(layout), "NODE", ontology.Classes.Select(c => c.Label).ToArray(),
                [ontology.NameProperty, nameof(Ontologies.GraphRagEntity.Aliases)]));
        }
        return definitions.AsReadOnly();
    }

    /// <summary>The same definitions as creation steps, in order: constraints first, then the indexes.</summary>
    public static IReadOnlyList<GraphMigrationStep> CreateSteps(GraphRagLayout layout, GraphRagEmbeddingSet embeddingSet, GraphRagOntology? ontology = null)
        => Array.AsReadOnly(Definitions(layout, embeddingSet, ontology).Select(definition => definition.IsIndex
            ? GraphMigrationStep.CreateIndex("create-" + definition.Name, definition)
            : GraphMigrationStep.CreateConstraint("create-" + definition.Name, definition)).ToArray());

    /// <summary>The full-text index over the ontology classes' names and aliases.</summary>
    public static string EntityNamesIndexName(GraphRagLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        return $"{layout.SchemaPrefix}_entity_names";
    }

    internal static string FullTextIndexName(GraphRagLayout layout) => layout.FullTextIndexName ?? $"{layout.SchemaPrefix}_chunk_text";

    private static GraphMigrationSchemaDefinition Unique(string name, string label, string property)
        => new(name, GraphMigrationSchemaKind.UniquenessConstraint, "NODE", label, [property]);

    private static GraphMigrationSchemaDefinition UniqueRelationship(string name, string type)
        => new(name, GraphMigrationSchemaKind.UniquenessConstraint, "RELATIONSHIP", type, [nameof(Ontologies.GraphRagMention.Id)]);
}
