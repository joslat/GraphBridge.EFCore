using System.Linq.Expressions;
using GraphBridge.EntityFrameworkCore.FluentCypher;
using Microsoft.EntityFrameworkCore;
using Neo4j.Driver;
using Xunit.Abstractions;
using Graph = GraphBridge.EntityFrameworkCore.FluentCypher.Cypher;

namespace GraphBridge.IntegrationTests;

[Collection(Neo4jIntegrationCollection.Name)]
public sealed partial class ProviderNativeTemporalTests(DisposableNeo4jFixture fixture, ITestOutputHelper output)
{
    [Neo4jIntegrationFact]
    public async Task Native_payload_queries_match_CLR_instants_while_Fluent_keeps_native_offsets()
    {
        var run = Guid.NewGuid().ToString("N");
        var instant = DateTimeOffset.UnixEpoch.AddTicks(-1);
        var values = new DateTimeOffset?[] { null, instant, instant.ToOffset(TimeSpan.FromHours(2)),
            instant.ToOffset(TimeSpan.FromHours(-2)), instant.AddTicks(1), instant.AddTicks(-1),
            DateTimeOffset.MinValue, DateTimeOffset.MaxValue };
        var rows = values.Select((value, index) => new Row { Id = Guid.NewGuid(), Run = run, Rank = index,
            Instant = value ?? instant, Optional = value }).ToArray();
        try
        {
            await Schema();
            await using (var seed = Context()) { seed.AddRange(rows); Assert.Equal(rows.Length, await seed.SaveChangesAsync()); }
            await using var db = Context();
            var root = db.Set<Row>().AsNoTracking().Where(r => r.Run == run);
            var materialized = await root.OrderBy(r => r.Rank).ToListAsync();
            for (var i = 0; i < rows.Length; i++)
            {
                Assert.True(rows[i].Instant.EqualsExact(materialized[i].Instant));
                Assert.Equal(rows[i].Optional?.Offset, materialized[i].Optional?.Offset);
                Assert.Equal(rows[i].Optional?.Ticks, materialized[i].Optional?.Ticks);
                var stored = await Stored(rows[i].Id);
                Assert.IsType<ZonedDateTime>(stored["Instant"]);
            }
            var comparisons = 0;
            async Task Check(Expression<Func<Row, bool>> predicate)
            {
                var query = root.Where(predicate);
                Assert.Equal(rows.Where(predicate.Compile()).Select(r => r.Rank).Order(), (await query.ToListAsync()).Select(r => r.Rank).Order());
                Assert.DoesNotContain(run, query.ToQueryString()); comparisons++;
            }
            foreach (var value in values)
            {
                await Check(r => r.Optional == value); await Check(r => r.Optional != value);
                await Check(r => r.Optional > value); await Check(r => r.Optional >= value);
                await Check(r => r.Optional < value); await Check(r => r.Optional <= value);
                await Check(r => !(r.Optional > value)); await Check(r => !(r.Optional == value));
            }
            await Check(r => r.Instant == r.Optional); await Check(r => r.Instant != r.Optional);
            await Check(r => r.Instant <= r.Optional); await Check(r => !(r.Instant < r.Optional));
            var sameWallClockDifferentInstant = new DateTimeOffset(instant.DateTime, TimeSpan.FromHours(2));
            await Check(r => r.Optional == sameWallClockDifferentInstant);
            Assert.Equal(rows.OrderBy(r => r.Optional).ThenBy(r => r.Rank).Select(r => r.Rank),
                await root.OrderBy(r => r.Optional).ThenBy(r => r.Rank).Select(r => r.Rank).ToListAsync());
            Assert.Equal(rows.OrderByDescending(r => r.Optional).ThenBy(r => r.Rank).Select(r => r.Rank),
                await root.OrderByDescending(r => r.Optional).ThenBy(r => r.Rank).Select(r => r.Rank).ToListAsync());
            var dto = await root.OrderBy(r => r.Rank).Select(r => new { r.Rank, r.Optional }).ToListAsync();
            Assert.Equal(rows.Select(r => r.Optional?.Ticks), dto.Select(r => r.Optional?.Ticks));
            var node = Graph.Node<Row>();
            var native = db.Cypher().Match(node).Where(node.Property(r => r.Run).Eq(run)
                .And(node.Property(r => r.Optional).Eq((DateTimeOffset?)instant)))
                .Return(() => new { node.Value.Rank, node.Value.Optional }).Limit(100);
            var nativeRows = await native.ToListAsync();
            Assert.Equal(1, Assert.Single(nativeRows).Rank);
            Assert.True(instant.EqualsExact(nativeRows[0].Optional!.Value));
            Assert.Equal(3, await root.Where(r => r.Optional == instant).CountAsync());
            Assert.Empty(db.ChangeTracker.Entries());
            output.WriteLine($"{comparisons} nullable/changed-parameter/property comparison oracles pass; ascending/descending instant order, exact native payloads, DTOs and native Fluent offset-sensitive equality pass.");
        }
        finally { await Cleanup(run); }
    }

    [Neo4jIntegrationFact]
    public async Task Offset_only_changes_null_removal_bulk_and_rollback_use_EF_tracking()
    {
        var run = Guid.NewGuid().ToString("N");
        var instant = DateTimeOffset.UnixEpoch.AddTicks(1234567);
        var row = new Row { Id = Guid.NewGuid(), Run = run, Instant = instant, Optional = instant };
        try
        {
            await Schema();
            await using var db = Context(); db.Add(row); await db.SaveChangesAsync();
            row.Instant = instant.ToOffset(TimeSpan.FromHours(3)); row.Optional = null;
            Assert.Equal(1, await db.SaveChangesAsync());
            var stored = await Stored(row.Id);
            Assert.Equal(10800, stored["Instant"].As<ZonedDateTime>().OffsetSeconds);
            Assert.False(stored.ContainsKey("Optional"));
            Assert.Equal(2L, stored["Version"]);
            await using (var transaction = await db.Database.BeginTransactionAsync())
            {
                row.Instant = instant.AddTicks(1); await db.SaveChangesAsync(); await transaction.RollbackAsync();
            }
            Assert.Equal(instant, (await Stored(row.Id))["Instant"].As<ZonedDateTime>().ToDateTimeOffset());
            await using var bulk = Context();
            var updated = instant.ToOffset(TimeSpan.FromHours(-4));
            Assert.Equal(1, await bulk.Set<Row>().Where(r => r.Run == run && r.Instant == instant)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.Instant, updated).SetProperty(r => r.Optional, updated)));
            stored = await Stored(row.Id);
            Assert.Equal(-14400, stored["Instant"].As<ZonedDateTime>().OffsetSeconds);
            Assert.Equal(3L, stored["Version"]);
            await using var stale = Context();
            var staleRow = await stale.Set<Row>().SingleAsync(r => r.Id == row.Id);
            await bulk.Set<Row>().Where(r => r.Id == row.Id).ExecuteUpdateAsync(s => s.SetProperty(r => r.Instant, instant));
            staleRow.Instant = instant.AddTicks(2);
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => stale.SaveChangesAsync());
            Assert.Equal(instant, (await Stored(row.Id))["Instant"].As<ZonedDateTime>().ToDateTimeOffset());
        }
        finally { await Cleanup(run); }
    }

    [Neo4jIntegrationFact]
    public async Task External_named_zone_is_read_without_rewrite_and_invalid_values_fail()
    {
        var run = Guid.NewGuid().ToString("N"); var id = Guid.NewGuid();
        try
        {
            await Schema();
            await Execute("CREATE (n:GBTemporal {Id:$id, Run:$run, Rank:0, Version:1, Instant:datetime('2026-10-12T12:00:00.0000001[Europe/Madrid]')})", new { id = id.ToString("D"), run });
            await using (var db = Context())
            {
                var row = await db.Set<Row>().SingleAsync(r => r.Id == id);
                Assert.True(new DateTimeOffset(2026, 10, 12, 12, 0, 0, TimeSpan.FromHours(2)).AddTicks(1).EqualsExact(row.Instant));
                Assert.Equal(0, await db.SaveChangesAsync());
            }
            Assert.IsType<ZoneId>((await Stored(id))["Instant"].As<ZonedDateTime>().Zone);
            foreach (var text in new[] { "2026-10-12T00:00:00.000000001Z", "2026-10-12T00:00:00+18:00", "+10000-01-01T00:00:00Z" })
            {
                await Execute("MATCH (n:GBTemporal {Id:$id}) SET n.Instant=datetime($text)", new { id = id.ToString("D"), text });
                await using var bad = Context();
                await Assert.ThrowsAnyAsync<Exception>(() => bad.Set<Row>().SingleAsync(r => r.Id == id));
                Assert.Empty(bad.ChangeTracker.Entries());
            }
            await Execute("MATCH (n:GBTemporal {Id:$id}) REMOVE n.Instant", new { id = id.ToString("D") });
            await using var missing = Context();
            await Assert.ThrowsAsync<InvalidOperationException>(() => missing.Set<Row>().SingleAsync(r => r.Id == id));
        }
        finally { await Cleanup(run); }
    }

    private TemporalContext Context() => new(new DbContextOptionsBuilder<TemporalContext>().UseNeo4j(fixture.Driver, fixture.Database).Options);
    private Task Schema() => Execute("CREATE CONSTRAINT gb_temporal_key IF NOT EXISTS FOR (n:GBTemporal) REQUIRE n.Id IS UNIQUE");
    private async Task Execute(string query, object? parameters = null)
    { await using var session = fixture.OpenSession(); await (await session.RunAsync(query, parameters)).ConsumeAsync(); }
    private async Task<IDictionary<string, object>> Stored(Guid id)
    {
        await using var session = fixture.OpenSession();
        return (await (await session.RunAsync("MATCH (n:GBTemporal {Id:$id}) RETURN properties(n) AS p", new { id = id.ToString("D") })).SingleAsync())["p"].As<IDictionary<string, object>>();
    }
    private Task Cleanup(string run) => Execute("MATCH (n:GBTemporal {Run:$run}) DELETE n", new { run });
    public sealed class TemporalContext(DbContextOptions<TemporalContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder b)
        { b.Entity<Row>().ToGraphNode("GBTemporal"); b.Entity<Row>().Property(r => r.Version).IsRowVersion(); }
    }
    public sealed class Row
    {
        public Guid Id { get; set; }
        public string Run { get; set; } = "";
        public int Rank { get; set; }
        public DateTimeOffset Instant { get; set; }
        public DateTimeOffset? Optional { get; set; }
        public long Version { get; set; }
    }
}
