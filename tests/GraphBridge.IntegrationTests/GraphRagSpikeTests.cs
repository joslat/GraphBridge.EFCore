using Microsoft.EntityFrameworkCore;
using Neo4j.Driver;

namespace GraphBridge.IntegrationTests;

// GraphRAG wave 0 spike (GR-31), question (b): vectors on mapped chunk nodes before PL-49 (GR-07).
// Recorded in ADR 0052. Remove when embeddings are EF properties (ADR 0054) and wave 1's live tests cover vectors.
[Collection(Neo4jIntegrationCollection.Name)]
public sealed class GraphRagSpikeTests(DisposableNeo4jFixture fixture)
{
    private const string Index = "graphbridge_spike_chunk_embedding";

    [Neo4jIntegrationFact]
    public async Task A_named_native_write_puts_vectors_on_mapped_chunks_that_typed_search_finds_and_EF_updates_keep()
    {
        var run = Guid.NewGuid().ToString("N");
        try
        {
            await Execute("CREATE CONSTRAINT graphbridge_spike_chunk_key IF NOT EXISTS FOR (c:GraphRagSpikeChunk) REQUIRE c.Id IS UNIQUE");
            await Execute($"CREATE VECTOR INDEX {Index} IF NOT EXISTS FOR (c:GraphRagSpikeChunk) ON (c.embedding) OPTIONS {{indexConfig: {{`vector.dimensions`:3, `vector.similarity_function`:'cosine'}}}}");
            var first = new SpikeChunk { Id = Guid.NewGuid(), Run = run, Order = 0, Text = "graphs connect facts" };
            var second = new SpikeChunk { Id = Guid.NewGuid(), Run = run, Order = 1, Text = "tables hold rows" };
            await using (var db = Context())
            {
                db.Chunks.AddRange(first, second);
                await db.SaveChangesAsync();
            }

            await using var context = Context();
            // Workaround for GR-07 (PL-49): vectors written by native Cypher, by key, in one bounded statement.
            var rows = new[] { (first.Id, new[] { 1.0, 0, 0 }), (second.Id, new[] { 0.0, 1, 0 }) }
                .Select(r => (object?)new Dictionary<string, object?> { ["id"] = r.Item1.ToString("D"), ["embedding"] = r.Item2 }).ToList();
            var counters = await context.Database.ExecuteCypherAsync(
                "UNWIND $rows AS row MATCH (c:GraphRagSpikeChunk {Id: row.id}) SET c.embedding = row.embedding",
                new Dictionary<string, object?> { ["rows"] = rows });
            Assert.Equal(2, counters.PropertiesSet);
            await Execute("CALL db.awaitIndex($index, 60)", new { index = Index });

            double[] query = [0.9, 0.1, 0];
            var hits = await context.Database.QueryVectorNodesAsync(Index, query, node => node["Id"].As<string>(), limit: 1);
            Assert.Equal(first.Id.ToString("D"), Assert.Single(hits.Results).Value);
            Assert.Equal(3, hits.Provenance.Dimensions);

            // EF reads only mapped properties, so the unmapped vector doesn't disturb materialization,
            // and an update of a mapped property leaves it in place.
            var tracked = await context.Chunks.SingleAsync(c => c.Id == first.Id);
            Assert.Equal("graphs connect facts", tracked.Text);
            tracked.Text = "graphs connect facts, edited";
            await context.SaveChangesAsync();
            var after = await context.Database.QueryVectorNodesAsync(Index, query, node => node["Text"].As<string>(), limit: 1);
            Assert.Equal("graphs connect facts, edited", Assert.Single(after.Results).Value);
        }
        finally
        {
            await Execute("MATCH (c:GraphRagSpikeChunk {Run: $run}) DELETE c", new { run });
            await Execute($"DROP INDEX {Index} IF EXISTS");
            await Execute("DROP CONSTRAINT graphbridge_spike_chunk_key IF EXISTS");
        }
    }

    private SpikeContext Context() => new(new DbContextOptionsBuilder<SpikeContext>().UseNeo4j(fixture.Driver, fixture.Database).Options);

    private async Task Execute(string text, object? parameters = null)
    {
        await using var session = fixture.OpenSession();
        await (parameters is null ? await session.RunAsync(text) : await session.RunAsync(text, parameters)).ConsumeAsync();
    }

    public sealed class SpikeChunk
    {
        public Guid Id { get; set; }
        public string Run { get; set; } = "";
        public int Order { get; set; }
        public string Text { get; set; } = "";
    }

    public sealed class SpikeContext(DbContextOptions<SpikeContext> options) : DbContext(options)
    {
        public DbSet<SpikeChunk> Chunks => Set<SpikeChunk>();
        protected override void OnModelCreating(ModelBuilder model)
            => model.Entity<SpikeChunk>().ToGraphNode("GraphRagSpikeChunk").Property(c => c.Id).ValueGeneratedNever();
    }
}
