using GraphBridge.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Neo4j.Driver;

namespace GraphBridge.UnitTests;

public sealed class AssociationBindingTests : IDisposable
{
    private readonly IDriver _driver = ProviderTestDriver.Create();
    public void Dispose() => Assert.Empty(((ProviderTestDriver)(object)_driver).Calls);

    [Fact]
    public void Explicit_binding_preserves_final_metadata_direction_payload_names_and_immutable_cached_projection()
    {
        Action<ModelBuilder> mapping = b =>
        {
            var join = Configure(b);
            join.ToGraphRelationship("ACTED_IN", [nameof(Credit.ActorId)], [nameof(Credit.FilmId)]);
            join.Property(e => e.Role).HasGraphPropertyName("role");
            b.Entity<Actor>().Property(e => e.Id).HasGraphPropertyName("actorId");
        };
        using var context = Context(mapping);
        var compiler = context.GetService<GraphBindingCompiler>();
        var type = context.Model.FindEntityType(typeof(Credit))!;
        var edge = compiler.GetAssociation(type);
        Assert.Same(type, edge.EntityType);
        Assert.Equal(GraphStorageKind.AssociationEdge, compiler.GetStorageKind(type));
        Assert.Equal(GraphStorageKind.Node, compiler.GetStorageKind(edge.Source.Node.EntityType));
        Assert.Equal("ACTED_IN", edge.RelationshipType);
        Assert.Equal(typeof(Actor), edge.Source.Node.EntityType.ClrType);
        Assert.Equal(typeof(Film), edge.Target.Node.EntityType.ClrType);
        Assert.Same(edge.Source.Node, compiler.GetNode(context.Model.FindEntityType(typeof(Actor))!));
        Assert.Same(type.GetForeignKeys().Single(f => f.PrincipalEntityType.ClrType == typeof(Actor)), edge.Source.ForeignKey);
        Assert.Equal("ActorId", Assert.Single(edge.Source.ForeignKeyProperties).Property.Name);
        Assert.Equal("actorId", Assert.Single(edge.Source.PrincipalKeyProperties).Name);
        Assert.Equal("role", Assert.Single(edge.PayloadProperties).Name);
        Assert.Equal(new[] { "ActorId", "FilmId" }, edge.KeyProperties.Select(p => p.Name));
        Assert.Throws<NotSupportedException>(() => ((IList<IProperty>)edge.KeyProperties).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<GraphPropertyBinding>)edge.PayloadProperties).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<GraphPropertyBinding>)edge.Source.ForeignKeyProperties).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<GraphPropertyBinding>)edge.Target.PrincipalKeyProperties).Clear());
        // The binding cache is per finalized model; EF's bounded model cache may evict between contexts.
        using var second = new DbContext(new DbContextOptionsBuilder().UseNeo4j(_driver).UseModel(context.Model).Options);
        Assert.Same(edge, second.GetService<GraphBindingCompiler>().GetAssociation(second.Model.FindEntityType(typeof(Credit))!));
        Assert.Contains("mapped as a node", Assert.Throws<InvalidOperationException>(() => compiler.GetAssociation(edge.Source.Node.EntityType)).Message);
    }

    [Fact]
    public void Explicit_role_order_determines_direction_including_self_referencing_associations()
    {
        using var context = Context(b =>
        {
            b.Entity<Actor>().ToGraphNode("Actor");
            var join = b.Entity<Credit>();
            join.HasKey(e => new { e.ActorId, e.FilmId });
            join.HasOne<Actor>().WithMany().HasForeignKey(e => e.ActorId);
            join.HasOne<Actor>().WithMany().HasForeignKey(e => e.FilmId);
            join.ToGraphRelationship("KNOWS", [nameof(Credit.FilmId)], [nameof(Credit.ActorId)]);
        });
        var edge = Binding<Credit>(context);
        Assert.Same(edge.Source.Node, edge.Target.Node);
        Assert.Equal(nameof(Credit.FilmId), Assert.Single(edge.Source.ForeignKeyProperties).Property.Name);
        Assert.Equal(nameof(Credit.ActorId), Assert.Single(edge.Target.ForeignKeyProperties).Property.Name);
    }

    [Fact]
    public void Composite_endpoint_slots_follow_declared_FK_and_principal_key_orders()
    {
        using var context = Context(b => ConfigureComposite(b));
        var edge = Binding<CompositeCredit>(context);
        Assert.Equal(new[] { "SourceTenant", "SourceCode" }, edge.Source.ForeignKeyProperties.Select(p => p.Property.Name));
        Assert.Equal(new[] { "Tenant", "Code" }, edge.Source.PrincipalKeyProperties.Select(p => p.Property.Name));
        Assert.Equal(new[] { "FilmId", "SourceCode", "SourceTenant" }, edge.KeyProperties.Select(p => p.Name));
        Assert.Equal(12L, edge.Source.ForeignKeyProperties[0].ToStore(12L));
        Assert.Equal("A", edge.Source.PrincipalKeyProperties[1].ToStore("A"));
    }

    [Fact]
    public void Explicit_dictionary_join_and_its_node_skip_navigations_are_validated_without_internal_detection()
    {
        using var context = Context(b =>
        {
            b.Entity<SkipActor>().ToGraphNode("Actor").HasMany(e => e.Films).WithMany(e => e.Actors)
                .UsingEntity<Dictionary<string, object>>("Participation",
                    r => r.HasOne<SkipFilm>().WithMany().HasForeignKey("FilmId"),
                    l => l.HasOne<SkipActor>().WithMany().HasForeignKey("ActorId"),
                    j => j.ToGraphRelationship("ACTED_IN", ["ActorId"], ["FilmId"]));
            b.Entity<SkipFilm>().ToGraphNode("Film");
        });
        var type = context.Model.FindEntityType("Participation")!;
        var edge = context.GetService<GraphBindingCompiler>().GetAssociation(type);
        Assert.True(type.IsPropertyBag);
        Assert.Empty(edge.PayloadProperties);
        Assert.Same(type, context.Model.FindEntityType(typeof(SkipActor))!.FindSkipNavigation(nameof(SkipActor.Films))!.JoinEntityType);
        Assert.Contains("MATCH (n:`Actor`)", context.Set<SkipActor>().ToQueryString());
    }

    [Fact]
    public void Explicit_join_reference_navigations_and_node_inverse_collections_remain_EF_metadata()
    {
        using var context = Context(b =>
        {
            b.Entity<NavigationActor>().ToGraphNode("Actor");
            b.Entity<NavigationFilm>().ToGraphNode("Film");
            var join = b.Entity<NavigationCredit>();
            join.HasKey(e => new { e.ActorId, e.FilmId });
            join.HasOne(e => e.Actor).WithMany(e => e.Credits).HasForeignKey(e => e.ActorId);
            join.HasOne(e => e.Film).WithMany(e => e.Credits).HasForeignKey(e => e.FilmId);
            join.ToGraphRelationship("ACTED_IN", ["ActorId"], ["FilmId"]);
        });
        var edge = Binding<NavigationCredit>(context);
        Assert.Equal(nameof(NavigationCredit.Actor), edge.Source.ForeignKey.DependentToPrincipal!.Name);
        Assert.Equal(nameof(NavigationActor.Credits), edge.Source.ForeignKey.PrincipalToDependent!.Name);
        Assert.Contains("MATCH (n:`Actor`)", context.Set<NavigationActor>().ToQueryString());
    }

    [Fact]
    public void Edge_node_binding_rejects_while_query_root_uses_native_directed_relationship()
    {
        using var context = Context(b => Configure(b));
        var edge = Binding<Credit>(context);
        Assert.Contains("association-edge storage", Assert.Throws<NotSupportedException>(() =>
            context.GetService<GraphBindingCompiler>().GetNode(edge.EntityType)).Message);
        Assert.Contains("MATCH (s:`Actor`)-[r:`ACTED_IN`]->(t:`Film`)", context.Set<Credit>().ToQueryString());
    }

    [Fact]
    public void Public_mapping_defensively_copies_role_arrays_and_validates_identifiers_and_roles()
    {
        var model = new ModelBuilder();
        var source = new[] { "ActorId" };
        var target = new[] { "FilmId" };
        EntityTypeBuilder builder = model.Entity<Credit>();
        Assert.Same(builder, builder.ToGraphRelationship("ACTED_IN", source, target));
        source[0] = "changed";
        target[0] = "changed";
        var savedSource = Assert.IsAssignableFrom<IReadOnlyList<string>>(builder.Metadata[GraphAnnotationNames.SourceForeignKeyProperties]);
        Assert.Equal("ActorId", Assert.Single(savedSource));
        Assert.Equal("FilmId", Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<string>>(builder.Metadata[GraphAnnotationNames.TargetForeignKeyProperties])));
        Assert.Throws<NotSupportedException>(() => ((IList<string>)savedSource)[0] = "changed");
        Assert.Throws<ArgumentException>(() => builder.ToGraphRelationship("bad`type", ["ActorId"], ["FilmId"]));
        Assert.Throws<ArgumentException>(() => builder.ToGraphRelationship("TYPE", [], ["FilmId"]));
        Assert.Throws<ArgumentException>(() => builder.ToGraphRelationship("TYPE", ["ActorId", "ActorId"], ["FilmId"]));
        Assert.Throws<ArgumentException>(() => builder.ToGraphRelationship("TYPE", [" "], ["FilmId"]));
        Assert.Throws<ArgumentNullException>(() => builder.ToGraphRelationship("TYPE", null!, ["FilmId"]));
    }

    [Fact]
    public void Node_and_edge_storage_are_mutually_exclusive_in_either_configuration_order()
    {
        Invalid(b => Configure(b).ToGraphNode("JoinNode"), "mutually exclusive");
        Invalid(b =>
        {
            b.Entity<Credit>().ToGraphNode("JoinNode");
            Configure(b);
        }, "mutually exclusive");
    }

    [Fact]
    public void Unmapped_relationships_and_property_bags_are_not_implicitly_promoted_to_edges()
    {
        Invalid(b =>
        {
            Configure(b).Metadata.RemoveAnnotation(GraphAnnotationNames.RelationshipType);
        }, "relationship storage");
        Invalid(b =>
        {
            var bag = b.SharedTypeEntity<Dictionary<string, object>>("Bag");
            bag.IndexerProperty<Guid>("Id");
            bag.HasKey("Id");
            bag.ToGraphNode("Bag");
        }, "property-bag");
        Invalid(b =>
        {
            b.Entity<Actor>().ToGraphNode("Actor").HasAnnotation(GraphAnnotationNames.SourceForeignKeyProperties, new[] { "Id" });
        }, "endpoint roles require");
    }

    [Fact]
    public void Raw_annotations_cannot_bypass_relationship_identifier_or_role_validation()
    {
        using var context = Context(b => Configure(b).HasAnnotation(GraphAnnotationNames.RelationshipType, "bad`type"));
        Assert.Throws<ArgumentException>(() => context.Model);
        Invalid(b => Configure(b).HasAnnotation(GraphAnnotationNames.SourceForeignKeyProperties, "ActorId"), "complete foreign key");
        Invalid(b => Configure(b).HasAnnotation(GraphAnnotationNames.TargetForeignKeyProperties, new[] { "missing" }), "exactly one complete");
        Invalid(b => ConfigureComposite(b).HasAnnotation(GraphAnnotationNames.SourceForeignKeyProperties,
            new[] { "SourceCode", "SourceTenant" }), "declared property order");
        Invalid(b => ConfigureComposite(b).HasAnnotation(GraphAnnotationNames.SourceForeignKeyProperties,
            new[] { "SourceTenant" }), "complete foreign key");
    }

    [Fact]
    public void Endpoint_roles_must_be_distinct_and_join_key_must_be_exact_endpoint_union()
    {
        Invalid(b => Configure(b).ToGraphRelationship("ACTED_IN", ["ActorId"], ["ActorId"]), "disjoint");
        Invalid(b => Configure(b).HasKey(e => e.ActorId), "exactly the union");
        Invalid(b => Configure(b).HasKey(e => new { e.ActorId, e.FilmId, e.Role }), "exactly the union");
        Invalid(b =>
        {
            var join = Configure(b);
            join.Property<Guid>("EdgeId");
            join.HasKey("EdgeId");
        }, "independently identified");
    }

    [Fact]
    public void Additional_foreign_keys_alternate_keys_and_generated_endpoint_values_are_rejected()
    {
        Invalid(b =>
        {
            var join = Configure(b);
            join.Property<Guid>("OtherId");
            join.HasOne<Actor>().WithMany().HasForeignKey("OtherId");
        }, "exactly two");
        Invalid(b => Configure(b).HasAlternateKey(e => e.Role), "alternate keys");
        Invalid(b => Configure(b).Property(e => e.ActorId).ValueGeneratedOnAdd(), "independently generated");
    }

    [Fact]
    public void Unmapped_endpoint_and_alternate_principal_key_are_rejected()
    {
        Invalid(b =>
        {
            Configure(b);
            b.Entity<Actor>().Metadata.RemoveAnnotation(GraphAnnotationNames.NodeLabel);
        }, "ToGraphNode");
        Invalid(b =>
        {
            var join = Configure(b);
            b.Entity<Actor>().Property<Guid>("AlternateId");
            join.HasOne<Actor>().WithMany().HasForeignKey(e => e.ActorId).HasPrincipalKey("AlternateId");
        }, "primary key");
    }

    [Fact]
    public void Unique_endpoint_foreign_keys_are_rejected_without_cross_context_multiplicity_enforcement()
    {
        Invalid(b =>
        {
            var join = Configure(b);
            join.HasOne<Actor>().WithOne().HasForeignKey<Credit>(e => e.ActorId);
        }, "many-to-one");
    }

    [Fact]
    public void Two_EF_entities_cannot_own_the_same_directed_relationship_storage_tuple()
    {
        Invalid(b =>
        {
            Configure(b);
            var second = b.SharedTypeEntity<Dictionary<string, object>>("SecondCredit");
            second.IndexerProperty<Guid>("SourceId");
            second.IndexerProperty<Guid>("TargetId");
            second.HasKey("SourceId", "TargetId");
            second.HasOne<Actor>().WithMany().HasForeignKey("SourceId");
            second.HasOne<Film>().WithMany().HasForeignKey("TargetId");
            second.ToGraphRelationship("ACTED_IN", ["SourceId"], ["TargetId"]);
        }, "already mapped by another association");
    }

    [Fact]
    public void Relationship_type_can_be_reused_for_distinct_directed_endpoint_bindings()
    {
        using var context = Context(b =>
        {
            Configure(b);
            var second = b.SharedTypeEntity<Dictionary<string, object>>("ReverseCredit");
            second.IndexerProperty<Guid>("SourceId");
            second.IndexerProperty<Guid>("TargetId");
            second.HasKey("SourceId", "TargetId");
            second.HasOne<Film>().WithMany().HasForeignKey("SourceId");
            second.HasOne<Actor>().WithMany().HasForeignKey("TargetId");
            second.ToGraphRelationship("ACTED_IN", ["SourceId"], ["TargetId"]);
        });
        var forward = Binding<Credit>(context);
        var reverse = context.GetService<GraphBindingCompiler>().GetAssociation(context.Model.FindEntityType("ReverseCredit")!);
        Assert.Same(forward.Source.Node, reverse.Target.Node);
        Assert.Same(forward.Target.Node, reverse.Source.Node);
    }

    [Fact]
    public void Payload_scalars_and_converters_follow_existing_property_binding_rules()
    {
        using var context = Context(b =>
        {
            var join = Configure(b);
            join.Property<DateTime>("When").HasConversion(value => value.Ticks, value => new DateTime(value));
            join.Property<string?>("Note");
        });
        var edge = Binding<Credit>(context);
        var when = edge.PayloadProperties.Single(p => p.Property.Name == "When");
        var value = new DateTime(2026, 10, 11);
        Assert.Equal(value.Ticks, when.ToStore(value));
        Assert.Equal(value, when.FromStore(value.Ticks));
        Assert.Null(edge.PayloadProperties.Single(p => p.Property.Name == "Note").ToStore(null));
        Invalid(b => Configure(b).Property<decimal>("Price"), "HasConversion");
        Invalid(b => Configure(b).Property(e => e.Role).HasGraphPropertyName("ActorId"), "more than once");
    }

    [Fact]
    public void Endpoint_inherits_principal_converter_and_reusing_one_converter_instance_is_supported()
    {
        var converter = new ValueConverter<Guid, string>(id => id.ToString("N"), value => Guid.ParseExact(value, "N"));
        foreach (var configureDependent in new[] { false, true })
        {
            using var context = Context(b =>
            {
                var join = Configure(b);
                b.Entity<Actor>().Property(e => e.Id).HasConversion(converter);
                if (configureDependent)
                    join.Property(e => e.ActorId).HasConversion(converter);
            });
            var edge = Binding<Credit>(context);
            var id = Guid.Parse("abcae840-e29b-41d4-a716-446655440000");
            Assert.Equal(id.ToString("N"), edge.Source.ForeignKeyProperties[0].ToStore(id));
            Assert.Equal(edge.Source.PrincipalKeyProperties[0].ToStore(id), edge.Source.ForeignKeyProperties[0].ToStore(id));
        }
    }

    [Fact]
    public void Different_custom_converter_semantics_and_incompatible_wire_types_are_rejected()
    {
        var principal = new ValueConverter<Guid, string>(id => id.ToString("N"), value => Guid.ParseExact(value, "N"));
        var dependent = new ValueConverter<Guid, string>(id => id.ToString("D"), value => Guid.ParseExact(value, "D"));
        Invalid(b =>
        {
            var join = Configure(b);
            b.Entity<Actor>().Property(e => e.Id).HasConversion(principal);
            join.Property(e => e.ActorId).HasConversion(dependent);
        }, "same resolved converter instance");
        Invalid(b => Configure(b).Property(e => e.ActorId).HasConversion(value => 1L, value => Guid.Empty), "incompatible model or wire type");
    }

    [Fact]
    public void Equivalent_but_separately_constructed_custom_converters_are_conservatively_rejected()
    {
        Invalid(b =>
        {
            var join = Configure(b);
            b.Entity<Actor>().Property(e => e.Id).HasConversion(
                new ValueConverter<Guid, string>(id => id.ToString("N"), value => Guid.ParseExact(value, "N")));
            join.Property(e => e.ActorId).HasConversion(
                new ValueConverter<Guid, string>(id => id.ToString("N"), value => Guid.ParseExact(value, "N")));
        }, "same resolved converter instance");
    }

    private ProviderModelContext Context(Action<ModelBuilder> mapping) => new(_driver, mapping);
    private static AssociationEdgeBinding Binding<T>(ProviderModelContext context)
        => context.GetService<GraphBindingCompiler>().GetAssociation(context.Model.FindEntityType(typeof(T))!);

    private void Invalid(Action<ModelBuilder> mapping, string expected)
    {
        using var context = Context(mapping);
        Assert.Contains(expected, Assert.Throws<InvalidOperationException>(() => context.Model).Message);
    }

    private static EntityTypeBuilder<Credit> Configure(ModelBuilder model)
    {
        model.Entity<Actor>().ToGraphNode("Actor");
        model.Entity<Film>().ToGraphNode("Film");
        var join = model.Entity<Credit>();
        join.HasKey(e => new { e.ActorId, e.FilmId });
        join.HasOne<Actor>().WithMany().HasForeignKey(e => e.ActorId);
        join.HasOne<Film>().WithMany().HasForeignKey(e => e.FilmId);
        return join.ToGraphRelationship("ACTED_IN", [nameof(Credit.ActorId)], [nameof(Credit.FilmId)]);
    }

    private static EntityTypeBuilder<CompositeCredit> ConfigureComposite(ModelBuilder model)
    {
        model.Entity<CompositeActor>().ToGraphNode("CompositeActor").HasKey(e => new { e.Tenant, e.Code });
        model.Entity<Film>().ToGraphNode("Film");
        var join = model.Entity<CompositeCredit>();
        join.HasKey(e => new { e.FilmId, e.SourceCode, e.SourceTenant });
        join.HasOne<CompositeActor>().WithMany().HasForeignKey(e => new { e.SourceTenant, e.SourceCode });
        join.HasOne<Film>().WithMany().HasForeignKey(e => e.FilmId);
        return join.ToGraphRelationship("ACTED_IN", ["SourceTenant", "SourceCode"], ["FilmId"]);
    }

    public sealed class Actor { public Guid Id { get; set; } }
    public sealed class Film { public Guid Id { get; set; } }
    public sealed class Credit
    {
        public Guid ActorId { get; set; }
        public Guid FilmId { get; set; }
        public string Role { get; set; } = "";
    }
    public sealed class CompositeActor { public long Tenant { get; set; } public string Code { get; set; } = ""; }
    public sealed class CompositeCredit
    {
        public long SourceTenant { get; set; }
        public string SourceCode { get; set; } = "";
        public Guid FilmId { get; set; }
    }
    public sealed class SkipActor { public Guid Id { get; set; } public List<SkipFilm> Films { get; } = []; }
    public sealed class SkipFilm { public Guid Id { get; set; } public List<SkipActor> Actors { get; } = []; }
    public sealed class NavigationActor { public Guid Id { get; set; } public List<NavigationCredit> Credits { get; } = []; }
    public sealed class NavigationFilm { public Guid Id { get; set; } public List<NavigationCredit> Credits { get; } = []; }
    public sealed class NavigationCredit
    {
        public Guid ActorId { get; set; }
        public Guid FilmId { get; set; }
        public NavigationActor Actor { get; set; } = null!;
        public NavigationFilm Film { get; set; } = null!;
    }
}
