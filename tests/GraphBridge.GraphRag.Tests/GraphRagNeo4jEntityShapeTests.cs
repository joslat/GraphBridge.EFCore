using GraphBridge.EntityFrameworkCore.Schema;
using GraphBridge.GraphRag;
using GraphBridge.GraphRag.Ontologies;

namespace GraphBridge.GraphRag.Tests;

/// <summary>GR-16 (ADR 0052, section 20) offline: an ontology stored as neo4j-graphrag stores extracted entities.</summary>
public sealed class GraphRagNeo4jEntityShapeTests
{
    [Fact]
    public void The_shape_labels_every_class_shares_FROM_CHUNK_and_names_things_name()
    {
        var shaped = GraphRagOntologies.Novel.WithNeo4jGraphRagEntities();
        Assert.Equal(["__Entity__", "__KGBuilder__"], shaped.EntityLabels);
        Assert.Equal("name", shaped.NameProperty);
        Assert.All(shaped.Classes, c => Assert.Equal("FROM_CHUNK", c.MentionType));
        // What the model sees doesn't change, but the fingerprint does, so a graph in the other shape is ingested again.
        Assert.Equal(GraphRagOntologies.Novel.Classes.Select(c => (c.Kind, c.Label)), shaped.Classes.Select(c => (c.Kind, c.Label)));
        Assert.NotEqual(GraphRagOntologies.Novel.Fingerprint, shaped.Fingerprint);
        Assert.Equal((nameof(GraphRagEntity.Name), 0), (GraphRagOntologies.Novel.NameProperty, GraphRagOntologies.Novel.EntityLabels.Count));
        Assert.Equal("MENTIONED_IN_CHARACTER", GraphRagOntologies.Novel.Classes[0].MentionType);

        // A label prefix keeps the shape.
        var prefixed = shaped.WithLabelPrefix("Rag");
        Assert.Equal(("RagCharacter", "FROM_CHUNK", "name"), (prefixed.Classes[0].Label, prefixed.Classes[0].MentionType, prefixed.NameProperty));
        Assert.Equal(shaped.EntityLabels, prefixed.EntityLabels);
    }

    [Fact]
    public void The_model_maps_the_labels_one_shared_mention_type_and_its_one_constraint()
    {
        var ontology = GraphRagOntologies.Novel.WithNeo4jGraphRagEntities();
        using var db = new GraphRagOntologyTests.OntologyContext(ontology, GraphRagLayout.Neo4jGraphRag);
        var manifest = GraphSchemaManifest.Create(db.Model);
        foreach (var type in ontology.Classes)
        {
            var node = manifest.Nodes.Single(n => n.Label == type.Label);
            Assert.Equal(["__Entity__", "__KGBuilder__"], node.AdditionalLabels);
            Assert.Contains(node.Properties, p => p.Name == "name");
            Assert.DoesNotContain(node.Properties, p => p.Name == "Name");
            var mention = manifest.Relationships.Single(r => r.RelationshipType == "FROM_CHUNK" && r.Source.NodeLabel == type.Label);
            Assert.Equal((GraphSchemaRelationshipIdentity.ApplicationId, "Id", "Chunk"), (mention.Identity, mention.KeyProperty, mention.Target.NodeLabel));
        }

        var definitions = GraphRagSchema.Definitions(GraphRagLayout.Neo4jGraphRag, new GraphRagEmbeddingSet("set", 8), ontology);
        Assert.Single(definitions, d => d.LabelOrType == "FROM_CHUNK");
        var names = definitions.Single(d => d.Name == GraphRagSchema.EntityNamesIndexName(GraphRagLayout.Neo4jGraphRag));
        Assert.Equal(["name", "Aliases"], names.Properties);
    }

    [Fact]
    public void A_fact_type_named_FROM_CHUNK_is_refused()
    {
        var clash = GraphRagOntology.Custom("clash", b => b.Class<Character>("A character.").Class<Place>("A place.")
            .Relationship<RelatedTo<Character, Place>, Character, Place>("FROM_CHUNK", "Clashes with the mention type."));
        Assert.Throws<ArgumentException>(() => clash.WithNeo4jGraphRagEntities());
    }
}
