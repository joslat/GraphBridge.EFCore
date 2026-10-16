using GraphBridge.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Neo4j.Driver;

namespace GraphBridge.UnitTests;

/// <summary>Checks schema descriptions against finalized EF bindings; no schema or driver calls occur.</summary>
public sealed class GraphKeyConstraintTests : IDisposable
{
    private readonly IDriver _driver = ProviderTestDriver.Create();
    public void Dispose() => Assert.Empty(((ProviderTestDriver)(object)_driver).Calls);

    [Theory]
    [InlineData("NODE_PROPERTY_UNIQUENESS")]
    [InlineData("UNIQUENESS")]
    [InlineData("NODE_KEY")]
    public void Exact_composite_store_key_constraint_accepts_either_supported_type_in_any_property_order(string type)
    {
        using var context = Context();
        var binding = Binding<CompositeNode>(context);
        Assert.Same(context.Model.FindEntityType(typeof(CompositeNode)), binding.EntityType);

        GraphKeyConstraintValidator.Validate([binding],
            [new GraphSchemaConstraint("NODE", type, ["CompositeEndpoint"], ["node_id", "partition_code"])]);
    }

    [Fact]
    public void Relationship_constraint_cannot_cover_a_node_endpoint()
    {
        using var context = Context();
        var constraint = CompositeConstraint() with { EntityType = "RELATIONSHIP" };
        Reject(Binding<CompositeNode>(context), [constraint]);
    }

    [Fact]
    public void Property_existence_constraint_does_not_establish_key_uniqueness()
    {
        using var context = Context();
        var constraint = CompositeConstraint() with { Type = "NODE_PROPERTY_EXISTENCE" };
        Reject(Binding<CompositeNode>(context), [constraint]);
    }

    [Fact]
    public void Label_match_is_exact_and_ordinal_not_a_membership_or_case_insensitive_check()
    {
        using var context = Context();
        var binding = Binding<CompositeNode>(context);
        Reject(binding, [CompositeConstraint() with { LabelsOrTypes = ["compositeEndpoint"] }]);
        Reject(binding, [CompositeConstraint() with { LabelsOrTypes = ["CompositeEndpoint", "ExtraLabel"] }]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Constraint_property_subset_or_superset_does_not_cover_the_exact_EF_key(bool extraProperty)
    {
        using var context = Context();
        var properties = extraProperty
            ? new[] { "partition_code", "node_id", "Name" }
            : new[] { "node_id" };
        Reject(Binding<CompositeNode>(context), [CompositeConstraint() with { Properties = properties }]);
    }

    [Fact]
    public void Separate_single_property_constraints_do_not_substitute_for_the_composite_key_constraint()
    {
        using var context = Context();
        Reject(Binding<CompositeNode>(context),
        [
            CompositeConstraint() with { Properties = ["partition_code"] },
            CompositeConstraint() with { Properties = ["node_id"] }
        ]);
    }

    [Fact]
    public void Constraint_uses_mapped_store_property_names_not_CLR_property_names()
    {
        using var context = Context();
        Reject(Binding<CompositeNode>(context),
            [CompositeConstraint() with { Properties = [nameof(CompositeNode.Partition), nameof(CompositeNode.Id)] }]);
    }

    [Fact]
    public void Every_required_endpoint_needs_coverage_and_duplicate_binding_references_are_harmless()
    {
        using var context = Context();
        var source = Binding<CompositeNode>(context);
        var target = Binding<TargetNode>(context);
        var missing = Assert.Throws<InvalidOperationException>(() =>
            GraphKeyConstraintValidator.Validate([source, target, source], [CompositeConstraint()]));
        Assert.Contains(target.Label, missing.Message);

        GraphKeyConstraintValidator.Validate([source, target, source, target],
        [
            CompositeConstraint(),
            new GraphSchemaConstraint("NODE", "NODE_PROPERTY_UNIQUENESS", ["TargetEndpoint"], ["target_id"]),
            new GraphSchemaConstraint("RELATIONSHIP", "RELATIONSHIP_PROPERTY_EXISTENCE", ["Unrelated"], ["other"])
        ]);
    }

    [Fact]
    public void Repeated_names_in_a_malformed_constraint_description_do_not_establish_the_key()
    {
        using var context = Context();
        Reject(Binding<CompositeNode>(context),
            [CompositeConstraint() with { Properties = ["partition_code", "node_id", "node_id"] }]);
    }

    private ProviderModelContext Context() => new(_driver, Configure);
    private static NodeBinding Binding<T>(ProviderModelContext context)
        => context.GetService<GraphBindingCompiler>().GetNode(context.Model.FindEntityType(typeof(T))!);
    private static GraphSchemaConstraint CompositeConstraint()
        => new("NODE", "NODE_PROPERTY_UNIQUENESS", ["CompositeEndpoint"], ["partition_code", "node_id"]);
    private static void Reject(NodeBinding binding, GraphSchemaConstraint[] constraints)
    {
        var error = Assert.Throws<InvalidOperationException>(() => GraphKeyConstraintValidator.Validate([binding], constraints));
        Assert.Contains(binding.Label, error.Message);
    }

    private static void Configure(ModelBuilder model)
    {
        var composite = model.Entity<CompositeNode>().ToGraphNode("CompositeEndpoint");
        composite.HasKey(e => new { e.Partition, e.Id });
        composite.Property(e => e.Partition).HasGraphPropertyName("partition_code");
        composite.Property(e => e.Id).HasGraphPropertyName("node_id");
        model.Entity<TargetNode>().ToGraphNode("TargetEndpoint").Property(e => e.Id).HasGraphPropertyName("target_id");
    }

    public sealed class CompositeNode
    {
        public int Partition { get; set; }
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
    }

    public sealed class TargetNode { public Guid Id { get; set; } }
}
