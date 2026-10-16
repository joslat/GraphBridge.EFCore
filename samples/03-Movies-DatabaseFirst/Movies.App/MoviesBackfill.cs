using GraphBridge.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore;
using Neo4j.Driver;

namespace GraphBridge.Samples.MoviesDemo;

/// <summary>Separate, explicitly confirmed identity migration for one dedicated legacy sample row. Never part of scaffolding.</summary>
public static class MoviesBackfill
{
    public const string Label = "GraphBridgeMoviesLegacyMovie";
    public const string SchemaId = "movies_sample_legacy_schema_v1";
    public const string MigrationId = "movies_sample_legacy_app_id_v1";
    public const string LegacyKey = "graphbridge-movies-legacy-1";
    public const string Title = "The Uncatalogued Reel";
    public const string LegacyConstraint = "graphbridge_movies_legacy_catalog_key";
    public const string ApplicationIdConstraint = "graphbridge_movies_legacy_app_id";
    public static readonly Guid ApplicationId = Guid.Parse("55000000-0000-0000-0000-000000000001");

    /// <summary>The predecessor is a human-selected exact journal head, never discovered/adopted automatically.</summary>
    public static GraphMigrationPlan SchemaPlan(string? predecessorId = null) => new(SchemaId, predecessorId,
    [
        GraphMigrationStep.CreateConstraint("legacy-selector-uniqueness", new(LegacyConstraint,
            GraphMigrationSchemaKind.UniquenessConstraint, "NODE", Label, ["legacyCatalogKey"])),
        GraphMigrationStep.CreateConstraint("application-id-uniqueness", new(ApplicationIdConstraint,
            GraphMigrationSchemaKind.UniquenessConstraint, "NODE", Label, ["appId"]))
    ], GraphMigrationRollbackPolicy.NoRollback("Explicit dedicated Movies legacy schema. Each schema step commits separately; retain completed steps and review any interrupted owner before manual recovery."));

    public static GraphMigrationPlan Plan() => new(MigrationId, SchemaId,
    [
        GraphMigrationStep.AssertPrecondition("one-reviewed-legacy-row", Scope(), 1),
        GraphMigrationStep.Backfill("assign-reviewed-application-id", Scope(), "appId", ApplicationId.ToString("D"), 1)
    ], GraphMigrationRollbackPolicy.NoRollback("Reviewed assignment of one fixed application Guid to one uniquely constrained legacy selector. This is not automatic key inference, random ID generation, bulk backfill or an atomic multi-step plan."));

    public static async Task InitializeAsync(IDriver driver, string database, bool confirmed,
        CancellationToken cancellationToken = default)
    {
        Confirm(confirmed);
        await using var db = MoviesWorkflow.BrowseContext(driver, database);
        var state = await db.Database.ValidateGraphMigrationAsync(Plan(), cancellationToken: cancellationToken);
        RefuseOwnerOrIncomplete(state);
        await db.Database.InitializeGraphMigrationsAsync(cancellationToken: cancellationToken);
    }

    public static async Task<GraphMigrationResult> PrepareSchemaAsync(IDriver driver, string database, bool confirmed,
        string? predecessorId = null, CancellationToken cancellationToken = default)
    {
        Confirm(confirmed);
        await using var db = MoviesWorkflow.BrowseContext(driver, database);
        await ReadStateAsync(db, allowMissing: true, cancellationToken);
        var plan = SchemaPlan(predecessorId);
        var state = await db.Database.ValidateGraphMigrationAsync(plan, cancellationToken: cancellationToken);
        RefuseOwnerOrIncomplete(state);
        if (!state.CanApply) throw InvalidReadiness(state);
        if (state.AlreadyApplied) await VerifySchemaAsync(db, cancellationToken);
        return await db.Database.ApplyGraphMigrationAsync(plan, cancellationToken: cancellationToken);
    }

    /// <summary>Seeds only an absent dedicated legacy row, after exact schema history is complete and successor readiness is verified.</summary>
    public static async Task<bool> SeedLegacyAsync(IDriver driver, string database, bool confirmed,
        string? predecessorId = null, CancellationToken cancellationToken = default)
    {
        Confirm(confirmed);
        await using var db = MoviesWorkflow.BrowseContext(driver, database);
        var schema = await db.Database.ValidateGraphMigrationAsync(SchemaPlan(predecessorId), cancellationToken: cancellationToken);
        RefuseOwnerOrIncomplete(schema);
        if (!schema.CanApply || !schema.AlreadyApplied)
            throw new InvalidOperationException("The exact reviewed Movies legacy schema plan must be completed before seed writes; no journal head is adopted automatically.");
        await VerifySchemaAsync(db, cancellationToken);
        var state = await db.Database.ValidateGraphMigrationAsync(Plan(), cancellationToken: cancellationToken);
        RefuseOwnerOrIncomplete(state);
        var existing = await ReadStateAsync(db, allowMissing: true, cancellationToken);
        if (state.AlreadyApplied)
        {
            if (existing?.ApplicationId != ApplicationId.ToString("D"))
                throw new InvalidOperationException("Completed identity migration has missing/drifted sample data. No reseed or repair is attempted.");
            return false;
        }
        if (existing?.ApplicationId is not null)
            throw new InvalidOperationException("An application ID is present without completed matching migration history. Resolve the conflicting or uncertain earlier operation explicitly.");

        // Validate the exact predecessor while the future seed is absent. The actual plan correctly expects one row.
        var preflight = new GraphMigrationPlan("movies_sample_legacy_seed_preflight", SchemaId,
            [GraphMigrationStep.AssertPrecondition("observed-legacy-count", Scope(), existing is null ? 0 : 1)],
            GraphMigrationRollbackPolicy.NoRollback("Read-only predecessor/seed check. This proposal is validated only, never applied."));
        var readiness = await db.Database.ValidateGraphMigrationAsync(preflight, cancellationToken: cancellationToken);
        if (!readiness.CanApply) throw InvalidReadiness(readiness);
        if (existing is not null) return false;

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var created = await db.Database.QueryCypherAsync(
            "OPTIONAL MATCH (existing:GraphBridgeMoviesLegacyMovie) WITH existing LIMIT 2 "
            + "WITH count(existing) AS existingCount WHERE existingCount = 0 "
            + "CREATE (n:GraphBridgeMoviesLegacyMovie {legacyCatalogKey:$legacy,title:$title}) RETURN n.legacyCatalogKey AS legacy",
            new Dictionary<string, object?> { ["legacy"] = LegacyKey, ["title"] = Title },
            record => record["legacy"].As<string>(), maxRows: 1, cancellationToken);
        if (created.Count != 1 || created[0] != LegacyKey)
            throw new InvalidOperationException("Dedicated legacy label changed during seed preflight; no commit or overwrite is attempted.");
        // A failed commit response can have an unknown outcome. Leave exact IDs/selector and journal unchanged for explicit review.
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    /// <summary>Read-only validation, including current sample data; never initializes or changes the journal.</summary>
    public static async Task<GraphMigrationValidation> ValidateAsync(IDriver driver, string database,
        CancellationToken cancellationToken = default)
    {
        await using var db = MoviesWorkflow.BrowseContext(driver, database);
        var state = await db.Database.ValidateGraphMigrationAsync(Plan(), cancellationToken: cancellationToken);
        RefuseOwnerOrIncomplete(state);
        await VerifySchemaAsync(db, cancellationToken);
        var observed = await ReadStateAsync(db, allowMissing: false, cancellationToken);
        if (state.AlreadyApplied)
        {
            if (observed!.ApplicationId != ApplicationId.ToString("D"))
                throw new InvalidOperationException("Completed migration history does not match the current legacy row. No repair or replay writes are attempted.");
        }
        else if (observed!.ApplicationId is not null)
            throw new InvalidOperationException("Legacy row already has an application ID without completed matching history. Refusing to replace or adopt it.");
        if (!state.CanApply) throw InvalidReadiness(state);
        return state;
    }

    public static Task<GraphMigrationResult> ApplyAsync(IDriver driver, string database, bool confirmed,
        CancellationToken cancellationToken = default)
    {
        Confirm(confirmed);
        cancellationToken.ThrowIfCancellationRequested();
        throw new InvalidOperationException("Save and review a Movies legacy before-image backup, then supply its path explicitly before applying this migration.");
    }

    public static async Task<GraphMigrationResult> ApplyAsync(IDriver driver, string database, bool confirmed,
        string backupPath, CancellationToken cancellationToken = default)
    {
        Confirm(confirmed);
        cancellationToken.ThrowIfCancellationRequested();
        await MoviesBackfillBackup.ValidateForApplyAsync(driver, database, backupPath, cancellationToken);
        await using var db = MoviesWorkflow.BrowseContext(driver, database);
        var result = await db.Database.ApplyGraphMigrationAsync(Plan(), cancellationToken: cancellationToken);
        var observed = await ReadStateAsync(db, allowMissing: false, cancellationToken);
        if (observed!.ApplicationId != ApplicationId.ToString("D"))
            throw new InvalidOperationException("Migration completed but the reviewed application ID was not observed. Inspect the retained row and journal; no repair is attempted.");
        return result;
    }

    private static GraphMigrationDataScope Scope() => new("NODE", Label,
        new Dictionary<string, object?> { ["legacyCatalogKey"] = LegacyKey, ["title"] = Title }, maxRows: 1);

    private static async Task VerifySchemaAsync(DbContext db, CancellationToken cancellationToken)
    {
        var schema = (await db.Database.InspectGraphSchemaAsync(cancellationToken: cancellationToken)).Observation;
        foreach (var pair in new[] { (Name: LegacyConstraint, Property: "legacyCatalogKey"), (Name: ApplicationIdConstraint, Property: "appId") })
        {
            var constraint = schema.Constraints.SingleOrDefault(c => c.Name == pair.Name);
            var index = schema.Indexes.SingleOrDefault(i => i.OwningConstraint == pair.Name);
            if (constraint is null || constraint.EntityType != "NODE" || constraint.Type is not ("UNIQUENESS" or "NODE_PROPERTY_UNIQUENESS")
                || constraint.LabelsOrTypes is not { Count: 1 } || constraint.LabelsOrTypes[0] != Label
                || constraint.Properties is not { Count: 1 } || constraint.Properties[0] != pair.Property
                || index is null || index.State != "ONLINE" || index.Type != "RANGE" || index.EntityType != "NODE"
                || index.LabelsOrTypes is not { Count: 1 } || index.LabelsOrTypes[0] != Label
                || index.Properties is not { Count: 1 } || index.Properties[0] != pair.Property)
                throw new InvalidOperationException("Current legacy identity schema differs from the reviewed completed plan. No repair, seed or backfill is attempted.");
        }
    }

    private static async Task<LegacyState?> ReadStateAsync(DbContext db, bool allowMissing, CancellationToken cancellationToken)
    {
        var rows = await db.Database.QueryCypherAsync(
            "MATCH (n:GraphBridgeMoviesLegacyMovie) RETURN n.legacyCatalogKey AS legacy,n.title AS title,n.appId AS appId,keys(n) AS properties LIMIT 2",
            new Dictionary<string, object?>(), record => new LegacyState(record["legacy"] as string, record["title"] as string,
                record["appId"] as string, record["appId"] is not null, record["properties"].As<List<string>>()), maxRows: 2, cancellationToken);
        if (rows.Count == 0 && allowMissing) return null;
        if (rows.Count != 1 || rows[0].LegacyKey != LegacyKey || rows[0].Title != Title
            || rows[0].Properties.Any(property => property is not ("legacyCatalogKey" or "title" or "appId"))
            || rows[0].HasApplicationId && rows[0].ApplicationId != ApplicationId.ToString("D"))
            throw new InvalidOperationException("Dedicated Movies legacy data is missing, duplicated or conflicting. Refusing to seed, overwrite, backfill or infer an identity from this state.");
        return rows[0];
    }

    private static void Confirm(bool confirmed)
    {
        if (!confirmed) throw new InvalidOperationException("Preview the plan and explicitly confirm this separate sample mutation. Scaffolding never invokes backfill.");
    }

    private static void RefuseOwnerOrIncomplete(GraphMigrationValidation state)
    {
        if (state.OwnerToken is not null || state.MigrationStatus is not (null or "Completed"))
            throw new InvalidOperationException("A migration owner or incomplete history remains. Stop/fence and inspect the earlier operation; this sample never takes over, resets or recovers the journal automatically.");
    }

    private static InvalidOperationException InvalidReadiness(GraphMigrationValidation state)
        => new("Reviewed migration prerequisites failed: " + string.Join("; ", state.Issues));

    private sealed record LegacyState(string? LegacyKey, string? Title, string? ApplicationId, bool HasApplicationId,
        IReadOnlyList<string> Properties);
}
