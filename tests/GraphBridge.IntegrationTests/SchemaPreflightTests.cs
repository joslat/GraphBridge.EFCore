using System.Diagnostics;
using System.Text.Json;
using GraphBridge.Samples.Blogging;
using Microsoft.EntityFrameworkCore;
using Neo4j.Driver;

namespace GraphBridge.IntegrationTests;

[Collection(Neo4jIntegrationCollection.Name)]
public sealed class SchemaPreflightTests(DisposableNeo4jFixture fixture)
{
    [Neo4jIntegrationFact]
    public async Task Gate_detects_missing_mapped_index_and_key_without_repairing_them()
    {
        await using var context = new PreflightContext(fixture.Driver, fixture.Database);
        var initial = await context.Database.InspectGraphSchemaAsync();
        var constraints = initial.Comparison.Proposals.ToArray();
        Assert.Equal(2, constraints.Length);
        try
        {
            var missing = await Check(context, 3);
            Assert.Contains(missing.GetProperty("blockers").EnumerateArray(), i => i.GetProperty("code").GetString() == "MissingIdentityConstraint");
            Assert.Contains(missing.GetProperty("blockers").EnumerateArray(), i => i.GetProperty("code").GetString() == "MissingRangeIndex");
            Assert.Equal(2, (await context.Database.InspectGraphSchemaAsync()).Comparison.Proposals.Count);
            foreach (var item in constraints) await Execute(item.Cypher);
            await Execute("CALL db.awaitIndexes(30)");
            var valid = await Check(context, 0);
            Assert.Equal(fixture.ServerVersion, valid.GetProperty("capabilities").GetProperty("serverVersion").GetString());
            Assert.Equal(fixture.ServerEdition.ToLowerInvariant(), valid.GetProperty("capabilities").GetProperty("edition").GetString()!.ToLowerInvariant());
            Assert.Empty(valid.GetProperty("blockers").EnumerateArray());
            Assert.False(valid.GetProperty("schemaModified").GetBoolean());
            Assert.False(valid.GetProperty("dataScanned").GetBoolean());
            var index = Assert.Single(constraints, p => p.Kind == EntityFrameworkCore.Schema.GraphSchemaProposalKind.RangeIndex);
            await Execute($"DROP INDEX `{index.Name}`");
            var drift = await Check(context, 3);
            Assert.Contains(drift.GetProperty("blockers").EnumerateArray(), i => i.GetProperty("code").GetString() == "MissingRangeIndex");
            Assert.Single((await context.Database.InspectGraphSchemaAsync()).Comparison.Proposals);
            Assert.Empty(context.ChangeTracker.Entries());
        }
        finally
        {
            foreach (var item in constraints)
                await Execute($"DROP {(item.Kind == EntityFrameworkCore.Schema.GraphSchemaProposalKind.RangeIndex ? "INDEX" : "CONSTRAINT")} `{item.Name}` IF EXISTS");
        }
    }

    [Neo4jIntegrationFact]
    public async Task Actual_cli_exits_three_for_drift_and_zero_after_explicit_fixture_setup()
    {
        // Fixed names belong to this sample on the marker-verified disposable fixture. Tests are serial.
        await Execute("CREATE CONSTRAINT graphbridge_blog_id IF NOT EXISTS FOR (n:GraphBridgeBlog) REQUIRE n.Id IS UNIQUE");
        await Execute("CREATE CONSTRAINT graphbridge_post_id IF NOT EXISTS FOR (n:GraphBridgePost) REQUIRE n.Id IS UNIQUE");
        try
        {
            await Execute("CALL db.awaitIndexes(30)");
            var valid = await Command("--check-schema");
            Assert.Equal(0, valid.ExitCode);
            Assert.Equal("compatible", JsonDocument.Parse(valid.Output).RootElement.GetProperty("outcome").GetString());
            await Execute("DROP CONSTRAINT graphbridge_blog_id");
            var drift = await Command("--check-schema");
            Assert.Equal(3, drift.ExitCode);
            Assert.Equal("drift", JsonDocument.Parse(drift.Output).RootElement.GetProperty("outcome").GetString());
            var rejected = await Command("--check-schema", "--prepare-schema");
            Assert.Equal(2, rejected.ExitCode);
            await using var context = new BloggingContext(fixture.Driver, fixture.Database);
            Assert.Contains((await context.Database.InspectGraphSchemaAsync()).Comparison.Issues, i => i.Code == "MissingIdentityConstraint");
        }
        finally { await Execute("CREATE CONSTRAINT graphbridge_blog_id IF NOT EXISTS FOR (n:GraphBridgeBlog) REQUIRE n.Id IS UNIQUE"); }
    }

    [Neo4jIntegrationFact]
    public async Task Inspection_errors_and_cancellation_emit_only_redacted_error_categories()
    {
        await using var missing = new BloggingContext(fixture.Driver, "missing_preflight_" + Guid.NewGuid().ToString("N"));
        var unavailable = await Check(missing, 2);
        Assert.Equal("InspectionFailed", unavailable.GetProperty("code").GetString());
        Assert.False(unavailable.TryGetProperty("database", out _));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await using var context = new BloggingContext(fixture.Driver, fixture.Database);
        var output = new StringWriter();
        Assert.Equal(2, await SchemaPreflight.RunAsync(context, output, canceled.Token));
        Assert.Equal("InspectionCanceled", JsonDocument.Parse(output.ToString()).RootElement.GetProperty("code").GetString());
    }

    private static async Task<JsonElement> Check(DbContext context, int expected)
    {
        var output = new StringWriter();
        Assert.Equal(expected, await SchemaPreflight.RunAsync(context, output));
        using var parsed = JsonDocument.Parse(output.ToString());
        Assert.Equal(expected, parsed.RootElement.GetProperty("exitCode").GetInt32());
        return parsed.RootElement.Clone();
    }

    private static async Task<(int ExitCode, string Output)> Command(params string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add(typeof(BloggingContext).Assembly.Location);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        try { await process.WaitForExitAsync(timeout.Token); }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
        var result = await stdout;
        var error = await stderr;
        Assert.DoesNotContain(Environment.GetEnvironmentVariable("GRAPHBRIDGE_NEO4J_PASSWORD")!, result + error);
        return (process.ExitCode, result);
    }

    private async Task Execute(string cypher)
    {
        await using var session = fixture.OpenSession();
        await (await session.RunAsync(cypher)).ConsumeAsync();
    }

    private sealed class PreflightContext(IDriver driver, string database)
        : DbContext(new DbContextOptionsBuilder().UseNeo4j(driver, database).Options)
    {
        protected override void OnModelCreating(ModelBuilder model)
        {
            model.Entity<Row>(row => { row.ToGraphNode("GraphBridgePreflightRow"); row.HasKey(r => r.Id); row.HasIndex(r => r.Name); });
        }
    }
    private sealed class Row { public Guid Id { get; set; } public string? Name { get; set; } }
}
