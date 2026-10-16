using GraphBridge.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore;
using Neo4j.Driver;
using V1 = GraphBridge.Samples.Organization;
using V2 = GraphBridge.Samples.Organization.V2;

namespace GraphBridge.Samples.OrganizationDemo;

public sealed record OrganizationResult(int Added, int Updated, int Deleted, int ParallelRelationships,
    long FakeJoinNodes, bool CalendarRoundTrip, bool NavigationFixup, string Cypher,
    string SourceSchemaFingerprint, string TargetSchemaFingerprint);

public sealed record OrganizationSchemaFingerprints(string Source, string Target);

/// <summary>Generated entities and EF perform application CRUD. Native queries below only observe the graph representation.</summary>
public static class OrganizationWorkflow
{
    public static V1.OrganizationContext Context(IDriver driver, string database)
        => new(new DbContextOptionsBuilder().UseNeo4j(driver, database).Options);
    public static V2.OrganizationContext ContextV2(IDriver driver, string database)
        => new(new DbContextOptionsBuilder().UseNeo4j(driver, database).Options);

    /// <summary>Compiler artifact versus actual finalized EF storage expectation; does not inspect or migrate the database.</summary>
    public static OrganizationSchemaFingerprints GetSchemaFingerprints(IDriver driver, string database, bool versionTwo = false)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "organization-schema", versionTwo ? "v2" : "v1", "graphbridge.binding.sha256");
        if (new FileInfo(path).Length > 128) throw new InvalidOperationException("Generated sample fingerprint artifact exceeds its expected size.");
        var source = File.ReadAllText(path).Trim();
        if (source.Length != 64 || source.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new InvalidOperationException("Generated sample fingerprint artifact is not a canonical SHA-256.");
        using DbContext actual = versionTwo ? ContextV2(driver, database) : Context(driver, database);
        var target = actual.Database.GetGraphSchemaManifest().Fingerprint;
        if (!StringComparer.Ordinal.Equals(source, target))
            throw new InvalidOperationException("Generated sample artifact and finalized EF mapping fingerprints differ; regenerate and review before running the example.");
        return new(source, target);
    }

    public static async Task<GraphMigrationResult> PrepareSchemaAsync(IDriver driver, string database, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var db = Context(driver, database);
        await db.Database.InitializeGraphMigrationsAsync(cancellationToken: cancellationToken);
        return await db.Database.ApplyGraphMigrationAsync(OrganizationPlans.Schema(), cancellationToken: cancellationToken);
    }

    public static async Task<OrganizationResult> RunAsync(IDriver driver, string database, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fingerprints = GetSchemaFingerprints(driver, database);
        var personId = Guid.NewGuid();
        var organizationId = Guid.NewGuid();
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var seedAcknowledged = false;
        try
        {
            var added = await SeedAsync(driver, database, personId, organizationId, firstId, secondId, cancellationToken);
            seedAcknowledged = true;
            await using var edit = Context(driver, database);
            var person = await edit.PersonSet.Where(person => person.Id == personId).SingleAsync(cancellationToken);
            var organization = await edit.OrganizationSet.Where(organization => organization.Id == organizationId).SingleAsync(cancellationToken);
            var query = edit.EmploymentSet.Where(employment => employment.PersonId == personId);
            var cypher = query.ToQueryString();
            var edges = await query.ToListAsync(cancellationToken);
            var first = edges.Single(employment => employment.EmploymentId == firstId);
            var second = edges.Single(employment => employment.EmploymentId == secondId);
            var fixup = edges.All(employment => ReferenceEquals(employment.Person, person) && ReferenceEquals(employment.Organization, organization));
            if (edges.Count != 2 || !fixup || first.ValidTo is not null || second.ValidTo != new DateOnly(2025, 12, 31))
                throw new InvalidOperationException("Generated relationship identity, calendar dates or EF reference fixup disagreed with the sample.");
            first.Role = "Principal Architect";
            first.ValidTo = new DateOnly(2026, 1, 31);
            var updated = await edit.SaveChangesAsync(cancellationToken);

            await using var verify = Context(driver, database);
            var changed = await verify.EmploymentSet.Where(employment => employment.EmploymentId == firstId).SingleAsync(cancellationToken);
            if (changed.Role != "Principal Architect" || changed.ValidFrom != new DateOnly(2025, 1, 1) || changed.ValidTo != new DateOnly(2026, 1, 31))
                throw new InvalidOperationException("The generated DateOnly converter or tracked edge update did not round trip.");
            changed.ValidTo = null;
            updated += await verify.SaveChangesAsync(cancellationToken);

            // Deliberate untracked native observations: no application writes bypass EF.
            var wire = await verify.Database.QueryCypherAsync(
                "MATCH (p:OrganizationSamplePerson {id:$person})-[r:ORGANIZATION_SAMPLE_WORKS_FOR]->(o:OrganizationSampleOrganization {id:$organization}) "
                + "RETURN r.employmentId AS id,r.role AS role,r.validFrom AS start,r.validTo AS end,r.personId AS personSlot,r.organizationId AS organizationSlot",
                new Dictionary<string, object?> { ["person"] = personId.ToString("D"), ["organization"] = organizationId.ToString("D") },
                record => new WireEmployment(record["id"].As<string>(), record["role"].As<string>(), record["start"].As<string>(),
                    record["end"], record["personSlot"], record["organizationSlot"]), maxRows: 2, cancellationToken: cancellationToken);
            var firstWire = wire.Single(edge => edge.Id == firstId.ToString("D"));
            if (wire.Count != 2 || firstWire.Start != "2025-01-01" || firstWire.End is not null
                || firstWire.Role != "Principal Architect" || wire.Any(edge => edge.PersonSlot is not null || edge.OrganizationSlot is not null))
                throw new InvalidOperationException("Stored graph direction, calendar wire values or endpoint-property lowering disagreed with the generated mapping.");
            var joinNodes = await verify.Database.QueryCypherAsync(
                "MATCH (n:Employment) WHERE n.employmentId IN $ids OR n.EmploymentId IN $ids RETURN count(n) AS count",
                new Dictionary<string, object?> { ["ids"] = new[] { firstId.ToString("D"), secondId.ToString("D") } },
                record => record["count"].As<long>(), maxRows: 1, cancellationToken: cancellationToken);
            if (joinNodes.Single() != 0) throw new InvalidOperationException("Employment was represented by a synthetic join node.");
            cancellationToken.ThrowIfCancellationRequested();
            // Once cleanup begins, finish it even if the caller cancels; do not turn an acknowledged delete into an apparent failure.
            var deleted = await CleanupAsync(driver, database, personId, organizationId, CancellationToken.None);
            return new(added, updated, deleted, wire.Count, joinNodes.Single(), true, fixup, cypher, fingerprints.Source, fingerprints.Target);
        }
        catch (Exception error)
        {
            error.Data["OrganizationSample.PersonId"] = personId;
            error.Data["OrganizationSample.OrganizationId"] = organizationId;
            error.Data["OrganizationSample.FirstEmploymentId"] = firstId;
            error.Data["OrganizationSample.SecondEmploymentId"] = secondId;
            // A failed seed acknowledgement may already have committed. Expose exact IDs and leave resolution explicit.
            if (seedAcknowledged)
            {
                try { await CleanupAsync(driver, database, personId, organizationId, CancellationToken.None); }
                catch (Exception cleanupError) { error.Data["OrganizationSample.CleanupFailure"] = cleanupError; }
            }
            throw;
        }
    }

    /// <summary>Seeds the two fixed migration-example IDs. Existing data causes a normal EF uniqueness failure; nothing is overwritten.</summary>
    public static Task<int> SeedMigrationDemoAsync(IDriver driver, string database, CancellationToken cancellationToken = default)
        => SeedAsync(driver, database, OrganizationPlans.DemoPersonId, OrganizationPlans.DemoOrganizationId,
            OrganizationPlans.FirstEmploymentId, OrganizationPlans.SecondEmploymentId, cancellationToken);

    public static async Task<GraphMigrationResult> ApplyVersionTwoAsync(IDriver driver, string database, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var db = Context(driver, database);
        return await db.Database.ApplyGraphMigrationAsync(OrganizationPlans.VersionTwo(), cancellationToken: cancellationToken);
    }

    /// <summary>Checks the journal before seeding and recognizes an exact seed committed by an interrupted prior invocation.</summary>
    public static async Task<GraphMigrationResult> RunMigrationDemoAsync(IDriver driver, string database, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var db = Context(driver, database);
        var plan = OrganizationPlans.VersionTwo();
        var validation = await db.Database.ValidateGraphMigrationAsync(plan, cancellationToken: cancellationToken);
        if (!validation.IsInitialized) throw new InvalidOperationException("Run --prepare-schema explicitly before the migration example.");
        if (validation.OwnerToken is not null)
            throw new InvalidOperationException($"Migration owner {validation.OwnerToken}, generation {validation.OwnerGeneration} remains active. Stop/fence the prior process before explicit recovery; this example never steals ownership.");
        if (validation.AlreadyApplied) return await db.Database.ApplyGraphMigrationAsync(plan, cancellationToken: cancellationToken);
        if (!(await db.Database.ValidateGraphMigrationAsync(OrganizationPlans.Schema(), cancellationToken: cancellationToken)).AlreadyApplied)
            throw new InvalidOperationException("The exact Organization schema migration must be completed before any demo seed writes.");

        var existingSeed = await HasExactMigrationSeedAsync(db, cancellationToken);
        // Validate the same predecessor with a read-only assertion against the currently observed seed count.
        // The real backfill precondition necessarily fails while its future input is absent; do not hide unrelated journal errors.
        var preflight = new GraphMigrationPlan("organization_sample_seed_preflight", OrganizationPlans.SchemaId,
            [GraphMigrationStep.AssertPrecondition("observed-first-employment",
                new("RELATIONSHIP", "ORGANIZATION_SAMPLE_WORKS_FOR", new Dictionary<string, object?>
                    { ["employmentId"] = OrganizationPlans.FirstEmploymentId.ToString("D") }, maxRows: 1), existingSeed ? 1 : 0)],
            GraphMigrationRollbackPolicy.NoRollback("Read-only sample seed prerequisite check. This preflight plan is never applied."));
        var readiness = await db.Database.ValidateGraphMigrationAsync(preflight, cancellationToken: cancellationToken);
        if (!readiness.CanApply)
            throw new InvalidOperationException("Sample seed prerequisite failed: " + string.Join("; ", readiness.Issues));
        if (existingSeed && !validation.CanApply)
            throw new InvalidOperationException("Existing sample data does not satisfy the migration precondition: " + string.Join("; ", validation.Issues));
        if (!existingSeed) await SeedMigrationDemoAsync(driver, database, cancellationToken);
        return await db.Database.ApplyGraphMigrationAsync(plan, cancellationToken: cancellationToken);
    }

    private static async Task<bool> HasExactMigrationSeedAsync(V1.OrganizationContext db, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var people = await db.PersonSet.Where(person => person.Id == OrganizationPlans.DemoPersonId).ToListAsync(cancellationToken);
        var organizations = await db.OrganizationSet.Where(organization => organization.Id == OrganizationPlans.DemoOrganizationId).ToListAsync(cancellationToken);
        var edges = await db.EmploymentSet.Where(edge => edge.EmploymentId == OrganizationPlans.FirstEmploymentId
            || edge.EmploymentId == OrganizationPlans.SecondEmploymentId).ToListAsync(cancellationToken);
        if (people.Count == 0 && organizations.Count == 0 && edges.Count == 0) return false;
        var first = edges.SingleOrDefault(edge => edge.EmploymentId == OrganizationPlans.FirstEmploymentId);
        var second = edges.SingleOrDefault(edge => edge.EmploymentId == OrganizationPlans.SecondEmploymentId);
        if (people.Count != 1 || people[0].Name != "Alice" || organizations.Count != 1 || organizations[0].Name != "Acme"
            || edges.Count != 2 || first is null || second is null
            || edges.Any(edge => edge.PersonId != OrganizationPlans.DemoPersonId || edge.OrganizationId != OrganizationPlans.DemoOrganizationId)
            || first.Role != "Architect" || first.ValidFrom != new DateOnly(2025, 1, 1) || first.ValidTo is not null
            || second.Role != "Advisor" || second.ValidFrom != new DateOnly(2025, 2, 1) || second.ValidTo != new DateOnly(2025, 12, 31))
            throw new InvalidOperationException("Fixed sample IDs contain partial or conflicting data. No overwrite/reseed is attempted; inspect the four IDs in OrganizationPlans.");
        return true;
    }

    public static async Task<IReadOnlyList<string>> ReadVersionTwoAsync(IDriver driver, string database, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var db = ContextV2(driver, database);
        var edges = await db.EmploymentSet.Where(employment => employment.PersonId == OrganizationPlans.DemoPersonId).ToListAsync(cancellationToken);
        if (edges.Count != 2) throw new InvalidOperationException("The migration example requires its two fixed employment IDs.");
        var first = edges.Single(edge => edge.EmploymentId == OrganizationPlans.FirstEmploymentId);
        var second = edges.Single(edge => edge.EmploymentId == OrganizationPlans.SecondEmploymentId);
        if (first.Position != "Architect" || first.Department != "Engineering" || first.ValidTo is not null
            || second.Position != "Advisor" || second.Department != "Advisory" || second.ValidTo != new DateOnly(2025, 12, 31))
            throw new InvalidOperationException("Version-two data did not preserve roles/dates and apply the reviewed department backfill.");
        return [first.Position + " / " + first.Department, second.Position + " / " + second.Department];
    }

    private static async Task<int> SeedAsync(IDriver driver, string database, Guid personId, Guid organizationId, Guid firstId, Guid secondId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var db = Context(driver, database);
        var person = new V1.Person { Id = personId, Name = "Alice" };
        var organization = new V1.Organization { Id = organizationId, Name = "Acme" };
        db.AddRange(
            new V1.Employment { EmploymentId = firstId, Person = person, Organization = organization,
                PersonId = personId, OrganizationId = organizationId, Role = "Architect", ValidFrom = new DateOnly(2025, 1, 1) },
            new V1.Employment { EmploymentId = secondId, Person = person, Organization = organization,
                PersonId = personId, OrganizationId = organizationId, Role = "Advisor", ValidFrom = new DateOnly(2025, 2, 1), ValidTo = new DateOnly(2025, 12, 31) });
        return await db.SaveChangesAsync(cancellationToken);
    }

    private static async Task<int> CleanupAsync(IDriver driver, string database, Guid personId, Guid organizationId, CancellationToken cancellationToken)
    {
        await using var db = Context(driver, database);
        db.RemoveRange(await db.EmploymentSet.Where(employment => employment.PersonId == personId).ToListAsync(cancellationToken));
        db.RemoveRange(await db.PersonSet.Where(person => person.Id == personId).ToListAsync(cancellationToken));
        db.RemoveRange(await db.OrganizationSet.Where(organization => organization.Id == organizationId).ToListAsync(cancellationToken));
        return await db.SaveChangesAsync(cancellationToken);
    }

    private sealed record WireEmployment(string Id, string Role, string Start, object? End, object? PersonSlot, object? OrganizationSlot);
}
