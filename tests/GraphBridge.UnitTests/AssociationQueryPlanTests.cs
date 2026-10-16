using GraphBridge.EntityFrameworkCore.Cypher;
using GraphBridge.EntityFrameworkCore.Diagnostics;
using GraphBridge.EntityFrameworkCore.Metadata;
using GraphBridge.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Neo4j.Driver;

namespace GraphBridge.UnitTests;

/// <summary>Offline projection/renderer preparation; this does not establish Neo4j execution or EF query fix-up.</summary>
public sealed class AssociationQueryPlanTests : IDisposable
{
    private readonly IDriver _driver = ProviderTestDriver.Create();
    public void Dispose() => Assert.Empty(((ProviderTestDriver)(object)_driver).Calls);

    [Fact]
    public void Association_projects_endpoint_principal_store_names_into_dependent_EF_slots_and_payload_from_relationship()
    {
        using var context = Context(ConfigureComposite);
        var binding = Read<Participation>(context);
        var entityType = context.Model.FindEntityType(typeof(Participation))!;
        Assert.Same(entityType, binding.EntityType);
        Assert.Equal(entityType.GetProperties().OrderBy(p => p.GetIndex()), binding.Properties.Select(p => p.ModelProperty.Property));
        Assert.Equal(entityType.GetProperties().Max(p => p.GetIndex()) + 1, binding.ValueBufferSize);
        Assert.Equal(new CypherAssociationRoot("Person", "PARTICIPATES", "Movie"), binding.Root);

        var tenant = Property(binding, nameof(Participation.SourceTenant));
        var code = Property(binding, nameof(Participation.SourceCode));
        var target = Property(binding, nameof(Participation.TargetId));
        Assert.Equal("unusedTenantFk", tenant.ModelProperty.Name);
        Assert.Equal(new CypherProperty("tenant_key", CypherPropertyOwner.Source), tenant.StoreProperty);
        Assert.Equal("unusedCodeFk", code.ModelProperty.Name);
        Assert.Equal(new CypherProperty("person_code", CypherPropertyOwner.Source), code.StoreProperty);
        Assert.Equal(new CypherProperty("movie_key", CypherPropertyOwner.Target), target.StoreProperty);
        Assert.Equal(new CypherProperty("role", CypherPropertyOwner.Relationship), Property(binding, "Role").StoreProperty);

        var text = CypherQueryRenderer.Render(binding.CreatePlan());
        Assert.StartsWith("MATCH (s:`Person`)-[r:`PARTICIPATES`]->(t:`Movie`)\nCALL (s, t) {", text);
        foreach (var property in binding.Properties)
            Assert.Contains($" AS `c{property.ModelProperty.Property.GetIndex()}`", text);
        Assert.DoesNotContain("unused", text);
        Assert.DoesNotContain("r.`Source", text);
        Assert.DoesNotContain("n.", text);
        Assert.DoesNotContain("DISTINCT", text);
        Assert.DoesNotContain("elementId", text);
    }

    [Fact]
    public void Projection_decodes_endpoint_converter_and_nullable_shadow_payload_at_actual_model_indices()
    {
        using var context = Context(ConfigureComposite);
        var binding = Read<Participation>(context);
        var id = Guid.Parse("ad66c130-4a75-4d90-8e97-a450abeb5402");
        var instant = new DateTime(2026, 10, 11);
        var storeValues = new Dictionary<string, object?>
        {
            ["SourceTenant"] = 12L, ["SourceCode"] = "person", ["TargetId"] = id.ToString("N"),
            ["Role"] = "lead", ["Rank"] = 4L, ["Note"] = null, ["When"] = instant.Ticks
        };
        var values = new object?[binding.ValueBufferSize];
        foreach (var slot in binding.Properties)
            values[slot.Index] = slot.ModelProperty.FromStore(storeValues[slot.ModelProperty.Property.Name]);
        var buffer = new ValueBuffer(values);
        Assert.Equal(id, buffer[Property(binding, "TargetId").Index]);
        Assert.Equal(instant, buffer[Property(binding, "When").Index]);
        Assert.Equal(4, buffer[Property(binding, "Rank").Index]);
        Assert.Null(buffer[Property(binding, "Note").Index]);
        Assert.True(Property(binding, "Note").ModelProperty.Property.IsShadowProperty());
        Assert.Equal(id.ToString("N"), Property(binding, "TargetId").ModelProperty.ToStore(id));
        Assert.Throws<InvalidOperationException>(() => Property(binding, "SourceCode").ModelProperty.FromStore(null));
        Assert.Throws<OverflowException>(() => Property(binding, "Rank").ModelProperty.FromStore(long.MaxValue));
    }

    [Fact]
    public void Self_association_follows_explicit_direction_even_when_labels_and_principal_keys_match()
    {
        using var context = Context(b =>
        {
            b.Entity<Movie>().ToGraphNode("Person").Property(e => e.Id).HasGraphPropertyName("person_key");
            var join = b.Entity<Connection>();
            join.HasKey(e => new { e.LeftId, e.RightId });
            join.HasOne<Movie>().WithMany().HasForeignKey(e => e.LeftId);
            join.HasOne<Movie>().WithMany().HasForeignKey(e => e.RightId);
            join.ToGraphRelationship("FOLLOWS", ["RightId"], ["LeftId"]);
        });
        var binding = Read<Connection>(context);
        Assert.Equal(CypherPropertyOwner.Source, Property(binding, "RightId").StoreProperty.Owner);
        Assert.Equal(CypherPropertyOwner.Target, Property(binding, "LeftId").StoreProperty.Owner);
        Assert.StartsWith("MATCH (s:`Person`)-[r:`FOLLOWS`]->(t:`Person`)", CypherQueryRenderer.Render(binding.CreatePlan()));
    }

    [Fact]
    public void Scalar_projection_retains_composite_renamed_and_converted_endpoint_keys()
    {
        using var context = Context(ConfigureComposite);
        var read = Read<Participation>(context);
        var role = Property(read, nameof(Participation.Role));
        var id = Guid.NewGuid();
        var plan = read.CreatePlan([role]);
        Assert.Equal(4, plan.Columns.Count);
        Assert.Contains(plan.Columns, c => c.Name == "tenant_key" && c.Owner == CypherPropertyOwner.Source);
        Assert.Contains(plan.Columns, c => c.Name == "person_code" && c.Owner == CypherPropertyOwner.Source);
        Assert.Contains(plan.Columns, c => c.Name == "movie_key" && c.Owner == CypherPropertyOwner.Target);
        var row = new Dictionary<string, object?>
        {
            [CypherQueryPlan.AssociationCountAlias] = 1L,
            [Property(read, "SourceTenant").Column.Alias] = 12L,
            [Property(read, "SourceCode").Column.Alias] = "person",
            [Property(read, "TargetId").Column.Alias] = id.ToString("N"),
            [role.Column.Alias] = "lead"
        };
        var values = read.ReadRow(row, [role]);
        Assert.Equal(id, values[Property(read, "TargetId").Index]);
        Assert.Equal("lead", values[role.Index]);
        row[Property(read, "TargetId").Column.Alias] = "malformed key";
        Assert.Throws<FormatException>(() => read.ReadRow(row, [role]));
    }

    [Fact]
    public void Shared_dictionary_join_preserves_its_named_EF_entity_type_and_indexer_properties()
    {
        using var context = Context(b =>
        {
            b.Entity<Movie>().ToGraphNode("Movie");
            var join = b.SharedTypeEntity<Dictionary<string, object>>("Recommendation");
            join.IndexerProperty<Guid>("FromId");
            join.IndexerProperty<Guid>("ToId");
            join.IndexerProperty<string?>("Note").HasGraphPropertyName("note");
            join.HasKey("FromId", "ToId");
            join.HasOne<Movie>().WithMany().HasForeignKey("FromId");
            join.HasOne<Movie>().WithMany().HasForeignKey("ToId");
            join.ToGraphRelationship("RECOMMENDS", ["FromId"], ["ToId"]);
        });
        var entityType = context.Model.FindEntityType("Recommendation")!;
        var binding = GraphReadBinding.FromAssociation(context.GetService<GraphBindingCompiler>().GetAssociation(entityType));
        Assert.Same(entityType, binding.EntityType);
        Assert.True(entityType.IsPropertyBag);
        Assert.All(binding.Properties, p => Assert.True(p.ModelProperty.Property.IsIndexerProperty()));
        Assert.Equal(new CypherProperty("note", CypherPropertyOwner.Relationship), Property(binding, "Note").StoreProperty);
        Assert.Contains("MATCH (s:`Movie`)-[r:`RECOMMENDS`]->(t:`Movie`)",
            context.Set<Dictionary<string, object>>("Recommendation").ToQueryString());
    }

    [Fact]
    public void Node_projection_keeps_existing_rendering_and_all_model_property_slots()
    {
        using var context = Context(b => b.Entity<Movie>().ToGraphNode("Movie").Property(e => e.Id).HasGraphPropertyName("movie_key"));
        var node = context.GetService<GraphBindingCompiler>().GetNode(context.Model.FindEntityType(typeof(Movie))!);
        var binding = GraphReadBinding.FromNode(node);
        Assert.Same(node.EntityType, binding.EntityType);
        Assert.Same(Assert.Single(node.Properties), Assert.Single(binding.Properties).ModelProperty);
        Assert.Equal("MATCH (n:`Movie`)\nRETURN n.`movie_key` AS `c0`", CypherQueryRenderer.Render(binding.CreatePlan()));
    }

    [Fact]
    public void Association_renderer_routes_predicates_ordering_and_scalar_projection_to_typed_owners()
    {
        var predicate = new CypherBinary(
            new CypherBinary(new CypherProperty("person_key", CypherPropertyOwner.Source), CypherOperator.Equal, new CypherParameter("p0")),
            CypherOperator.And,
            new CypherBinary(new CypherProperty("role", CypherPropertyOwner.Relationship), CypherOperator.NotEqual, new CypherParameter("p1")));
        var plan = new CypherQueryPlan(new CypherAssociationRoot("Person", "ACTED_IN", "Movie"),
            [new("role", "c2", CypherPropertyOwner.Relationship)], predicate,
            [new("role", true, CypherPropertyOwner.Relationship), new("movie_key", false, CypherPropertyOwner.Target)], "p2", "p3");
        Assert.Equal("MATCH (s:`Person`)-[r:`ACTED_IN`]->(t:`Movie`)\n"
            + "CALL (s, t) {\n  MATCH (s)-[candidate:`ACTED_IN`]->(t)\n  WITH candidate LIMIT 2\n  RETURN count(*) AS `__gb_association_count`\n}\nWITH s, r, t, `__gb_association_count`\n"
            + "WHERE (coalesce(s.`person_key` = $p0, (s.`person_key` IS NULL AND $p0 IS NULL)) AND (NOT coalesce(r.`role` = $p1, (r.`role` IS NULL AND $p1 IS NULL))))\n"
            + "RETURN r.`role` AS `c2`, `__gb_association_count` AS `__gb_association_count`\nORDER BY r.`role` IS NULL DESC, r.`role` ASC, t.`movie_key` IS NULL ASC, t.`movie_key` DESC\nSKIP $p2\nLIMIT $p3",
            CypherQueryRenderer.Render(plan));
    }

    [Fact]
    public void Association_endpoint_comparison_and_negation_keep_existing_null_compensation()
    {
        var plan = new CypherQueryPlan(new CypherAssociationRoot("Person", "KNOWS", "Person"),
            [new("Id", "c0", CypherPropertyOwner.Source)],
            new CypherNot(new CypherBinary(new CypherProperty("Id", CypherPropertyOwner.Source), CypherOperator.Equal,
                new CypherProperty("Id", CypherPropertyOwner.Target))));
        Assert.Contains("WHERE (NOT coalesce(coalesce(s.`Id` = t.`Id`, (s.`Id` IS NULL AND t.`Id` IS NULL)), false))",
            CypherQueryRenderer.Render(plan));
    }

    [Fact]
    public void Read_projection_and_plan_lists_are_immutable_including_record_with_replacements()
    {
        using var context = Context(ConfigureComposite);
        var binding = Read<Participation>(context);
        Assert.Throws<NotSupportedException>(() => ((IList<GraphReadProperty>)binding.Properties).Clear());
        var columns = new List<CypherColumn> { new("name", "c0") };
        var orders = new List<CypherOrdering> { new("name", true) };
        var plan = new CypherQueryPlan("Person", columns, Orderings: orders);
        columns.Clear();
        orders.Clear();
        Assert.Single(plan.Columns);
        Assert.Single(plan.Orderings!);
        Assert.Throws<NotSupportedException>(() => ((IList<CypherColumn>)plan.Columns).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<CypherOrdering>)plan.Orderings!).Clear());
        columns.Add(new("age", "c1"));
        orders.Add(new("age", false));
        var replacement = plan with { Columns = columns, Orderings = orders };
        columns[0] = new("changed", "c9");
        orders[0] = new("changed", true);
        Assert.Equal("age", Assert.Single(replacement.Columns).Name);
        Assert.Equal("age", Assert.Single(replacement.Orderings!).Name);
        Assert.Equal("name", Assert.Single(plan.Columns).Name);
    }

    [Theory]
    [InlineData((int)CypherPropertyOwner.Source)]
    [InlineData((int)CypherPropertyOwner.Relationship)]
    [InlineData((int)CypherPropertyOwner.Target)]
    [InlineData(99)]
    public void Node_root_rejects_unavailable_owners_in_columns_predicates_and_ordering(int owner)
        => AssertInvalidOwner(new CypherNodeRoot("Person"), (CypherPropertyOwner)owner, CypherPropertyOwner.Node);

    [Theory]
    [InlineData((int)CypherPropertyOwner.Node)]
    [InlineData(99)]
    public void Association_root_rejects_unavailable_owners_in_columns_predicates_and_ordering(int owner)
        => AssertInvalidOwner(new CypherAssociationRoot("Person", "KNOWS", "Person"), (CypherPropertyOwner)owner, CypherPropertyOwner.Relationship);

    [Theory]
    [InlineData("bad`token")]
    [InlineData("value) MATCH (n)")]
    [InlineData("")]
    [InlineData("two words")]
    [InlineData("1bad")]
    public void Association_renderer_validates_every_identifier_and_parameter_position(string token)
    {
        CypherQueryRoot root = new CypherAssociationRoot("Person", "ACTED_IN", "Movie");
        var column = new CypherColumn("role", "c0", CypherPropertyOwner.Relationship);
        var invalid = new CypherQueryPlan[]
        {
            new(new CypherAssociationRoot(token, "ACTED_IN", "Movie"), [column]),
            new(new CypherAssociationRoot("Person", token, "Movie"), [column]),
            new(new CypherAssociationRoot("Person", "ACTED_IN", token), [column]),
            new(root, [column with { Name = token }]),
            new(root, [column with { Alias = token }]),
            new(root, [column], new CypherProperty(token, CypherPropertyOwner.Source)),
            new(root, [column], new CypherParameter(token)),
            new(root, [column], Orderings: [new(token, true, CypherPropertyOwner.Target)]),
            new(root, [column], SkipParameter: token),
            new(root, [column], LimitParameter: token)
        };
        foreach (var plan in invalid)
            Assert.Throws<InvalidOperationException>(() => CypherQueryRenderer.Render(plan));
    }

    [Fact]
    public void Association_values_remain_separate_parameters_and_runtime_translation_uses_guarded_read_plan()
    {
        using var context = Context(ConfigureComposite);
        var binding = Read<Participation>(context);
        var role = Property(binding, "Role");
        var secret = "value'`) MATCH (victim) //";
        var parameter = new Neo4jQueryParameter("p0", "value", null, role.ModelProperty);
        var plan = binding.CreatePlan() with
        {
            Predicate = new CypherBinary(role.StoreProperty, CypherOperator.Equal, new CypherParameter(parameter.Name))
        };
        Assert.Equal(secret, parameter.Resolve(new Dictionary<string, object?> { ["value"] = secret }));
        var text = CypherQueryRenderer.Render(plan);
        Assert.Contains("r.`role` = $p0", text);
        Assert.DoesNotContain(secret, text);
        var translated = context.Set<Participation>().Where(p => p.Role == secret).Select(p => p.Role).ToQueryString();
        Assert.Contains("r.`role` = $p0", translated);
        Assert.DoesNotContain(secret, translated);
        Assert.Contains("s.`tenant_key` AS", translated);
        Assert.Contains("s.`person_code` AS", translated);
        Assert.Contains("t.`movie_key` AS", translated);
        Assert.Contains(CypherQueryPlan.AssociationCountAlias, translated);
    }

    [Fact]
    public void Association_aggregates_reject_at_translation_before_transport_for_entity_and_scalar_shapes()
    {
        using var context = Context(ConfigureComposite);
        var query = context.Set<Participation>();
        Assert.Throws<Neo4jQueryTranslationException>(() => query.Count());
        Assert.Throws<Neo4jQueryTranslationException>(() => query.LongCount());
        Assert.Throws<Neo4jQueryTranslationException>(() => query.Any());
        Assert.Throws<Neo4jQueryTranslationException>(() => query.Select(p => p.Role).Any());
    }

    [Fact]
    public void Association_FK_inherited_custom_converter_rejects_server_order_and_ranges()
    {
        using var context = Context(model =>
        {
            ConfigureComposite(model);
            model.Entity<Person>().Property(p => p.Tenant).HasConversion(value => -value, value => -value);
        });
        var query = context.Set<Participation>();
        Assert.Throws<Neo4jQueryTranslationException>(() => query.Where(p => p.SourceTenant > 0).ToQueryString());
        Assert.Throws<Neo4jQueryTranslationException>(() => query.OrderBy(p => p.SourceTenant).ToQueryString());
        Assert.Contains("s.`tenant_key` = $p0", query.Where(p => p.SourceTenant == 3).ToQueryString());
    }

    [Theory]
    [InlineData((int)CypherAggregate.Count)]
    [InlineData((int)CypherAggregate.Any)]
    public void Association_aggregates_remain_rejected_until_duplicate_identity_diagnostics_are_preserved(int aggregate)
    {
        var plan = new CypherQueryPlan(new CypherAssociationRoot("Person", "ACTED_IN", "Movie"),
            [new("role", "c0", CypherPropertyOwner.Relationship)], Aggregate: (CypherAggregate)aggregate);
        Assert.Contains("cardinality diagnostic", Assert.Throws<NotSupportedException>(() => CypherQueryRenderer.Render(plan)).Message);
    }

    [Fact]
    public void Unknown_aggregate_is_rejected_instead_of_rendering_any()
        => Assert.Throws<InvalidOperationException>(() => CypherQueryRenderer.Render(
            new CypherQueryPlan("Person", [new("Id", "c0")], Aggregate: (CypherAggregate)99)));

    private static void AssertInvalidOwner(CypherQueryRoot root, CypherPropertyOwner invalid, CypherPropertyOwner valid)
    {
        var column = new CypherColumn("Id", "c0", valid);
        Assert.Throws<InvalidOperationException>(() => CypherQueryRenderer.Render(new(root, [column with { Owner = invalid }])));
        Assert.Throws<InvalidOperationException>(() => CypherQueryRenderer.Render(new(root, [column], new CypherProperty("Id", invalid))));
        Assert.Throws<InvalidOperationException>(() => CypherQueryRenderer.Render(new(root, [column], Orderings: [new("Id", true, invalid)])));
    }

    private ProviderModelContext Context(Action<ModelBuilder> mapping) => new(_driver, mapping);
    private static GraphReadBinding Read<T>(ProviderModelContext context)
        => GraphReadBinding.FromAssociation(context.GetService<GraphBindingCompiler>().GetAssociation(context.Model.FindEntityType(typeof(T))!));
    private static GraphReadProperty Property(GraphReadBinding binding, string name)
        => binding.Properties.Single(p => p.ModelProperty.Property.Name == name);

    private static void ConfigureComposite(ModelBuilder model)
    {
        var source = model.Entity<Person>().ToGraphNode("Person");
        source.HasKey(e => new { e.Tenant, e.Code });
        source.Property(e => e.Tenant).HasGraphPropertyName("tenant_key");
        source.Property(e => e.Code).HasGraphPropertyName("person_code");
        var converter = new ValueConverter<Guid, string>(id => id.ToString("N"), value => Guid.ParseExact(value, "N"));
        model.Entity<Movie>().ToGraphNode("Movie").Property(e => e.Id).HasGraphPropertyName("movie_key").HasConversion(converter);
        var join = model.Entity<Participation>();
        join.HasKey(e => new { e.TargetId, e.SourceCode, e.SourceTenant });
        join.HasOne<Person>().WithMany().HasForeignKey(e => new { e.SourceTenant, e.SourceCode });
        join.HasOne<Movie>().WithMany().HasForeignKey(e => e.TargetId);
        join.Property(e => e.SourceTenant).HasGraphPropertyName("unusedTenantFk");
        join.Property(e => e.SourceCode).HasGraphPropertyName("unusedCodeFk");
        join.Property(e => e.TargetId).HasGraphPropertyName("unusedTargetFk");
        join.Property(e => e.Role).HasGraphPropertyName("role");
        join.Property<string?>("Note").HasGraphPropertyName("note");
        join.Property<DateTime>("When").HasGraphPropertyName("instant").HasConversion(value => value.Ticks, value => new DateTime(value));
        join.ToGraphRelationship("PARTICIPATES", ["SourceTenant", "SourceCode"], ["TargetId"]);
    }

    public sealed class Person { public long Tenant { get; set; } public string Code { get; set; } = ""; }
    public sealed class Movie { public Guid Id { get; set; } }
    public sealed class Connection { public Guid LeftId { get; set; } public Guid RightId { get; set; } }
    public sealed class Participation
    {
        public Guid TargetId { get; set; }
        public string SourceCode { get; set; } = "";
        public long SourceTenant { get; set; }
        public string Role { get; set; } = "";
        public int? Rank { get; set; }
    }
}
