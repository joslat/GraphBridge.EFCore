using GraphBridge.EntityFrameworkCore.Migrations;

namespace GraphBridge.Samples.OrganizationDemo;

/// <summary>Explicit reviewed plans for this isolated sample; generation and context startup never apply them.</summary>
public static class OrganizationPlans
{
    public const string SchemaId = "organization_sample_schema_v1";
    public const string PersonConstraint = "organization_sample_person_id";
    public const string OrganizationConstraint = "organization_sample_organization_id";
    public const string EmploymentConstraint = "organization_sample_employment_id";
    public static readonly Guid DemoPersonId = Guid.Parse("11000000-0000-0000-0000-000000000001");
    public static readonly Guid DemoOrganizationId = Guid.Parse("22000000-0000-0000-0000-000000000001");
    public static readonly Guid FirstEmploymentId = Guid.Parse("33000000-0000-0000-0000-000000000001");
    public static readonly Guid SecondEmploymentId = Guid.Parse("33000000-0000-0000-0000-000000000002");

    public static GraphMigrationPlan Schema() => new(SchemaId, null,
    [
        GraphMigrationStep.CreateConstraint("person-key", new(PersonConstraint, GraphMigrationSchemaKind.UniquenessConstraint,
            "NODE", "OrganizationSamplePerson", ["id"])),
        GraphMigrationStep.CreateConstraint("organization-key", new(OrganizationConstraint, GraphMigrationSchemaKind.UniquenessConstraint,
            "NODE", "OrganizationSampleOrganization", ["id"])),
        GraphMigrationStep.CreateConstraint("employment-key", new(EmploymentConstraint, GraphMigrationSchemaKind.UniquenessConstraint,
            "RELATIONSHIP", "ORGANIZATION_SAMPLE_WORKS_FOR", ["employmentId"]))
    ], GraphMigrationRollbackPolicy.NoRollback("Explicit disposable sample schema. Previously completed schema steps are retained after failure."));

    public static GraphMigrationPlan VersionTwo()
    {
        var first = Employment(FirstEmploymentId);
        var second = Employment(SecondEmploymentId);
        return new("organization_sample_employment_v2", SchemaId,
        [
            GraphMigrationStep.Backfill("first-department", first, "department", "Engineering", 1),
            GraphMigrationStep.Backfill("second-department", second, "department", "Advisory", 1),
            GraphMigrationStep.RenameProperty("first-position", first, "role", "position", 1,
                "Reviewed removal of role after copying its value to absent position, for one exact sample employment ID."),
            GraphMigrationStep.RenameProperty("second-position", second, "role", "position", 1,
                "Reviewed removal of role after copying its value to absent position, for one exact sample employment ID.")
        ], GraphMigrationRollbackPolicy.NoRollback("One-way sample backfill and rename. Each step commits separately; stop the prior worker before explicit recovery."));
    }

    private static GraphMigrationDataScope Employment(Guid id)
        => new("RELATIONSHIP", "ORGANIZATION_SAMPLE_WORKS_FOR",
            new Dictionary<string, object?> { ["employmentId"] = id.ToString("D") }, maxRows: 1);
}
