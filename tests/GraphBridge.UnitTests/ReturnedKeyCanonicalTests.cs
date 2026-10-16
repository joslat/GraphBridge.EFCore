using GraphBridge.EntityFrameworkCore.Metadata;
using GraphBridge.EntityFrameworkCore.Query;
using GraphBridge.EntityFrameworkCore.Cypher;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Neo4j.Driver;

namespace GraphBridge.UnitTests;

public sealed class ReturnedKeyCanonicalTests : IDisposable
{
    private const string LowerId = "abcdef00-1234-5678-90ab-cdef00123456";
    private const string UpperId = "ABCDEF00-1234-5678-90AB-CDEF00123456";
    private readonly IDriver _driver = ProviderTestDriver.Create();
    public void Dispose() => Assert.Empty(((ProviderTestDriver)(object)_driver).Calls);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Node_full_and_selected_key_reads_require_finalized_converter_canonical_form(bool customConverter)
    {
        using var context = Context(customConverter);
        var compiler = context.GetService<GraphBindingCompiler>();
        var binding = GraphReadBinding.FromNode(compiler.GetNode(context.Model.FindEntityType(typeof(Source))!));
        var id = Slot(binding, nameof(Source.Id));
        var name = Slot(binding, nameof(Source.Name));
        var row = new Dictionary<string, object?>
        {
            [id.Column.Alias] = customConverter ? UpperId : LowerId,
            [name.Column.Alias] = "payload"
        };
        Assert.Equal(Guid.Parse(LowerId), binding.ReadRow(row)[id.Index]);
        Assert.Equal(Guid.Parse(LowerId), binding.ReadRow(row, [id])[id.Index]);
        row[id.Column.Alias] = customConverter ? LowerId : UpperId;
        Assert.Contains("canonical store representation", Assert.Throws<InvalidOperationException>(() => binding.ReadRow(row)).Message);
        Assert.Contains("canonical store representation", Assert.Throws<InvalidOperationException>(() => binding.ReadRow(row, [id])).Message);
        Assert.Empty(context.ChangeTracker.Entries());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Association_hidden_endpoint_keys_validate_inherited_converters_for_full_and_scalar_reads(bool customConverter)
    {
        using var context = Context(customConverter);
        var binding = GraphReadBinding.FromAssociation(context.GetService<GraphBindingCompiler>()
            .GetAssociation(context.Model.FindEntityType(typeof(Edge))!));
        var source = Slot(binding, nameof(Edge.SourceId));
        var target = Slot(binding, nameof(Edge.TargetId));
        var role = Slot(binding, nameof(Edge.Role));
        var row = new Dictionary<string, object?>
        {
            [source.Column.Alias] = customConverter ? UpperId : LowerId,
            [target.Column.Alias] = LowerId,
            [role.Column.Alias] = "payload",
            [CypherQueryPlan.AssociationCountAlias] = 1L
        };
        Assert.Equal(Guid.Parse(LowerId), binding.ReadRow(row)[source.Index]);
        Assert.Equal("payload", binding.ReadRow(row, [role])[role.Index]);
        row[source.Column.Alias] = customConverter ? LowerId : UpperId;
        Assert.Contains("canonical store representation", Assert.Throws<InvalidOperationException>(() => binding.ReadRow(row)).Message);
        Assert.Contains("canonical store representation", Assert.Throws<InvalidOperationException>(() => binding.ReadRow(row, [role])).Message);
        row[source.Column.Alias] = customConverter ? UpperId : LowerId;
        row[target.Column.Alias] = UpperId;
        Assert.Contains("canonical store representation", Assert.Throws<InvalidOperationException>(() => binding.ReadRow(row, [role])).Message);
        Assert.Empty(context.ChangeTracker.Entries());
    }

    [Fact]
    public void Node_payload_scalar_projection_keeps_existing_shape_without_hidden_keys()
    {
        using var context = Context(false);
        var binding = GraphReadBinding.FromNode(context.GetService<GraphBindingCompiler>().GetNode(context.Model.FindEntityType(typeof(Source))!));
        var name = Slot(binding, nameof(Source.Name));
        Assert.Equal(name.Column, Assert.Single(binding.CreatePlan([name]).Columns));
        Assert.Equal("payload", binding.ReadRow(new Dictionary<string, object?> { [name.Column.Alias] = "payload" }, [name])[name.Index]);
    }

    [Fact]
    public void Canonical_string_node_key_retains_existing_empty_string_contract()
    {
        using var context = new ProviderModelContext(_driver, model => model.Entity<StringNode>().ToGraphNode("StringKeyNode"));
        var binding = GraphReadBinding.FromNode(context.GetService<GraphBindingCompiler>().GetNode(context.Model.FindEntityType(typeof(StringNode))!));
        var id = Slot(binding, nameof(StringNode.Id));
        Assert.Equal("", binding.ReadRow(new Dictionary<string, object?> { [id.Column.Alias] = "" })[id.Index]);
    }

    private ProviderModelContext Context(bool customConverter) => new(_driver, model =>
    {
        var source = model.Entity<Source>();
        source.ToGraphNode("CanonicalSource");
        source.Property(e => e.Id).HasGraphPropertyName("source_key").ValueGeneratedNever();
        if (customConverter)
            source.Property(e => e.Id).HasConversion(v => v.ToString("D").ToUpperInvariant(), v => Guid.Parse(v));
        model.Entity<Target>().ToGraphNode("CanonicalTarget").Property(e => e.Id).HasGraphPropertyName("target_key").ValueGeneratedNever();
        var edge = model.Entity<Edge>();
        edge.HasKey(e => new { e.SourceId, e.TargetId });
        edge.HasOne<Source>().WithMany().HasForeignKey(e => e.SourceId);
        edge.HasOne<Target>().WithMany().HasForeignKey(e => e.TargetId);
        edge.ToGraphRelationship("CANONICAL_ASSOCIATION", [nameof(Edge.SourceId)], [nameof(Edge.TargetId)]);
    });

    private static GraphReadProperty Slot(GraphReadBinding binding, string name)
        => binding.Properties.Single(p => p.ModelProperty.Property.Name == name);
    public sealed class Source { public Guid Id { get; set; } public string? Name { get; set; } }
    public sealed class Target { public Guid Id { get; set; } }
    public sealed class Edge { public Guid SourceId { get; set; } public Guid TargetId { get; set; } public string? Role { get; set; } }
    public sealed class StringNode { public string Id { get; set; } = ""; }
}
