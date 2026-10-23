using System.Collections.Concurrent;
using System.Diagnostics;
using GraphBridge.EntityFrameworkCore.Diagnostics;
using GraphBridge.EntityFrameworkCore.FluentCypher;
using Microsoft.EntityFrameworkCore;
using Neo4j.Driver;
using Graph = GraphBridge.EntityFrameworkCore.FluentCypher.Cypher;

namespace GraphBridge.IntegrationTests;

/// <summary>ADR 0050 live checks: tags in the server's view, timeouts enforced by the server, counters equal to a readback.</summary>
[Collection(Neo4jIntegrationCollection.Name)]
public sealed class ProviderOperationalControlsTests(DisposableNeo4jFixture fixture)
{
    [Neo4jIntegrationFact]
    public async Task A_tag_is_visible_in_SHOW_TRANSACTIONS_and_the_timeout_ends_the_statement_on_the_server()
    {
        await using var db = Context();
        var tag = "graphbridge-tag-" + Guid.NewGuid().ToString("N");
        // The same comment text LINQ generates for TagWith(tag); the statement is slow only so it can be observed.
        var linqText = db.Set<Item>().TagWith(tag).ToQueryString();
        Assert.StartsWith("// " + tag + "\n\n", linqText);
        db.Database.SetGraphCommandTimeout(TimeSpan.FromSeconds(3));
        var slow = db.Database.QueryCypherAsync("// " + tag + "\n\nUNWIND range(1, 2000000000) AS x WITH x WHERE x < 0 RETURN count(x) AS c",
            new Dictionary<string, object?>(), r => r["c"].As<long>());

        var seen = false;
        var watch = Stopwatch.StartNew();
        while (!seen && !slow.IsCompleted && watch.Elapsed < TimeSpan.FromSeconds(2.5))
        {
            await using var observer = fixture.OpenSession();
            var cursor = await observer.RunAsync("SHOW TRANSACTIONS YIELD currentQuery RETURN currentQuery");
            seen = (await cursor.ToListAsync(r => r["currentQuery"] as string ?? "")).Any(query => query.Contains("// " + tag, StringComparison.Ordinal));
            if (!seen) await Task.Delay(100);
        }
        Assert.True(seen, "SHOW TRANSACTIONS did not show the tag comment in currentQuery while the statement ran.");

        var error = await Assert.ThrowsAnyAsync<Neo4jException>(() => slow);
        Assert.StartsWith("Neo.ClientError.Transaction.TransactionTimedOut", error.Code);
    }

    [Neo4jIntegrationFact]
    public async Task Timeouts_are_accepted_on_every_owned_path_and_refused_per_query_inside_an_application_transaction()
    {
        var run = await SeedAsync();
        try
        {
            await using var db = Context(TimeSpan.FromSeconds(30));
            Assert.Equal(TimeSpan.FromSeconds(30), db.Database.GetGraphCommandTimeout());
            Assert.Equal(3, (await db.Set<Item>().Where(i => i.Run == run).ToListAsync()).Count);
            var item = Graph.Node<Item>();
            var names = await db.Cypher().Match(item).Where(() => item.Value.Run == run).Return(() => item.Value.Name)
                .OrderBy(() => item.Value.Name).WithTimeout(TimeSpan.FromSeconds(5)).TagWith("names").Limit(10).ToListAsync();
            Assert.Equal(new[] { "a", "b", "c" }, names);
            Assert.Equal(1L, Assert.Single(await db.Database.QueryCypherAsync("RETURN 1 AS one", new Dictionary<string, object?>(), r => r["one"].As<long>())));

            await using var transaction = await db.Database.BeginTransactionAsync();
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.Cypher().Match(item).Where(() => item.Value.Run == run)
                .Return(() => item.Value.Name).WithTimeout(TimeSpan.FromSeconds(5)).Limit(10).ToListAsync());
            Assert.Equal(3, (await db.Cypher().Match(item).Where(() => item.Value.Run == run).Return(() => item.Value.Name).Limit(10).ToListAsync()).Count);
            await transaction.RollbackAsync();
        }
        finally { await CleanupAsync(run); }
    }

    [Neo4jIntegrationFact]
    public async Task Write_counters_equal_an_independent_readback_for_native_SaveChanges_and_bulk_writes()
    {
        var run = "poc_" + Guid.NewGuid().ToString("N");
        await ConstraintsAsync();
        var spans = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == GraphBridgeDiagnostics.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = spans.Enqueue
        };
        ActivitySource.AddActivityListener(listener);
        try
        {
            await using var db = Context();
            var created = await db.Database.ExecuteCypherAsync(
                "CREATE (:POCItem {item_key: $a, Run: $run, Name: 'a'})-[:POC_LINK]->(:POCItem {item_key: $b, Run: $run, Name: 'b'})",
                new Dictionary<string, object?> { ["a"] = Guid.NewGuid().ToString("D"), ["b"] = Guid.NewGuid().ToString("D"), ["run"] = run });
            Assert.Equal(new GraphWriteCounters(2, 0, 1, 0, 6, 2, 0), created);
            Assert.Equal(2L, await CountAsync(run));

            db.Add(new Item { Id = Guid.NewGuid(), Run = run, Name = "c" });
            db.Add(new Item { Id = Guid.NewGuid(), Run = run, Name = "d" });
            await db.SaveChangesAsync();
            Assert.Equal(4L, await CountAsync(run));
            var save = spans.Last(s => s.OperationName == GraphBridgeDiagnostics.SaveChangesActivityName);
            Assert.Equal(2, save.GetTagItem("graphbridge.counters.nodes_created"));

            await ExecuteAsync("MATCH (:POCItem {Run: $run})-[r:POC_LINK]->() DELETE r", new { run });
            var deleted = await db.Set<Item>().Where(i => i.Run == run).TagWith("cleanup").ExecuteDeleteAsync();
            Assert.Equal(4, deleted);
            Assert.Equal(0L, await CountAsync(run));
            var bulk = spans.Last(s => s.OperationName == GraphBridgeDiagnostics.BulkActivityName);
            Assert.Equal(4, bulk.GetTagItem("graphbridge.counters.nodes_deleted"));
        }
        finally { await CleanupAsync(run); }
    }

    private EntityContext Context(TimeSpan? timeout = null) => new(timeout is { } value
        ? new DbContextOptionsBuilder<EntityContext>().UseNeo4j(fixture.Driver, fixture.Database, neo4j => neo4j.CommandTimeout(value)).Options
        : new DbContextOptionsBuilder<EntityContext>().UseNeo4j(fixture.Driver, fixture.Database).Options);

    private Task ConstraintsAsync() => ExecuteAsync("CREATE CONSTRAINT poc_item_key IF NOT EXISTS FOR (n:POCItem) REQUIRE n.item_key IS UNIQUE");

    private async Task<string> SeedAsync()
    {
        await ConstraintsAsync();
        var run = "poc_" + Guid.NewGuid().ToString("N");
        foreach (var name in new[] { "a", "b", "c" })
            await ExecuteAsync("CREATE (:POCItem {item_key: $id, Run: $run, Name: $name})", new { id = Guid.NewGuid().ToString("D"), run, name });
        return run;
    }

    private async Task<long> CountAsync(string run)
    {
        await using var session = fixture.OpenSession();
        var cursor = await session.RunAsync("MATCH (n:POCItem {Run: $run}) RETURN count(n) AS c", new { run });
        return (await cursor.SingleAsync())["c"].As<long>();
    }

    private async Task ExecuteAsync(string text, object? parameters = null)
    {
        await using var session = fixture.OpenSession();
        await (parameters is null ? await session.RunAsync(text) : await session.RunAsync(text, parameters)).ConsumeAsync();
    }

    private async Task CleanupAsync(string run)
    {
        await ExecuteAsync("MATCH (n:POCItem {Run: $run})-[r]-() DELETE r", new { run });
        await ExecuteAsync("MATCH (n:POCItem {Run: $run}) DELETE n", new { run });
    }

    public sealed class Item { public Guid Id { get; set; } public string Run { get; set; } = ""; public string Name { get; set; } = ""; }

    public sealed class EntityContext(DbContextOptions<EntityContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder model)
            => model.Entity<Item>().ToGraphNode("POCItem").Property(i => i.Id).HasGraphPropertyName("item_key");
    }
}
