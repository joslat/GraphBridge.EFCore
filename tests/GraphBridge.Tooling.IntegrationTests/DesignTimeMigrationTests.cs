using System.Text.Json;
using GraphBridge.EntityFrameworkCore.Migrations;
using GraphBridge.Tooling.DesignTime;
using Microsoft.EntityFrameworkCore;
using Neo4j.Driver;

namespace GraphBridge.IntegrationTests;

[Collection(ToolingIntegrationCollection.Name)]
public sealed class DesignTimeMigrationTests(DisposableNeo4jFixture fixture)
{
    [Neo4jIntegrationFact]
    public async Task Status_reports_uninitialized_ready_and_applied_without_changing_graph_or_schema()
    {
        // This collection's fresh owned fixture must not contain someone else's migration journal.
        Assert.Equal(0L, await Scalar("MATCH (n:__GraphBridgeMigrationControl) RETURN count(n) AS value"));
        var plan = new GraphMigrationPlan("design-status-" + Guid.NewGuid().ToString("N"), null,
            [GraphMigrationStep.AssertPrecondition("empty", new GraphMigrationDataScope("NODE", "GraphBridgeDesignCommandWitness",
                new Dictionary<string, object?>(), 1), 0)],
            GraphMigrationRollbackPolicy.NoRollback("Disposable acceptance fixture only."));
        DbContext Create() => new(new DbContextOptionsBuilder().UseNeo4j(fixture.Driver, fixture.Database).Options);
        try
        {
            await Check(expectedExit: 3, initialized: false, applied: false);
            Assert.Equal(0L, await Scalar("MATCH (n:__GraphBridgeMigrationControl) RETURN count(n) AS value"));
            await using (var setup = Create()) await setup.Database.InitializeGraphMigrationsAsync();
            await Check(expectedExit: 0, initialized: true, applied: false);
            await using (var setup = Create()) await setup.Database.ApplyGraphMigrationAsync(plan);
            await Check(expectedExit: 0, initialized: true, applied: true);
        }
        finally
        {
            // Explicit cleanup of this test's initially absent journal; no DETACH or foreign data deletion.
            foreach (var label in new[] { "__GraphBridgeMigrationStep", "__GraphBridgeMigration", "__GraphBridgeMigrationControl" })
                await Execute($"MATCH (n:`{label}`) DELETE n");
            foreach (var name in new[] { "gb_migration_control_key", "gb_migration_history_key", "gb_migration_step_key" })
                await Execute($"DROP CONSTRAINT `{name}` IF EXISTS");
        }

        async Task Check(int expectedExit, bool initialized, bool applied)
        {
            var before = await Snapshot();
            using var output = new StringWriter(); using var error = new StringWriter();
            Assert.Equal(expectedExit, await GraphMigrationCommands.RunAsync(["status", plan.Id], [plan], Create, output, error));
            Assert.Equal("", error.ToString());
            using var json = JsonDocument.Parse(output.ToString());
            Assert.True(json.RootElement.GetProperty("databaseObserved").GetBoolean());
            Assert.Equal(initialized, json.RootElement.GetProperty("IsInitialized").GetBoolean());
            Assert.Equal(applied, json.RootElement.GetProperty("AlreadyApplied").GetBoolean());
            Assert.False(json.RootElement.TryGetProperty("OwnerToken", out _));
            Assert.Equal(before, await Snapshot());
        }
    }

    private async Task<string> Snapshot()
    {
        await using var session = fixture.OpenSession();
        var nodes = await (await session.RunAsync("MATCH (n) RETURN elementId(n) AS id, labels(n) AS labels, properties(n) AS properties ORDER BY id")).ToListAsync(r => r.Values);
        var schema = await (await session.RunAsync("SHOW CONSTRAINTS YIELD name, type, labelsOrTypes, properties RETURN name, type, labelsOrTypes, properties ORDER BY name")).ToListAsync(r => r.Values);
        return JsonSerializer.Serialize(new { nodes, schema });
    }
    private async Task<long> Scalar(string query)
    {
        await using var session = fixture.OpenSession();
        return (await (await session.RunAsync(query)).SingleAsync())["value"].As<long>();
    }
    private async Task Execute(string query)
    {
        await using var session = fixture.OpenSession();
        await (await session.RunAsync(query)).ConsumeAsync();
    }
}
