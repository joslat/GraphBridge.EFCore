using GraphBridge.EntityFrameworkCore.Schema;

namespace GraphBridge.UnitTests;

/// <summary>Pure schema evidence/proposal tests; finalized-model compilation is covered separately.</summary>
public sealed class GraphSchemaComparisonTests
{
    private static readonly GraphSchemaCapabilities Community = GraphSchemaCapabilities.Detect("2026.09.0", "community", ["5", "25"]);
    private static readonly GraphSchemaCapabilities Enterprise = GraphSchemaCapabilities.Detect("2026.09.0", "enterprise", ["25"]);

    [Theory]
    [InlineData("NODE_PROPERTY_UNIQUENESS", false)]
    [InlineData("UNIQUENESS", false)]
    [InlineData("NODE_KEY", true)]
    public void Exact_key_set_is_order_independent_but_uniqueness_does_not_imply_existence(string type, bool exists)
    {
        var manifest = Manifest(Node("Thing", ["tenant", "id"], [Property("tenant"), Property("id")]));
        var result = Compare(manifest, [new("key", type, "NODE", ["Thing"], ["id", "tenant"])]);
        Assert.DoesNotContain(result.Issues, value => value.Status == GraphSchemaStatus.Fail);
        Assert.Contains(result.Issues, value => value.Code == "IdentityConstraintPresent");
        Assert.Equal(exists ? 0 : 2, result.Issues.Count(value => value.Code == "PropertyExistenceApplicationEnforced"));
        Assert.DoesNotContain(result.Proposals, value => value.Kind == GraphSchemaProposalKind.UniquenessConstraint);
    }

    [Theory]
    [InlineData("NODE", "NODE_PROPERTY_EXISTENCE", "Thing", "tenant,id")]
    [InlineData("RELATIONSHIP", "RELATIONSHIP_PROPERTY_UNIQUENESS", "Thing", "tenant,id")]
    [InlineData("NODE", "NODE_PROPERTY_UNIQUENESS", "thing", "tenant,id")]
    [InlineData("NODE", "NODE_PROPERTY_UNIQUENESS", "Thing", "id")]
    [InlineData("NODE", "NODE_PROPERTY_UNIQUENESS", "Thing", "tenant,id,extra")]
    public void Wrong_schema_cannot_satisfy_required_composite_identity(string entityType, string type, string label, string properties)
    {
        var manifest = Manifest(Node("Thing", ["tenant", "id"], [Property("tenant"), Property("id")]));
        var result = Compare(manifest, [new("wrong", type, entityType, [label], properties.Split(','))]);
        Assert.Equal(GraphSchemaStatus.Fail, result.Status);
        Assert.Contains(result.Issues, value => value.Code == "MissingIdentityConstraint" && value.Status == GraphSchemaStatus.Fail);
    }

    [Theory]
    [InlineData("RELATIONSHIP_PROPERTY_UNIQUENESS")]
    [InlineData("RELATIONSHIP_UNIQUENESS")]
    [InlineData("RELATIONSHIP_KEY")]
    public void Identified_ID_requires_typewide_uniqueness_while_association_pair_has_no_synthetic_property(string type)
    {
        var manifest = new GraphSchemaManifest([], [Edge("EVENT", identified: true), Edge("LINK", identified: false)], []);
        var missing = Compare(manifest, []);
        var proposal = Assert.Single(missing.Proposals);
        Assert.Equal("EVENT", proposal.LabelOrType);
        Assert.Equal(new[] { "edge_id" }, proposal.Properties);
        Assert.Contains("FOR ()-[r:`EVENT`]-() REQUIRE (r.`edge_id`) IS UNIQUE", proposal.Cypher);
        var result = Compare(manifest, [new("edge_key", type, "RELATIONSHIP", ["EVENT"], ["edge_id"])]);
        Assert.DoesNotContain(result.Issues, value => value.Status == GraphSchemaStatus.Fail);
        Assert.Contains(result.Issues, value => value.Code == "EndpointPairApplicationEnforced");
        Assert.DoesNotContain(result.Proposals, value => value.LabelOrType == "LINK");
    }

    [Fact]
    public void Community_proposals_are_deterministic_CREATE_only_and_preserve_index_order()
    {
        var node = Node("Thing", ["tenant", "id"], [Property("tenant"), Property("id"), Property("name"), Property("rank")],
            [new(["rank", "name"], false), new(["name"], true)]);
        var result = Compare(Manifest(node), []);
        Assert.Equal(3, result.Proposals.Count);
        var required = Assert.Single(result.Proposals, value => value.IsRequired);
        Assert.EndsWith("FOR (n:`Thing`) REQUIRE (n.`id`, n.`tenant`) IS UNIQUE", required.Cypher);
        var index = Assert.Single(result.Proposals, value => value.Kind == GraphSchemaProposalKind.RangeIndex);
        Assert.EndsWith("FOR (n:`Thing`) ON (n.`rank`, n.`name`)", index.Cypher);
        Assert.All(result.Proposals, value =>
        {
            Assert.Matches("^gb_(ct|ix)_[a-f0-9]{24}$", value.Name);
            Assert.StartsWith("CREATE ", value.Cypher);
            Assert.DoesNotContain("IF NOT EXISTS", value.Cypher);
            Assert.DoesNotContain("DROP", value.Cypher);
        });
        var reordered = Node("Thing", ["id", "tenant"], node.Properties.Reverse(), node.Indexes.Reverse());
        Assert.Equal(result.Proposals.Select(value => value.Cypher), Compare(Manifest(reordered), []).Proposals.Select(value => value.Cypher));
    }

    [Fact]
    public void Enterprise_exact_key_and_types_can_pass_and_missing_hardening_is_only_advisory()
    {
        var manifest = Manifest(Node("Thing", ["id"], [Property("id")]));
        var observations = new GraphSchemaObservation(
            [new("key", "NODE_KEY", "NODE", ["Thing"], ["id"]),
             new("type", "NODE_PROPERTY_TYPE", "NODE", ["Thing"], ["id"], "STRING")], []);
        var result = GraphSchemaComparer.Compare(manifest, Enterprise, observations);
        Assert.Equal(GraphSchemaStatus.Pass, result.Status);
        Assert.Empty(result.Proposals);
        var missing = GraphSchemaComparer.Compare(manifest, Enterprise, new([], []));
        Assert.Contains(missing.Proposals, value => value.Kind == GraphSchemaProposalKind.ExistenceConstraint
            && value.Cypher.EndsWith("REQUIRE n.`id` IS NOT NULL", StringComparison.Ordinal));
        Assert.Contains(missing.Proposals, value => value.Kind == GraphSchemaProposalKind.PropertyTypeConstraint
            && value.Cypher.EndsWith("REQUIRE n.`id` IS :: STRING", StringComparison.Ordinal));
    }

    [Fact]
    public void Related_existence_or_type_conflicts_are_failures_and_never_trigger_drops()
    {
        var manifest = Manifest(Node("Thing", ["id"], [Property("id"), Property("optional", required: false)]));
        var result = Compare(manifest,
            [new("key", "NODE_PROPERTY_UNIQUENESS", "NODE", ["Thing"], ["id"]),
             new("required_optional", "NODE_PROPERTY_EXISTENCE", "NODE", ["Thing"], ["optional"]),
             new("wrong_type", "NODE_PROPERTY_TYPE", "NODE", ["Thing"], ["id"], "INTEGER"),
             new("unmapped", "NODE_PROPERTY_EXISTENCE", "NODE", ["Thing"], ["missing_property"])]);
        Assert.Equal(GraphSchemaStatus.Fail, result.Status);
        Assert.Contains(result.Issues, value => value.Code == "NullablePropertyConstrained");
        Assert.Contains(result.Issues, value => value.Code == "PropertyTypeConflict");
        Assert.Contains(result.Issues, value => value.Code == "UnmappedRequiredProperty");
        Assert.Empty(result.Proposals);
    }

    [Fact]
    public void Extra_unrelated_schema_is_advisory_even_when_identifiers_could_not_be_rendered()
    {
        var result = Compare(new GraphSchemaManifest([], [], []),
            [new("foreign constraint", "FUTURE_TYPE", "NODE", ["external label"], ["external-property"])],
            [new("lookup", "ONLINE", "LOOKUP", "NODE", null, null)]);
        Assert.Equal(GraphSchemaStatus.Warn, result.Status);
        Assert.All(result.Issues, value => Assert.Equal(GraphSchemaIssueCategory.UnmanagedSchema, value.Category));
        Assert.Empty(result.Proposals);
    }

    [Theory]
    [InlineData("DATE")]
    [InlineData("LOCAL TIME")]
    [InlineData("ZONED TIME")]
    [InlineData("LOCAL DATETIME")]
    [InlineData("ZONED DATETIME")]
    [InlineData("DURATION")]
    [InlineData("POINT")]
    [InlineData("UUID")]
    public void Recognized_incompatible_server_scalar_types_are_conflicts(string observedType)
    {
        var manifest = Manifest(Node("Thing", ["id"], [Property("id")]));
        var result = Compare(manifest,
            [new("key", "NODE_KEY", "NODE", ["Thing"], ["id"]),
             new("type", "NODE_PROPERTY_TYPE", "NODE", ["Thing"], ["id"], observedType)]);
        Assert.Contains(result.Issues, issue => issue.Code == "PropertyTypeConflict" && issue.Status == GraphSchemaStatus.Fail);
    }

    [Fact]
    public void Alternate_keys_require_exact_uniqueness_and_are_not_optional_index_intent()
    {
        var node = new GraphSchemaNode("Thing", ["id"], [Property("id"), Property("business_id")], [],
            alternateKeys: [new(["business_id"], true)]);
        var manifest = Manifest(node);
        var result = Compare(manifest, [new("key", "NODE_KEY", "NODE", ["Thing"], ["id"])]);
        Assert.Contains(result.Issues, issue => issue.Code == "MissingAlternateKeyConstraint" && issue.Status == GraphSchemaStatus.Fail);
        Assert.True(Assert.Single(result.Proposals).IsRequired);
        var covered = Compare(manifest,
            [new("key", "NODE_KEY", "NODE", ["Thing"], ["id"]),
             new("business", "NODE_PROPERTY_UNIQUENESS", "NODE", ["Thing"], ["business_id"])]);
        Assert.Contains(covered.Issues, issue => issue.Code == "AlternateKeyConstraintPresent");
        Assert.DoesNotContain(covered.Issues, issue => issue.Status == GraphSchemaStatus.Fail);
    }

    [Fact]
    public void Occupied_deterministic_name_blocks_DDL_even_if_collision_is_with_an_index()
    {
        var manifest = Manifest(Node("Thing", ["id"], [Property("id")]));
        var name = Assert.Single(Compare(manifest, []).Proposals).Name;
        var result = Compare(manifest, [], [new(name, "ONLINE", "TEXT", "NODE", ["Other"], ["text"])]);
        Assert.Contains(result.Issues, value => value.Code == "SchemaNameConflict" && value.Status == GraphSchemaStatus.Fail);
        Assert.Empty(result.Proposals);
    }

    [Fact]
    public void Existing_standalone_range_index_does_not_prove_uniqueness_and_blocks_blind_constraint_creation()
    {
        var manifest = Manifest(Node("Thing", ["id"], [Property("id")]));
        var result = Compare(manifest, [], [new("already_indexed", "ONLINE", "RANGE", "NODE", ["Thing"], ["id"])]);
        Assert.Contains(result.Issues, value => value.Code == "MissingIdentityConstraint");
        Assert.Contains(result.Issues, value => value.Code == "ExistingIndexBlocksConstraint");
        Assert.Empty(result.Proposals);
    }

    [Fact]
    public void Planned_uniqueness_owns_its_index_so_duplicate_range_DDL_is_suppressed()
    {
        var manifest = Manifest(Node("Thing", ["id"], [Property("id"), Property("name")],
            [new(["id"], false), new(["name"], false), new(["name"], true)]));
        var result = Compare(manifest, []);
        Assert.Equal(2, result.Proposals.Count);
        Assert.All(result.Proposals, value => Assert.Equal(GraphSchemaProposalKind.UniquenessConstraint, value.Kind));
        Assert.Equal(2, result.Issues.Count(value => value.Code == "ConstraintBackingIndexRequiresReview"));
    }

    [Theory]
    [InlineData("ONLINE", "rank,name", "RangeIndexPresent")]
    [InlineData("POPULATING", "rank,name", "RangeIndexNotOnline")]
    [InlineData("ONLINE", "name,rank", "MissingRangeIndex")]
    public void Optional_indexes_check_online_state_and_order(string state, string names, string expectedCode)
    {
        var manifest = Manifest(Node("Thing", ["id"], [Property("id"), Property("name"), Property("rank")], [new(["rank", "name"], false)]));
        var result = Compare(manifest, [new("key", "NODE_KEY", "NODE", ["Thing"], ["id"])],
            [new("ordered", state, "RANGE", "NODE", ["Thing"], names.Split(','))]);
        Assert.Contains(result.Issues, value => value.Code == expectedCode);
        Assert.Equal(expectedCode == "MissingRangeIndex" ? 1 : 0, result.Proposals.Count);
    }

    [Fact]
    public void Nononline_required_constraint_backing_index_is_a_failure()
    {
        var manifest = Manifest(Node("Thing", ["id"], [Property("id")]));
        var result = Compare(manifest, [new("key", "NODE_KEY", "NODE", ["Thing"], ["id"])],
            [new("key", "FAILED", "RANGE", "NODE", ["Thing"], ["id"], "key")]);
        Assert.Contains(result.Issues, value => value.Code == "ConstraintIndexNotOnline" && value.Status == GraphSchemaStatus.Fail);
        Assert.Empty(result.Proposals);
    }

    [Fact]
    public void Shared_association_type_never_receives_ambiguous_typewide_DDL()
    {
        var edge = Edge("SHARED", identified: false, properties: [Property("payload")], indexes: [new(["payload"], true)]);
        var other = new GraphSchemaRelationship("SHARED", GraphSchemaRelationshipIdentity.EndpointPair, null,
            new("OtherSource", ["id"], "Cascade"), new("OtherTarget", ["id"], "Cascade"), [Property("payload")], []);
        var manifest = new GraphSchemaManifest([], [edge, other], []);
        var result = GraphSchemaComparer.Compare(manifest, Enterprise, new([], []));
        Assert.Contains(result.Issues, value => value.Code == "SharedRelationshipType");
        Assert.Empty(result.Proposals);
    }

    [Fact]
    public void Unknown_capabilities_suppress_DDL_and_opaque_graph_types_are_not_claimed_compared()
    {
        var manifest = Manifest(Node("Thing", ["id"], [Property("id")]));
        var result = GraphSchemaComparer.Compare(manifest,
            GraphSchemaCapabilities.Detect("unknown", "unknown", []), new([], [], "opaque graph type"));
        Assert.Empty(result.Proposals);
        Assert.Contains(result.Issues, value => value.Code == "GraphTypeObservedNotCompared");
        Assert.Contains(result.Issues, value => value.Code == "ProposalCapabilityUnavailable");
    }

    [Fact]
    public void Invalid_manifest_identifiers_never_enter_DDL_text()
    {
        var manifest = Manifest(Node("Thing`) DELETE n //", ["id"], [Property("id")]));
        Assert.Throws<ArgumentException>(() => Compare(manifest, []));
    }

    private static GraphSchemaComparison Compare(GraphSchemaManifest manifest,
        IEnumerable<GraphObservedConstraint> constraints, IEnumerable<GraphObservedIndex>? indexes = null)
        => GraphSchemaComparer.Compare(manifest, Community, new(constraints, indexes ?? []));
    private static GraphSchemaManifest Manifest(GraphSchemaNode node) => new([node], [], []);
    private static GraphSchemaNode Node(string label, IEnumerable<string> keys, IEnumerable<GraphSchemaProperty> properties,
        IEnumerable<GraphSchemaIndex>? indexes = null) => new(label, keys, properties, indexes ?? []);
    private static GraphSchemaProperty Property(string name, bool required = true)
        => new(name, "STRING", required, name == "id", GraphSchemaConcurrency.None, "Never", "Save", "Save", "native");
    private static GraphSchemaRelationship Edge(string type, bool identified,
        IEnumerable<GraphSchemaProperty>? properties = null, IEnumerable<GraphSchemaIndex>? indexes = null)
        => new(type, identified ? GraphSchemaRelationshipIdentity.ApplicationId : GraphSchemaRelationshipIdentity.EndpointPair,
            identified ? "edge_id" : null, new("Source", ["id"], "Cascade"), new("Target", ["id"], "Cascade"),
            properties ?? (identified ? [Property("edge_id")] : []), indexes ?? []);
}
