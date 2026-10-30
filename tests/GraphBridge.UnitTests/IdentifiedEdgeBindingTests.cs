using GraphBridge.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Neo4j.Driver;

namespace GraphBridge.UnitTests;

public sealed class IdentifiedEdgeBindingTests : IDisposable
{
    private readonly IDriver _driver = ProviderTestDriver.Create();
    public void Dispose() => Assert.Empty(((ProviderTestDriver)(object)_driver).Calls);

    [Fact]
    public void Identified_binding_uses_final_model_key_roles_payload_and_immutable_cache()
    {
        using var context = Context(Configure);
        var type = context.Model.FindEntityType(typeof(Event))!;
        var compiler = context.GetService<GraphBindingCompiler>();
        var binding = compiler.GetIdentifiedEdge(type);
        Assert.Same(type, binding.EntityType);
        Assert.Equal(GraphStorageKind.IdentifiedEdge, compiler.GetStorageKind(type));
        Assert.Equal("event_key", binding.KeyProperty.Name);
        Assert.Same(type.FindPrimaryKey()!.Properties[0], binding.KeyProperty.Property);
        Assert.Equal("source_key", Assert.Single(binding.Source.PrincipalKeyProperties).Name);
        Assert.Equal("target_key", Assert.Single(binding.Target.PrincipalKeyProperties).Name);
        Assert.DoesNotContain(binding.PayloadProperties, p => p.Property.IsPrimaryKey() || p.Property.IsForeignKey());
        Assert.Equal(new[] { "Note", "Role", "Token", "Version" }, binding.PayloadProperties.Select(p => p.Property.Name));
        Assert.Throws<NotSupportedException>(() => ((IList<GraphPropertyBinding>)binding.PayloadProperties).Clear());
        Assert.Contains("identified-edge", Assert.Throws<NotSupportedException>(() => compiler.GetNode(type)).Message);
        using var second = new DbContext(new DbContextOptionsBuilder().UseNeo4j(_driver).UseModel(context.Model).Options);
        Assert.Same(binding, second.GetService<GraphBindingCompiler>().GetIdentifiedEdge(second.Model.FindEntityType(typeof(Event))!));
    }

    [Fact]
    public void Nongeneric_API_copies_roles_and_requires_explicit_identity_mode()
    {
        var source = new[] { nameof(Event.SourceId) };
        using var context = Context(model =>
        {
            Configure(model);
            model.Entity(typeof(Event)).ToGraphIdentifiedRelationship("EVENT", nameof(Event.Id), source, [nameof(Event.TargetId)]);
            source[0] = "changed after mapping";
        });
        var binding = Binding(context);
        Assert.Equal(nameof(Event.SourceId), Assert.Single(binding.Source.ForeignKeyProperties).Property.Name);
        var mutable = new ModelBuilder();
        var pair = mutable.Entity<Event>().ToGraphRelationship("PAIR", ["SourceId"], ["TargetId"]);
        Assert.Throws<InvalidOperationException>(() => pair.ToGraphIdentifiedRelationship("EVENT", "Id", ["SourceId"], ["TargetId"]));
        var other = new ModelBuilder().Entity<Event>().ToGraphIdentifiedRelationship("EVENT", "Id", ["SourceId"], ["TargetId"]);
        Assert.Throws<InvalidOperationException>(() => other.ToGraphRelationship("PAIR", ["SourceId"], ["TargetId"]));
    }

    [Theory]
    [InlineData("wrong-key")]
    [InlineData("composite-key")]
    [InlineData("endpoint-key")]
    [InlineData("version-key")]
    public void Identity_must_be_a_single_stable_separate_EF_primary_key(string failure)
    {
        using var context = Context(model =>
        {
            Configure(model);
            var edge = model.Entity<Event>();
            switch (failure)
            {
                case "wrong-key": edge.HasAnnotation(GraphAnnotationNames.IdentifiedEdgeKey, "Role"); break;
                case "composite-key": edge.HasKey(e => new { e.Id, e.SourceId }); break;
                case "endpoint-key": edge.HasKey(e => e.SourceId); edge.HasAnnotation(GraphAnnotationNames.IdentifiedEdgeKey, "SourceId"); break;
                default: edge.Property(e => e.Id).IsConcurrencyToken(); break;
            }
        });
        Assert.Contains("identified-edge", Assert.Throws<InvalidOperationException>(() => context.Model).Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Identified_type_owners_need_distinct_endpoints_and_cannot_overlap_pair_storage(bool pair)
    {
        using var context = Context(model =>
        {
            Configure(model);
            var second = model.Entity<OtherEvent>();
            second.HasOne<Source>().WithMany().HasForeignKey(e => e.SourceId);
            second.HasOne<Target>().WithMany().HasForeignKey(e => e.TargetId);
            if (pair)
            {
                second.HasKey(e => new { e.SourceId, e.TargetId });
                second.ToGraphRelationship("EVENT", [nameof(OtherEvent.SourceId)], [nameof(OtherEvent.TargetId)]);
            }
            else second.ToGraphIdentifiedRelationship("EVENT", nameof(OtherEvent.Id), [nameof(OtherEvent.SourceId)], [nameof(OtherEvent.TargetId)]);
        });
        var error = Assert.Throws<InvalidOperationException>(() => context.Model);
        // ADR 0056: a second owner of the type with the same endpoint types couldn't be told apart.
        Assert.Contains(pair ? "cannot be shared" : "already has an owner from", error.Message);
    }

    [Fact]
    public void Owners_of_one_type_in_opposite_directions_are_different_pairs()
    {
        // ADR 0056: from Target to Source is not the pair from Source to Target, so reads still tell them apart.
        using var context = Context(model =>
        {
            Configure(model);
            var reverse = model.Entity<OtherEvent>();
            reverse.HasOne<Target>().WithMany().HasForeignKey(e => e.SourceId);
            reverse.HasOne<Source>().WithMany().HasForeignKey(e => e.TargetId);
            reverse.Property(e => e.Id).HasGraphPropertyName("event_key");
            reverse.ToGraphIdentifiedRelationship("EVENT", nameof(OtherEvent.Id), [nameof(OtherEvent.SourceId)], [nameof(OtherEvent.TargetId)]);
        });
        Assert.Contains("(s:`Target`)-[r:`EVENT`]->(t:`Source`)", context.Set<OtherEvent>().ToQueryString());
    }

    [Theory]
    [InlineData("RELATIONSHIP_PROPERTY_UNIQUENESS")]
    [InlineData("RELATIONSHIP_KEY")]
    public void Exact_type_wide_relationship_key_schema_is_required(string type)
    {
        using var context = Context(Configure);
        var binding = Binding(context);
        var valid = new GraphSchemaConstraint("RELATIONSHIP", type, ["EVENT"], ["event_key"]);
        GraphKeyConstraintValidator.ValidateIdentifiedEdges([binding, binding], [valid]);
        foreach (var invalid in new[]
        {
            valid with { EntityType = "NODE" },
            valid with { Type = "RELATIONSHIP_PROPERTY_EXISTENCE" },
            valid with { LabelsOrTypes = ["Other"] },
            valid with { Properties = ["Id"] },
            valid with { Properties = ["event_key", "event_key"] },
            valid with { Properties = ["event_key", "SourceId"] }
        })
            Assert.Contains("relationship uniqueness", Assert.Throws<InvalidOperationException>(() =>
                GraphKeyConstraintValidator.ValidateIdentifiedEdges([binding], [invalid])).Message);
        Assert.Throws<InvalidOperationException>(() => GraphKeyConstraintValidator.ValidateIdentifiedEdges([binding], []));
    }

    [Fact]
    public void Identified_edges_cannot_supply_skip_navigation_pair_identity()
    {
        using var context = Context(model =>
        {
            model.Entity<SkipSource>().ToGraphNode("Source").HasMany(e => e.Targets).WithMany(e => e.Sources)
                .UsingEntity<Event>(r => r.HasOne<SkipTarget>().WithMany().HasForeignKey(e => e.TargetId),
                    l => l.HasOne<SkipSource>().WithMany().HasForeignKey(e => e.SourceId),
                    edge =>
                    {
                        edge.HasKey(e => e.Id);
                        edge.ToGraphIdentifiedRelationship("EVENT", nameof(Event.Id), ["SourceId"], ["TargetId"]);
                    });
            model.Entity<SkipTarget>().ToGraphNode("Target");
        });
        Assert.Contains("skip navigations", Assert.Throws<InvalidOperationException>(() => context.Model).Message);
    }

    [Theory]
    [InlineData("edge-id")]
    [InlineData("node-key")]
    [InlineData("endpoint-fk")]
    public void Custom_identity_comparers_cannot_collapse_distinct_graph_keys(string property)
    {
        using var context = Context(model =>
        {
            Configure(model);
            var comparer = new ValueComparer<Guid>((a, b) => true, value => 0, value => value);
            if (property == "edge-id") model.Entity<Event>().Property(e => e.Id).Metadata.SetValueComparer(comparer);
            else if (property == "node-key") model.Entity<Source>().Property(e => e.Id).Metadata.SetValueComparer(comparer);
            else model.Entity<Event>().Property(e => e.SourceId).Metadata.SetValueComparer(comparer);
        });
        Assert.Contains("custom key comparer", Assert.Throws<InvalidOperationException>(() => context.Model).Message);
    }

    private ProviderModelContext Context(Action<ModelBuilder> mapping) => new(_driver, mapping);
    private static IdentifiedEdgeBinding Binding(DbContext context)
        => context.GetService<GraphBindingCompiler>().GetIdentifiedEdge(context.Model.FindEntityType(typeof(Event))!);

    internal static void Configure(ModelBuilder model)
    {
        model.Entity<Source>().ToGraphNode("Source").Property(e => e.Id).HasGraphPropertyName("source_key");
        model.Entity<Target>().ToGraphNode("Target").Property(e => e.Id).HasGraphPropertyName("target_key");
        var edge = model.Entity<Event>();
        edge.HasOne<Source>().WithMany().HasForeignKey(e => e.SourceId);
        edge.HasOne<Target>().WithMany().HasForeignKey(e => e.TargetId);
        edge.ToGraphIdentifiedRelationship("EVENT", nameof(Event.Id), [nameof(Event.SourceId)], [nameof(Event.TargetId)]);
        edge.Property(e => e.Id).HasGraphPropertyName("event_key");
        edge.Property(e => e.Token).IsConcurrencyToken();
        edge.Property(e => e.Version).IsRowVersion();
    }

    public sealed class Source { public Guid Id { get; set; } }
    public sealed class Target { public Guid Id { get; set; } }
    public sealed class Event
    {
        public Guid Id { get; set; }
        public Guid SourceId { get; set; }
        public Guid TargetId { get; set; }
        public string? Role { get; set; }
        public string? Note { get; set; }
        public string? Token { get; set; }
        public long Version { get; set; }
    }
    public sealed class OtherEvent { public Guid Id { get; set; } public Guid SourceId { get; set; } public Guid TargetId { get; set; } }
    public sealed class SkipSource { public Guid Id { get; set; } public List<SkipTarget> Targets { get; } = []; }
    public sealed class SkipTarget { public Guid Id { get; set; } public List<SkipSource> Sources { get; } = []; }
}
