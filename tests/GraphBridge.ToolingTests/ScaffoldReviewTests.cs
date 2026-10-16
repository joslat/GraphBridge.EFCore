using System.Text.Json;
using GraphBridge.EntityFrameworkCore.Schema;
using GraphBridge.Tooling.Generation;
using GraphBridge.Tooling.Scaffolding;

namespace GraphBridge.ToolingTests;

public sealed class ScaffoldReviewTests
{
    [Fact]
    public void Equivalent_reordered_observations_preserve_report_and_compiled_model()
    {
        var snapshot = Snapshot();
        var reversed = new GraphScaffoldSnapshot(snapshot.Capabilities, snapshot.Schema, snapshot.Labels.Reverse(), [],
            snapshot.Nodes.Reverse(), [], snapshot.SampleLimit);
        var first = GraphScaffoldReview.Compare(snapshot, Policy(), snapshot, Policy());
        var second = GraphScaffoldReview.Compare(reversed, Policy(), reversed, Policy());
        Assert.True(first.Success);
        Assert.True(first.ModelsEquivalent);
        Assert.True(first.Current.RoundTripVerified);
        Assert.Empty(first.ObservationChanges);
        Assert.Empty(first.ArtifactChanges);
        Assert.Equal(first.ToJson(), second.ToJson());
        Assert.DoesNotContain("\r", first.ToJson());
        using var document = JsonDocument.Parse(first.ToJson());
        Assert.Equal(1, document.RootElement.GetProperty("formatVersion").GetInt32());
    }

    [Fact]
    public void Requiredness_change_has_stable_semantic_path_and_distinct_model_fingerprint()
    {
        var snapshot = Snapshot();
        var report = GraphScaffoldReview.Compare(snapshot, Policy(), snapshot, Policy(requiredName: true));
        Assert.True(report.Success);
        Assert.False(report.ModelsEquivalent);
        Assert.Empty(report.ObservationChanges);
        var change = Assert.Single(report.ModelChanges!);
        Assert.Equal("/nodes/Person/properties/name/isRequired", change.Path);
        Assert.Equal("Changed", change.Kind);
        Assert.Equal("false", change.Before);
        Assert.Equal("true", change.After);
        Assert.NotEqual(report.Baseline.ModelFingerprint, report.Current.ModelFingerprint);
    }

    [Fact]
    public void Logical_renaming_changes_artifacts_but_not_storage_model()
    {
        var report = GraphScaffoldReview.Compare(Snapshot(), Policy(), Snapshot(), Policy(clrName: "RenamedPerson"));
        Assert.True(report.Success);
        Assert.True(report.ModelsEquivalent);
        Assert.NotEmpty(report.ArtifactChanges);
        Assert.NotEqual(report.Baseline.SourceFingerprint, report.Current.SourceFingerprint);
    }

    [Fact]
    public void Observed_index_state_is_separate_from_the_generated_storage_model()
    {
        var before = Snapshot();
        var after = new GraphScaffoldSnapshot(before.Capabilities,
            new(before.Schema.Constraints, [new("idx/name~review", "ONLINE", "RANGE", "NODE", ["Person"], ["name"])]),
            before.Labels, before.RelationshipTypes, before.Nodes, before.Relationships, before.SampleLimit);
        var report = GraphScaffoldReview.Compare(before, Policy(), after, Policy());
        Assert.True(report.Success);
        Assert.True(report.ModelsEquivalent);
        Assert.Contains(report.ObservationChanges, c => c.Path == "/schema/indexes/idx~1name~0review/state" && c.After == "\"ONLINE\"");
    }

    [Fact]
    public void Missing_constraint_preserves_reviewable_errors_without_claiming_model_equivalence()
    {
        var good = Snapshot();
        var bad = new GraphScaffoldSnapshot(good.Capabilities, new([], []), good.Labels, [], good.Nodes, [], good.SampleLimit);
        var report = GraphScaffoldReview.Compare(good, Policy(), bad, Policy());
        Assert.False(report.Success);
        Assert.True(report.Current.RoundTripVerified);
        Assert.Null(report.ModelsEquivalent);
        Assert.Null(report.ModelChanges);
        Assert.Null(report.Current.ModelFingerprint);
        Assert.Empty(report.Current.Files);
        var error = Assert.Single(report.Current.Diagnostics, d => d.Code == "MissingUniqueConstraint");
        Assert.Equal("Error", error.Severity);
        Assert.Contains("DTO-only", error.Action);
    }

    [Fact]
    public void Dto_only_pair_succeeds_without_a_false_tracked_equivalence_claim()
    {
        var policy = new GraphScaffoldPolicy("Example", "Browsing");
        var report = GraphScaffoldReview.Compare(Snapshot(), policy, Snapshot(), policy);
        Assert.True(report.Success);
        Assert.Null(report.ModelsEquivalent);
        Assert.Null(report.ModelChanges);
        Assert.Equal(2, report.Current.Files.Count);
    }

    [Fact]
    public void Mixed_types_include_the_conflict_and_action_without_coercing_data()
    {
        var before = Snapshot();
        var after = new GraphScaffoldSnapshot(before.Capabilities, before.Schema, before.Labels, [],
            [.. before.Nodes, new(["Person"], new Dictionary<string, GraphScaffoldValueKind>
                { ["id"] = GraphScaffoldValueKind.Integer, ["name"] = GraphScaffoldValueKind.Integer })], [], 10);
        var report = GraphScaffoldReview.Compare(before, Policy(), after, Policy());
        Assert.False(report.Success);
        Assert.Contains(report.Current.Diagnostics, d => d.Code == "MixedPropertyTypes" && d.Subject == "Person.name" && d.Action.Contains("do not migrate", StringComparison.Ordinal));
        Assert.Null(report.ModelsEquivalent);
    }

    [Fact]
    public void Conflicting_edge_direction_preserves_endpoint_review_action()
    {
        var person = Snapshot();
        var schema = new GraphSchemaObservation([.. person.Schema.Constraints,
            new("movie_id", "UNIQUENESS", "NODE", ["Movie"], ["id"]),
            new("link_id", "RELATIONSHIP_UNIQUENESS", "RELATIONSHIP", ["LINK"], ["id"])], []);
        var nodes = person.Nodes.Append(new(["Movie"], new Dictionary<string, GraphScaffoldValueKind> { ["id"] = GraphScaffoldValueKind.Integer })).ToArray();
        var payload = new Dictionary<string, GraphScaffoldValueKind> { ["id"] = GraphScaffoldValueKind.Integer };
        var before = new GraphScaffoldSnapshot(person.Capabilities, schema, ["Person", "Movie"], ["LINK"], nodes,
            [new("LINK", ["Person"], ["Movie"], payload)], 10);
        var after = new GraphScaffoldSnapshot(person.Capabilities, schema, before.Labels, before.RelationshipTypes, nodes,
            [new("LINK", ["Movie"], ["Person"], payload)], 10);
        var policy = new GraphScaffoldPolicy("Example", "Context", [new("Person", "Person", ["id"], true), new("Movie", "Movie", ["id"], true)],
            [new("LINK", "Link", "Person", "Movie", "id", true)]);
        var report = GraphScaffoldReview.Compare(before, policy, after, policy);
        Assert.True(report.Baseline.Success);
        Assert.False(report.Current.Success);
        Assert.Contains(report.Current.Diagnostics, d => d.Code == "ConflictingEndpoints" && d.Action.Contains("directed", StringComparison.Ordinal));
        Assert.Null(report.ModelsEquivalent);
    }

    internal static GraphScaffoldPolicy Policy(bool requiredName = false, string clrName = "Person") => new("Example", "PeopleContext",
        [new("Person", clrName, ["id"], true, [new("name", "Name", GraphScalarType.String, Required: requiredName)])]);

    internal static GraphScaffoldSnapshot Snapshot() => new(GraphSchemaCapabilities.Detect("2026.09.0", "community", ["25", "5"]),
        new([new("person_id", "UNIQUENESS", "NODE", ["Person"], ["id"])], []), ["Person"], [],
        [new(["Person"], new Dictionary<string, GraphScaffoldValueKind> { ["id"] = GraphScaffoldValueKind.Integer, ["name"] = GraphScaffoldValueKind.String }),
            new(["Person"], new Dictionary<string, GraphScaffoldValueKind> { ["name"] = GraphScaffoldValueKind.String, ["id"] = GraphScaffoldValueKind.Integer, ["score"] = GraphScaffoldValueKind.Float })], [], 10);
}
