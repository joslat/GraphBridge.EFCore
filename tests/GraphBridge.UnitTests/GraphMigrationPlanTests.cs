using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GraphBridge.EntityFrameworkCore.Migrations;
using GraphBridge.EntityFrameworkCore.Schema;

namespace GraphBridge.UnitTests;

public sealed class GraphMigrationPlanTests
{
    [Fact]
    public void Canonical_contract_has_explicit_version_typed_values_and_SHA256()
    {
        var plan = Plan(GraphMigrationStep.Backfill("fill", Scope(new() { ["tenant"] = 7 }), "active", true, 2));
        Assert.Equal(1, plan.FormatVersion);
        Assert.Matches("^[0-9a-f]{64}$", plan.Checksum);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(plan.CanonicalJson))).ToLowerInvariant(), plan.Checksum);
        using var json = JsonDocument.Parse(plan.CanonicalJson);
        var root = json.RootElement;
        Assert.Equal(1, root.GetProperty("formatVersion").GetInt32());
        Assert.Equal("migrate-2", root.GetProperty("id").GetString());
        Assert.Equal("migrate-1", root.GetProperty("predecessorId").GetString());
        Assert.Equal("NoRollback", root.GetProperty("rollbackPolicy").GetProperty("kind").GetString());
        var step = root.GetProperty("steps")[0];
        Assert.Equal("int64", step.GetProperty("scope").GetProperty("filters").GetProperty("tenant").GetProperty("type").GetString());
        Assert.Equal("7", step.GetProperty("scope").GetProperty("filters").GetProperty("tenant").GetProperty("value").GetString());
        Assert.Equal("boolean", step.GetProperty("value").GetProperty("type").GetString());
        Assert.True(step.GetProperty("value").GetProperty("value").GetBoolean());
        Assert.Equal(GraphMigrationStepRenderer.Render(plan.Steps[0]).Cypher, step.GetProperty("command").GetProperty("cypher").GetString());
        Assert.Equal(GraphMigrationStepRenderer.RenderPrecondition(plan.Steps[0]).Cypher, step.GetProperty("preconditionCommand").GetProperty("cypher").GetString());
    }

    [Fact]
    public void Filters_are_canonicalized_by_name_and_Int32_uses_the_Int64_wire_contract()
    {
        var first = Plan(GraphMigrationStep.Backfill("fill", Scope(new() { ["z"] = 3, ["a"] = "text" }), "score", 4, 2));
        var second = Plan(GraphMigrationStep.Backfill("fill", Scope(new() { ["a"] = "text", ["z"] = 3L }), "score", 4L, 2));
        Assert.Equal(first.CanonicalJson, second.CanonicalJson);
        Assert.Equal(first.Checksum, second.Checksum);
        var saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var french = Plan(GraphMigrationStep.Backfill("fill", Scope(), "score", 1.25, 2));
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            Assert.Equal(french.CanonicalJson, Plan(GraphMigrationStep.Backfill("fill", Scope(), "score", 1.25, 2)).CanonicalJson);
        }
        finally { CultureInfo.CurrentCulture = saved; }
    }

    [Fact]
    public void Canonical_values_distinguish_null_boolean_integer_float_and_string()
    {
        object?[] values = [null, false, 0L, 0.0, "0"];
        var checksums = values.Select(value => Plan(GraphMigrationStep.AssertPrecondition("check", Scope(new() { ["value"] = value }), 2)).Checksum);
        Assert.Equal(values.Length, checksums.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Checksum_covers_plan_identity_predecessor_step_identity_order_and_rollback_reason()
    {
        var check = GraphMigrationStep.AssertPrecondition("check", Scope(), 2);
        var fill = GraphMigrationStep.Backfill("fill", Scope(), "active", true, 2);
        var baseline = new GraphMigrationPlan("migrate-2", "migrate-1", [check, fill], NoRollback());
        GraphMigrationPlan[] changes =
        [
            new("migrate-3", "migrate-1", [check, fill], NoRollback()),
            new("migrate-2", null, [check, fill], NoRollback()),
            new("migrate-2", "migrate-0", [check, fill], NoRollback()),
            new("migrate-2", "migrate-1", [fill, check], NoRollback()),
            new("migrate-2", "migrate-1", [GraphMigrationStep.AssertPrecondition("renamed", Scope(), 2), fill], NoRollback()),
            new("migrate-2", "migrate-1", [check, fill], GraphMigrationRollbackPolicy.NoRollback("Restore a reviewed snapshot instead."))
        ];
        Assert.All(changes, changed => Assert.NotEqual(baseline.Checksum, changed.Checksum));
    }

    [Fact]
    public void Checksum_covers_data_scope_bounds_expected_count_target_value_and_destructive_intent()
    {
        var baseline = Plan(GraphMigrationStep.Backfill("step", Scope(new() { ["tenant"] = 1 }), "new", "value", 2));
        GraphMigrationStep[] changes =
        [
            GraphMigrationStep.Backfill("step", Scope(new() { ["tenant"] = 2 }), "new", "value", 2),
            GraphMigrationStep.Backfill("step", Scope(new() { ["other"] = 1 }), "new", "value", 2),
            GraphMigrationStep.Backfill("step", Scope(new() { ["tenant"] = 1 }, maxRows: 9), "new", "value", 2),
            GraphMigrationStep.Backfill("step", Scope(new() { ["tenant"] = 1 }, entityType: "RELATIONSHIP"), "new", "value", 2),
            GraphMigrationStep.Backfill("step", Scope(new() { ["tenant"] = 1 }, label: "Other"), "new", "value", 2),
            GraphMigrationStep.Backfill("step", Scope(new() { ["tenant"] = 1 }), "other", "value", 2),
            GraphMigrationStep.Backfill("step", Scope(new() { ["tenant"] = 1 }), "new", "changed", 2),
            GraphMigrationStep.Backfill("step", Scope(new() { ["tenant"] = 1 }), "new", "value", 1)
        ];
        Assert.All(changes, changed => Assert.NotEqual(baseline.Checksum, Plan(changed).Checksum));
        var rename = Plan(GraphMigrationStep.RenameProperty("step", Scope(), "old", "new", 2, "Approved rename."));
        Assert.NotEqual(rename.Checksum, Plan(GraphMigrationStep.RenameProperty("step", Scope(), "old", "new", 2, "Different intent.")).Checksum);
        Assert.NotEqual(rename.Checksum, Plan(GraphMigrationStep.RenameProperty("step", Scope(), "other", "new", 2, "Approved rename.")).Checksum);
    }

    [Fact]
    public void Checksum_preserves_ordered_schema_definition_and_create_vs_drop_semantics()
    {
        var original = Definition(properties: ["tenant", "id"]);
        var baseline = Plan(GraphMigrationStep.CreateConstraint("step", original));
        GraphMigrationStep[] changes =
        [
            GraphMigrationStep.CreateConstraint("step", Definition(name: "other", properties: ["tenant", "id"])),
            GraphMigrationStep.CreateConstraint("step", Definition(entityType: "RELATIONSHIP", properties: ["tenant", "id"])),
            GraphMigrationStep.CreateConstraint("step", Definition(label: "Other", properties: ["tenant", "id"])),
            GraphMigrationStep.CreateConstraint("step", Definition(properties: ["id", "tenant"])),
            GraphMigrationStep.CreateConstraint("step", Definition(properties: ["tenant", "other"])),
            GraphMigrationStep.CreateIndex("step", Definition(kind: GraphMigrationSchemaKind.RangeIndex, properties: ["tenant", "id"])),
            GraphMigrationStep.DropConstraint("step", original, "Reviewed obsolete identity.")
        ];
        Assert.All(changes, changed => Assert.NotEqual(baseline.Checksum, Plan(changed).Checksum));
        var textType = Plan(GraphMigrationStep.CreateConstraint("type", Definition(kind: GraphMigrationSchemaKind.PropertyTypeConstraint, propertyType: "STRING")));
        var intType = Plan(GraphMigrationStep.CreateConstraint("type", Definition(kind: GraphMigrationSchemaKind.PropertyTypeConstraint, propertyType: "INTEGER")));
        Assert.NotEqual(textType.Checksum, intType.Checksum);
    }

    [Fact]
    public void Input_collections_and_all_exposed_collections_are_immutable_copies()
    {
        string[] properties = ["tenant", "id"];
        var definition = Definition(properties: properties);
        var filters = new Dictionary<string, object?> { ["tenant"] = 7 };
        var scope = Scope(filters);
        GraphMigrationStep[] steps = [GraphMigrationStep.CreateConstraint("create", definition), GraphMigrationStep.Backfill("fill", scope, "active", true, 2)];
        var plan = new GraphMigrationPlan("migration", null, steps, NoRollback());
        var before = plan.CanonicalJson;
        properties[0] = "changed";
        filters["tenant"] = 8;
        steps[0] = GraphMigrationStep.AssertPrecondition("replacement", Scope(), 0);
        Assert.Equal("tenant", definition.Properties[0]);
        Assert.Equal(7L, scope.Filters["tenant"]);
        Assert.Equal("create", plan.Steps[0].Id);
        Assert.Equal(before, plan.CanonicalJson);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)definition.Properties)[0] = "other");
        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, object?>)scope.Filters).Add("other", 1));
        Assert.Throws<NotSupportedException>(() => ((IList<GraphMigrationStep>)plan.Steps).Clear());
        var preview = plan.Preview();
        Assert.Throws<NotSupportedException>(() => ((IList<GraphMigrationStepPreview>)preview.Steps).Clear());
        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, object?>)preview.Steps[1].Command.Parameters)["expected"] = 9L);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)preview.Steps[1].Impact.Properties).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<GraphMigrationCapability>)preview.Steps[0].RequiredCapabilities).Clear());
    }

    [Fact]
    public void Preview_contains_exact_readiness_destructive_and_no_rollback_contracts_without_database_dependencies()
    {
        var step = GraphMigrationStep.DropIndex("drop", Definition(kind: GraphMigrationSchemaKind.RangeIndex), "Obsolete search index.");
        var plan = Plan(step, GraphMigrationStep.CreateConstraint("unique", Definition()));
        var preview = plan.Preview();
        Assert.Equal(plan.Checksum, preview.Checksum);
        Assert.Equal(plan.Id, preview.Id);
        Assert.Equal(plan.PredecessorId, preview.PredecessorId);
        Assert.Equal("NoRollback", preview.RollbackPolicy.Kind);
        Assert.Equal("Obsolete search index.", preview.Steps[0].DestructiveReason);
        Assert.Contains("exactly matches", preview.Steps[0].Precondition);
        Assert.Contains("ONLINE", preview.Steps[1].Postcondition);
        Assert.Null(preview.Steps[0].PreconditionCommand);
        Assert.Equal("DROP INDEX `reviewed_schema`", preview.Steps[0].Command.Cypher);
        Assert.Equal(preview.Steps.Select(s => s.Command.Cypher), plan.Preview().Steps.Select(s => s.Command.Cypher));
    }

    [Theory]
    [InlineData("NODE", "CREATE CONSTRAINT `reviewed_schema` FOR (n:`Records`) REQUIRE (n.`tenant`, n.`id`) IS UNIQUE", GraphMigrationCapability.NodeUniqueness)]
    [InlineData("RELATIONSHIP", "CREATE CONSTRAINT `reviewed_schema` FOR ()-[r:`Records`]-() REQUIRE (r.`tenant`, r.`id`) IS UNIQUE", GraphMigrationCapability.RelationshipUniqueness)]
    public void Unique_schema_is_exact_parameter_free_and_never_masks_conflicts(string entityType, string expected, GraphMigrationCapability capability)
    {
        var step = GraphMigrationStep.CreateConstraint("create", Definition(entityType: entityType, properties: ["tenant", "id"]));
        var command = GraphMigrationStepRenderer.Render(step);
        Assert.Equal(expected, command.Cypher);
        Assert.Empty(command.Parameters);
        Assert.Equal(capability, Assert.Single(step.RequiredCapabilities));
        Assert.DoesNotContain("IF NOT EXISTS", command.Cypher);
    }

    [Fact]
    public void Range_existence_type_and_expected_definition_drop_commands_have_explicit_capabilities()
    {
        var range = GraphMigrationStep.CreateIndex("range", Definition(kind: GraphMigrationSchemaKind.RangeIndex));
        Assert.Equal("CREATE RANGE INDEX `reviewed_schema` FOR (n:`Records`) ON (n.`id`)", GraphMigrationStepRenderer.Render(range).Cypher);
        Assert.Equal(GraphMigrationCapability.RangeIndexes, Assert.Single(range.RequiredCapabilities));
        Assert.Contains("separately", range.Postcondition);
        var existence = GraphMigrationStep.CreateConstraint("exists", Definition(kind: GraphMigrationSchemaKind.ExistenceConstraint));
        Assert.Equal("CREATE CONSTRAINT `reviewed_schema` FOR (n:`Records`) REQUIRE n.`id` IS NOT NULL", GraphMigrationStepRenderer.Render(existence).Cypher);
        Assert.Equal(GraphMigrationCapability.PropertyExistence, Assert.Single(existence.RequiredCapabilities));
        var type = GraphMigrationStep.CreateConstraint("type", Definition(kind: GraphMigrationSchemaKind.PropertyTypeConstraint, propertyType: "STRING"));
        Assert.Equal("CREATE CONSTRAINT `reviewed_schema` FOR (n:`Records`) REQUIRE n.`id` IS :: STRING", GraphMigrationStepRenderer.Render(type).Cypher);
        Assert.Equal(GraphMigrationCapability.PropertyType, Assert.Single(type.RequiredCapabilities));
        Assert.Equal("DROP CONSTRAINT `reviewed_schema`", GraphMigrationStepRenderer.Render(GraphMigrationStep.DropConstraint("drop", Definition(), "Replaced after review.")).Cypher);
    }

    [Fact]
    public void Proposal_import_uses_structural_metadata_and_ignores_untrusted_Cypher()
    {
        var proposal = new GraphSchemaProposal("reviewed_schema", GraphSchemaProposalKind.UniquenessConstraint,
            "NODE", "Records", ["id"], true, "MATCH (n) DETACH DELETE n");
        var step = GraphMigrationStep.CreateConstraint("create", GraphMigrationSchemaDefinition.FromProposal(proposal));
        Assert.Equal("CREATE CONSTRAINT `reviewed_schema` FOR (n:`Records`) REQUIRE (n.`id`) IS UNIQUE", GraphMigrationStepRenderer.Render(step).Cypher);
        var type = new GraphSchemaProposal("reviewed_type", GraphSchemaProposalKind.PropertyTypeConstraint,
            "NODE", "Records", ["id"], false, "unused");
        Assert.Throws<ArgumentException>(() => GraphMigrationSchemaDefinition.FromProposal(type));
        Assert.Equal("STRING", GraphMigrationSchemaDefinition.FromProposal(type, "STRING").PropertyType);
    }

    [Fact]
    public void Rename_bounds_distinct_candidates_locks_before_fresh_checks_and_only_then_changes_properties()
    {
        var step = GraphMigrationStep.RenameProperty("rename", Scope(new() { ["tenant"] = 7 }), "old", "current", 2, "Reviewed rename.");
        var command = GraphMigrationStepRenderer.Render(step);
        const string expected = "MATCH (e:`Records`)\nWHERE coalesce(e.`tenant` = $filter0, (e.`tenant` IS NULL AND $filter0 IS NULL))\nWITH DISTINCT e LIMIT $limit\nWITH collect(e) AS rows\nFOREACH (e IN rows | SET e.`current` = e.`current`)\nWITH rows, size(rows) AS matched, size([e IN rows WHERE e:`Records` AND coalesce(properties(e).`tenant` = $filter0, (properties(e).`tenant` IS NULL AND $filter0 IS NULL)) AND properties(e).`old` IS NOT NULL AND properties(e).`current` IS NULL]) AS eligible\nFOREACH (e IN CASE WHEN matched = $expected AND matched <= $maxRows AND eligible = matched THEN rows ELSE [] END |\n  SET e.`current` = properties(e).`old`\n  REMOVE e.`old`\n)\nRETURN matched, eligible, CASE WHEN matched = $expected AND matched <= $maxRows AND eligible = matched THEN matched ELSE 0 END AS affected";
        Assert.Equal(expected, command.Cypher);
        Assert.Equal(11L, command.Parameters["limit"]);
        Assert.Equal(10L, command.Parameters["maxRows"]);
        Assert.Equal(2L, command.Parameters["expected"]);
        Assert.Equal(7L, command.Parameters["filter0"]);
        Assert.DoesNotContain("CALL", command.Cypher);
        Assert.DoesNotContain("__GraphBridge", command.Cypher);
        Assert.Contains("Concurrent scope insertions are not excluded", step.Precondition);
    }

    [Theory]
    [InlineData("NODE", "MATCH (e:`Records`)")]
    [InlineData("RELATIONSHIP", "MATCH ()-[e:`Records`]->()")]
    public void Explicit_full_scope_backfill_is_bounded_and_values_stay_in_parameters(string entityType, string pattern)
    {
        const string value = "quote' `identifier` $parameter // not executable";
        var step = GraphMigrationStep.Backfill("fill", Scope(entityType: entityType), "description", value, 2);
        var command = GraphMigrationStepRenderer.Render(step);
        Assert.StartsWith(pattern + "\nWITH DISTINCT e LIMIT $limit", command.Cypher);
        Assert.DoesNotContain(value, command.Cypher);
        Assert.Equal(value, command.Parameters["value"]);
        Assert.Contains("FOREACH (e IN rows | SET e.`description` = e.`description`)", command.Cypher);
        Assert.Contains("properties(e).`description` IS NULL", command.Cypher);
        Assert.Contains("SET e.`description` = $value", command.Cypher);
        Assert.DoesNotContain("REMOVE", command.Cypher);
    }

    [Fact]
    public void Advisory_precondition_and_assertion_commands_are_read_only_even_for_mutating_steps()
    {
        GraphMigrationStep[] steps =
        [
            GraphMigrationStep.Backfill("fill", Scope(new() { ["optional"] = null }), "value", 7, 2),
            GraphMigrationStep.RenameProperty("rename", Scope(), "old", "new", 2, "Reviewed."),
            GraphMigrationStep.AssertPrecondition("assert", Scope(), 2)
        ];
        foreach (var step in steps)
        {
            var command = GraphMigrationStepRenderer.RenderPrecondition(step);
            Assert.DoesNotContain("SET ", command.Cypher);
            Assert.DoesNotContain("REMOVE ", command.Cypher);
            Assert.DoesNotContain("FOREACH", command.Cypher);
            Assert.EndsWith("RETURN matched, eligible, 0 AS affected", command.Cypher);
            Assert.DoesNotContain("value", command.Parameters.Keys);
        }
        Assert.Equal(GraphMigrationStepRenderer.RenderPrecondition(steps[2]).Cypher, GraphMigrationStepRenderer.Render(steps[2]).Cypher);
        Assert.Throws<ArgumentException>(() => GraphMigrationStepRenderer.RenderPrecondition(GraphMigrationStep.CreateConstraint("create", Definition())));
    }

    [Theory]
    [InlineData("__GraphBridgeMigration")]
    [InlineData("__GraphBridgeMigrationControl")]
    [InlineData("__graphbridgemigrationCustom")]
    public void Journal_labels_and_types_cannot_be_targeted_by_schema_or_data(string reserved)
    {
        Assert.Throws<ArgumentException>(() => Definition(label: reserved));
        Assert.Throws<ArgumentException>(() => Scope(label: reserved));
        Assert.Throws<ArgumentException>(() => Scope(entityType: "RELATIONSHIP", label: reserved));
    }

    [Theory]
    [InlineData("gb_migration_history_key")]
    [InlineData("GB_MIGRATION_custom")]
    public void Journal_schema_namespace_is_reserved(string reserved)
        => Assert.Throws<ArgumentException>(() => Definition(name: reserved));

    [Theory]
    [InlineData("name` MATCH (n)")]
    [InlineData("invalid-name")]
    [InlineData("9bad")]
    [InlineData("")]
    public void All_interpolated_identifiers_are_validated(string unsafeName)
    {
        Assert.ThrowsAny<ArgumentException>(() => Definition(name: unsafeName));
        Assert.ThrowsAny<ArgumentException>(() => Definition(label: unsafeName));
        Assert.ThrowsAny<ArgumentException>(() => Definition(properties: [unsafeName]));
        Assert.ThrowsAny<ArgumentException>(() => Scope(new() { [unsafeName] = 1 }));
        Assert.ThrowsAny<ArgumentException>(() => GraphMigrationStep.Backfill("fill", Scope(), unsafeName, true, 2));
        Assert.ThrowsAny<ArgumentException>(() => GraphMigrationStep.RenameProperty("rename", Scope(), unsafeName, "new", 2, "Reviewed."));
        Assert.ThrowsAny<ArgumentException>(() => GraphMigrationStep.RenameProperty("rename", Scope(), "old", unsafeName, 2, "Reviewed."));
    }

    [Fact]
    public void Invalid_schema_combinations_and_kind_mismatch_fail_during_construction()
    {
        Assert.Throws<ArgumentException>(() => Definition(properties: []));
        Assert.Throws<ArgumentException>(() => Definition(properties: ["id", "id"]));
        Assert.Throws<ArgumentException>(() => Definition(entityType: "node"));
        Assert.Throws<ArgumentOutOfRangeException>(() => Definition(kind: (GraphMigrationSchemaKind)99));
        Assert.Throws<ArgumentException>(() => Definition(kind: GraphMigrationSchemaKind.ExistenceConstraint, properties: ["a", "b"]));
        Assert.Throws<ArgumentException>(() => Definition(kind: GraphMigrationSchemaKind.PropertyTypeConstraint, propertyType: "STRING | INTEGER"));
        Assert.Throws<ArgumentException>(() => Definition(propertyType: "STRING"));
        Assert.Throws<ArgumentException>(() => GraphMigrationStep.CreateIndex("index", Definition()));
        Assert.Throws<ArgumentException>(() => GraphMigrationStep.CreateConstraint("constraint", Definition(kind: GraphMigrationSchemaKind.RangeIndex)));
        Assert.Throws<ArgumentException>(() => GraphMigrationStep.DropIndex("index", Definition(), "Reviewed."));
    }

    [Fact]
    public void Explicit_finite_bounds_and_exact_counts_are_mandatory()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Scope(maxRows: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Scope(maxRows: 100001));
        Assert.Throws<ArgumentNullException>(() => new GraphMigrationDataScope("NODE", "Records", null!, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => GraphMigrationStep.AssertPrecondition("check", Scope(), -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => GraphMigrationStep.Backfill("fill", Scope(), "active", true, 11));
        Assert.Equal(0, GraphMigrationStep.AssertPrecondition("check", Scope(), 0).ExpectedCount);
        Assert.Equal(10, GraphMigrationStep.AssertPrecondition("check", Scope(), 10).ExpectedCount);
    }

    [Fact]
    public void Mutable_complex_nonfinite_and_implicit_application_values_are_rejected()
    {
        object[] unsupported = [new byte[] { 1 }, new object(), new Dictionary<string, object>(), Guid.NewGuid(), DateTime.UtcNow, 1m, double.NaN, double.PositiveInfinity];
        foreach (var value in unsupported)
        {
            Assert.Throws<ArgumentException>(() => Scope(new() { ["value"] = value }));
            Assert.Throws<ArgumentException>(() => GraphMigrationStep.Backfill("fill", Scope(), "value", value, 2));
        }
        Assert.Throws<ArgumentNullException>(() => GraphMigrationStep.Backfill("fill", Scope(), "value", null!, 2));
    }

    [Fact]
    public void Stable_unique_step_ids_predecessors_and_explicit_rollback_destructive_reasons_are_required()
    {
        var step = GraphMigrationStep.AssertPrecondition("check", Scope(), 2);
        Assert.Throws<ArgumentException>(() => new GraphMigrationPlan("same", "same", [step], NoRollback()));
        Assert.Throws<ArgumentException>(() => Plan());
        Assert.Throws<ArgumentException>(() => Plan(step, step));
        Assert.Throws<ArgumentException>(() => Plan((GraphMigrationStep)null!));
        Assert.Throws<ArgumentException>(() => new GraphMigrationPlan("unsafe id", null, [step], NoRollback()));
        Assert.Throws<ArgumentException>(() => GraphMigrationStep.AssertPrecondition(new string('a', 129), Scope(), 2));
        Assert.ThrowsAny<ArgumentException>(() => GraphMigrationRollbackPolicy.NoRollback(" "));
        Assert.ThrowsAny<ArgumentException>(() => GraphMigrationStep.DropConstraint("drop", Definition(), ""));
        Assert.ThrowsAny<ArgumentException>(() => GraphMigrationStep.DropIndex("drop", Definition(kind: GraphMigrationSchemaKind.RangeIndex), ""));
        Assert.ThrowsAny<ArgumentException>(() => GraphMigrationStep.RenameProperty("rename", Scope(), "old", "new", 2, ""));
        Assert.Throws<ArgumentException>(() => GraphMigrationStep.RenameProperty("rename", Scope(), "same", "same", 2, "Reviewed."));
    }

    private static GraphMigrationRollbackPolicy NoRollback() => GraphMigrationRollbackPolicy.NoRollback("Restore the reviewed backup if compensation is required.");
    private static GraphMigrationPlan Plan(params GraphMigrationStep[] steps) => new("migrate-2", "migrate-1", steps, NoRollback());
    private static GraphMigrationDataScope Scope(Dictionary<string, object?>? filters = null, int maxRows = 10,
        string entityType = "NODE", string label = "Records") => new(entityType, label, filters ?? [], maxRows);
    private static GraphMigrationSchemaDefinition Definition(string name = "reviewed_schema",
        GraphMigrationSchemaKind kind = GraphMigrationSchemaKind.UniquenessConstraint, string entityType = "NODE",
        string label = "Records", string[]? properties = null, string? propertyType = null)
        => new(name, kind, entityType, label, properties ?? ["id"], propertyType);
}
