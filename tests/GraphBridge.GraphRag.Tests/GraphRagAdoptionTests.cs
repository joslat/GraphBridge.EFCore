using System.Text.Json;
using GraphBridge.EntityFrameworkCore.Migrations;
using GraphBridge.GraphRag;
using Microsoft.Extensions.AI;

namespace GraphBridge.GraphRag.Tests;

/// <summary>Wave 9 (GR-18) offline: layout-named indexes, adoption plans, the text locator and grounding.</summary>
public sealed class GraphRagAdoptionTests
{
    private static readonly GraphRagEmbeddingSet Set = new("model", 8);

    [Fact]
    public void A_layout_can_name_existing_indexes_and_their_names_are_validated()
    {
        var named = GraphRagLayout.Neo4jGraphRag with { VectorIndexName = "chunk_embeddings", FullTextIndexName = "chunk_text" };
        var definitions = GraphRagSchema.Definitions(named, Set);
        Assert.Equal("chunk_embeddings", definitions.Single(d => d.Kind == GraphMigrationSchemaKind.VectorIndex).Name);
        Assert.Equal("chunk_text", definitions.Single(d => d.Kind == GraphMigrationSchemaKind.FullTextIndex).Name);
        Assert.Equal(Set.VectorIndexName, GraphRagSchema.Definitions(GraphRagLayout.Default, Set).Single(d => d.Kind == GraphMigrationSchemaKind.VectorIndex).Name);

        Assert.Throws<ArgumentException>(() => GraphRagSchema.Definitions(GraphRagLayout.Default with { VectorIndexName = "x` IF EXISTS" }, Set));
        Assert.Throws<ArgumentException>(() => GraphRagSchema.Definitions(GraphRagLayout.Default with { FullTextIndexName = "" }, Set));
        Assert.Throws<ArgumentException>(() => GraphRagAdoption.Plan(GraphRagLayout.Default with { VectorIndexName = "9vec" }, Set));
    }

    [Fact]
    public void An_adoption_plan_prepares_keys_and_unnamed_indexes_only()
    {
        var unnamed = GraphRagAdoption.Plan(GraphRagLayout.Neo4jGraphRag, Set);
        Assert.Equal(GraphRagSchema.CreateSteps(GraphRagLayout.Neo4jGraphRag, Set).Count, unnamed.PrepareSteps.Count);

        var named = GraphRagAdoption.Plan(GraphRagLayout.Neo4jGraphRag with { VectorIndexName = "chunk_embeddings" }, Set);
        Assert.DoesNotContain(named.PrepareSteps, s => s.Schema?.Kind == GraphMigrationSchemaKind.VectorIndex);
        Assert.Single(named.PrepareSteps, s => s.Schema?.Kind == GraphMigrationSchemaKind.FullTextIndex);
        Assert.Equal(5, named.PrepareSteps.Count(s => s.Kind == GraphMigrationStepKind.CreateConstraint));

        var both = GraphRagAdoption.Plan(GraphRagLayout.Neo4jGraphRag with { VectorIndexName = "v", FullTextIndexName = "t" }, Set);
        Assert.All(both.PrepareSteps, s => Assert.Equal(GraphMigrationStepKind.CreateConstraint, s.Kind));

        Assert.Throws<ArgumentException>(() => GraphRagAdoption.Plan(GraphRagLayout.Default, Set, new GraphRagAdoptionOptions { DocumentPathProperty = "path` }" }));
        Assert.Throws<ArgumentException>(() => GraphRagAdoption.Plan(GraphRagLayout.Default, Set, new GraphRagAdoptionOptions { MediaType = " " }));
        Assert.Throws<ArgumentNullException>(() => GraphRagAdoption.Plan(null!, Set));
    }

    [Fact]
    public void The_locator_finds_passages_exactly_or_across_whitespace_runs_in_order_and_counts_utf8_bytes()
    {
        const string text = "Café au lait.\r\n\r\nThe  crew\tof four trained. The crew of four trained.";
        var locator = new GraphRagTextLocator(text);

        var exact = locator.Find("The  crew\tof four trained.", 0)!.Value;
        Assert.Equal(text.IndexOf("The  crew", StringComparison.Ordinal), exact.Start);
        var second = locator.Find("The crew of four trained.", exact.End)!.Value;
        Assert.Equal(text.LastIndexOf("The crew", StringComparison.Ordinal), second.Start);
        var loose = locator.Find("lait. The crew of", 0)!.Value;
        Assert.Equal(("lait.", "of"), (text[loose.Start..(loose.Start + 5)], text[(loose.End - 2)..loose.End]));

        Assert.Null(locator.Find("The crew of five", 0));
        Assert.Null(locator.Find("Café", 1));
        Assert.Null(locator.Find("", 0));
        Assert.Equal(5, locator.ByteOffset(4));   // "Café": the é takes two bytes
    }

    [Fact]
    public void Grounding_is_source_by_default_and_serializes_as_text()
    {
        var citation = new GraphRagCitation("d", "c", 0, 4, "text");
        Assert.Equal(GraphRagGrounding.Source, citation.Grounding);
        var json = JsonSerializer.Serialize(citation with { Grounding = GraphRagGrounding.Chunk });
        Assert.Contains("\"Grounding\":\"Chunk\"", json, StringComparison.Ordinal);
        Assert.Equal(GraphRagGrounding.Chunk, JsonSerializer.Deserialize<GraphRagCitation>(json)!.Grounding);

        // Tool output names grounding only when it is Chunk, so what a model reads for ingested documents is unchanged.
        var tool = new GraphRagToolCitation("d", "c", 0, 4, "text");
        Assert.DoesNotContain("Grounding", JsonSerializer.Serialize(tool, AIJsonUtilities.DefaultOptions), StringComparison.Ordinal);
        using var chunked = JsonDocument.Parse(JsonSerializer.Serialize(tool with { Grounding = GraphRagGrounding.Chunk }, AIJsonUtilities.DefaultOptions));
        Assert.Equal("Chunk", chunked.RootElement.GetProperty("grounding").GetString());
    }
}
