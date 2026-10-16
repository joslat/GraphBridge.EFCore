using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Neo4j.Driver;
using Xunit.Abstractions;

namespace GraphBridge.IntegrationTests;

[Collection(Neo4jIntegrationCollection.Name)]
public sealed class ProviderNullSemanticsTests(DisposableNeo4jFixture fixture, ITestOutputHelper output)
{
    [Neo4jIntegrationFact]
    public async Task Nullable_comparison_truth_tables_and_negations_match_CLR_for_every_null_pair()
    {
        var runId = Guid.NewGuid().ToString("N");
        int?[] values = [null, -1, 0, 1];
        var rows = (from left in values from right in values select new NullRow
        {
            Id = Guid.NewGuid(), RunId = runId, Left = left, Right = right,
            Flag = left is null ? null : left > 0, Text = right is null ? null : right == 0 ? "" : "value"
        }).ToArray();
        try
        {
            await SeedAsync(rows);
            await using var context = Context();
            var root = context.Rows.AsNoTracking().Where(r => r.RunId == runId);
            var comparisons = 0;
            async Task CheckAsync(Expression<Func<NullRow, bool>> predicate)
            {
                // In-memory execution is an independent test oracle, never a provider fallback.
                var expected = rows.Where(predicate.Compile()).Select(r => r.Id).OrderBy(id => id).ToArray();
                var query = root.Where(predicate);
                var actual = (await query.ToListAsync()).Select(r => r.Id).OrderBy(id => id).ToArray();
                Assert.Equal(expected, actual);
                Assert.DoesNotContain(runId, query.ToQueryString());
                comparisons++;
            }
            Expression<Func<NullRow, bool>>[] pairs =
            [
                r => r.Left == r.Right, r => r.Left != r.Right,
                r => r.Left > r.Right, r => r.Left >= r.Right,
                r => r.Left < r.Right, r => r.Left <= r.Right,
                r => !(r.Left == r.Right), r => !(r.Left != r.Right),
                r => !(r.Left > r.Right), r => !(r.Left >= r.Right),
                r => !(r.Left < r.Right), r => !(r.Left <= r.Right),
                r => (r.Left == r.Right || r.Flag == true) && r.Text != null,
                r => !(r.Left > r.Right && r.Flag != false),
                r => !(r.Left == null || r.Right == null)
            ];
            foreach (var predicate in pairs) await CheckAsync(predicate);
            foreach (var value in values)
            {
                await CheckAsync(r => r.Left == value);
                await CheckAsync(r => r.Left != value);
                await CheckAsync(r => r.Left > value);
                await CheckAsync(r => r.Left >= value);
                await CheckAsync(r => r.Left < value);
                await CheckAsync(r => r.Left <= value);
                await CheckAsync(r => !(r.Left == value));
                await CheckAsync(r => !(r.Left != value));
                await CheckAsync(r => !(r.Left > value));
                await CheckAsync(r => !(r.Left >= value));
                await CheckAsync(r => !(r.Left < value));
                await CheckAsync(r => !(r.Left <= value));
            }
            foreach (bool? flag in new bool?[] { null, false, true })
            {
                await CheckAsync(r => r.Flag == flag);
                await CheckAsync(r => r.Flag != flag);
                await CheckAsync(r => !(r.Flag == flag));
                await CheckAsync(r => !(r.Flag != flag));
            }
            foreach (var text in new string?[] { null, "", "value", "VALUE" })
            {
                await CheckAsync(r => r.Text == text);
                await CheckAsync(r => r.Text != text);
                await CheckAsync(r => !(r.Text == text));
            }
            Assert.Equal(87, comparisons);
            Assert.Empty(context.ChangeTracker.Entries());
            output.WriteLine("87 LINQ/Cypher predicate comparisons over all 16 nullable integer pairs, nullable booleans and missing/empty/case-distinct strings match the independent CLR oracle.");
            output.WriteLine(root.Where(r => !(r.Left > r.Right)).ToQueryString());
        }
        finally { await CleanupAsync(runId); }
    }

    [Neo4jIntegrationFact]
    public async Task Missing_property_and_explicit_null_assignment_have_the_same_nullable_query_semantics()
    {
        var runId = Guid.NewGuid().ToString("N");
        var absent = new NullRow { Id = Guid.NewGuid(), RunId = runId };
        var removed = new NullRow { Id = Guid.NewGuid(), RunId = runId, Left = 4, Text = "remove" };
        try
        {
            await SeedAsync([absent, removed]);
            await using (var session = fixture.OpenSession())
            {
                await (await session.RunAsync(
                    "MATCH (n:GraphBridgeNullRow {Id: $id, RunId: $runId}) SET n.Left = null, n.Text = null",
                    new { id = removed.Id.ToString("D"), runId })).ConsumeAsync();
                var records = await (await session.RunAsync(
                    "MATCH (n:GraphBridgeNullRow {RunId: $runId}) RETURN keys(n) AS properties", new { runId })).ToListAsync();
                Assert.Equal(2, records.Count);
                foreach (var record in records)
                {
                    var properties = record["properties"].As<List<string>>();
                    Assert.DoesNotContain("Left", properties);
                    Assert.DoesNotContain("Text", properties);
                }
            }
            await using var context = Context();
            var found = await context.Rows.Where(r => r.RunId == runId && r.Left == null && r.Text == null).ToListAsync();
            Assert.Equal(2, found.Count);
            Assert.All(found, r => { Assert.Null(r.Left); Assert.Null(r.Text); });
            Assert.Empty(await context.Rows.Where(r => r.RunId == runId && !(r.Left == null)).ToListAsync());
        }
        finally { await CleanupAsync(runId); }
    }

    private NullContext Context() => new(new DbContextOptionsBuilder<NullContext>().UseNeo4j(fixture.Driver, fixture.Database).Options);

    private async Task SeedAsync(NullRow[] rows)
    {
        await using var session = fixture.OpenSession();
        await (await session.RunAsync("CREATE CONSTRAINT graphbridge_null_row_key IF NOT EXISTS FOR (n:GraphBridgeNullRow) REQUIRE n.Id IS UNIQUE")).ConsumeAsync();
        await using var context = Context();
        context.AddRange(rows);
        Assert.Equal(rows.Length, await context.SaveChangesAsync());
    }

    private async Task CleanupAsync(string runId)
    {
        await using var session = fixture.OpenSession();
        await (await session.RunAsync("MATCH (n:GraphBridgeNullRow {RunId: $runId}) DELETE n", new { runId })).ConsumeAsync();
    }

    public sealed class NullRow
    {
        public Guid Id { get; set; }
        public string RunId { get; set; } = "";
        public int? Left { get; set; }
        public int? Right { get; set; }
        public bool? Flag { get; set; }
        public string? Text { get; set; }
    }
    public sealed class NullContext(DbContextOptions<NullContext> options) : DbContext(options)
    {
        public DbSet<NullRow> Rows => Set<NullRow>();
        protected override void OnModelCreating(ModelBuilder builder)
        {
            builder.Entity<NullRow>().ToGraphNode("GraphBridgeNullRow");
            builder.Entity<NullRow>().Property(r => r.Id).ValueGeneratedNever();
        }
    }
}
