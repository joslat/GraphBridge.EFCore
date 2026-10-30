using System.Text;
using GraphBridge.EntityFrameworkCore.Migrations;
using GraphBridge.GraphRag;
using Microsoft.Extensions.AI;

namespace GraphRagStarter.Tests;

// Offline: no database and no model.
public sealed class StarterTests
{
    [Fact]
    public void The_schema_plan_holds_key_constraints_a_vector_index_and_a_full_text_index()
    {
        var steps = Starter.SchemaPlan.Steps;
        Assert.Equal(5, steps.Count(s => s.Kind == GraphMigrationStepKind.CreateConstraint));
        Assert.Contains(steps, s => s.Schema?.Kind == GraphMigrationSchemaKind.VectorIndex);
        Assert.Contains(steps, s => s.Schema?.Kind == GraphMigrationSchemaKind.FullTextIndex);
    }

    [Fact]
    public async Task The_stand_in_embedder_is_deterministic_and_never_all_zeros()
    {
        var embedder = new HashingEmbedder(Starter.EmbeddingSet.Dimensions);
        var vectors = await embedder.GenerateAsync(["Graph retrieval finds passages.", "Graph retrieval finds passages.", ""]);
        Assert.Equal(vectors[0].Vector.ToArray(), vectors[1].Vector.ToArray());
        Assert.Equal(Starter.EmbeddingSet.Dimensions, vectors[2].Vector.Length);
        Assert.Contains(vectors[2].Vector.ToArray(), x => x != 0);
    }

    [Fact]
    public void A_quote_is_located_at_its_exact_bytes_or_not_at_all()
    {
        const string text = "Reciprocal rank fusion merges the two lists into one ranking. Café included.";
        var item = new GraphRagEvidenceItem("chunk", "document", text, 100, 100 + Encoding.UTF8.GetByteCount(text), 1, []);
        var citation = GraphRagCitationVerifier.Locate(item, "merges the two lists")!;
        Assert.Equal("merges the two lists", Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(text), citation.StartByte - 100, citation.EndByte - citation.StartByte));
        Assert.Null(GraphRagCitationVerifier.Locate(item, "splits the two lists"));
    }
}
