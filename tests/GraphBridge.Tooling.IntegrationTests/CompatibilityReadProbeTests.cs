using System.Text.Json;
using GraphBridge.Samples.FluentCypher;
using GraphBridge.Tooling.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Neo4j.Driver;

namespace GraphBridge.IntegrationTests;

[Collection(ToolingIntegrationCollection.Name)]
public sealed class CompatibilityReadProbeTests(DisposableNeo4jFixture fixture)
{
    [Neo4jIntegrationFact]
    public async Task Selected_profile_preserves_read_results_metadata_and_explicit_search_boundary()
    {
        var expectedVersion = Environment.GetEnvironmentVariable("GRAPHBRIDGE_PROBE_VERSION") ?? "2026.09.0";
        Assert.Equal(expectedVersion, fixture.ServerVersion);
        Assert.Equal("community", fixture.ServerEdition.ToLowerInvariant());
        var run = Guid.NewGuid().ToString("N");
        var constraints = new[] { "gb_probe_person_" + run, "gb_probe_movie_" + run, "gb_probe_credit_" + run };
        GraphVerificationReport? report = null;
        string? dialect = null;
        var searchRejected = false;
        var baseline = await State(run);
        try
        {
            // Explicit owned-fixture provisioning; not part of the read-only observation.
            await Execute($"CREATE CONSTRAINT {constraints[0]} FOR (n:FGQPerson) REQUIRE n.Id IS UNIQUE");
            await Execute($"CREATE CONSTRAINT {constraints[1]} FOR (n:FGQMovie) REQUIRE n.Id IS UNIQUE");
            await Execute($"CREATE CONSTRAINT {constraints[2]} FOR ()-[r:FGQ_ACTED]-() REQUIRE r.Id IS UNIQUE");
            await Execute("""
                CREATE (a:FGQPerson {Id:randomUUID(), RunId:$run, Name:'Ada'}),
                       (b:FGQPerson {Id:randomUUID(), RunId:$run, Name:'Bob'}),
                       (m:FGQMovie {Id:randomUUID(), RunId:$run, Title:'Shared', Year:2024}),
                       (a)-[:FGQ_ACTED {Id:randomUUID()}]->(m),
                       (b)-[:FGQ_ACTED {Id:randomUUID()}]->(m)
                """, new { run });
            var before = await State(run);
            using var db = new Context(fixture.Driver, fixture.Database);
            var catalog = FluentMovieContracts.Create(run);
            var doctor = await GraphDoctor.InspectAsync(db, catalog, "owned.community.read-probe.v1");
            Assert.Equal(GraphEvidenceStatus.Pass, doctor.Status);
            report = await GraphVerify.InspectAsync(db, catalog, "owned.community.read-probe.v1");
            Assert.Equal(GraphEvidenceStatus.Pass, report.Status);
            Assert.All(report.Contracts, c =>
            {
                Assert.Equal(GraphEvidenceStatus.Pass, c.Planning);
                Assert.Equal(GraphEvidenceStatus.NotRun, c.Semantics);
                Assert.NotEmpty(c.Plan!.Operators);
            });
            Assert.Equal(new[] { "Ada", "Bob" }, await db.Set<Person>().AsNoTracking()
                .Where(p => p.RunId == run).OrderBy(p => p.Name).Select(p => p.Name).Take(10).ToListAsync());
            var fluent = await FluentMovieQueries.Coactors(db, run, "Ada").ToListAsync();
            await using (var session = fixture.Driver.AsyncSession(b => b.WithDatabase(fixture.Database).WithDefaultAccessMode(AccessMode.Read)))
            {
                const string native = "MATCH (a:FGQPerson {RunId:$run,Name:$name})-[:FGQ_ACTED]->(:FGQMovie)<-[:FGQ_ACTED]-(b:FGQPerson) WHERE b.RunId=$run AND b.Id<>a.Id RETURN DISTINCT b.Name AS name ORDER BY name LIMIT 10";
                var control = await (await session.RunAsync(native, new { run, name = "Ada" })).ToListAsync(r => r["name"].As<string>());
                Assert.Equal(new[] { "Bob" }, control);
                Assert.Equal(control, fluent);
                var plan = await (await session.RunAsync("EXPLAIN " + native, new { run, name = "Ada" })).ConsumeAsync();
                Assert.True(plan.HasPlan);
                Assert.False(plan.HasProfile);
                dialect = plan.Plan.Arguments.TryGetValue("version", out var version) ? version.ToString() : null;
            }
            if (expectedVersion == "5.26.18")
            {
                Assert.DoesNotContain("25", fixture.CypherVersions);
                await Assert.ThrowsAsync<NotSupportedException>(() => db.Database.QueryVectorNodesAsync("gb_probe_absent", new[] { 1d, 0d }, n => n.ElementId));
                await Assert.ThrowsAsync<NotSupportedException>(() => db.Database.QueryFullTextNodesAsync("gb_probe_absent", "Ada", n => n.ElementId));
                searchRejected = true;
            }
            using var invalidDriver = GraphDatabase.Driver("bolt://127.0.0.1:17687", AuthTokens.Basic("neo4j", "invalid-" + run));
            using var invalid = new Context(invalidDriver, fixture.Database);
            var denied = await GraphDoctor.InspectAsync(invalid, catalog, "owned.invalid-auth.v1");
            Assert.Equal(GraphEvidenceStatus.Blocked, denied.Status);
            Assert.Contains(denied.Diagnostics, d => d.Code == "GBD-CONN-002");
            Assert.DoesNotContain(run, denied.ToJson());
            Assert.Empty(db.ChangeTracker.Entries());
            Assert.Null(db.Database.CurrentTransaction);
            Assert.Equal(before, await State(run));
        }
        finally
        {
            await Execute("MATCH (:FGQPerson {RunId:$run})-[r:FGQ_ACTED]->(:FGQMovie {RunId:$run}) DELETE r", new { run });
            await Execute("MATCH (n:FGQPerson {RunId:$run}) DELETE n", new { run });
            await Execute("MATCH (n:FGQMovie {RunId:$run}) DELETE n", new { run });
            foreach (var name in constraints) await Execute($"DROP CONSTRAINT {name} IF EXISTS");
        }
        Assert.Equal(baseline, await State(run));
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(root.FullName, "GraphBridge.sln")))
            root = root.Parent ?? throw new InvalidOperationException("Repository root not found.");
        var output = Path.Combine(root.FullName, ".artifacts", "next-roadmap", "profile-probe");
        Directory.CreateDirectory(output);
        await File.WriteAllTextAsync(Path.Combine(output, "verify.json"), report!.ToJson());
        await File.WriteAllTextAsync(Path.Combine(output, "receipt.json"), JsonSerializer.Serialize(new
        {
            fixture.ServerVersion, fixture.ServerEdition, fixture.CypherVersions, ObservedPlanDialect = dialect,
            Account = "Community built-in administrator; no restricted-user permission claim",
            LinqNames = new[] { "Ada", "Bob" }, FluentAndNativeCoactors = new[] { "Bob" },
            SearchExplicitlyRejected = searchRejected, AuthenticationFailureRedacted = true,
            ObservationPreservedGraphAndSchema = true, FixtureDataAndSchemaCleanup = true,
            Scope = "Read-only observations after explicit owned provisioning; no general CRUD or release-range claim"
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private sealed class Context(IDriver driver, string database) : DbContext(new DbContextOptionsBuilder().UseNeo4j(driver, database).Options)
    {
        protected override void OnModelCreating(ModelBuilder b) => FluentMovieQueries.Configure(b);
    }
    private async Task Execute(string query, object? parameters = null)
    {
        await using var session = fixture.OpenSession();
        await (await session.RunAsync(query, parameters)).ConsumeAsync();
    }
    private async Task<string[]> State(string run)
    {
        await using var session = fixture.OpenSession();
        var nodes = await (await session.RunAsync("MATCH (n) WHERE n.RunId=$run RETURN n.Id AS id, n.Name AS name, n.Title AS title, n.Year AS year ORDER BY id", new { run }))
            .ToListAsync(r => JsonSerializer.Serialize(r.Values));
        var edges = await (await session.RunAsync("MATCH (a:FGQPerson {RunId:$run})-[r:FGQ_ACTED]->(b:FGQMovie {RunId:$run}) RETURN r.Id AS id,a.Id AS source,b.Id AS target ORDER BY id", new { run }))
            .ToListAsync(r => JsonSerializer.Serialize(r.Values));
        var schema = await (await session.RunAsync("SHOW CONSTRAINTS YIELD name, type RETURN name,type ORDER BY name"))
            .ToListAsync(r => JsonSerializer.Serialize(r.Values));
        var indexes = await (await session.RunAsync("SHOW INDEXES YIELD name,type RETURN name,type ORDER BY name"))
            .ToListAsync(r => JsonSerializer.Serialize(r.Values));
        return [.. nodes, .. edges, .. schema, .. indexes];
    }
}
