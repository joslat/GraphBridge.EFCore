using GraphBridge.EntityFrameworkCore.Migrations;
using GraphBridge.EntityFrameworkCore.Schema;
using GraphBridge.GraphRag;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Neo4j.Driver;

namespace GraphBridge.GraphRag.Tests;

/// <summary>Wave 1 (GR-02, GR-33): the lexical model, layouts, embedding sets and the schema the application applies.</summary>
public sealed class GraphRagModelTests
{
    [Fact]
    public void The_default_layout_maps_nodes_and_child_to_parent_edges_with_run_ids()
    {
        using var db = new RagContext(GraphRagLayout.Default);
        var manifest = GraphSchemaManifest.Create(db.Model);
        Assert.Equal(["Chunk", "Document", "GraphRagLease", "IngestionRun", "Section"], manifest.Nodes.Select(n => n.Label).Order(StringComparer.Ordinal));
        Assert.Equal(
            [("IN_DOCUMENT", "Chunk", "Document"), ("IN_SECTION", "Chunk", "Section"), ("NEXT_CHUNK", "Chunk", "Chunk"),
             ("SECTION_OF", "Section", "Document"), ("SUBSECTION_OF", "Section", "Section")],
            manifest.Relationships.Select(r => (r.RelationshipType, r.Source.NodeLabel, r.Target.NodeLabel)).Order());
        Assert.All(manifest.Relationships, r => Assert.Contains(r.Properties, p => p.Name == nameof(GraphRagChunkDocument.RunId)));
        var embedding = manifest.Nodes.Single(n => n.Label == "Chunk").Properties.Single(p => p.Name == nameof(GraphRagChunk.Embedding));
        Assert.Equal("LIST<FLOAT>", embedding.ProviderType);
        Assert.Equal(8, embedding.VectorDimensions);
        Assert.Same(GraphRagLayout.Default, db.Model.FindAnnotation(GraphRagModelBuilderExtensions.LayoutAnnotation)!.Value);
    }

    [Fact]
    public void The_neo4j_graphrag_layout_writes_chunks_as_that_library_reads_them()
    {
        using var db = new RagContext(GraphRagLayout.Neo4jGraphRag);
        var manifest = GraphSchemaManifest.Create(db.Model);
        Assert.Contains(manifest.Relationships, r => r is { RelationshipType: "FROM_DOCUMENT", Source.NodeLabel: "Chunk", Target.NodeLabel: "Document" });
        Assert.Contains(manifest.Relationships, r => r is { RelationshipType: "NEXT_CHUNK", Source.NodeLabel: "Chunk", Target.NodeLabel: "Chunk" });
        var chunk = manifest.Nodes.Single(n => n.Label == "Chunk");
        Assert.Equal(["id"], chunk.KeyProperties);
        Assert.Subset(chunk.Properties.Select(p => p.Name).ToHashSet(), new HashSet<string> { "id", "index", "text", "embedding" });
    }

    [Fact]
    public void Layouts_validate_names_and_custom_labels_avoid_collisions()
    {
        Assert.Throws<ArgumentException>(() => new RagContext(GraphRagLayout.Default with { ChunkLabel = "Chunk`) DETACH DELETE (x" }).Model);
        Assert.Throws<ArgumentException>(() => new RagContext(GraphRagLayout.Default with { SectionLabel = "Document" }).Model);
        Assert.Throws<ArgumentException>(() => new RagContext(GraphRagLayout.Default with { NextChunkType = "IN_DOCUMENT" }).Model);
        using var custom = new RagContext(GraphRagLayout.Default with { DocumentLabel = "RagDocument", ChunkLabel = "RagChunk" });
        Assert.Contains(GraphSchemaManifest.Create(custom.Model).Nodes, n => n.Label == "RagChunk");
        Assert.Throws<ArgumentOutOfRangeException>(() => new ModelBuilder().AddGraphRag(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ModelBuilder().AddGraphRag(4097));
    }

    [Fact]
    public void Embedding_set_ids_change_with_every_setting_and_survive_with_copies()
    {
        var set = new GraphRagEmbeddingSet("text-embedding-3-small", 1536);
        Assert.Matches("^[0-9a-f]{16}$", set.Id);
        Assert.Equal($"graphrag_chunk_embedding_{set.Id}", set.VectorIndexName);
        GraphRagEmbeddingSet[] others =
        [
            set with { Model = "other" }, set with { Dimensions = 768 }, set with { Similarity = GraphVectorSimilarity.Euclidean },
            set with { Normalize = true }, set with { ModelRevision = "2" }
        ];
        Assert.Equal(others.Length, others.Select(o => o.Id).Append(set.Id).Distinct().Count() - 1);
        Assert.Equal(set.Id, (set with { }).Id);
        Assert.Equal(set.Id, new GraphRagEmbeddingSet("text-embedding-3-small", 1536).Id);
    }

    [Fact]
    public void The_schema_is_deterministic_constraints_then_indexes_for_the_application_plan()
    {
        var set = new GraphRagEmbeddingSet("model", 8);
        var definitions = GraphRagSchema.Definitions(GraphRagLayout.Default, set);
        Assert.Equal(
            ["graphrag_document_key", "graphrag_section_key", "graphrag_chunk_key", "graphrag_run_key", "graphrag_lease_key", set.VectorIndexName, "graphrag_chunk_text"],
            definitions.Select(d => d.Name));
        var vector = definitions.Single(d => d.Kind == GraphMigrationSchemaKind.VectorIndex);
        Assert.Equal(("Chunk", "Embedding", 8, GraphVectorSimilarity.Cosine), (vector.LabelOrType, vector.Properties[0], vector.VectorDimensions!.Value, vector.VectorSimilarity!.Value));
        var text = definitions.Single(d => d.Kind == GraphMigrationSchemaKind.FullTextIndex);
        Assert.Equal(["Text"], text.Properties);
        var steps = GraphRagSchema.CreateSteps(GraphRagLayout.Default, set);
        Assert.Equal(5, steps.Count(s => s.Kind == GraphMigrationStepKind.CreateConstraint));
        Assert.Equal(2, steps.Count(s => s.Kind == GraphMigrationStepKind.CreateIndex));
        var plan = new GraphMigrationPlan("app-002-graphrag", "app-001", steps, GraphMigrationRollbackPolicy.NoRollback("Reviewed."));
        Assert.Equal(plan.Checksum, new GraphMigrationPlan("app-002-graphrag", "app-001", GraphRagSchema.CreateSteps(GraphRagLayout.Default, set),
            GraphMigrationRollbackPolicy.NoRollback("Reviewed.")).Checksum);

        var interop = GraphRagSchema.Definitions(GraphRagLayout.Neo4jGraphRag with { SchemaPrefix = "rag" }, set);
        Assert.Contains(interop, d => d is { Name: "rag_chunk_key" } && d.Properties[0] == "id");
        Assert.Contains(interop, d => d.Kind == GraphMigrationSchemaKind.VectorIndex && d.Properties[0] == "embedding" && d.Name.StartsWith("rag_", StringComparison.Ordinal));
        Assert.Contains(interop, d => d.Kind == GraphMigrationSchemaKind.FullTextIndex && d.Properties[0] == "text");
        Assert.Throws<ArgumentException>(() => GraphRagSchema.Definitions(GraphRagLayout.Default, set with { Model = " " }));
    }

    internal sealed class RagContext(GraphRagLayout layout) : DbContext(new DbContextOptionsBuilder<RagContext>()
        .UseNeo4j(GraphDatabase.Driver("neo4j://localhost:7687", AuthTokens.None), "neo4j")
        .ReplaceService<IModelCacheKeyFactory, LayoutCacheKey>().Options)
    {
        public GraphRagLayout Layout { get; } = layout;
        protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.AddGraphRag(8, o => o.Layout = Layout);
    }

    // Each layout builds its own model.
    internal sealed class LayoutCacheKey : IModelCacheKeyFactory
    {
        public object Create(DbContext context, bool designTime) => (context.GetType(), ((RagContext)context).Layout, designTime);
    }
}
