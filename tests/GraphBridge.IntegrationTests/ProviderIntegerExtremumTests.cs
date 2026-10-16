using Microsoft.EntityFrameworkCore;
using Neo4j.Driver;

namespace GraphBridge.IntegrationTests;

[Collection(Neo4jIntegrationCollection.Name)]
public sealed class ProviderIntegerExtremumTests(DisposableNeo4jFixture fixture)
{
    [Neo4jIntegrationFact]
    public async Task Min_Max_selector_and_scalar_projection_match_integral_control_values()
    {
        var run = Guid.NewGuid().ToString("N");
        try
        {
            await Seed(run, Row(1, int.MinValue, long.MaxValue, null, null), Row(2, 7, 9, 3, -5), Row(3, int.MaxValue, long.MinValue, 12, 18));
            await using var db = Context();
            var root = db.Set<Item>().Where(p => p.RunId == run);
            Assert.Equal(int.MinValue, await root.MinAsync(p => p.Number));
            Assert.Equal(int.MaxValue, await root.Select(p => p.Number).MaxAsync());
            Assert.Equal(long.MinValue, await root.MinAsync(p => p.Wide));
            Assert.Equal(long.MaxValue, await root.Select(p => p.Wide).MaxAsync());
            Assert.Equal(3, await root.MinAsync(p => p.Optional));
            Assert.Equal(12, await root.Select(p => p.Optional).MaxAsync());
            Assert.Equal(-5L, await root.Select(p => p.OptionalWide).MinAsync());
            Assert.Equal(18L, await root.MaxAsync(p => p.OptionalWide));
            Assert.Equal((int?)int.MinValue, await root.MinAsync(p => (int?)p.Number));
            Assert.Equal(7, await root.Where(p => p.Number < int.MaxValue).OrderByDescending(p => p.Number).MaxAsync(p => p.Number));
            Assert.Empty(db.ChangeTracker.Entries());
        }
        finally { await Cleanup(run); }
    }

    [Neo4jIntegrationFact]
    public async Task Empty_and_all_null_inputs_preserve_nullable_and_required_result_semantics()
    {
        var run = Guid.NewGuid().ToString("N");
        try
        {
            await Seed(run, Row(1, 4, 5, null, null));
            await using var db = Context();
            var root = db.Set<Item>().Where(p => p.RunId == run);
            Assert.Null(await root.MinAsync(p => p.Optional));
            Assert.Null(await root.MaxAsync(p => p.OptionalWide));
            var empty = root.Where(p => p.Number < 0);
            Assert.Null(await empty.MinAsync(p => p.Optional));
            Assert.Null(await empty.MaxAsync(p => p.OptionalWide));
            Assert.Null(await empty.MinAsync(p => (int?)p.Number));
            Assert.Null(await empty.MaxAsync(p => (long?)p.Wide));
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => empty.MinAsync(p => p.Number));
            Assert.Equal("Sequence contains no elements.", error.Message);
            await Assert.ThrowsAsync<InvalidOperationException>(() => empty.MaxAsync(p => p.Wide));
            Assert.Equal(4, await root.MaxAsync(p => p.Number));
            Assert.Empty(db.ChangeTracker.Entries());
        }
        finally { await Cleanup(run); }
    }

    [Neo4jIntegrationFact]
    public async Task Invalid_integer_inputs_cannot_hide_behind_a_valid_extremum()
    {
        var run = Guid.NewGuid().ToString("N");
        try
        {
            await using var db = Context();
            var root = db.Set<Item>().Where(p => p.RunId == run);
            foreach (var corrupt in new object?[] { null, "wrong-type", 1.5, true, (long)int.MaxValue + 1, (long)int.MinValue - 1 })
            {
                await Seed(run, Row(1, 0, 0, 0, 0), Row(2, corrupt, 2, 2, 2));
                Assert.Contains("aggregate validation failed", (await Assert.ThrowsAsync<InvalidOperationException>(() => root.MinAsync(p => p.Number))).Message);
                Assert.Contains("aggregate validation failed", (await Assert.ThrowsAsync<InvalidOperationException>(() => root.MaxAsync(p => p.Number))).Message);
                Assert.Equal(0, await root.Where(p => p.Id == 1).MinAsync(p => p.Number));
                await Cleanup(run);
            }
            await Seed(run, Row(1, 1, 1, "bad-optional", 1));
            await Assert.ThrowsAsync<InvalidOperationException>(() => root.MinAsync(p => p.Optional));
            await Cleanup(run);
            await Seed(run, Row(1, 1, "bad-wide", 1, "bad-optional-wide"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => root.MaxAsync(p => p.Wide));
            await Assert.ThrowsAsync<InvalidOperationException>(() => root.MinAsync(p => p.OptionalWide));
            Assert.Empty(db.ChangeTracker.Entries());
        }
        finally { await Cleanup(run); }
    }

    [Neo4jIntegrationFact]
    public async Task Aggregate_rebinds_parameters_and_borrows_explicit_transaction_without_accepting_entities()
    {
        var run = Guid.NewGuid().ToString("N");
        try
        {
            await Seed(run, Row(1, 1, 1, 1, 1), Row(2, 8, 8, 8, 8));
            await using var db = Context();
            var minimum = 1;
            var query = db.Set<Item>().Where(p => p.RunId == run && p.Number >= minimum);
            Assert.Equal(1, await query.MinAsync(p => p.Number));
            minimum = 2;
            Assert.Equal(8, await query.MinAsync(p => p.Number));
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => query.MaxAsync(p => p.Number, canceled.Token));
            await using (var transaction = await db.Database.BeginTransactionAsync())
            {
                await db.Database.QueryCypherAsync(
                    "MATCH (n:GraphBridgeIntegerAggregate {RunId: $run}) SET n.Number = 12 RETURN count(*) AS count",
                    new Dictionary<string, object?> { ["run"] = run }, row => row["count"].As<long>());
                Assert.Equal(12, await query.MinAsync(p => p.Number));
                await transaction.RollbackAsync();
            }
            Assert.Equal(8, await query.MaxAsync(p => p.Number));
            Assert.Empty(db.ChangeTracker.Entries());
        }
        finally { await Cleanup(run); }
    }

    private AggregateContext Context() => new(new DbContextOptionsBuilder<AggregateContext>().UseNeo4j(fixture.Driver, fixture.Database).Options);
    private async Task Seed(string run, params Dictionary<string, object?>[] rows)
    {
        foreach (var row in rows) row["RunId"] = run;
        await using var session = fixture.OpenSession();
        await (await session.RunAsync("UNWIND $rows AS row CREATE (n:GraphBridgeIntegerAggregate) SET n = row", new { rows })).ConsumeAsync();
    }
    private async Task Cleanup(string run)
    {
        await using var session = fixture.OpenSession();
        await (await session.RunAsync("MATCH (n:GraphBridgeIntegerAggregate {RunId: $run}) DELETE n", new { run })).ConsumeAsync();
    }
    private static Dictionary<string, object?> Row(int id, object? number, object? wide, object? optional, object? optionalWide)
        => new() { ["Id"] = id, ["Number"] = number, ["Wide"] = wide, ["Optional"] = optional, ["OptionalWide"] = optionalWide };
    public sealed class Item
    {
        public int Id { get; set; }
        public string RunId { get; set; } = "";
        public int Number { get; set; }
        public long Wide { get; set; }
        public int? Optional { get; set; }
        public long? OptionalWide { get; set; }
    }
    public sealed class AggregateContext(DbContextOptions options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder builder)
        {
            builder.Entity<Item>().ToGraphNode("GraphBridgeIntegerAggregate");
            builder.Entity<Item>().Property(p => p.Id).ValueGeneratedNever();
        }
    }
}
