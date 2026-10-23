using GraphBridge.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Neo4j.Driver;
using Xunit.Abstractions;

namespace GraphBridge.IntegrationTests;

/// <summary>ADR 0051 live checks: counts and stored values, the update selector, refusals, rollback, concurrent writers.</summary>
[Collection(Neo4jIntegrationCollection.Name)]
public sealed class ProviderUpsertTests(DisposableNeo4jFixture fixture, ITestOutputHelper output)
{
    [Neo4jIntegrationFact]
    public async Task Counts_equal_a_readback_and_stored_values_equal_SaveChanges_for_every_mapped_type()
    {
        var run = await SetupAsync();
        try
        {
            await using var db = Context();
            var saved = Item(run, "saved");
            db.Add(saved);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var upserted = Item(run, "upserted");
            var created = await db.Items.ExecuteUpsertAsync([upserted, Item(run, "second")]);
            Assert.Equal((2, 0), (created.Created, created.Matched));
            Assert.Equal(3L, await CountAsync(run));
            Assert.Empty(db.ChangeTracker.Entries());

            // Same values under another key: every stored property but the key and Label equals what SaveChanges wrote.
            var bySave = await PropertiesAsync(saved.Id);
            var byUpsert = await PropertiesAsync(upserted.Id);
            Assert.Equal(Without(bySave), Without(byUpsert));

            upserted.Score = 99;
            var again = await db.Items.ExecuteUpsertAsync([upserted]);
            Assert.Equal((0, 1), (again.Created, again.Matched));
            Assert.Equal(99L, (await PropertiesAsync(upserted.Id))["Score"]);
            Assert.Equal(3L, await CountAsync(run));
        }
        finally { await CleanupAsync(run); }
    }

    [Neo4jIntegrationFact]
    public async Task The_selector_updates_only_named_properties_and_an_empty_one_changes_nothing()
    {
        var run = await SetupAsync();
        try
        {
            await using var db = Context();
            var item = Item(run, "before");
            await db.Items.ExecuteUpsertAsync([item]);
            var changed = Item(run, "after");
            changed.Id = item.Id;
            changed.Score = 7;
            changed.Note = null;

            await db.Items.ExecuteUpsertAsync([changed], i => new { i.Name, i.Note });
            var stored = await PropertiesAsync(item.Id);
            Assert.Equal("after", stored["Name"]);
            Assert.False(stored.ContainsKey("Note"));
            Assert.Equal((long)item.Score, stored["Score"]);

            changed.Name = "ignored";
            var result = await db.Items.ExecuteUpsertAsync([changed], i => new { });
            Assert.Equal((0, 1), (result.Created, result.Matched));
            Assert.Equal("after", (await PropertiesAsync(item.Id))["Name"]);
        }
        finally { await CleanupAsync(run); }
    }

    [Neo4jIntegrationFact]
    public async Task A_missing_constraint_is_refused_before_any_write()
    {
        await using var db = new UnconstrainedContext(new DbContextOptionsBuilder<UnconstrainedContext>().UseNeo4j(fixture.Driver, fixture.Database).Options);
        var key = Guid.NewGuid();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => db.Set<Loose>().ExecuteUpsertAsync([new Loose { Id = key, Name = "x" }]));
        Assert.Contains("uniqueness constraint", error.Message);
        await using var session = fixture.OpenSession();
        var cursor = await session.RunAsync("MATCH (n:UPLoose) RETURN count(n) AS c");
        Assert.Equal(0L, (await cursor.SingleAsync())["c"].As<long>());
    }

    [Neo4jIntegrationFact]
    public async Task A_failure_inside_an_application_transaction_rolls_back_everything()
    {
        var run = await SetupAsync();
        try
        {
            await using var db = Context();
            await using var transaction = await db.Database.BeginTransactionAsync();
            await db.Items.ExecuteUpsertAsync([Item(run, "kept until the failure")]);
            // Two keys with one Email: the second MERGE breaks the Email constraint on the server.
            var first = Item(run, "a"); var second = Item(run, "b");
            first.Email = second.Email = "same-" + run + "@example.org";
            await Assert.ThrowsAsync<DbUpdateException>(() => db.Items.ExecuteUpsertAsync([first, second]));
            Assert.Equal(0L, await CountAsync(run));
        }
        finally { await CleanupAsync(run); }
    }

    [Neo4jIntegrationFact]
    public async Task Concurrent_upserts_of_the_same_new_keys_leave_one_node_per_key()
    {
        var run = await SetupAsync();
        try
        {
            var items = Enumerable.Range(0, 200).Select(i => Item(run, "n" + i)).ToArray();
            async Task<GraphUpsertResult?> Upsert()
            {
                await using var db = Context();
                try { return await db.Items.ExecuteUpsertAsync(items); }
                catch (DbUpdateException error)
                {
                    output.WriteLine($"One call failed: {(error.InnerException as Neo4jException)?.Code ?? error.Message}");
                    return null;
                }
            }
            var results = await Task.WhenAll(Upsert(), Upsert());
            if (results.All(r => r is not null))
                output.WriteLine($"Both calls succeeded: created {results[0]!.Created} and {results[1]!.Created}, matched {results[0]!.Matched} and {results[1]!.Matched}.");
            Assert.Equal(200L, await CountAsync(run));
            await using var session = fixture.OpenSession();
            var cursor = await session.RunAsync("MATCH (n:UPItem {Run: $run}) WITH n.item_key AS key, count(*) AS c WHERE c > 1 RETURN count(key) AS duplicates", new { run });
            Assert.Equal(0L, (await cursor.SingleAsync())["duplicates"].As<long>());
            // Either both calls succeed, together creating every node once, or the losing call fails and a repeat succeeds.
            if (results.All(r => r is not null))
                Assert.Equal(200, results.Sum(r => r!.Created));
            else
            {
                var repeat = await Upsert();
                Assert.Equal((0, 200), (repeat!.Created, repeat.Matched));
            }
        }
        finally { await CleanupAsync(run); }
    }

    [Neo4jIntegrationFact]
    public async Task A_cancelled_call_writes_nothing()
    {
        var run = await SetupAsync();
        try
        {
            await using var db = Context();
            using var cancelled = new CancellationTokenSource();
            await cancelled.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => db.Items.ExecuteUpsertAsync([Item(run, "x")], cancellationToken: cancelled.Token));
            Assert.Equal(0L, await CountAsync(run));
        }
        finally { await CleanupAsync(run); }
    }

    private ItemContext Context() => new(new DbContextOptionsBuilder<ItemContext>().UseNeo4j(fixture.Driver, fixture.Database).Options);

    private static UpItem Item(string run, string name) => new()
    {
        Id = Guid.NewGuid(), Run = run, Name = name, Note = "note", Score = 42, Big = long.MaxValue, Ratio = 0.25, Active = true,
        Kind = ItemKind.Second, Label = ItemKind.Third, Seen = new DateTimeOffset(2026, 10, 23, 9, 30, 0, TimeSpan.FromHours(2)).AddTicks(1234567),
        Email = name + "-" + Guid.NewGuid().ToString("N") + "@example.org"
    };

    private async Task<string> SetupAsync()
    {
        await ExecuteAsync("CREATE CONSTRAINT up_item_key IF NOT EXISTS FOR (n:UPItem) REQUIRE n.item_key IS UNIQUE");
        await ExecuteAsync("CREATE CONSTRAINT up_item_email IF NOT EXISTS FOR (n:UPItem) REQUIRE n.Email IS UNIQUE");
        return "up_" + Guid.NewGuid().ToString("N");
    }

    private async Task<long> CountAsync(string run)
    {
        await using var session = fixture.OpenSession();
        var cursor = await session.RunAsync("MATCH (n:UPItem {Run: $run}) RETURN count(n) AS c", new { run });
        return (await cursor.SingleAsync())["c"].As<long>();
    }

    private async Task<Dictionary<string, object>> PropertiesAsync(Guid id)
    {
        await using var session = fixture.OpenSession();
        var cursor = await session.RunAsync("MATCH (n:UPItem {item_key: $id}) RETURN properties(n) AS p", new { id = id.ToString("D") });
        return (await cursor.SingleAsync())["p"].As<Dictionary<string, object>>();
    }

    private static Dictionary<string, object> Without(Dictionary<string, object> properties)
        => properties.Where(p => p.Key is not ("item_key" or "Name" or "Email")).ToDictionary(p => p.Key, p => p.Value);

    private async Task ExecuteAsync(string text, object? parameters = null)
    {
        await using var session = fixture.OpenSession();
        await (parameters is null ? await session.RunAsync(text) : await session.RunAsync(text, parameters)).ConsumeAsync();
    }

    private Task CleanupAsync(string run) => ExecuteAsync("MATCH (n:UPItem {Run: $run}) DELETE n", new { run });

    public enum ItemKind { First, Second, Third }

    public sealed class UpItem
    {
        public Guid Id { get; set; }
        public string Run { get; set; } = "";
        public string Name { get; set; } = "";
        public string? Note { get; set; }
        public int Score { get; set; }
        public long Big { get; set; }
        public double Ratio { get; set; }
        public bool Active { get; set; }
        public ItemKind Kind { get; set; }
        public ItemKind Label { get; set; }
        public DateTimeOffset Seen { get; set; }
        public string Email { get; set; } = "";
    }

    public sealed class ItemContext(DbContextOptions<ItemContext> options) : DbContext(options)
    {
        public DbSet<UpItem> Items => Set<UpItem>();
        protected override void OnModelCreating(ModelBuilder model)
        {
            var item = model.Entity<UpItem>().ToGraphNode("UPItem");
            item.Property(i => i.Id).HasGraphPropertyName("item_key").ValueGeneratedNever();
            item.Property(i => i.Label).HasConversion<string>();
        }
    }

    public sealed class Loose { public Guid Id { get; set; } public string Name { get; set; } = ""; }

    public sealed class UnconstrainedContext(DbContextOptions<UnconstrainedContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder model)
            => model.Entity<Loose>().ToGraphNode("UPLoose").Property(l => l.Id).ValueGeneratedNever();
    }
}
