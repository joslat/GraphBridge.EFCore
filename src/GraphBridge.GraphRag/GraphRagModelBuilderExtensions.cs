using GraphBridge.GraphRag;
using GraphBridge.GraphRag.Ontologies;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Microsoft.EntityFrameworkCore;

/// <summary>Maps the GraphRAG lexical layer into an application's EF model (ADR 0052, section 2).</summary>
public static class GraphRagModelBuilderExtensions
{
    internal const string LayoutAnnotation = "GraphBridge:GraphRagLayout";
    internal const string OntologyAnnotation = "GraphBridge:GraphRagOntology";

    /// <summary>
    /// Maps documents, sections, chunks, their links, ingestion runs and leases. Every structural link is an association
    /// edge from the child to its parent or successor, carrying the run ID that wrote it. With an
    /// <see cref="GraphRagModelOptions.Ontology"/>, also maps its classes as nodes, a mention type per class, its facts as
    /// association edges and its quote types (ADR 0052, section 7).
    /// </summary>
    /// <param name="embeddingDimensions">1 to 4,096: the chunk embedding's <c>IsGraphVector</c> dimensions, which must equal
    /// the embedding set's.</param>
    public static ModelBuilder AddGraphRag(this ModelBuilder modelBuilder, int embeddingDimensions, Action<GraphRagModelOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        if (embeddingDimensions is < 1 or > 4096)
            throw new ArgumentOutOfRangeException(nameof(embeddingDimensions), "Embedding dimensions must be 1 to 4,096.");
        var options = new GraphRagModelOptions();
        configure?.Invoke(options);
        var layout = options.Layout ?? throw new ArgumentException("GraphRagModelOptions.Layout can't be null.", nameof(configure));
        layout.Validate();
        modelBuilder.HasAnnotation(LayoutAnnotation, layout);

        Node<GraphRagDocument>(modelBuilder, layout.DocumentLabel, nameof(GraphRagDocument.Id));
        Node<GraphRagSection>(modelBuilder, layout.SectionLabel, nameof(GraphRagSection.Id));
        var chunk = Node<GraphRagChunk>(modelBuilder, layout.ChunkLabel, nameof(GraphRagChunk.Id));
        chunk.Property(c => c.Id).HasGraphPropertyName(layout.ChunkIdProperty);
        chunk.Property(c => c.Order).HasGraphPropertyName(layout.ChunkOrderProperty);
        chunk.Property(c => c.Text).HasGraphPropertyName(layout.ChunkTextProperty);
        chunk.Property(c => c.Embedding).HasGraphPropertyName(layout.ChunkEmbeddingProperty).IsGraphVector(embeddingDimensions);
        Node<GraphRagIngestionRun>(modelBuilder, layout.RunLabel, nameof(GraphRagIngestionRun.Id));
        Node<GraphRagLease>(modelBuilder, layout.LeaseLabel, nameof(GraphRagLease.Key));

        Edge<GraphRagChunkDocument, GraphRagChunk, GraphRagDocument>(modelBuilder, layout.ChunkDocumentType,
            nameof(GraphRagChunkDocument.ChunkId), nameof(GraphRagChunkDocument.DocumentId));
        Edge<GraphRagSectionDocument, GraphRagSection, GraphRagDocument>(modelBuilder, layout.SectionDocumentType,
            nameof(GraphRagSectionDocument.SectionId), nameof(GraphRagSectionDocument.DocumentId));
        Edge<GraphRagChunkSection, GraphRagChunk, GraphRagSection>(modelBuilder, layout.ChunkSectionType,
            nameof(GraphRagChunkSection.ChunkId), nameof(GraphRagChunkSection.SectionId));
        Edge<GraphRagNextChunk, GraphRagChunk, GraphRagChunk>(modelBuilder, layout.NextChunkType,
            nameof(GraphRagNextChunk.ChunkId), nameof(GraphRagNextChunk.NextChunkId));
        Edge<GraphRagSubsection, GraphRagSection, GraphRagSection>(modelBuilder, layout.SubsectionType,
            nameof(GraphRagSubsection.SectionId), nameof(GraphRagSubsection.ParentSectionId));
        if (options.Ontology is { } ontology) MapOntology(modelBuilder, layout, ontology);
        return modelBuilder;
    }

    private static void MapOntology(ModelBuilder modelBuilder, GraphRagLayout layout, GraphRagOntology ontology)
    {
        string[] labels = [layout.DocumentLabel, layout.SectionLabel, layout.ChunkLabel, layout.RunLabel, layout.LeaseLabel];
        if (ontology.Classes.FirstOrDefault(c => labels.Contains(c.Label)) is { } clash)
            throw new ArgumentException($"The ontology class label '{clash.Label}' is also a GraphRagLayout label; pick other layout labels.");
        string[] types = [layout.ChunkDocumentType, layout.SectionDocumentType, layout.ChunkSectionType, layout.NextChunkType, layout.SubsectionType];
        var ontologyTypes = ontology.Classes.Select(c => c.MentionType).Concat(ontology.Relationships.Select(r => r.Type)).Concat(ontology.Quotes.Select(q => q.Type));
        if (ontologyTypes.FirstOrDefault(types.Contains) is { } typeClash)
            throw new ArgumentException($"The ontology relationship type '{typeClash}' is also a GraphRagLayout type; pick other layout types.");
        modelBuilder.HasAnnotation(OntologyAnnotation, ontology);

        foreach (var type in ontology.Classes)
        {
            var node = modelBuilder.Entity(type.ClrType).ToGraphNode(type.Label);
            node.HasKey(nameof(GraphRagEntity.Id));
            node.Property(nameof(GraphRagEntity.Id)).ValueGeneratedNever();
            var mention = modelBuilder.Entity(type.MentionClrType);
            mention.HasKey(nameof(GraphRagMention.Id));
            mention.Property(nameof(GraphRagMention.Id)).ValueGeneratedNever();
            mention.HasOne(type.ClrType).WithMany().HasForeignKey(nameof(GraphRagMention.EntityId));
            mention.HasOne(typeof(GraphRagChunk)).WithMany().HasForeignKey(nameof(GraphRagMention.ChunkId));
            mention.ToGraphIdentifiedRelationship(type.MentionType, nameof(GraphRagMention.Id), [nameof(GraphRagMention.EntityId)], [nameof(GraphRagMention.ChunkId)]);
        }
        foreach (var relationship in ontology.Relationships)
        {
            var fact = modelBuilder.Entity(relationship.ClrType);
            fact.HasKey(nameof(GraphRagFact.FromId), nameof(GraphRagFact.ToId));
            fact.HasOne(relationship.From).WithMany().HasForeignKey(nameof(GraphRagFact.FromId));
            fact.HasOne(relationship.To).WithMany().HasForeignKey(nameof(GraphRagFact.ToId));
            fact.ToGraphRelationship(relationship.Type, [nameof(GraphRagFact.FromId)], [nameof(GraphRagFact.ToId)]);
        }
        foreach (var quote in ontology.Quotes)
        {
            var edge = modelBuilder.Entity(quote.ClrType);
            edge.HasKey(nameof(GraphRagQuote.Id));
            edge.Property(nameof(GraphRagQuote.Id)).ValueGeneratedNever();
            edge.HasOne(quote.Speaker).WithMany().HasForeignKey(nameof(GraphRagQuote.SpeakerId));
            edge.HasOne(typeof(GraphRagChunk)).WithMany().HasForeignKey(nameof(GraphRagQuote.ChunkId));
            edge.ToGraphIdentifiedRelationship(quote.Type, nameof(GraphRagQuote.Id), [nameof(GraphRagQuote.SpeakerId)], [nameof(GraphRagQuote.ChunkId)]);
        }
    }

    private static EntityTypeBuilder<T> Node<T>(ModelBuilder modelBuilder, string label, string key) where T : class
    {
        var node = modelBuilder.Entity<T>().ToGraphNode(label);
        node.HasKey(key);
        node.Property(key).ValueGeneratedNever();
        return node;
    }

    private static void Edge<TEdge, TSource, TTarget>(ModelBuilder modelBuilder, string type, string source, string target)
        where TEdge : class where TSource : class where TTarget : class
    {
        var edge = modelBuilder.Entity<TEdge>();
        edge.HasKey(source, target);
        edge.HasOne<TSource>().WithMany().HasForeignKey(source);
        edge.HasOne<TTarget>().WithMany().HasForeignKey(target);
        edge.ToGraphRelationship(type, [source], [target]);
    }
}
