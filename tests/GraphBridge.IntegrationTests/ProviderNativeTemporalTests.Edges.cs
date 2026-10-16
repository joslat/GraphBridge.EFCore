using GraphBridge.EntityFrameworkCore.FluentCypher;
using Microsoft.EntityFrameworkCore;
using Neo4j.Driver;
using Graph = GraphBridge.EntityFrameworkCore.FluentCypher.Cypher;

namespace GraphBridge.IntegrationTests;

public sealed partial class ProviderNativeTemporalTests
{
    [Neo4jIntegrationFact]
    public async Task Identified_edge_temporal_payloads_keep_native_offsets_and_LINQ_instant_order()
    {
        var run = Guid.NewGuid().ToString("N"); var instant = DateTimeOffset.UnixEpoch.AddTicks(-1);
        var source = new Endpoint { Id = Guid.NewGuid(), Run = run };
        var target = new Endpoint { Id = Guid.NewGuid(), Run = run };
        var edges = new[]
        {
            new Edge { Id = Guid.NewGuid(), Run = run, Source = source, Target = target, Rank = 0, Instant = instant },
            new Edge { Id = Guid.NewGuid(), Run = run, Source = source, Target = target, Rank = 1, Instant = instant.ToOffset(TimeSpan.FromHours(4)), Optional = instant },
            new Edge { Id = Guid.NewGuid(), Run = run, Source = source, Target = target, Rank = 2, Instant = instant.AddTicks(1), Optional = instant.AddTicks(1) }
        };
        try
        {
            await Execute("CREATE CONSTRAINT gb_temporal_endpoint IF NOT EXISTS FOR (n:GBTemporalEndpoint) REQUIRE n.Id IS UNIQUE");
            await Execute("CREATE CONSTRAINT gb_temporal_edge IF NOT EXISTS FOR ()-[r:GB_TEMPORAL_EDGE]-() REQUIRE r.Id IS UNIQUE");
            await using (var seed = EdgeContext()) { seed.AddRange(edges); Assert.Equal(5, await seed.SaveChangesAsync()); }
            await using var db = EdgeContext();
            var root = db.Set<Edge>().Where(r => r.Run == run);
            var equal = await root.Where(r => r.Instant == instant).OrderBy(r => r.Instant).ThenBy(r => r.Rank).ToListAsync();
            Assert.Equal(new[] { 0, 1 }, equal.Select(r => r.Rank));
            Assert.Equal(TimeSpan.FromHours(4), equal[1].Instant.Offset);
            Assert.Equal(new[] { 0, 1, 2 }, await root.OrderBy(r => r.Optional).ThenBy(r => r.Rank).Select(r => r.Rank).ToListAsync());
            equal[1].Instant = instant.ToOffset(TimeSpan.FromHours(-3)); equal[1].Optional = null;
            Assert.Equal(1, await db.SaveChangesAsync());
            await using (var session = fixture.OpenSession())
            {
                var stored = await (await session.RunAsync("MATCH ()-[r:GB_TEMPORAL_EDGE {Id:$id}]->() RETURN properties(r) AS p", new { id = edges[1].Id.ToString("D") })).SingleAsync();
                var properties = stored["p"].As<IDictionary<string, object>>();
                Assert.Equal(-10800, properties["Instant"].As<ZonedDateTime>().OffsetSeconds);
                Assert.False(properties.ContainsKey("Optional")); Assert.Equal(2L, properties["Version"]);
            }
            var a = Graph.Node<Endpoint>(); var b = Graph.Node<Endpoint>(); var e = Graph.Edge<Edge>();
            var native = await db.Cypher().Match(a.Out(e, b)).Where(e.Property(r => r.Run).Eq(run)
                .And(e.Property(r => r.Instant).Eq(instant))).Return(() => e.Value.Instant).Limit(10).ToListAsync();
            Assert.True(instant.EqualsExact(Assert.Single(native)));
            await using var fresh = EdgeContext();
            Assert.Equal(2, await fresh.Set<Edge>().Where(r => r.Run == run && r.Instant == instant)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.Optional, (DateTimeOffset?)instant)));
            Assert.All(await fresh.Set<Edge>().Where(r => r.Run == run && r.Instant == instant).ToListAsync(), e => Assert.Equal(instant, e.Optional));
            await Execute("MATCH ()-[r:GB_TEMPORAL_EDGE {Id:$id}]->() REMOVE r.Instant", new { id = edges[0].Id.ToString("D") });
            await using var invalid = EdgeContext();
            await Assert.ThrowsAsync<InvalidOperationException>(() => invalid.Set<Edge>().SingleAsync(r => r.Id == edges[0].Id));
            Assert.Empty(invalid.ChangeTracker.Entries());
        }
        finally
        {
            await Execute("MATCH ()-[r:GB_TEMPORAL_EDGE {Run:$run}]->() DELETE r", new { run });
            await Execute("MATCH (n:GBTemporalEndpoint {Run:$run}) DELETE n", new { run });
        }
    }

    private TemporalEdgeContext EdgeContext() => new(new DbContextOptionsBuilder<TemporalEdgeContext>().UseNeo4j(fixture.Driver, fixture.Database).Options);
    public sealed class TemporalEdgeContext(DbContextOptions<TemporalEdgeContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder b)
        {
            b.Entity<Endpoint>().ToGraphNode("GBTemporalEndpoint");
            var edge = b.Entity<Edge>();
            edge.HasOne(e => e.Source).WithMany().HasForeignKey(e => e.SourceId);
            edge.HasOne(e => e.Target).WithMany().HasForeignKey(e => e.TargetId);
            edge.ToGraphIdentifiedRelationship("GB_TEMPORAL_EDGE", nameof(Edge.Id), [nameof(Edge.SourceId)], [nameof(Edge.TargetId)]);
            edge.Property(e => e.Version).IsRowVersion();
        }
    }
    public sealed class Endpoint { public Guid Id { get; set; } public string Run { get; set; } = ""; }
    public sealed class Edge
    {
        public Guid Id { get; set; }
        public string Run { get; set; } = "";
        public int Rank { get; set; }
        public Guid SourceId { get; set; }
        public Guid TargetId { get; set; }
        public Endpoint Source { get; set; } = null!;
        public Endpoint Target { get; set; } = null!;
        public DateTimeOffset Instant { get; set; }
        public DateTimeOffset? Optional { get; set; }
        public long Version { get; set; }
    }
}
