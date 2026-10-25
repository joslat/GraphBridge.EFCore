using GraphBridge.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore;
using Neo4j.Driver;

namespace GraphBridge.IntegrationTests;

// Embeddings as EF properties (ADR 0054) on the disposable fixture. The index is created from the migration step's
// DDL; ProviderMigrationTests covers the journaled migration path, which owns the fixture's journal.
[Collection(Neo4jIntegrationCollection.Name)]
public sealed class GraphVectorPropertyTests(DisposableNeo4jFixture fixture) : IAsyncLifetime
{
    private const string Label = "GraphBridgeVectorChunk";
    private const string Index = "graphbridge_vector_chunk_embedding";
    private const int Dimensions = 1536;
    private readonly string _run = Guid.NewGuid().ToString("N");

    public async Task InitializeAsync()
    {
        if (!Enabled) return;
        // The label belongs to these tests; leftovers of an interrupted run would compete in nearest-neighbour results.
        await Execute($"MATCH (c:{Label}) DELETE c");
        await Execute("CREATE CONSTRAINT graphbridge_vector_chunk_key IF NOT EXISTS FOR (c:GraphBridgeVectorChunk) REQUIRE c.Id IS UNIQUE");
        await Execute($"DROP INDEX {Index} IF EXISTS");
        var step = GraphMigrationStep.CreateIndex("embedding",
            GraphMigrationSchemaDefinition.VectorIndex(Index, "NODE", Label, nameof(Chunk.Embedding), Dimensions, GraphVectorSimilarity.Cosine));
        await Execute(GraphMigrationStepRenderer.Render(step).Cypher);
        await Execute("CALL db.awaitIndex($index, 60)", new { index = Index });
    }

    public async Task DisposeAsync()
    {
        if (!Enabled) return;
        await Execute($"MATCH (c:{Label} {{Run: $run}}) DELETE c", new { run = _run });
        await Execute($"DROP INDEX {Index} IF EXISTS");
        await Execute("DROP CONSTRAINT graphbridge_vector_chunk_key IF EXISTS");
    }

    private static bool Enabled => Environment.GetEnvironmentVariable("GRAPHBRIDGE_INTEGRATION") == "1";

    [Neo4jIntegrationFact]
    public async Task SaveChanges_upserts_and_queries_round_trip_1536_dimensions_bit_for_bit_for_both_types()
    {
        var first = NewChunk("first", Vector(1));
        first.Memory = Vector(2);
        await using (var db = Context())
        {
            db.Chunks.Add(first);
            await db.SaveChangesAsync();
        }
        await using (var db = Context())
        {
            var read = await db.Chunks.SingleAsync(c => c.Id == first.Id);
            AssertBits(first.Embedding!, read.Embedding!);
            AssertBits(first.Memory!.Value.ToArray(), read.Memory!.Value.ToArray());
            AssertBits(first.Embedding!, (await db.Chunks.Where(c => c.Id == first.Id).Select(c => c.Embedding).SingleAsync())!);
            Assert.Equal(Dimensions, await Scalar($"MATCH (c:{Label} {{Id: $id}}) RETURN size(c.Embedding) AS value", first.Id));

            // An update of another property leaves the vector; an in-place change is detected and written.
            read.Text = "edited";
            await db.SaveChangesAsync();
            read.Embedding![7] = -0.125f;
            await db.SaveChangesAsync();
        }
        await using (var db = Context())
        {
            var read = await db.Chunks.SingleAsync(c => c.Id == first.Id);
            Assert.Equal("edited", read.Text);
            Assert.Equal(-0.125f, read.Embedding![7]);
            first.Embedding![7] = -0.125f;
            AssertBits(first.Embedding, read.Embedding);
            // A null removes the stored property on update.
            read.Memory = null;
            await db.SaveChangesAsync();
            Assert.Equal(0L, await Scalar($"MATCH (c:{Label} {{Id: $id}}) WHERE c.Memory IS NOT NULL RETURN count(c) AS value", first.Id));
            Assert.Equal(1, await db.Chunks.CountAsync(c => c.Run == _run && c.Memory == null));
        }

        var upserted = NewChunk("upserted", Vector(3));
        await using (var db = Context())
            Assert.Equal(1, (await db.Chunks.ExecuteUpsertAsync([upserted])).Created);
        await using (var db = Context())
            AssertBits(upserted.Embedding!, (await db.Chunks.SingleAsync(c => c.Id == upserted.Id)).Embedding!);
    }

    [Neo4jIntegrationFact]
    public async Task Bulk_updates_and_deletes_select_entities_that_carry_vectors()
    {
        await using var db = Context();
        db.Chunks.AddRange(NewChunk("bulk-a", Vector(4)), NewChunk("bulk-b", Vector(5)));
        await db.SaveChangesAsync();
        // The recheck after locking reads each vector again as a new list; equal items are no change.
        Assert.Equal(2, await db.Chunks.Where(c => c.Run == _run).ExecuteUpdateAsync(s => s.SetProperty(c => c.Text, "bulk-edited")));
        Assert.Equal(2, await db.Chunks.Where(c => c.Run == _run && c.Text == "bulk-edited").ExecuteDeleteAsync());
        Assert.Equal(0, await db.Chunks.CountAsync(c => c.Run == _run));
    }

    [Neo4jIntegrationFact]
    public async Task Typed_vector_search_finds_vectors_written_through_EF()
    {
        var near = NewChunk("near", Axis(0));
        var far = NewChunk("far", Axis(1));
        await using var db = Context();
        db.Chunks.AddRange(near, far);
        await db.SaveChangesAsync();
        var query = Axis(0).Select(v => (double)v).ToArray();
        query[2] = 0.1;
        var hits = await db.Database.QueryVectorNodesAsync(Index, query, node => node["Id"].As<string>(), limit: 1);
        Assert.Equal(near.Id, Assert.Single(hits.Results).Value);
        Assert.Equal(Dimensions, hits.Provenance.Dimensions);
    }

    [Neo4jIntegrationFact]
    public async Task Bad_vectors_are_refused_before_anything_is_written()
    {
        await using (var db = Context())
        {
            db.Chunks.Add(NewChunk("short", new float[3] { 1, 2, 3 }));
            var error = await Assert.ThrowsAnyAsync<Exception>(() => db.SaveChangesAsync());
            Assert.Contains($"{Dimensions} dimensions", error.ToString());
        }
        await using (var db = Context())
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => db.Chunks.ExecuteUpsertAsync([NewChunk("good", Vector(4)), NewChunk("zero", new float[Dimensions])]));
            Assert.StartsWith("Entity 1 (zero-based)", error.Message);
            Assert.Contains("all zeros", error.Message);
        }
        var stored = NewChunk("stored", Vector(5));
        await using (var db = Context())
        {
            db.Chunks.Add(stored);
            await db.SaveChangesAsync();
            var nan = Vector(6);
            nan[0] = float.NaN;
            await Assert.ThrowsAnyAsync<Exception>(() => db.Chunks.Where(c => c.Id == stored.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.Embedding, nan)));
        }
        Assert.Equal(1L, await Scalar($"MATCH (c:{Label} {{Run: $id}}) RETURN count(c) AS value", _run));
        await using (var db = Context())
            AssertBits(stored.Embedding!, (await db.Chunks.SingleAsync(c => c.Id == stored.Id)).Embedding!);
    }

    [Neo4jIntegrationFact]
    public async Task Vectors_written_natively_are_read_and_wrong_ones_fail_materialisation()
    {
        var id = "native-" + _run;
        await Execute($"CREATE (:{Label} {{Id: $id, Run: $run, Text: 'native', Embedding: [i IN range(1, {Dimensions}) | toFloat(i) / 1000.0]}})", new { id, run = _run });
        await using (var db = Context())
        {
            var read = await db.Chunks.SingleAsync(c => c.Id == id);
            Assert.Equal(0.001f, read.Embedding![0]);
            Assert.Equal((float)(Dimensions / 1000.0), read.Embedding[^1]);
        }
        await Execute($"MATCH (c:{Label} {{Id: $id}}) SET c.Embedding = [1, 2, 3]", new { id });
        await using (var db = Context())
        {
            var error = await Assert.ThrowsAnyAsync<Exception>(() => db.Chunks.SingleAsync(c => c.Id == id));
            Assert.Contains($"it is mapped with {Dimensions} dimensions", error.ToString());
        }
    }

    private static void AssertBits(float[] expected, float[] actual)
        => Assert.Equal(expected.Select(BitConverter.SingleToInt32Bits), actual.Select(BitConverter.SingleToInt32Bits));

    private static float[] Vector(int seed)
    {
        var random = new Random(seed);
        var vector = new float[Dimensions];
        for (var i = 0; i < Dimensions; i++) vector[i] = (float)(random.NextDouble() * 2 - 1) * (i % 97 == 0 ? 1e-30f : 1f);
        return vector;
    }

    private static float[] Axis(int axis)
    {
        var vector = new float[Dimensions];
        vector[axis] = 1f;
        return vector;
    }

    private Chunk NewChunk(string name, float[] embedding) => new() { Id = name + "-" + _run, Run = _run, Text = name, Embedding = embedding };

    private VectorContext Context() => new(new DbContextOptionsBuilder<VectorContext>().UseNeo4j(fixture.Driver, fixture.Database).Options);

    private async Task Execute(string text, object? parameters = null)
    {
        await using var session = fixture.OpenSession();
        await (parameters is null ? await session.RunAsync(text) : await session.RunAsync(text, parameters)).ConsumeAsync();
    }

    private async Task<long> Scalar(string text, string id)
    {
        await using var session = fixture.OpenSession();
        return (await (await session.RunAsync(text, new { id })).SingleAsync())["value"].As<long>();
    }

    public sealed class Chunk
    {
        public string Id { get; set; } = "";
        public string Run { get; set; } = "";
        public string Text { get; set; } = "";
        public float[]? Embedding { get; set; }
        public ReadOnlyMemory<float>? Memory { get; set; }
    }

    public sealed class VectorContext(DbContextOptions<VectorContext> options) : DbContext(options)
    {
        public DbSet<Chunk> Chunks => Set<Chunk>();
        protected override void OnModelCreating(ModelBuilder model)
        {
            var chunk = model.Entity<Chunk>().ToGraphNode(Label);
            chunk.Property(c => c.Embedding).IsGraphVector(Dimensions);
            chunk.Property(c => c.Memory).IsGraphVector(Dimensions);
        }
    }
}
