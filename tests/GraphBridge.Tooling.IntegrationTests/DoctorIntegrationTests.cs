using GraphBridge.Tooling.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Neo4j.Driver;
using Graph = GraphBridge.EntityFrameworkCore.FluentCypher.Cypher;

namespace GraphBridge.IntegrationTests;

[Collection(ToolingIntegrationCollection.Name)]
public sealed class DoctorIntegrationTests(DisposableNeo4jFixture fixture)
{
    private const string Label = "GraphBridgeDoctorPrivateItem";
    private const string Constraint = "gb_doctor_private_key";

    [Neo4jIntegrationFact]
    public async Task Connected_doctor_preserves_graph_schema_and_borrowed_transaction_while_reporting_observed_profile()
    {
        var id = Guid.NewGuid().ToString("N");
        await Execute($"CREATE CONSTRAINT {Constraint} FOR (n:{Label}) REQUIRE n.id IS UNIQUE");
        try
        {
            await Execute($"CREATE (n:{Label} {{id:$id, Name:'secret-existing-value'}})", new { id });
            using var db = new Context(fixture.Driver, fixture.Database);
            var before = await State();
            var report = await GraphDoctor.InspectAsync(db, Catalog(), "owned.community.v1");
            Assert.Equal(GraphEvidenceStatus.Pass, report.Status);
            Assert.Equal(fixture.ServerVersion, report.Database!.ServerVersion);
            Assert.Equal(fixture.ServerEdition.ToLowerInvariant(), report.Database.Edition);
            Assert.Equal(GraphEvidenceStatus.NotRun, Assert.Single(report.Queries).Execution);
            Assert.Equal(before, await State());
            Assert.Empty(db.ChangeTracker.Entries());
            foreach (var text in new[] { report.ToJson(), report.ToText() })
            {
                Assert.DoesNotContain(Label, text);
                Assert.DoesNotContain(Constraint, text);
                Assert.DoesNotContain("secret-", text);
            }

            await using var transaction = await db.Database.BeginTransactionAsync();
            await db.Database.QueryCypherAsync($"CREATE (n:{Label} {{id:$id, Name:'secret-uncommitted'}}) RETURN n.id AS id",
                new Dictionary<string, object?> { ["id"] = id + "-borrowed" }, r => r["id"].As<string>(), maxRows: 1);
            var borrowed = await GraphDoctor.InspectAsync(db, Catalog(), "owned.community.v1");
            Assert.Equal(GraphEvidenceStatus.Pass, borrowed.Status);
            Assert.Same(transaction, db.Database.CurrentTransaction);
            Assert.Equal(before, await State()); // Independent session cannot see the uncommitted node.
            await transaction.RollbackAsync();
            Assert.Equal(before, await State());
        }
        finally
        {
            await Execute($"MATCH (n:{Label}) WHERE n.id IN $ids DELETE n", new { ids = new[] { id, id + "-borrowed" } });
            await Execute($"DROP CONSTRAINT {Constraint} IF EXISTS");
        }
    }

    [Neo4jIntegrationFact]
    public async Task Missing_schema_bounded_metadata_and_cancellation_return_nonpassing_evidence_without_repair()
    {
        using var db = new Context(fixture.Driver, fixture.Database);
        var before = await State();
        var missing = await GraphDoctor.InspectAsync(db, Catalog(), "owned.community.v1");
        Assert.Equal(GraphEvidenceStatus.Fail, missing.Status);
        Assert.Contains(missing.Database!.Findings, f => f.Code == "MissingIdentityConstraint");
        var bounded = await GraphDoctor.InspectAsync(db, Catalog(), "owned.community.v1", maxSchemaRows: 1);
        Assert.Equal(GraphEvidenceStatus.Blocked, bounded.Status);
        Assert.Null(bounded.Database);
        var canceled = await GraphDoctor.InspectAsync(db, Catalog(), "owned.community.v1", cancellationToken: new CancellationToken(true));
        Assert.Equal(GraphEvidenceStatus.Blocked, canceled.Status);
        Assert.Contains(canceled.Diagnostics, d => d.Code == "GBD-EXEC-001");
        Assert.Equal(before, await State());
    }

    [Neo4jIntegrationFact]
    public async Task Real_authentication_and_connection_failures_are_redacted_and_blocked()
    {
        using var wrong = GraphDatabase.Driver("bolt://127.0.0.1:17687", AuthTokens.Basic("neo4j", "intentionally-wrong-" + Guid.NewGuid().ToString("N")),
            config => config.WithConnectionTimeout(TimeSpan.FromSeconds(2)));
        using var rejected = new Context(wrong, fixture.Database);
        var denied = await GraphDoctor.InspectAsync(rejected, Catalog(), "owned.denied.v1");
        Assert.Equal(GraphEvidenceStatus.Blocked, denied.Status);
        Assert.Contains(denied.Diagnostics, d => d.Code == "GBD-CONN-002");
        Assert.Null(denied.Database);
        Assert.DoesNotContain("intentionally-wrong", denied.ToJson());
        using var absent = GraphDatabase.Driver("bolt://127.0.0.1:1", AuthTokens.None,
            config => config.WithConnectionTimeout(TimeSpan.FromSeconds(1)));
        using var unreachable = new Context(absent, fixture.Database);
        var disconnected = await GraphDoctor.InspectAsync(unreachable, Catalog(), "owned.unreachable.v1");
        Assert.Equal(GraphEvidenceStatus.Blocked, disconnected.Status);
        Assert.Contains(disconnected.Diagnostics, d => d.Code == "GBD-CONN-001");
        Assert.Null(disconnected.Database);
        Assert.DoesNotContain("127.0.0.1", disconnected.ToText());
    }

    private static GraphQueryCatalog Catalog() => new([GraphQueryContract.Fluent(new GraphQueryDefinition(
        "doctor.items.v1", "Synthetic item names", "DoctorIntegrationTests.cs", [new("name", GraphResultKind.String)],
        GraphResultComparison.Multiset, GraphDuplicatePolicy.Preserve, new(10, TimeSpan.FromSeconds(5))), db =>
        {
            var item = Graph.Node<Item>();
            return db.Cypher().Match(item).Where(item.Property(p => p.Name).Eq("secret-query-value"))
                .Return(() => item.Value.Name).Limit(10);
        })]);
    private sealed class Item { public string Id { get; set; } = ""; public string Name { get; set; } = ""; }
    private sealed class Context(IDriver driver, string database) : DbContext(new DbContextOptionsBuilder().UseNeo4j(driver, database).Options)
    {
        protected override void OnModelCreating(ModelBuilder b)
        {
            b.Entity<Item>().ToGraphNode(Label);
            b.Entity<Item>().Property(p => p.Id).HasGraphPropertyName("id");
        }
    }
    private async Task Execute(string query, object? parameters = null)
    {
        await using var session = fixture.OpenSession();
        await (await session.RunAsync(query, parameters)).ConsumeAsync();
    }
    private async Task<string[]> State()
    {
        await using var session = fixture.OpenSession();
        var nodes = await (await session.RunAsync($"MATCH (n:{Label}) RETURN n.id AS id, n.Name AS name ORDER BY id"))
            .ToListAsync(r => r["id"].As<string>() + ":" + r["name"].As<string>());
        var constraints = await (await session.RunAsync("SHOW CONSTRAINTS YIELD name RETURN name ORDER BY name"))
            .ToListAsync(r => r["name"].As<string>());
        var indexes = await (await session.RunAsync("SHOW INDEXES YIELD name RETURN name ORDER BY name"))
            .ToListAsync(r => r["name"].As<string>());
        return [.. nodes, .. constraints, .. indexes];
    }
}
