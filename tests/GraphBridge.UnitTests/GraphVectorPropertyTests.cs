using GraphBridge.EntityFrameworkCore.Diagnostics;
using GraphBridge.EntityFrameworkCore.FluentCypher;
using GraphBridge.EntityFrameworkCore.Metadata;
using GraphBridge.EntityFrameworkCore.Query;
using GraphBridge.EntityFrameworkCore.Schema;
using Microsoft.EntityFrameworkCore;
using Graph = GraphBridge.EntityFrameworkCore.FluentCypher.Cypher;

namespace GraphBridge.UnitTests;

/// <summary>Embeddings as EF properties (ADR 0054): opt-in mapping, write checks, read conversion, tracking and query refusals.
/// The live suite round-trips vectors on the server.</summary>
public sealed class GraphVectorPropertyTests
{
    [Fact]
    public void Bulk_rechecks_compare_vectors_read_twice_item_by_item()
    {
        // The bulk executor re-reads candidates after locking them; a vector arrives as a new list each time.
        List<object> first = [0.5, -1.0, 2.0], again = [0.5, -1.0, 2.0];
        Assert.True(Neo4jBulkExecutor.SameStoreValue(first, again));
        Assert.False(Neo4jBulkExecutor.SameStoreValue(first, new List<object> { 0.5, -1.0, 2.5 }));
        Assert.False(Neo4jBulkExecutor.SameStoreValue(first, new List<object> { 0.5, -1.0 }));
        Assert.False(Neo4jBulkExecutor.SameStoreValue(first, null));
        Assert.True(Neo4jBulkExecutor.SameStoreValue("same", "same"));
        Assert.True(Neo4jBulkExecutor.SameStoreValue(null, null));
    }

    [Fact]
    public void An_unmarked_float_array_still_fails_model_building_and_IsGraphVector_opts_in()
    {
        using var unmarked = new ProviderModelContext(ProviderTestDriver.Create(), b =>
        {
            b.Entity<Chunk>().ToGraphNode("VectorChunk");
            b.Entity<Chunk>().Property(c => c.Embedding);
        });
        var refused = Assert.ThrowsAny<InvalidOperationException>(() => unmarked.Model);
        Assert.Contains("Embedding", refused.Message);

        using var db = Context();
        var embedding = db.Model.FindEntityType(typeof(Chunk))!.FindProperty(nameof(Chunk.Embedding))!;
        Assert.Equal(3, embedding[GraphAnnotationNames.VectorDimensions]);
        Assert.Equal(typeof(float[]), embedding.GetTypeMapping().ClrType);
        Assert.Null(embedding.GetTypeMapping().Converter);
        Assert.Equal(typeof(ReadOnlyMemory<float>), db.Model.FindEntityType(typeof(Passage))!.FindProperty(nameof(Passage.Vector))!.GetTypeMapping().ClrType);
    }

    [Fact]
    public void IsGraphVector_accepts_only_float_vectors_of_1_to_4096_dimensions()
    {
        Assert.Throws<ArgumentException>(() => Build(b => b.Entity<Chunk>().Property(c => c.Text).IsGraphVector(3)));
        Assert.Throws<ArgumentOutOfRangeException>(() => Build(b => b.Entity<Chunk>().Property(c => c.Embedding).IsGraphVector(0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => Build(b => b.Entity<Chunk>().Property(c => c.Embedding).IsGraphVector(4097)));
        Build(b => b.Entity<Chunk>().Property(c => c.Embedding).IsGraphVector(4096));
        Build(b => b.Entity<Chunk>().Property(nameof(Chunk.Embedding)).IsGraphVector(1));
    }

    [Fact]
    public void Model_validation_keeps_a_vector_payload_only()
    {
        using var token = new ProviderModelContext(ProviderTestDriver.Create(), b =>
        {
            Map(b, 3);
            b.Entity<Chunk>().Property(c => c.Embedding).IsConcurrencyToken();
        });
        Assert.Contains("payload only", Assert.Throws<InvalidOperationException>(() => token.Model).Message);
        using var converted = new ProviderModelContext(ProviderTestDriver.Create(), b =>
        {
            Map(b, 3);
            b.Entity<Chunk>().Property(c => c.Embedding).HasConversion(v => v, v => v);
        });
        Assert.Contains("value converter", Assert.ThrowsAny<InvalidOperationException>(() => converted.Model).Message);
    }

    [Fact]
    public void Writes_store_exact_float32_values_and_refuse_bad_vectors_without_printing_them()
    {
        using var db = Context();
        var binding = Binding(db, typeof(Chunk), nameof(Chunk.Embedding));
        float[] vector = [0.1f, -2.5f, 3e-8f];
        var stored = Assert.IsType<double[]>(binding.ToStore(vector));
        Assert.Equal(vector.Select(v => (double)v), stored);
        Assert.Equal(vector, Assert.IsType<float[]>(binding.FromStore(stored.Cast<object>().ToList())));
        Assert.Null(binding.ToStore(null));

        var length = Assert.Throws<InvalidOperationException>(() => binding.ToStore(new[] { 0.25f, 0.75f }));
        Assert.Contains("Embedding'", length.Message);
        Assert.Contains("2 values", length.Message);
        Assert.Contains("3 dimensions", length.Message);
        Assert.DoesNotContain("0.25", length.Message);
        Assert.Contains("NaN or infinite", Assert.Throws<InvalidOperationException>(() => binding.ToStore(new[] { 1f, float.NaN, 0f })).Message);
        Assert.Contains("NaN or infinite", Assert.Throws<InvalidOperationException>(() => binding.ToStore(new[] { 1f, float.PositiveInfinity, 0f })).Message);
        Assert.Contains("all zeros", Assert.Throws<InvalidOperationException>(() => binding.ToStore(new float[3])).Message);

        var memory = Binding(db, typeof(Passage), nameof(Passage.Vector));
        Assert.Equal(vector.Select(v => (double)v), Assert.IsType<double[]>(memory.ToStore(new ReadOnlyMemory<float>(vector))));
        // The default of a non-nullable ReadOnlyMemory<float> is empty, which is the wrong length.
        Assert.Contains("0 values", Assert.Throws<InvalidOperationException>(() => memory.ToStore(ReadOnlyMemory<float>.Empty)).Message);
    }

    [Fact]
    public void Reads_convert_numeric_lists_of_the_declared_length_and_refuse_the_rest()
    {
        using var db = Context();
        var binding = Binding(db, typeof(Chunk), nameof(Chunk.Embedding));
        Assert.Equal(new[] { 1f, (float)0.1, -3f }, (float[])binding.FromStore(new List<object> { 1L, 0.1, -3.0 })!);
        Assert.Null(binding.FromStore(null));
        Assert.Contains("2 values", Assert.Throws<InvalidOperationException>(() => binding.FromStore(new List<object> { 1.0, 2.0 })).Message);
        Assert.Contains("non-numeric", Assert.Throws<InvalidOperationException>(() => binding.FromStore(new List<object> { 1.0, "2", 3.0 })).Message);
        Assert.Contains("finite float32", Assert.Throws<InvalidOperationException>(() => binding.FromStore(new List<object> { 1.0, double.MaxValue, 3.0 })).Message);
        Assert.Contains("not a list", Assert.Throws<InvalidOperationException>(() => binding.FromStore("1,2,3")).Message);
        var memory = Assert.IsType<ReadOnlyMemory<float>>(Binding(db, typeof(Passage), nameof(Passage.Vector)).FromStore(new List<object> { 1.0, 2.0, 3.0 }));
        Assert.Equal(new[] { 1f, 2f, 3f }, memory.ToArray());
    }

    [Fact]
    public void Change_tracking_compares_contents_bit_for_bit_and_snapshots_copies()
    {
        using var db = Context();
        var chunk = new Chunk { Id = "c1", Text = "graphs", Embedding = [1f, 2f, 3f] };
        db.Attach(chunk);
        chunk.Embedding[1] = 5f;
        db.ChangeTracker.DetectChanges();
        Assert.True(db.Entry(chunk).Property(c => c.Embedding).IsModified);

        var comparer = db.Entry(chunk).Property(c => c.Embedding).Metadata.GetValueComparer();
        Assert.True(comparer.Equals(new[] { 1f, 2f }, new[] { 1f, 2f }));
        Assert.False(comparer.Equals(new[] { 0f }, new[] { -0f }));
        Assert.False(comparer.Equals(new[] { 1f }, new[] { 1f, 2f }));
        Assert.True(comparer.Equals(null, null));
        Assert.False(comparer.Equals(new[] { 1f }, null));
        Assert.Equal(comparer.GetHashCode(new[] { 1f, 2f }), comparer.GetHashCode(new[] { 1f, 2f }));
        float[] original = [1f, 2f];
        var snapshot = Assert.IsType<float[]>(comparer.Snapshot(original));
        Assert.NotSame(original, snapshot);
        Assert.Equal(original, snapshot);

        var passage = new Passage { Id = "p1", Vector = new[] { 1f, 2f, 3f } };
        db.Attach(passage);
        passage.Vector = new[] { 1f, 2f, 3f };
        db.ChangeTracker.DetectChanges();
        Assert.False(db.Entry(passage).Property(p => p.Vector).IsModified);
        passage.Vector = new[] { 1f, 2f, 4f };
        db.ChangeTracker.DetectChanges();
        Assert.True(db.Entry(passage).Property(p => p.Vector).IsModified);
    }

    [Fact]
    public void The_manifest_records_a_vector_with_its_dimensions_and_leaves_other_manifests_alone()
    {
        using var db = Context();
        var manifest = GraphSchemaManifest.Create(db.Model);
        var embedding = manifest.Nodes.Single(n => n.Label == "VectorChunk").Properties.Single(p => p.Name == nameof(Chunk.Embedding));
        Assert.Equal("LIST<FLOAT>", embedding.ProviderType);
        Assert.Equal(3, embedding.VectorDimensions);
        Assert.Contains("\"vectorDimensions\":3", manifest.CanonicalJson);
        Assert.Null(manifest.Nodes.Single(n => n.Label == "VectorChunk").Properties.Single(p => p.Name == nameof(Chunk.Text)).VectorDimensions);
        using var wider = Context(dimensions: 4);
        Assert.NotEqual(manifest.Fingerprint, GraphSchemaManifest.Create(wider.Model).Fingerprint);
        using var plain = new ProviderModelContext(ProviderTestDriver.Create(), b => b.Entity<Chunk>().ToGraphNode("VectorChunk").Ignore(c => c.Embedding));
        Assert.DoesNotContain("vectorDimensions", GraphSchemaManifest.Create(plain.Model).CanonicalJson);
    }

    [Fact]
    public void LINQ_returns_and_null_tests_a_vector_and_refuses_every_other_use()
    {
        using var db = Context();
        var chunks = db.Set<Chunk>();
        Assert.Contains("Embedding", chunks.Select(c => c.Embedding).ToQueryString());
        Assert.Contains("Embedding", chunks.Where(c => c.Embedding == null).ToQueryString());
        Assert.Contains("Embedding", chunks.Where(c => c.Embedding != null).Select(c => c.Id).ToQueryString());
        float[] probe = [1f, 2f, 3f];
        Expect("GBQ120", "typed vector search", () => chunks.Where(c => c.Embedding == probe).ToQueryString());
        Expect("GBQ120", "typed vector search", () => chunks.Where(c => probe != c.Embedding).ToQueryString());
        Expect("GBQ120", null, () => chunks.Where(c => Equals(c.Embedding, probe)).ToQueryString());
        Expect("GBQ120", "typed vector search", () => chunks.OrderBy(c => c.Embedding).ToQueryString());
        Expect("GBQ120", null, () => chunks.Where(c => c.Embedding!.Length == 3).ToQueryString());
        Expect("GBQ120", null, () => chunks.Where(c => c.Embedding![0] > 0.5f).ToQueryString());
        Expect("GBQ120", null, () => chunks.Where(c => c.Embedding!.Any()).ToQueryString());
        Expect("GBQ130", "typed vector search", () => chunks.Where(c => new[] { probe }.Contains(c.Embedding)).ToQueryString());
        Expect("GBQ105", null, () => chunks.Max(c => c.Embedding));
        var copy = Assert.ThrowsAny<Exception>(() => chunks.ExecuteUpdate(s => s.SetProperty(c => c.Embedding, c => c.Embedding)));
        Assert.Contains("[GBQ160]", copy.Message);
    }

    [Fact]
    public void Fluent_Cypher_returns_and_null_tests_a_vector_and_refuses_it_in_predicates_ordering_and_aggregates()
    {
        using var db = Context();
        var chunk = Graph.Node<Chunk>();
        Assert.Contains("Embedding", db.Cypher().Match(chunk).Return(() => chunk.Value.Embedding).ToQueryString());
        Assert.Contains("IS NULL", db.Cypher().Match(chunk).Where(chunk.Property(c => c.Embedding).IsNull()).Return(() => chunk.Value.Id).ToQueryString());
        var embeddings = Graph.Collect(chunk.Property(c => c.Embedding), 5);
        Assert.Contains("collect(", db.Cypher().Match(chunk).With(embeddings).Return(() => embeddings.Value).ToQueryString());
        float[] probe = [1f, 2f, 3f];
        Assert.Equal("GBFC114", Code(() => db.Cypher().Match(chunk).Where(chunk.Property(c => c.Embedding).Eq(probe)).Return(() => chunk.Value.Id).ToQueryString()));
        Assert.Equal("GBFC114", Code(() => db.Cypher().Match(chunk).Where(chunk.Property(c => c.Embedding).In(probe)).Return(() => chunk.Value.Id).ToQueryString()));
        Assert.Equal("GBFC114", Code(() => db.Cypher().Match(chunk).Return(() => chunk.Value.Embedding).OrderBy(chunk.Property(c => c.Embedding)).ToQueryString()));
        var largest = Graph.Max(chunk.Property(c => c.Embedding));
        Assert.Equal("GBFC114", Code(() => db.Cypher().Match(chunk).With(largest).Return(() => largest.Value).ToQueryString()));
    }

    private static void Expect(string code, string? alternative, Action query)
    {
        var error = Assert.Throws<Neo4jQueryTranslationException>(query);
        Assert.Equal(code, error.Code);
        if (alternative is not null) Assert.Contains(alternative, error.Message);
    }

    private static void Expect(string code, string? alternative, Func<object?> query) => Expect(code, alternative, () => { query(); });

    private static string Code(Func<object> action)
    {
        var error = Assert.Throws<FluentCypherException>(() => action());
        return error.Code;
    }

    private static GraphPropertyBinding Binding(DbContext db, Type entity, string property)
        => new(db.Model.FindEntityType(entity)!.FindProperty(property)!, property);

    private static ProviderModelContext Context(int dimensions = 3) => new(ProviderTestDriver.Create(), b => Map(b, dimensions));

    private static void Build(Action<ModelBuilder> extra)
    {
        using var db = new ProviderModelContext(ProviderTestDriver.Create(), b => { Map(b, 3); extra(b); });
        _ = db.Model;
    }

    private static void Map(ModelBuilder b, int dimensions)
    {
        b.Entity<Chunk>().ToGraphNode("VectorChunk").Property(c => c.Embedding).IsGraphVector(dimensions);
        b.Entity<Passage>().ToGraphNode("VectorPassage").Property(p => p.Vector).IsGraphVector(3);
    }

    public sealed class Chunk
    {
        public string Id { get; set; } = "";
        public string Text { get; set; } = "";
        public float[]? Embedding { get; set; }
    }

    public sealed class Passage
    {
        public string Id { get; set; } = "";
        public ReadOnlyMemory<float> Vector { get; set; }
    }
}
