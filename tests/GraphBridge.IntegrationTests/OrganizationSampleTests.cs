using GraphBridge.EntityFrameworkCore.Migrations;
using GraphBridge.Samples.OrganizationDemo;
using Microsoft.EntityFrameworkCore;
using Neo4j.Driver;

namespace GraphBridge.IntegrationTests;

[Collection(Neo4jIntegrationCollection.Name)]
public sealed class OrganizationSampleTests(DisposableNeo4jFixture fixture) : IAsyncLifetime
{
    private static bool Enabled => Environment.GetEnvironmentVariable("GRAPHBRIDGE_INTEGRATION") == "1";

    public Task InitializeAsync() => Enabled ? ResetOwnedFixtureArtifactsAsync() : Task.CompletedTask;
    public Task DisposeAsync() => Enabled ? ResetOwnedFixtureArtifactsAsync() : Task.CompletedTask;

    [Neo4jIntegrationFact]
    public async Task Generated_sample_runs_real_EF_CRUD_parallel_edges_calendar_dates_and_reference_fixup()
    {
        var setup = await OrganizationWorkflow.PrepareSchemaAsync(fixture.Driver, fixture.Database);
        Assert.Equal(3, setup.CompletedSteps);
        Assert.False(setup.AlreadyApplied);
        var result = await OrganizationWorkflow.RunAsync(fixture.Driver, fixture.Database);
        Assert.Equal(4, result.Added);
        Assert.Equal(2, result.Updated);
        Assert.Equal(4, result.Deleted);
        Assert.Equal(2, result.ParallelRelationships);
        Assert.Equal(0L, result.FakeJoinNodes);
        Assert.True(result.CalendarRoundTrip);
        Assert.True(result.NavigationFixup);
        await using var actual = OrganizationWorkflow.Context(fixture.Driver, fixture.Database);
        var manifest = actual.Database.GetGraphSchemaManifest();
        AssertCompilerManifest(manifest, versionTwo: false);
        Assert.Equal(manifest.Fingerprint, result.SourceSchemaFingerprint);
        Assert.Equal(manifest.Fingerprint, result.TargetSchemaFingerprint);
        Assert.Contains("ORGANIZATION_SAMPLE_WORKS_FOR", result.Cypher);
        Assert.Contains("$p0", result.Cypher);
        Assert.Equal(0L, await Scalar("MATCH ()-[r:ORGANIZATION_SAMPLE_WORKS_FOR]->() RETURN count(r) AS count"));
    }

    [Neo4jIntegrationFact]
    public async Task Regeneration_requires_explicit_backfill_and_rename_before_v2_can_read_existing_edges()
    {
        await OrganizationWorkflow.PrepareSchemaAsync(fixture.Driver, fixture.Database);
        Assert.Equal(4, await OrganizationWorkflow.SeedMigrationDemoAsync(fixture.Driver, fixture.Database));
        await using var v1 = OrganizationWorkflow.Context(fixture.Driver, fixture.Database);
        await using var beforeV2 = OrganizationWorkflow.ContextV2(fixture.Driver, fixture.Database);
        var before = await GraphRows();
        Assert.Equal(2, before.Count);
        Assert.All(before, row => { Assert.NotNull(row["role"]); Assert.Null(row["position"]); Assert.Null(row["department"]); });
        Assert.Equal("2025-01-01", before.Single(row => row["id"].As<string>() == OrganizationPlans.FirstEmploymentId.ToString("D"))["start"]);
        Assert.Contains("Position", (await Assert.ThrowsAsync<InvalidOperationException>(() => beforeV2.EmploymentSet
            .Where(edge => edge.EmploymentId == OrganizationPlans.FirstEmploymentId).SingleAsync())).Message);
        Assert.Empty(beforeV2.ChangeTracker.Entries());

        var v1Manifest = v1.Database.GetGraphSchemaManifest();
        var v2Manifest = beforeV2.Database.GetGraphSchemaManifest();
        AssertCompilerManifest(v1Manifest, versionTwo: false);
        AssertCompilerManifest(v2Manifest, versionTwo: true);
        Assert.NotEqual(v1Manifest.Fingerprint, v2Manifest.Fingerprint);
        Assert.Equal(v1Manifest.Nodes.Select(node => (node.Label, string.Join(",", node.KeyProperties))),
            v2Manifest.Nodes.Select(node => (node.Label, string.Join(",", node.KeyProperties))));
        var edgeManifest = Assert.Single(v2Manifest.Relationships);
        Assert.Equal("employmentId", edgeManifest.KeyProperty);
        Assert.DoesNotContain(edgeManifest.Properties, property => property.Name is "personId" or "organizationId" or "role");

        var plan = OrganizationPlans.VersionTwo();
        var preview = plan.Preview();
        Assert.Equal("NoRollback", preview.RollbackPolicy.Kind);
        Assert.Equal(4, preview.Steps.Count);
        Assert.All(plan.Steps, step => { Assert.Equal(1, step.Scope!.MaxRows); Assert.Equal(1L, step.ExpectedCount); });
        Assert.All(preview.Steps, step => Assert.DoesNotContain("33000000-", step.Command.Cypher));
        var validation = await v1.Database.ValidateGraphMigrationAsync(plan);
        Assert.True(validation.CanApply, string.Join("; ", validation.Issues));
        Assert.All(await GraphRows(), row => Assert.Null(row["position"])); // Preview/validation do not perform the rename.
        // Resuming after seed committed but before migration started must reuse only the exact seed, without duplicate CREATEs.
        var applied = await OrganizationWorkflow.RunMigrationDemoAsync(fixture.Driver, fixture.Database);
        Assert.Equal(4, applied.CompletedSteps);
        Assert.False(applied.AlreadyApplied);
        Assert.Equal(new[] { "Architect / Engineering", "Advisor / Advisory" }, await OrganizationWorkflow.ReadVersionTwoAsync(fixture.Driver, fixture.Database));

        var rows = await GraphRows();
        Assert.All(rows, row =>
        {
            Assert.Null(row["role"]);
            Assert.NotNull(row["position"]);
            Assert.NotNull(row["department"]);
            Assert.Null(row["personSlot"]);
            Assert.Null(row["organizationSlot"]);
            Assert.Equal(OrganizationPlans.DemoPersonId.ToString("D"), row["person"]);
            Assert.Equal(OrganizationPlans.DemoOrganizationId.ToString("D"), row["organization"]);
        });
        var first = rows.Single(row => row["id"].As<string>() == OrganizationPlans.FirstEmploymentId.ToString("D"));
        var second = rows.Single(row => row["id"].As<string>() == OrganizationPlans.SecondEmploymentId.ToString("D"));
        Assert.Null(first["end"]);
        Assert.Equal("2025-12-31", second["end"]);
        Assert.True((await OrganizationWorkflow.RunMigrationDemoAsync(fixture.Driver, fixture.Database)).AlreadyApplied);
        Assert.Null((await v1.Database.ValidateGraphMigrationAsync(plan)).OwnerToken);

        await using var edit = OrganizationWorkflow.ContextV2(fixture.Driver, fixture.Database);
        var employment = await edit.EmploymentSet.Where(edge => edge.EmploymentId == OrganizationPlans.FirstEmploymentId).SingleAsync();
        employment.Position = "Director";
        employment.Department = null;
        Assert.Equal(1, await edit.SaveChangesAsync());
        await using var fresh = OrganizationWorkflow.ContextV2(fixture.Driver, fixture.Database);
        var reloaded = await fresh.EmploymentSet.Where(edge => edge.EmploymentId == OrganizationPlans.FirstEmploymentId).SingleAsync();
        Assert.Equal("Director", reloaded.Position);
        Assert.Null(reloaded.Department);
        Assert.Equal(new DateOnly(2025, 1, 1), reloaded.ValidFrom);
        Assert.Null((await GraphRows()).Single(row => row["id"].As<string>() == OrganizationPlans.FirstEmploymentId.ToString("D"))["department"]);
    }

    [Neo4jIntegrationFact]
    public async Task Generated_restrict_policy_keeps_nodes_and_edges_when_endpoint_delete_is_attempted()
    {
        await OrganizationWorkflow.PrepareSchemaAsync(fixture.Driver, fixture.Database);
        await OrganizationWorkflow.SeedMigrationDemoAsync(fixture.Driver, fixture.Database);
        await using var db = OrganizationWorkflow.Context(fixture.Driver, fixture.Database);
        var person = await db.PersonSet.Where(person => person.Id == OrganizationPlans.DemoPersonId).SingleAsync();
        db.Remove(person); // Edges are deliberately not loaded, so storage must enforce restrictive deletion too.
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal(2, (await GraphRows()).Count);
        await using var fresh = OrganizationWorkflow.Context(fixture.Driver, fixture.Database);
        Assert.Equal("Alice", (await fresh.PersonSet.Where(person => person.Id == OrganizationPlans.DemoPersonId).SingleAsync()).Name);
    }

    [Neo4jIntegrationFact]
    public async Task Migration_demo_rejects_wrong_predecessor_before_creating_fixed_sample_entities()
    {
        await OrganizationWorkflow.PrepareSchemaAsync(fixture.Driver, fixture.Database);
        await using var db = OrganizationWorkflow.Context(fixture.Driver, fixture.Database);
        var other = new GraphMigrationPlan("organization_other_head", OrganizationPlans.SchemaId,
            [GraphMigrationStep.AssertPrecondition("empty", new("NODE", "OrganizationSamplePerson", new Dictionary<string, object?>(), 1), 0)],
            GraphMigrationRollbackPolicy.NoRollback("Fixture-only head advancement"));
        await db.Database.ApplyGraphMigrationAsync(other);
        Assert.Contains("prerequisite", (await Assert.ThrowsAsync<InvalidOperationException>(() =>
            OrganizationWorkflow.RunMigrationDemoAsync(fixture.Driver, fixture.Database))).Message);
        Assert.Equal(0L, await Scalar("MATCH (n:OrganizationSamplePerson) RETURN count(n) AS count"));
        Assert.Equal(0L, await Scalar("MATCH (n:OrganizationSampleOrganization) RETURN count(n) AS count"));
        Assert.Empty(await GraphRows());
    }

    [Neo4jIntegrationFact]
    public async Task Migration_demo_first_invocation_seeds_then_applies_and_replay_does_not_reseed()
    {
        await OrganizationWorkflow.PrepareSchemaAsync(fixture.Driver, fixture.Database);
        var first = await OrganizationWorkflow.RunMigrationDemoAsync(fixture.Driver, fixture.Database);
        Assert.False(first.AlreadyApplied);
        Assert.Equal(4, first.CompletedSteps);
        Assert.Equal(new[] { "Architect / Engineering", "Advisor / Advisory" }, await OrganizationWorkflow.ReadVersionTwoAsync(fixture.Driver, fixture.Database));
        Assert.True((await OrganizationWorkflow.RunMigrationDemoAsync(fixture.Driver, fixture.Database)).AlreadyApplied);
        Assert.Equal(2, (await GraphRows()).Count);
    }

    [Neo4jIntegrationFact]
    public async Task Migration_demo_never_overwrites_conflicting_seed_values()
    {
        await OrganizationWorkflow.PrepareSchemaAsync(fixture.Driver, fixture.Database);
        await OrganizationWorkflow.SeedMigrationDemoAsync(fixture.Driver, fixture.Database);
        await using var db = OrganizationWorkflow.Context(fixture.Driver, fixture.Database);
        var first = await db.EmploymentSet.Where(edge => edge.EmploymentId == OrganizationPlans.FirstEmploymentId).SingleAsync();
        first.Role = "User changed this seed";
        Assert.Equal(1, await db.SaveChangesAsync());
        Assert.Contains("conflicting", (await Assert.ThrowsAsync<InvalidOperationException>(() =>
            OrganizationWorkflow.RunMigrationDemoAsync(fixture.Driver, fixture.Database))).Message);
        var rows = await GraphRows();
        Assert.Equal("User changed this seed", rows.Single(row => row["id"].As<string>() == OrganizationPlans.FirstEmploymentId.ToString("D"))["role"]);
        Assert.All(rows, row => { Assert.Null(row["position"]); Assert.Null(row["department"]); });
        var validation = await db.Database.ValidateGraphMigrationAsync(OrganizationPlans.VersionTwo());
        Assert.Null(validation.OwnerToken);
        Assert.Null(validation.MigrationStatus);
    }

    [Neo4jIntegrationFact]
    public async Task Pre_cancelled_sample_and_schema_setup_leave_owned_data_and_migration_journal_empty()
    {
        var token = new CancellationToken(canceled: true);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => OrganizationWorkflow.PrepareSchemaAsync(fixture.Driver, fixture.Database, token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => OrganizationWorkflow.RunAsync(fixture.Driver, fixture.Database, token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => OrganizationWorkflow.RunMigrationDemoAsync(fixture.Driver, fixture.Database, token));
        Assert.Equal(0L, await Scalar("MATCH (n:OrganizationSamplePerson) RETURN count(n) AS count"));
        Assert.Equal(0L, await Scalar("MATCH (n:OrganizationSampleOrganization) RETURN count(n) AS count"));
        Assert.Empty(await GraphRows());
        foreach (var label in new[] { GraphMigrationJournal.ControlLabel, GraphMigrationJournal.HistoryLabel, GraphMigrationJournal.StepLabel })
            Assert.Equal(0L, await Scalar($"MATCH (n:{label}) RETURN count(n) AS count"));
        foreach (var name in new[] { OrganizationPlans.PersonConstraint, OrganizationPlans.OrganizationConstraint, OrganizationPlans.EmploymentConstraint,
                     GraphMigrationJournal.ControlConstraint, GraphMigrationJournal.HistoryConstraint, GraphMigrationJournal.StepConstraint })
            Assert.Equal(0L, await Scalar($"SHOW CONSTRAINTS YIELD name WHERE name = '{name}' RETURN count(*) AS count"));
    }

    [Neo4jIntegrationFact]
    public async Task Pre_cancelled_migration_preserves_acknowledged_seed_and_journal_then_explicit_resume_uses_exact_seed()
    {
        await OrganizationWorkflow.PrepareSchemaAsync(fixture.Driver, fixture.Database);
        Assert.Equal(4, await OrganizationWorkflow.SeedMigrationDemoAsync(fixture.Driver, fixture.Database));
        await using var db = OrganizationWorkflow.Context(fixture.Driver, fixture.Database);
        var before = await db.Database.ValidateGraphMigrationAsync(OrganizationPlans.VersionTwo());
        Assert.True(before.CanApply, string.Join("; ", before.Issues));
        var token = new CancellationToken(canceled: true);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => OrganizationWorkflow.RunMigrationDemoAsync(fixture.Driver, fixture.Database, token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => OrganizationWorkflow.ApplyVersionTwoAsync(fixture.Driver, fixture.Database, token));
        var after = await db.Database.ValidateGraphMigrationAsync(OrganizationPlans.VersionTwo());
        Assert.True(after.CanApply, string.Join("; ", after.Issues));
        Assert.Null(after.OwnerToken);
        Assert.Null(after.MigrationStatus);
        Assert.Empty(after.CompletedStepIds);
        Assert.Equal(before.OwnerGeneration, after.OwnerGeneration);
        Assert.True((await db.Database.ValidateGraphMigrationAsync(OrganizationPlans.Schema())).AlreadyApplied);
        var rows = await GraphRows();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => { Assert.Null(row["position"]); Assert.Null(row["department"]); });
        Assert.Equal("Architect", rows.Single(row => row["id"].As<string>() == OrganizationPlans.FirstEmploymentId.ToString("D"))["role"]);
        Assert.Equal("Advisor", rows.Single(row => row["id"].As<string>() == OrganizationPlans.SecondEmploymentId.ToString("D"))["role"]);

        var resumed = await OrganizationWorkflow.RunMigrationDemoAsync(fixture.Driver, fixture.Database);
        Assert.False(resumed.AlreadyApplied);
        Assert.Equal(4, resumed.CompletedSteps);
        Assert.Equal(new[] { "Architect / Engineering", "Advisor / Advisory" }, await OrganizationWorkflow.ReadVersionTwoAsync(fixture.Driver, fixture.Database));
        Assert.Equal(2, (await GraphRows()).Count);
    }

    private static void AssertCompilerManifest(GraphBridge.EntityFrameworkCore.Schema.GraphSchemaManifest manifest, bool versionTwo)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "organization-schema", versionTwo ? "v2" : "v1");
        Assert.Equal(File.ReadAllText(Path.Combine(directory, "graphbridge.binding.json")).TrimEnd('\r', '\n'), manifest.CanonicalJson);
        Assert.Equal(File.ReadAllText(Path.Combine(directory, "graphbridge.binding.sha256")).Trim(), manifest.Fingerprint);
    }

    private async Task<List<IRecord>> GraphRows()
    {
        await using var session = fixture.OpenSession();
        var cursor = await session.RunAsync(
            "MATCH (p:OrganizationSamplePerson {id:$person})-[r:ORGANIZATION_SAMPLE_WORKS_FOR]->(o:OrganizationSampleOrganization {id:$organization}) "
            + "RETURN p.id AS person,o.id AS organization,r.employmentId AS id,r.role AS role,r.position AS position,r.department AS department,"
            + "r.validFrom AS start,r.validTo AS end,r.personId AS personSlot,r.organizationId AS organizationSlot",
            new { person = OrganizationPlans.DemoPersonId.ToString("D"), organization = OrganizationPlans.DemoOrganizationId.ToString("D") });
        return await cursor.ToListAsync();
    }

    private async Task<long> Scalar(string cypher)
    {
        await using var session = fixture.OpenSession();
        return (await (await session.RunAsync(cypher)).SingleAsync())["count"].As<long>();
    }

    private async Task Execute(string cypher, object? parameters = null)
    {
        await using var session = fixture.OpenSession();
        await (parameters is null ? await session.RunAsync(cypher) : await session.RunAsync(cypher, parameters)).ConsumeAsync();
    }

    private async Task ResetOwnedFixtureArtifactsAsync()
    {
        // Test-only cleanup after DisposableNeo4jFixture verified host/database/marker ownership. Never exposed by the app.
        await Execute("MATCH ()-[r:ORGANIZATION_SAMPLE_WORKS_FOR]->() DELETE r");
        await Execute("MATCH (n:OrganizationSamplePerson) DELETE n");
        await Execute("MATCH (n:OrganizationSampleOrganization) DELETE n");
        foreach (var label in new[] { GraphMigrationJournal.StepLabel, GraphMigrationJournal.HistoryLabel, GraphMigrationJournal.ControlLabel })
            await Execute($"MATCH (n:{label}) DELETE n");
        foreach (var name in new[] { OrganizationPlans.PersonConstraint, OrganizationPlans.OrganizationConstraint, OrganizationPlans.EmploymentConstraint,
                     GraphMigrationJournal.ControlConstraint, GraphMigrationJournal.HistoryConstraint, GraphMigrationJournal.StepConstraint })
            await Execute($"DROP CONSTRAINT {name} IF EXISTS");
    }
}
