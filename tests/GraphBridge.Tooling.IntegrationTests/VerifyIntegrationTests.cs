using GraphBridge.Samples.FluentCypher;
using GraphBridge.Tooling.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Neo4j.Driver;

namespace GraphBridge.IntegrationTests;

[Collection(ToolingIntegrationCollection.Name)]
public sealed class VerifyIntegrationTests(DisposableNeo4jFixture fixture)
{
    [Neo4jIntegrationFact]
    public async Task Three_actual_contracts_have_read_only_plans_and_preserve_borrowed_transaction()
    {
        var run = Guid.NewGuid().ToString("N");
        var names = new[] { "gb_verify_person_" + run, "gb_verify_movie_" + run, "gb_verify_credit_" + run };
        try
        {
            await Execute($"CREATE CONSTRAINT {names[0]} FOR (n:FGQPerson) REQUIRE n.Id IS UNIQUE");
            await Execute($"CREATE CONSTRAINT {names[1]} FOR (n:FGQMovie) REQUIRE n.Id IS UNIQUE");
            await Execute($"CREATE CONSTRAINT {names[2]} FOR ()-[r:FGQ_ACTED]-() REQUIRE r.Id IS UNIQUE");
            await Execute("CREATE (:FGQPerson {Id:$id, RunId:$run, Name:'private-person-value'})", new { id = Guid.NewGuid().ToString("D"), run });
            using var db = new Context(fixture.Driver, fixture.Database);
            var catalog = FluentMovieContracts.Create(run, "private-person-value");
            var before = await State(run);
            var report = await GraphVerify.InspectAsync(db, catalog, "owned.community.v1");
            Assert.Equal(GraphEvidenceStatus.Pass, report.Status);
            Assert.Equal(0, report.ExitCode);
            Assert.Equal(fixture.ServerVersion, report.Evidence.Database!.ServerVersion);
            Assert.Equal(3, report.Contracts.Count);
            foreach (var contract in report.Contracts)
            {
                Assert.Equal(GraphEvidenceStatus.Pass, contract.Compilation);
                Assert.Equal(GraphEvidenceStatus.Pass, contract.Planning);
                Assert.Equal(GraphEvidenceStatus.NotRun, contract.Semantics);
                Assert.NotEmpty(contract.Plan!.Operators);
                Assert.Equal(report.Evidence.Queries.Single(q => q.ContractId == contract.ContractId).StatementFingerprint, contract.Plan.StatementFingerprint);
            }
            Assert.Equal(before, await State(run));
            Assert.Empty(db.ChangeTracker.Entries());
            var json = report.ToJson();
            Assert.DoesNotContain(run, json);
            Assert.DoesNotContain("private-person-value", json);
            Assert.DoesNotContain("FGQPerson", json);
            Assert.DoesNotContain(names[0], json);

            await using (var transaction = await db.Database.BeginTransactionAsync())
            {
                await db.Database.QueryCypherAsync("CREATE (:FGQPerson {Id:$id,RunId:$run,Name:'private-uncommitted'}) RETURN 1 AS count",
                    new Dictionary<string, object?> { ["id"] = Guid.NewGuid().ToString("D"), ["run"] = run }, r => r["count"].As<long>(), maxRows: 1);
                var borrowed = await GraphVerify.InspectAsync(db, catalog, "owned.community.v1");
                Assert.Equal(GraphEvidenceStatus.Pass, borrowed.Status);
                Assert.Same(transaction, db.Database.CurrentTransaction);
                Assert.Equal(before, await State(run));
                var count = await db.Database.QueryCypherAsync("MATCH (n:FGQPerson {RunId:$run}) RETURN count(n) AS count",
                    new Dictionary<string, object?> { ["run"] = run }, r => r["count"].As<long>(), maxRows: 1);
                Assert.Equal(2, Assert.Single(count));
                await transaction.RollbackAsync();
            }
            Assert.Equal(before, await State(run));
            var bounded = await GraphVerify.InspectAsync(db, catalog, "owned.community.v1", maxPlanNodes: 1);
            Assert.Equal(GraphEvidenceStatus.Blocked, bounded.Status);
            Assert.Equal(1, bounded.ExitCode);
            Assert.Null(bounded.Contracts[0].Plan);
            Assert.All(bounded.Contracts.Skip(1), c => Assert.Equal(GraphEvidenceStatus.NotRun, c.Planning));
            Assert.Equal(before, await State(run));
        }
        finally
        {
            await Execute("MATCH (n:FGQPerson {RunId:$run}) DELETE n", new { run });
            foreach (var name in names) await Execute($"DROP CONSTRAINT {name} IF EXISTS");
        }
        using var missing = new Context(fixture.Driver, fixture.Database);
        var failed = await GraphVerify.InspectAsync(missing, FluentMovieContracts.Create(run), "owned.community.v1");
        Assert.Equal(GraphEvidenceStatus.Fail, failed.Status);
        Assert.Equal(1, failed.ExitCode);
        Assert.All(failed.Contracts, c => Assert.Equal(GraphEvidenceStatus.NotRun, c.Planning));
    }

    private sealed class Context(IDriver driver, string database) : DbContext(new DbContextOptionsBuilder().UseNeo4j(driver, database).Options)
    {
        protected override void OnModelCreating(ModelBuilder b) => FluentMovieQueries.Configure(b);
    }
    private async Task Execute(string text, object? parameters = null)
    {
        await using var session = fixture.OpenSession();
        await (await session.RunAsync(text, parameters)).ConsumeAsync();
    }
    private async Task<string[]> State(string run)
    {
        await using var session = fixture.OpenSession();
        var nodes = await (await session.RunAsync("MATCH (n:FGQPerson {RunId:$run}) RETURN n.Id AS id,n.Name AS name ORDER BY id", new { run }))
            .ToListAsync(r => r["id"].As<string>() + ":" + r["name"].As<string>());
        var constraints = await (await session.RunAsync("SHOW CONSTRAINTS YIELD name RETURN name ORDER BY name")).ToListAsync(r => r["name"].As<string>());
        var indexes = await (await session.RunAsync("SHOW INDEXES YIELD name RETURN name ORDER BY name")).ToListAsync(r => r["name"].As<string>());
        return [.. nodes, .. constraints, .. indexes];
    }
}
