using GraphBridge.EntityFrameworkCore.Migrations;
using GraphBridge.Samples.MoviesDemo;
using GraphBridge.Tooling.Generation;
using GraphBridge.Tooling.Scaffolding;
using Microsoft.EntityFrameworkCore;
using Neo4j.Driver;
using System.Text.Json.Nodes;

namespace GraphBridge.IntegrationTests;

[Collection(ToolingIntegrationCollection.Name)]
public sealed class MoviesBackfillTests(DisposableNeo4jFixture fixture) : IAsyncLifetime
{
    private static bool Enabled => Environment.GetEnvironmentVariable("GRAPHBRIDGE_INTEGRATION") == "1";
    private readonly string _backupDirectory = Path.Combine(Path.GetTempPath(), "graphbridge-movies-backup-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _backupPaths = [];
    public Task InitializeAsync() => Enabled ? Cleanup() : Task.CompletedTask;
    public async Task DisposeAsync()
    {
        try { if (Enabled) await Cleanup(); }
        finally
        {
            foreach (var path in _backupPaths) if (File.Exists(path)) File.Delete(path);
            if (Directory.Exists(_backupDirectory)) Directory.Delete(_backupDirectory, recursive: false);
        }
    }

    [Neo4jIntegrationFact]
    public async Task Explicit_backfill_preview_confirmation_dry_run_apply_and_replay_preserve_reviewed_identity()
    {
        var before = await State();
        var plan = MoviesBackfill.Plan();
        Assert.Equal(MoviesBackfill.SchemaId, plan.PredecessorId);
        Assert.All(plan.Steps, step => Assert.Equal(1, step.Scope!.MaxRows));
        Assert.All(plan.Preview().Steps, step => Assert.DoesNotContain(MoviesBackfill.ApplicationId.ToString("D"), step.Command.Cypher));
        await Assert.ThrowsAsync<InvalidOperationException>(() => MoviesBackfill.InitializeAsync(fixture.Driver, fixture.Database, false));
        await Assert.ThrowsAsync<InvalidOperationException>(() => MoviesBackfill.PrepareSchemaAsync(fixture.Driver, fixture.Database, false));
        await Assert.ThrowsAsync<InvalidOperationException>(() => MoviesBackfill.SeedLegacyAsync(fixture.Driver, fixture.Database, false));
        await Assert.ThrowsAsync<InvalidOperationException>(() => MoviesBackfill.ApplyAsync(fixture.Driver, fixture.Database, false));
        Assert.Equal(before, await State());
        await Prepare();
        Assert.True(await MoviesBackfill.SeedLegacyAsync(fixture.Driver, fixture.Database, true));
        Assert.False(await MoviesBackfill.SeedLegacyAsync(fixture.Driver, fixture.Database, true));
        Assert.False((await CompileLegacy()).Success); // Unique constraint alone cannot supply an absent required ID.
        var seeded = await State();
        var validation = await MoviesBackfill.ValidateAsync(fixture.Driver, fixture.Database);
        Assert.True(validation.CanApply);
        Assert.False(validation.AlreadyApplied);
        Assert.Equal(seeded, await State());
        var backup = await Backup();
        var backupBytes = await File.ReadAllBytesAsync(backup);
        Assert.Equal(seeded, await State());
        var result = await MoviesBackfill.ApplyAsync(fixture.Driver, fixture.Database, true, backup);
        Assert.False(result.AlreadyApplied);
        Assert.Equal(2, result.CompletedSteps);
        Assert.Equal(MoviesBackfill.ApplicationId.ToString("D"), await AppId());
        var compilation = await CompileLegacy();
        Assert.True(compilation.Success, string.Join("; ", compilation.Diagnostics.Select(d => d.Message)));
        Assert.NotNull(compilation.Model);
        // Replay may increment the journal's serialization lock counter; it must retain the graph identity.
        var applied = await AppId();
        Assert.True((await MoviesBackfill.ApplyAsync(fixture.Driver, fixture.Database, true, backup)).AlreadyApplied);
        Assert.False(await MoviesBackfill.SeedLegacyAsync(fixture.Driver, fixture.Database, true));
        Assert.Equal(applied, await AppId());
        Assert.True((await MoviesBackfill.ValidateAsync(fixture.Driver, fixture.Database)).AlreadyApplied);
        Assert.Equal(backupBytes, await File.ReadAllBytesAsync(backup));
        using var preserved = System.Text.Json.JsonDocument.Parse(backupBytes);
        Assert.True(preserved.RootElement.GetProperty("ApplicationIdWasAbsent").GetBoolean());
        Assert.Equal(plan.Checksum, preserved.RootElement.GetProperty("PlanChecksum").GetString());
        Assert.Equal(MoviesBackfill.SchemaId, preserved.RootElement.GetProperty("JournalHeadBefore").GetString());
        Assert.False(preserved.RootElement.GetProperty("Properties").TryGetProperty("appId", out _));
        var latePath = NewBackupPath();
        await Assert.ThrowsAsync<InvalidOperationException>(() => MoviesBackfillBackup.SaveAsync(fixture.Driver, fixture.Database, FixtureId, latePath));
        Assert.False(File.Exists(latePath));
    }

    [Neo4jIntegrationFact]
    public async Task Conflicting_application_identity_is_never_adopted_or_overwritten()
    {
        await Prepare();
        await MoviesBackfill.SeedLegacyAsync(fixture.Driver, fixture.Database, true);
        var backup = await Backup();
        await Execute("MATCH (n:GraphBridgeMoviesLegacyMovie) SET n.appId = 'foreign-identity'");
        var before = await State();
        await Assert.ThrowsAsync<InvalidOperationException>(() => MoviesBackfill.ApplyAsync(fixture.Driver, fixture.Database, true, backup));
        await Assert.ThrowsAsync<InvalidOperationException>(() => MoviesBackfill.SeedLegacyAsync(fixture.Driver, fixture.Database, true));
        Assert.Equal(before, await State());
        Assert.Equal("foreign-identity", await AppId());
        await using var db = MoviesWorkflow.BrowseContext(fixture.Driver, fixture.Database);
        var validation = await db.Database.ValidateGraphMigrationAsync(MoviesBackfill.Plan());
        Assert.Null(validation.OwnerToken);
        Assert.Null(validation.MigrationStatus);
    }

    [Neo4jIntegrationFact]
    public async Task Completed_history_does_not_authorize_repair_of_missing_identity()
    {
        await Prepare();
        await MoviesBackfill.SeedLegacyAsync(fixture.Driver, fixture.Database, true);
        var backup = await Backup();
        await MoviesBackfill.ApplyAsync(fixture.Driver, fixture.Database, true, backup);
        await Execute("MATCH (n:GraphBridgeMoviesLegacyMovie) REMOVE n.appId");
        var before = await State();
        await Assert.ThrowsAsync<InvalidOperationException>(() => MoviesBackfill.ApplyAsync(fixture.Driver, fixture.Database, true, backup));
        await Assert.ThrowsAsync<InvalidOperationException>(() => MoviesBackfill.SeedLegacyAsync(fixture.Driver, fixture.Database, true));
        Assert.Equal(before, await State());
        Assert.Null(await AppId());
    }

    [Neo4jIntegrationFact]
    public async Task Unrelated_journal_head_rejects_seed_before_creating_a_legacy_row()
    {
        await Prepare();
        await using var db = MoviesWorkflow.BrowseContext(fixture.Driver, fixture.Database);
        var other = new GraphMigrationPlan("movies_test_other_head", MoviesBackfill.SchemaId,
            [GraphMigrationStep.AssertPrecondition("empty", new("NODE", MoviesBackfill.Label, new Dictionary<string, object?>(), 1), 0)],
            GraphMigrationRollbackPolicy.NoRollback("Disposable test-only head advancement"));
        await db.Database.ApplyGraphMigrationAsync(other);
        var before = await State();
        await Assert.ThrowsAsync<InvalidOperationException>(() => MoviesBackfill.SeedLegacyAsync(fixture.Driver, fixture.Database, true));
        Assert.Equal(before, await State());
        Assert.Null(await AppId());
    }

    [Neo4jIntegrationFact]
    public async Task Backup_never_overwrites_and_missing_malformed_or_tampered_files_prevent_graph_and_journal_changes()
    {
        await Prepare();
        await MoviesBackfill.SeedLegacyAsync(fixture.Driver, fixture.Database, true);
        var backup = await Backup();
        var bytes = await File.ReadAllBytesAsync(backup);
        var before = await State();
        await Assert.ThrowsAsync<IOException>(() => MoviesBackfillBackup.SaveAsync(fixture.Driver, fixture.Database, FixtureId, backup));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(backup));
        await Assert.ThrowsAsync<FileNotFoundException>(() => MoviesBackfill.ApplyAsync(fixture.Driver, fixture.Database, true, NewBackupPath()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => MoviesBackfill.ApplyAsync(fixture.Driver, fixture.Database, true));
        foreach (var fault in new[] { "checksum", "fixture", "database", "labels", "title", "presence", "unknown", "duplicate", "null", "oversized" })
        {
            var path = NewBackupPath();
            var json = JsonNode.Parse(bytes)!.AsObject();
            switch (fault)
            {
                case "checksum": json["PlanChecksum"] = new string('0', 64); break;
                case "fixture": json["FixtureId"] = Guid.NewGuid().ToString("N"); break;
                case "database": json["Database"] = "foreign"; break;
                case "labels": json["Labels"] = new JsonArray("ExtraLabel", MoviesBackfill.Label); break;
                case "title": json["Properties"]!["Title"] = "Changed"; break;
                case "presence": json["ApplicationIdWasAbsent"] = false; break;
                case "unknown": json["Password"] = "not-a-secret"; break;
            }
            var text = fault switch
            {
                "duplicate" => json.ToJsonString().Insert(1, "\"FormatVersion\":1,"),
                "null" => "null",
                "oversized" => new string(' ', MoviesBackfillBackup.MaximumBytes + 1),
                _ => json.ToJsonString()
            };
            await File.WriteAllTextAsync(path, text);
            await Assert.ThrowsAsync<InvalidOperationException>(() => MoviesBackfill.ApplyAsync(fixture.Driver, fixture.Database, true, path));
            Assert.Equal(before, await State());
            Assert.Null(await AppId());
        }
    }

    [Neo4jIntegrationFact]
    public async Task Backup_preserves_extra_labels_and_first_apply_rejects_changed_labels()
    {
        await Prepare();
        await MoviesBackfill.SeedLegacyAsync(fixture.Driver, fixture.Database, true);
        await Execute("MATCH (n:GraphBridgeMoviesLegacyMovie) SET n:GraphBridgeMoviesLegacyExtra");
        var backup = await Backup();
        var text = await File.ReadAllTextAsync(backup);
        Assert.Contains("GraphBridgeMoviesLegacyExtra", text);
        await Execute("MATCH (n:GraphBridgeMoviesLegacyMovie) REMOVE n:GraphBridgeMoviesLegacyExtra");
        var before = await State();
        await Assert.ThrowsAsync<InvalidOperationException>(() => MoviesBackfill.ApplyAsync(fixture.Driver, fixture.Database, true, backup));
        Assert.Equal(before, await State());
        Assert.Equal(text, await File.ReadAllTextAsync(backup));
    }

    [Fact]
    public async Task Precancelled_backup_and_apply_perform_no_file_or_driver_access_and_confirmation_remains_first()
    {
        var driver = System.Reflection.DispatchProxy.Create<IDriver, MoviesNoTransportDriver>();
        var token = new CancellationToken(canceled: true);
        var path = Path.Combine(_backupDirectory, "must-not-exist.json");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => MoviesBackfillBackup.SaveAsync(driver, "neo4j", Guid.NewGuid().ToString("N"), path, token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => MoviesBackfill.ApplyAsync(driver, "neo4j", true, path, token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => MoviesBackfill.ApplyAsync(driver, "neo4j", false, path, token));
        Assert.Equal(0, ((MoviesNoTransportDriver)(object)driver).Calls);
        Assert.False(Directory.Exists(_backupDirectory));
    }

    private static string FixtureId => Environment.GetEnvironmentVariable("GRAPHBRIDGE_FIXTURE_ID")!;
    private string NewBackupPath()
    {
        Directory.CreateDirectory(_backupDirectory);
        var path = Path.Combine(_backupDirectory, Guid.NewGuid().ToString("N") + ".json");
        _backupPaths.Add(path);
        return path;
    }
    private async Task<string> Backup()
    {
        var path = NewBackupPath();
        await MoviesBackfillBackup.SaveAsync(fixture.Driver, fixture.Database, FixtureId, path);
        return path;
    }

    private async Task Prepare()
    {
        await MoviesBackfill.InitializeAsync(fixture.Driver, fixture.Database, true);
        Assert.Equal(2, (await MoviesBackfill.PrepareSchemaAsync(fixture.Driver, fixture.Database, true)).CompletedSteps);
    }
    private async Task<GraphScaffoldCompilationResult> CompileLegacy()
    {
        var snapshot = await GraphDatabaseInspector.InspectAsync(fixture.Driver, fixture.Database,
            new GraphInspectionOptions(labels: [MoviesBackfill.Label], relationshipTypes: []));
        return GraphScaffoldCompiler.Compile(snapshot, new("GraphBridge.Samples.LegacyMovies", "LegacyMoviesContext",
            [new(MoviesBackfill.Label, "LegacyMovie", ["appId"], true,
                [new("appId", "AppId", GraphScalarType.Guid, GraphScalarConversion.GuidString, Required: true)])]));
    }
    private async Task<string?> AppId()
    {
        await using var session = fixture.OpenSession();
        var rows = await (await session.RunAsync("MATCH (n:GraphBridgeMoviesLegacyMovie) RETURN n.appId AS id LIMIT 2")).ToListAsync();
        Assert.True(rows.Count <= 1);
        return rows.Count == 0 ? null : rows[0]["id"] as string;
    }
    private async Task<string[]> State()
    {
        await using var session = fixture.OpenSession();
        var nodes = await session.RunAsync("MATCH (n) WHERE n:GraphBridgeMoviesLegacyMovie OR n:__GraphBridgeMigrationControl OR n:__GraphBridgeMigration OR n:__GraphBridgeMigrationStep RETURN labels(n) AS labels, properties(n) AS properties ORDER BY labels(n), coalesce(n.id,n.legacyCatalogKey)");
        var data = await nodes.ToListAsync(r => System.Text.Json.JsonSerializer.Serialize(r.Values));
        var schema = await session.RunAsync("SHOW CONSTRAINTS YIELD name RETURN name ORDER BY name");
        return [.. data, .. await schema.ToListAsync(r => r["name"].As<string>())];
    }
    private async Task Execute(string cypher)
    {
        await using var session = fixture.OpenSession();
        await (await session.RunAsync(cypher)).ConsumeAsync();
    }
    private async Task Cleanup()
    {
        // Test-only reset of the serial, owned disposable fixture. The executable never clears history or data.
        await MoviesFixture.VerifyFixtureAsync(fixture.Driver, fixture.Database,
            Environment.GetEnvironmentVariable("GRAPHBRIDGE_FIXTURE_ID")!);
        await Execute("MATCH (n:GraphBridgeMoviesLegacyMovie) DELETE n");
        foreach (var label in new[] { "__GraphBridgeMigrationStep", "__GraphBridgeMigration", "__GraphBridgeMigrationControl" })
            await Execute($"MATCH (n:{label}) DELETE n");
        foreach (var name in new[] { MoviesBackfill.LegacyConstraint, MoviesBackfill.ApplicationIdConstraint,
                     "gb_migration_control_key", "gb_migration_history_key", "gb_migration_step_key" })
            await Execute($"DROP CONSTRAINT {name} IF EXISTS");
    }
}
