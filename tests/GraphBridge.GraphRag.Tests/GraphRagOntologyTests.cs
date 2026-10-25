using System.Text.Json;
using GraphBridge.EntityFrameworkCore.Migrations;
using GraphBridge.EntityFrameworkCore.Schema;
using GraphBridge.GraphRag.Ontologies;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Neo4j.Driver;

namespace GraphBridge.GraphRag.Tests;

/// <summary>Wave 4 (GR-43, GR-32): the standard ontologies, custom ones, their EF mapping, the model schema and the schema steps.</summary>
public sealed class GraphRagOntologyTests
{
    [Fact]
    public void The_standard_ontologies_have_the_design_tables_classes_and_facts()
    {
        Assert.Equal(["Person", "Organization", "Place", "Event", "Concept"], GraphRagOntologies.Generic.Classes.Select(c => c.Label));
        Assert.Equal(12 + 25, GraphRagOntologies.Generic.Relationships.Count);
        Assert.Equal(25, GraphRagOntologies.Generic.Relationships.Count(r => r.Type == "RELATED_TO"));
        Assert.Equal(["Event", "Organization", "Person", "Place"],
            GraphRagOntologies.Generic.Relationships.Where(r => r.Type == "LOCATED_IN").Select(r => r.FromKind).Order(StringComparer.Ordinal));
        Assert.Empty(GraphRagOntologies.Generic.Quotes);
        Assert.Equal(["Date"], GraphRagOntologies.Generic.Classes.Single(c => c.Label == "Event").Properties);

        Assert.Equal(["Character", "Place", "Group", "Event"], GraphRagOntologies.Novel.Classes.Select(c => c.Label));
        Assert.Equal(["KIN_OF", "RELATED_TO", "MEMBER_OF", "PARTICIPATES_IN", "TAKES_PLACE_IN"], GraphRagOntologies.Novel.Relationships.Select(r => r.Type));
        Assert.Equal(("SAYS", "Character", true), GraphRagOntologies.Novel.Quotes.Select(q => (q.Type, q.SpeakerKind, q.Addressee)).Single());
        Assert.False(GraphRagOntologies.Novel.Classes.Single(c => c.Label == "Group").Aliases);

        Assert.Equal(6, GraphRagOntologies.Article.Classes.Count);
        Assert.Contains(GraphRagOntologies.Article.Relationships, r => r is { Type: "AUTHORED", FromKind: "Person", ToKind: "Document" });
        Assert.Contains(GraphRagOntologies.Article.Relationships, r => r is { Type: "ABOUT", FromKind: "Document", ToKind: "Topic" });
        Assert.Equal("QUOTED", GraphRagOntologies.Article.Quotes.Single().Type);

        Assert.Contains("the man", GraphRagOntologies.Novel.Placeholders);
        Assert.Equal("MENTIONED_IN_PERSON", GraphRagOntologies.Generic.Classes[0].MentionType);
        string[] fingerprints = [GraphRagOntologies.Generic.Fingerprint, GraphRagOntologies.Novel.Fingerprint, GraphRagOntologies.Article.Fingerprint];
        Assert.Equal(3, fingerprints.Distinct().Count());
        Assert.All(fingerprints, f => Assert.Matches("^[0-9a-f]{64}$", f));
    }

    [Fact]
    public void Custom_ontologies_are_validated_and_fingerprinted_by_content()
    {
        static GraphRagOntology Build(string description) => GraphRagOntology.Custom("lab", b => b
            .Class<Person>(description).Class<Place>("A place.")
            .Relationship<LocatedIn<Person>, Person, Place>("LOCATED_IN", "Where someone is.")
            .Placeholders("The  MAN"));
        Assert.Equal(Build("Someone.").Fingerprint, Build("Someone.").Fingerprint);
        Assert.NotEqual(Build("Someone.").Fingerprint, Build("Somebody.").Fingerprint);
        Assert.Equal(["the man"], Build("Someone.").Placeholders);

        Assert.Throws<ArgumentException>(() => GraphRagOntology.Custom("empty", _ => { }));
        Assert.Throws<ArgumentException>(() => GraphRagOntology.Custom("twice", b => b.Class<Person>("A.").Class<Person>("B.")));
        Assert.Throws<ArgumentException>(() => GraphRagOntology.Custom("unregistered", b => b.Class<Person>("A.")
            .Relationship<LocatedIn<Person>, Person, Place>("LOCATED_IN", "Place isn't registered.")));
        Assert.Throws<ArgumentException>(() => GraphRagOntology.Custom("injection", b => b.Class<Person>("A.").Class<Place>("B.")
            .Relationship<LocatedIn<Person>, Person, Place>("LOCATED_IN`]->() DETACH DELETE (x", "Bad type.")));
        Assert.Throws<ArgumentException>(() => GraphRagOntology.Custom("same triple", b => b.Class<Person>("A.").Class<Place>("B.")
            .Relationship<LocatedIn<Person>, Person, Place>("LOCATED_IN", "One.")
            .Relationship<RelatedTo<Person, Place>, Person, Place>("LOCATED_IN", "Two.")));
        Assert.Throws<ArgumentException>(() => GraphRagOntology.Custom("mention clash", b => b.Class<Person>("A.").Class<Place>("B.")
            .Relationship<LocatedIn<Person>, Person, Place>("MENTIONED_IN_PERSON", "Clashes with the mention type.")));
        Assert.Throws<ArgumentException>(() => GraphRagOntology.Custom("not a property", b => b.Class<Person>("A.", true, "Date")));
        Assert.Throws<ArgumentException>(() => GraphRagOntology.Custom("base property", b => b.Class<Event>("A.", true, "Name")));
        Assert.Throws<ArgumentException>(() => GraphRagOntology.Custom("document both ends", b => b.Class<Person>("A.")
            .Relationship<About, GraphRagDocument, GraphRagDocument>("ABOUT", "Itself.")));

        Assert.Equal("MENTIONED_IN_CHARACTER", new GraphRagOntologyClass(typeof(Character), "Character", "Character", "", true, []).MentionType);
        Assert.Equal("MENTIONED_IN_TV_SHOW", new GraphRagOntologyClass(typeof(Character), "Character", "TVShow", "", true, []).MentionType);
        Assert.Equal("MENTIONED_IN_STAR_SHIP", new GraphRagOntologyClass(typeof(Character), "Character", "StarShip", "", true, []).MentionType);

        // A label prefix changes the stored labels and mention types, not the kinds or the fact types.
        var prefixed = GraphRagOntologies.Novel.WithLabelPrefix("Rag");
        Assert.Equal(("Character", "RagCharacter", "MENTIONED_IN_RAG_CHARACTER"), (prefixed.Classes[0].Kind, prefixed.Classes[0].Label, prefixed.Classes[0].MentionType));
        Assert.Equal(GraphRagOntologies.Novel.Relationships, prefixed.Relationships);
        Assert.NotEqual(GraphRagOntologies.Novel.Fingerprint, prefixed.Fingerprint);
        Assert.Throws<ArgumentException>(() => GraphRagOntologies.Novel.WithLabelPrefix("Rag-"));
        Assert.Equal("the white rabbit", GraphRagOntology.Normalize("  The \t White\nRABBIT "));
    }

    [Theory]
    [InlineData("generic")]
    [InlineData("novel")]
    [InlineData("article")]
    public void Each_standard_ontology_maps_nodes_identified_mentions_and_fact_associations(string name)
    {
        var ontology = Standard(name);
        using var db = new OntologyContext(ontology);
        var manifest = GraphSchemaManifest.Create(db.Model);
        foreach (var type in ontology.Classes)
        {
            Assert.Equal(["Id"], manifest.Nodes.Single(n => n.Label == type.Label).KeyProperties);
            var mention = manifest.Relationships.Single(r => r.RelationshipType == type.MentionType);
            Assert.Equal((GraphSchemaRelationshipIdentity.ApplicationId, "Id", type.Label, "Chunk"),
                (mention.Identity, mention.KeyProperty, mention.Source.NodeLabel, mention.Target.NodeLabel));
        }
        string Label(string kind) => kind == "Document" ? "Document" : ontology.FindClass(kind)!.Label;
        foreach (var fact in ontology.Relationships)
        {
            var expected = (fact.Type, Label(fact.FromKind), Label(fact.ToKind));
            var mapped = manifest.Relationships.Single(r => (r.RelationshipType, r.Source.NodeLabel, r.Target.NodeLabel) == expected);
            Assert.Equal(GraphSchemaRelationshipIdentity.EndpointPair, mapped.Identity);
            Assert.Contains(mapped.Properties, p => p.Name == nameof(GraphRagFact.DocumentIds));
        }
        foreach (var quote in ontology.Quotes)
            Assert.Contains(manifest.Relationships, r => r is { Identity: GraphSchemaRelationshipIdentity.ApplicationId } && r.RelationshipType == quote.Type
                && r.Source.NodeLabel == Label(quote.SpeakerKind) && r.Target.NodeLabel == "Chunk");
        Assert.Same(ontology, db.Model.FindAnnotation(GraphRagModelBuilderExtensions.OntologyAnnotation)!.Value);
    }

    [Fact]
    public void An_ontology_label_or_type_that_the_layout_uses_is_refused()
    {
        var chunk = GraphRagOntology.Custom("clash", b => b.Class<Chunk>("A class named like the lexical chunk."));
        Assert.Throws<ArgumentException>(() => new OntologyContext(chunk).Model);
        using var renamed = new OntologyContext(chunk, GraphRagLayout.Default with { ChunkLabel = "Passage" });
        Assert.Contains(GraphSchemaManifest.Create(renamed.Model).Nodes, n => n.Label == "Chunk");
        Assert.Contains(GraphSchemaManifest.Create(renamed.Model).Relationships, r => r is { RelationshipType: "MENTIONED_IN_CHUNK", Target.NodeLabel: "Passage" });
    }

    [Fact]
    public void The_model_schema_constrains_replies_and_describes_kinds_patterns_and_quotes()
    {
        using var db = new OntologyContext(GraphRagOntologies.Article);
        var schema = GraphRagModelSchema.From(db.Model);
        Assert.Equal(GraphRagOntologies.Article.Classes.Select(c => c.Label), schema.Classes.Select(c => c.Label));
        using var prefixed = new OntologyContext(GraphRagOntologies.Article.WithLabelPrefix("Rag"));
        var prefixedSchema = GraphRagModelSchema.From(prefixed.Model);
        Assert.Equal(("Person", "RagPerson"), (prefixedSchema.Classes[0].Kind, prefixedSchema.Classes[0].Label));
        Assert.Equal(schema.ToPromptText(), prefixedSchema.ToPromptText());
        Assert.Equal(GraphRagOntologies.Article.Relationships.Count, schema.Patterns.Count);
        using var json = JsonDocument.Parse(schema.ToJsonSchema());
        var root = json.RootElement.GetProperty("properties");
        var entity = root.GetProperty("entities").GetProperty("items").GetProperty("properties");
        Assert.Equal(schema.Classes.Select(c => c.Kind), entity.GetProperty("kind").GetProperty("enum").EnumerateArray().Select(e => e.GetString()));
        Assert.True(entity.TryGetProperty("date", out _));
        var relationship = root.GetProperty("relationships").GetProperty("items").GetProperty("properties");
        Assert.Contains("Document", relationship.GetProperty("fromKind").GetProperty("enum").EnumerateArray().Select(e => e.GetString()));
        Assert.True(relationship.TryGetProperty("role", out _));
        Assert.Equal(["QUOTED"], root.GetProperty("quotes").GetProperty("items").GetProperty("properties").GetProperty("type").GetProperty("enum")
            .EnumerateArray().Select(e => e.GetString()));
        var prompt = schema.ToPromptText();
        Assert.Contains("- Person: A named individual. Fields: name, aliases, description.", prompt);
        Assert.Contains("- AUTHORED: Person -> Document.", prompt);
        Assert.Contains("- QUOTED: spoken by a Person.", prompt);

        using var novel = new OntologyContext(GraphRagOntologies.Novel);
        var novelSchema = GraphRagModelSchema.From(novel.Model);
        using var novelJson = JsonDocument.Parse(novelSchema.ToJsonSchema());
        var novelRelationship = novelJson.RootElement.GetProperty("properties").GetProperty("relationships").GetProperty("items").GetProperty("properties");
        Assert.DoesNotContain("Document", novelRelationship.GetProperty("toKind").GetProperty("enum").EnumerateArray().Select(e => e.GetString()));
        Assert.NotEqual(schema.Fingerprint, novelSchema.Fingerprint);
        Assert.Equal(novelSchema.Fingerprint, GraphRagModelSchema.From(novel.Model).Fingerprint);

        using var lexical = new GraphRagModelTests.RagContext(GraphRagLayout.Default);
        Assert.Throws<InvalidOperationException>(() => GraphRagModelSchema.From(lexical.Model));
    }

    [Fact]
    public void Schema_definitions_add_the_ontology_keys_ahead_of_the_indexes_and_one_names_index()
    {
        var set = new GraphRagEmbeddingSet("m", 8);
        Assert.Equal(GraphRagSchema.Definitions(GraphRagLayout.Default, set).Select(d => d.Name),
            GraphRagSchema.Definitions(GraphRagLayout.Default, set, null).Select(d => d.Name));
        var definitions = GraphRagSchema.Definitions(GraphRagLayout.Default, set, GraphRagOntologies.Novel);
        Assert.Equal(7 + 4 * 2 + 1 + 1, definitions.Count);
        var firstIndex = definitions.ToList().FindIndex(d => d.IsIndex);
        Assert.All(definitions.Skip(firstIndex), d => Assert.True(d.IsIndex));
        Assert.Contains(definitions, d => d is { Name: "graphrag_character_key", EntityType: "NODE", LabelOrType: "Character" });
        Assert.Contains(definitions, d => d is { Name: "graphrag_mentioned_in_character_key", EntityType: "RELATIONSHIP", LabelOrType: "MENTIONED_IN_CHARACTER" });
        Assert.Contains(definitions, d => d is { Name: "graphrag_says_key", EntityType: "RELATIONSHIP", LabelOrType: "SAYS" });
        var names = definitions.Single(d => d.Name == GraphRagSchema.EntityNamesIndexName(GraphRagLayout.Default));
        Assert.Equal(GraphMigrationSchemaKind.FullTextIndex, names.Kind);
        Assert.Equal(["Character", "Place", "Group", "Event"], names.LabelsOrTypes);
        Assert.Equal(["Name", "Aliases"], names.Properties);
        Assert.Equal(definitions.Count, GraphRagSchema.CreateSteps(GraphRagLayout.Default, set, GraphRagOntologies.Novel).Count);
    }

    internal static GraphRagOntology Standard(string name) => name switch
    {
        "generic" => GraphRagOntologies.Generic,
        "novel" => GraphRagOntologies.Novel,
        _ => GraphRagOntologies.Article
    };

    public sealed class Chunk : GraphRagEntity { }

    internal sealed class OntologyContext(GraphRagOntology ontology, GraphRagLayout? layout = null) : DbContext(new DbContextOptionsBuilder<OntologyContext>()
        .UseNeo4j(GraphDatabase.Driver("neo4j://localhost:7687", AuthTokens.None), "neo4j")
        .ReplaceService<IModelCacheKeyFactory, OntologyCacheKey>().Options)
    {
        public GraphRagOntology Ontology { get; } = ontology;
        public GraphRagLayout Layout { get; } = layout ?? GraphRagLayout.Default;
        protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.AddGraphRag(8, o => (o.Layout, o.Ontology) = (Layout, Ontology));
    }

    internal sealed class OntologyCacheKey : IModelCacheKeyFactory
    {
        public object Create(DbContext context, bool designTime) => (context.GetType(), ((OntologyContext)context).Ontology, ((OntologyContext)context).Layout, designTime);
    }
}
