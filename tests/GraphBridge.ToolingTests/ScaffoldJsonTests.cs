using System.Text.Json.Nodes;
using GraphBridge.EntityFrameworkCore.Schema;
using GraphBridge.Tooling.Generation;
using GraphBridge.Tooling.Scaffolding;

namespace GraphBridge.ToolingTests;

public sealed class ScaffoldJsonTests
{
    [Fact]
    public void Snapshot_roundtrip_is_deterministic_and_preserves_unknown_schema_kinds_without_values()
    {
        var snapshot = Snapshot();
        var json = GraphScaffoldJson.WriteSnapshot(snapshot);
        Assert.DoesNotContain('\r', json);
        Assert.Equal(json, GraphScaffoldJson.WriteSnapshot(GraphScaffoldJson.ReadSnapshot(json)));
        var decoded = GraphScaffoldJson.ReadSnapshot(json);
        Assert.Equal("FUTURE_INDEX", Assert.Single(decoded.Schema.Indexes).Type);
        Assert.Equal("FUTURE_CONSTRAINT", Assert.Single(decoded.Schema.Constraints).Type);
        Assert.Equal(GraphScaffoldValueKind.Unsupported, decoded.Nodes.Single(n => n.Labels.Contains("Person")).Properties["opaque"]);
        Assert.Null(decoded.Schema.GraphTypeSpecification);
        Assert.DoesNotContain("elementId", json);
        Assert.DoesNotContain("password", json);
        Assert.Equal(snapshot.Capabilities.AdvertisedCypherVersions, decoded.Capabilities.AdvertisedCypherVersions);
    }

    [Fact]
    public void Snapshot_order_is_canonical_but_composite_constraint_property_order_is_preserved()
    {
        var first = Snapshot();
        var second = new GraphScaffoldSnapshot(first.Capabilities, first.Schema, first.Labels.Reverse(), first.RelationshipTypes.Reverse(),
            first.Nodes.Reverse(), first.Relationships.Reverse(), first.SampleLimit);
        Assert.Equal(GraphScaffoldJson.WriteSnapshot(first), GraphScaffoldJson.WriteSnapshot(second));
        Assert.Equal(new[] { "tenant", "id" }, GraphScaffoldJson.ReadSnapshot(GraphScaffoldJson.WriteSnapshot(first)).Schema.Constraints[0].Properties);
    }

    [Fact]
    public void Policy_roundtrip_preserves_review_flags_key_order_and_conversions()
    {
        var policy = new GraphScaffoldPolicy("Example.Movies", "MoviesContext",
            [new("Person", "Person", ["tenant", "id"], true,
                [new("id", "Id", GraphScalarType.Guid, GraphScalarConversion.GuidString, true),
                    new("name", "Name", GraphScalarType.String, Ignore: true)])],
            [new("ACTED_IN", "Role", "Person", "Movie", "roleId", true,
                [new("since", "Since", GraphScalarType.DateOnly, GraphScalarConversion.DateOnlyIsoString)])]);
        var json = GraphScaffoldJson.WritePolicy(policy);
        Assert.DoesNotContain('\r', json);
        var decoded = GraphScaffoldJson.ReadPolicy(json);
        Assert.Equal(json, GraphScaffoldJson.WritePolicy(decoded));
        Assert.True(decoded.Nodes[0].ApproveRequiredKey);
        Assert.Equal(new[] { "tenant", "id" }, decoded.Nodes[0].KeyProperties);
        Assert.Equal(GraphScalarConversion.GuidString, decoded.Nodes[0].Properties[0].Conversion);
        Assert.True(decoded.Nodes[0].Properties[1].Ignore);
        Assert.Equal(GraphScalarConversion.DateOnlyIsoString, decoded.Relationships[0].Properties[0].Conversion);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("formatVersion")]
    [InlineData("schema")]
    public void Snapshot_rejects_unknown_or_duplicate_fields(string name)
    {
        var json = GraphScaffoldJson.WriteSnapshot(Snapshot());
        Assert.Throws<ArgumentException>(() => GraphScaffoldJson.ReadSnapshot(json.Insert(1, "\"" + name + "\":null,")));
    }

    [Theory]
    [InlineData("FutureType")]
    [InlineData("0")]
    [InlineData("string")]
    public void Property_kinds_require_known_exact_enum_names(string kind)
    {
        var json = GraphScaffoldJson.WriteSnapshot(Snapshot()).Replace("\"Unsupported\"", "\"" + kind + "\"", StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => GraphScaffoldJson.ReadSnapshot(json));
    }

    [Fact]
    public void Duplicate_property_names_and_nested_unknown_fields_are_rejected()
    {
        var root = JsonNode.Parse(GraphScaffoldJson.WriteSnapshot(Snapshot()))!;
        root["nodes"]![0]!["extra"] = true;
        Assert.Throws<ArgumentException>(() => GraphScaffoldJson.ReadSnapshot(root.ToJsonString()));
        var json = GraphScaffoldJson.WriteSnapshot(Snapshot()).Replace("\"opaque\": \"Unsupported\"", "\"opaque\":\"Unsupported\",\"opaque\":\"String\"", StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => GraphScaffoldJson.ReadSnapshot(json));
    }

    [Theory]
    [InlineData("{\"formatVersion\":1}")]
    [InlineData("[]")]
    [InlineData("{\"formatVersion\":1,}")]
    [InlineData("/*comment*/{}")]
    public void Malformed_or_incomplete_interchange_is_rejected(string json)
    {
        Assert.Throws<ArgumentException>(() => GraphScaffoldJson.ReadSnapshot(json));
        Assert.Throws<ArgumentException>(() => GraphScaffoldJson.ReadPolicy(json));
    }

    [Fact]
    public void Bounds_cover_bytes_nesting_strings_array_counts_and_sample_limit()
    {
        Assert.Throws<ArgumentException>(() => GraphScaffoldJson.ReadSnapshot(new string(' ', 4 * 1024 * 1024 + 1)));
        Assert.Throws<ArgumentException>(() => GraphScaffoldJson.ReadPolicy(new string('[', 33) + "0" + new string(']', 33)));
        var root = JsonNode.Parse(GraphScaffoldJson.WriteSnapshot(Snapshot()))!;
        root["capabilities"]!["edition"] = new string('e', 1025);
        Assert.Throws<ArgumentException>(() => GraphScaffoldJson.ReadSnapshot(root.ToJsonString()));
        root = JsonNode.Parse(GraphScaffoldJson.WriteSnapshot(Snapshot()))!;
        root["sampleLimit"] = 0;
        Assert.Throws<ArgumentException>(() => GraphScaffoldJson.ReadSnapshot(root.ToJsonString()));
        root = JsonNode.Parse(GraphScaffoldJson.WriteSnapshot(Snapshot()))!;
        root["nodes"] = new JsonArray(Enumerable.Range(0, 10001).Select(_ => root["nodes"]![0]!.DeepClone()).ToArray());
        Assert.Throws<ArgumentException>(() => GraphScaffoldJson.ReadSnapshot(root.ToJsonString()));
    }

    [Fact]
    public void Per_label_sample_limit_does_not_limit_total_snapshot_rows()
    {
        var snapshot = Snapshot();
        Assert.True(snapshot.Nodes.Count > snapshot.SampleLimit);
        Assert.Equal(2, GraphScaffoldJson.ReadSnapshot(GraphScaffoldJson.WriteSnapshot(snapshot)).Nodes.Count);
    }

    [Fact]
    public void Writer_and_reader_reject_combined_sample_overflow_before_generation()
    {
        var source = Snapshot();
        var oversized = new GraphScaffoldSnapshot(source.Capabilities, source.Schema, source.Labels, source.RelationshipTypes,
            Enumerable.Repeat(source.Nodes[0], 5001), Enumerable.Repeat(source.Relationships[0], 5000), 1);
        Assert.Throws<ArgumentException>(() => GraphScaffoldJson.WriteSnapshot(oversized));
        var root = JsonNode.Parse(GraphScaffoldJson.WriteSnapshot(source))!;
        var node = root["nodes"]![0]!;
        var relationship = root["relationships"]![0]!;
        root["nodes"] = new JsonArray(Enumerable.Range(0, 5001).Select(_ => node.DeepClone()).ToArray());
        root["relationships"] = new JsonArray(Enumerable.Range(0, 5000).Select(_ => relationship.DeepClone()).ToArray());
        Assert.Throws<ArgumentException>(() => GraphScaffoldJson.ReadSnapshot(root.ToJsonString()));
    }

    [Fact]
    public void Writer_preflight_bounds_aggregate_metadata_text_and_field_counts()
    {
        var source = Snapshot();
        var excessiveText = new GraphScaffoldSnapshot(source.Capabilities, source.Schema,
            Enumerable.Range(0, 1100).Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(1024, 'x')),
            source.RelationshipTypes, source.Nodes, source.Relationships, 1);
        Assert.Throws<ArgumentException>(() => GraphScaffoldJson.WriteSnapshot(excessiveText));
        var manyFields = new GraphScaffoldNodeSample(["Person"], Enumerable.Range(0, 1000)
            .ToDictionary(i => "p" + i, _ => GraphScaffoldValueKind.String));
        var excessiveFields = new GraphScaffoldSnapshot(source.Capabilities, source.Schema, source.Labels, source.RelationshipTypes,
            Enumerable.Repeat(manyFields, 66), [], 1);
        Assert.Throws<ArgumentException>(() => GraphScaffoldJson.WriteSnapshot(excessiveFields));
        Assert.Throws<ArgumentException>(() => GraphScaffoldJson.WritePolicy(new("Example", new string('c', 1025))));
    }

    [Fact]
    public void Policy_rejects_duplicate_unknown_and_numeric_enum_fields()
    {
        var json = GraphScaffoldJson.WritePolicy(Policy());
        Assert.Throws<ArgumentException>(() => GraphScaffoldJson.ReadPolicy(json.Insert(1, "\"namespace\":\"Duplicate\",")));
        Assert.Throws<ArgumentException>(() => GraphScaffoldJson.ReadPolicy(json.Replace("\"required\": true", "\"required\": true, \"unknown\": false", StringComparison.Ordinal)));
        Assert.Throws<ArgumentException>(() => GraphScaffoldJson.ReadPolicy(json.Replace("\"String\"", "0", StringComparison.Ordinal)));
    }

    internal static GraphScaffoldSnapshot Snapshot() => new(GraphSchemaCapabilities.Detect("2026.09.0", "community", ["25", "5"]),
        new([new("future", "FUTURE_CONSTRAINT", "NODE", ["Person"], ["tenant", "id"])],
            [new("future_index", "ONLINE", "FUTURE_INDEX", "NODE", ["Person"], ["id"]) ]),
        ["Person", "Movie"], ["ACTED_IN"],
        [new(["Person"], new Dictionary<string, GraphScaffoldValueKind> { ["id"] = GraphScaffoldValueKind.String, ["opaque"] = GraphScaffoldValueKind.Unsupported }),
            new(["Movie"], new Dictionary<string, GraphScaffoldValueKind> { ["title"] = GraphScaffoldValueKind.String })],
        [new("ACTED_IN", ["Person"], ["Movie"], new Dictionary<string, GraphScaffoldValueKind> { ["roles"] = GraphScaffoldValueKind.StringList }, true)], 1);

    internal static GraphScaffoldPolicy Policy() => new("Example", "MoviesContext", [new("Person", "Person", ["id"], true,
        [new("id", "Id", GraphScalarType.String, Required: true), new("opaque", "Opaque", GraphScalarType.String, Ignore: true)])]);
}
