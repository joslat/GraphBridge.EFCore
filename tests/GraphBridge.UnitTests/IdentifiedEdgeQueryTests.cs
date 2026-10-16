using GraphBridge.EntityFrameworkCore.Cypher;
using GraphBridge.EntityFrameworkCore.Diagnostics;
using GraphBridge.EntityFrameworkCore.Metadata;
using GraphBridge.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Neo4j.Driver;

namespace GraphBridge.UnitTests;

public sealed class IdentifiedEdgeQueryTests : IDisposable
{
    private readonly IDriver _driver = ProviderTestDriver.Create();
    public void Dispose() => Assert.Empty(((ProviderTestDriver)(object)_driver).Calls);

    [Fact]
    public void Finalized_binding_uses_relationship_identity_principal_store_keys_and_actual_EF_slots()
    {
        using var context = Context();
        var read = Binding(context);
        Assert.IsType<CypherIdentifiedEdgeRoot>(read.Root);
        Assert.Equal(CypherPropertyOwner.Relationship, Slot(read, nameof(Edge.Id)).StoreProperty.Owner);
        Assert.Equal("edge_key", Slot(read, nameof(Edge.Id)).StoreProperty.Name);
        Assert.Equal("source_key", Slot(read, nameof(Edge.SourceId)).StoreProperty.Name);
        Assert.Equal("target_key", Slot(read, nameof(Edge.TargetId)).StoreProperty.Name);
        Assert.Equal(CypherPropertyOwner.Source, Slot(read, nameof(Edge.SourceTenant)).StoreProperty.Owner);
        Assert.Equal(CypherPropertyOwner.Target, Slot(read, nameof(Edge.TargetTenant)).StoreProperty.Owner);
        var values = read.ReadRow(Row(read));
        Assert.Equal(Guid.Parse("00000000-0000-0000-0000-000000000001"), values[Slot(read, nameof(Edge.Id)).Index]);
        Assert.Equal(1.23m, values[Slot(read, nameof(Edge.Weight)).Index]);
        Assert.Null(values[Slot(read, nameof(Edge.Note)).Index]);
        Assert.Equal(read.EntityType.GetProperties().Count(), read.Properties.Count);
        Assert.Equal(2, read.RequiredUniqueEndpoints.Count);
        Assert.Single(read.RequiredUniqueIdentifiedEdges);
        Assert.Throws<NotSupportedException>(() => ((IList<IdentifiedEdgeBinding>)read.RequiredUniqueIdentifiedEdges).Clear());
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("null")]
    [InlineData("zero")]
    [InlineData("two")]
    [InlineData("integer")]
    [InlineData("text")]
    public void Identity_diagnostic_rejects_before_any_property_conversion(string fault)
    {
        using var context = Context();
        var read = Binding(context);
        var row = Row(read);
        row[Slot(read, nameof(Edge.Id)).Column.Alias] = "bad Guid";
        row[CypherQueryPlan.IdentifiedEdgeCountAlias] = fault switch
        {
            "null" => null, "zero" => 0L, "two" => 2L, "integer" => (object)1, "text" => "1", _ => 1L
        };
        if (fault == "missing") row.Remove(CypherQueryPlan.IdentifiedEdgeCountAlias);
        Assert.Contains("identity diagnostic", Assert.Throws<InvalidOperationException>(() => read.ReadRow(row)).Message);
        Assert.Empty(context.ChangeTracker.Entries());
    }

    [Theory]
    [InlineData(nameof(Edge.Id))]
    [InlineData(nameof(Edge.SourceTenant))]
    [InlineData(nameof(Edge.SourceId))]
    [InlineData(nameof(Edge.TargetTenant))]
    [InlineData(nameof(Edge.TargetId))]
    public void Scalar_projection_preserves_and_validates_every_identity_and_endpoint_slot(string missing)
    {
        using var context = Context();
        var read = Binding(context);
        var note = Slot(read, nameof(Edge.Note));
        var plan = read.CreatePlan([note]);
        Assert.Equal(6, plan.Columns.Count);
        var row = Row(read);
        row.Remove(Slot(read, nameof(Edge.Weight)).Column.Alias);
        Assert.Null(read.ReadRow(row, [note])[note.Index]);
        row[Slot(read, missing).Column.Alias] = null;
        Assert.Contains("required property", Assert.Throws<InvalidOperationException>(() => read.ReadRow(row, [note])).Message);
    }

    [Fact]
    public void Entity_and_scalar_LINQ_use_distinct_global_identity_guard_with_redacted_parameters()
    {
        using var context = Context();
        var id = Guid.NewGuid();
        var sourceId = Guid.NewGuid();
        var query = context.Set<Edge>().Where(e => e.Id == id && e.SourceId == sourceId)
            .OrderBy(e => e.Note).Take(2).Select(e => e.Note);
        var text = query.ToQueryString();
        Assert.StartsWith("MATCH (s:`IdentifiedSource`)-[r:`IDENTIFIED_EVENT`]->(t:`IdentifiedTarget`)", text);
        Assert.Contains("CALL (r)", text);
        Assert.Contains("MATCH ()-[candidate:`IDENTIFIED_EVENT`]->()", text);
        Assert.Contains("WHERE candidate.`edge_key` = r.`edge_key`", text);
        Assert.Contains("r.`edge_key` = $", text);
        Assert.Contains("s.`source_key` = $", text);
        Assert.Contains("r.`edge_key` AS", text);
        Assert.Contains("t.`target_key` AS", text);
        Assert.Contains(CypherQueryPlan.IdentifiedEdgeCountAlias, text);
        Assert.DoesNotContain(CypherQueryPlan.AssociationCountAlias, text);
        Assert.DoesNotContain(id.ToString(), text);
        Assert.DoesNotContain("DISTINCT", text);
        Assert.DoesNotContain("elementId", text);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    public void Empty_or_whitespace_wire_ID_rejects_before_conversion_for_full_and_scalar_rows(string id)
    {
        using var context = Context();
        var read = Binding(context);
        var row = Row(read);
        row[Slot(read, nameof(Edge.Id)).Column.Alias] = id;
        Assert.Contains("application ID must not", Assert.Throws<InvalidOperationException>(() => read.ReadRow(row)).Message);
        Assert.Contains("application ID must not", Assert.Throws<InvalidOperationException>(() => read.ReadRow(row, [Slot(read, nameof(Edge.Note))])).Message);
    }

    [Fact]
    public void Noncanonical_Guid_wire_ID_rejects_before_full_or_scalar_shaper_can_merge_EF_identities()
    {
        using var context = Context();
        var read = Binding(context);
        var row = Row(read);
        var slot = Slot(read, nameof(Edge.Id));
        row[slot.Column.Alias] = "ABCDEF00-1234-5678-90AB-CDEF00123456";
        Assert.Contains("canonical store representation", Assert.Throws<InvalidOperationException>(() => read.ReadRow(row)).Message);
        Assert.Contains("canonical store representation", Assert.Throws<InvalidOperationException>(() => read.ReadRow(row, [Slot(read, nameof(Edge.Note))])).Message);
        row[slot.Column.Alias] = "abcdef00-1234-5678-90ab-cdef00123456";
        Assert.Equal(Guid.Parse("abcdef00-1234-5678-90ab-cdef00123456"), read.ReadRow(row)[slot.Index]);
    }

    [Fact]
    public void Canonical_ID_guard_uses_the_finalized_converter_instead_of_assuming_builtin_format()
    {
        using var context = Context(model => model.Entity<Edge>().Property(e => e.Id)
            .HasConversion(v => v.ToString("D").ToUpperInvariant(), v => Guid.Parse(v)));
        var read = Binding(context);
        var row = Row(read);
        var slot = Slot(read, nameof(Edge.Id));
        row[slot.Column.Alias] = "ABCDEF00-1234-5678-90AB-CDEF00123456";
        Assert.Equal(Guid.Parse("abcdef00-1234-5678-90ab-cdef00123456"), read.ReadRow(row)[slot.Index]);
        row[slot.Column.Alias] = "abcdef00-1234-5678-90ab-cdef00123456";
        Assert.Contains("canonical store representation", Assert.Throws<InvalidOperationException>(() => read.ReadRow(row, [Slot(read, nameof(Edge.Note))])).Message);
    }

    [Fact]
    public void Guard_is_global_to_type_before_payload_filter_ordering_and_paging()
    {
        var plan = new CypherQueryPlan(new CypherIdentifiedEdgeRoot("Person", "EVENT", "Movie", "edgeId"),
            [new("payload", "c0", CypherPropertyOwner.Relationship)],
            new CypherBinary(new CypherProperty("payload", CypherPropertyOwner.Relationship), CypherOperator.Equal,
                new CypherParameter("p0")), [new("payload", true, CypherPropertyOwner.Relationship)], "p1", "p2");
        Assert.Equal("""
            MATCH (s:`Person`)-[r:`EVENT`]->(t:`Movie`)
            CALL (r) {
              MATCH ()-[candidate:`EVENT`]->()
              WHERE candidate.`edgeId` = r.`edgeId`
              WITH candidate LIMIT 2
              RETURN count(*) AS `__gb_identified_edge_count`
            }
            WITH s, r, t, `__gb_identified_edge_count`
            WHERE coalesce(r.`payload` = $p0, (r.`payload` IS NULL AND $p0 IS NULL))
            RETURN r.`payload` AS `c0`, `__gb_identified_edge_count` AS `__gb_identified_edge_count`
            ORDER BY r.`payload` IS NULL DESC, r.`payload` ASC
            SKIP $p1
            LIMIT $p2
            """, CypherQueryRenderer.Render(plan));
    }

    [Fact]
    public void Identified_aggregate_cannot_drop_identity_diagnostics()
    {
        using var context = Context();
        Assert.Throws<Neo4jQueryTranslationException>(() => context.Set<Edge>().Count());
        Assert.Throws<Neo4jQueryTranslationException>(() => context.Set<Edge>().LongCount());
        Assert.Throws<Neo4jQueryTranslationException>(() => context.Set<Edge>().Select(e => e.Note).Any());
        Assert.Throws<NotSupportedException>(() => CypherQueryRenderer.Render(new(
            new CypherIdentifiedEdgeRoot("S", "E", "T", "id"), [], Aggregate: CypherAggregate.Count)));
    }

    [Fact]
    public void Identified_renderer_rejects_unsafe_key_reserved_alias_duplicates_and_unavailable_owner()
    {
        var root = new CypherIdentifiedEdgeRoot("S", "E", "T", "id");
        Assert.Throws<InvalidOperationException>(() => CypherQueryRenderer.Render(new(root with { KeyProperty = "id` RETURN 1" }, [])));
        Assert.Throws<InvalidOperationException>(() => CypherQueryRenderer.Render(new(root,
            [new("p", CypherQueryPlan.IdentifiedEdgeCountAlias, CypherPropertyOwner.Relationship)])));
        Assert.Throws<InvalidOperationException>(() => CypherQueryRenderer.Render(new(root,
            [new("p", "same", CypherPropertyOwner.Source), new("q", "same", CypherPropertyOwner.Target)])));
        Assert.Throws<InvalidOperationException>(() => CypherQueryRenderer.Render(new(root, [new("p", "c0")])));
    }

    private ProviderModelContext Context(Action<ModelBuilder>? configure = null) => new(_driver, model =>
    {
        var source = model.Entity<Source>();
        source.ToGraphNode("IdentifiedSource");
        source.HasKey(e => new { e.Tenant, e.Id });
        source.Property(e => e.Id).HasGraphPropertyName("source_key").ValueGeneratedNever();
        var target = model.Entity<Target>();
        target.ToGraphNode("IdentifiedTarget");
        target.HasKey(e => new { e.Tenant, e.Id });
        target.Property(e => e.Id).HasGraphPropertyName("target_key").ValueGeneratedNever();
        var edge = model.Entity<Edge>();
        edge.HasKey(e => e.Id);
        edge.Property(e => e.Id).HasGraphPropertyName("edge_key").ValueGeneratedNever();
        edge.Property(e => e.Weight).HasConversion(v => (long)(v * 100), v => v / 100m);
        edge.HasOne<Source>().WithMany().HasForeignKey(e => new { e.SourceTenant, e.SourceId });
        edge.HasOne<Target>().WithMany().HasForeignKey(e => new { e.TargetTenant, e.TargetId });
        edge.ToGraphIdentifiedRelationship("IDENTIFIED_EVENT", nameof(Edge.Id),
            [nameof(Edge.SourceTenant), nameof(Edge.SourceId)], [nameof(Edge.TargetTenant), nameof(Edge.TargetId)]);
        configure?.Invoke(model);
    });

    private static GraphReadBinding Binding(ProviderModelContext context)
        => GraphReadBinding.FromIdentifiedEdge(context.GetService<GraphBindingCompiler>().GetIdentifiedEdge(context.Model.FindEntityType(typeof(Edge))!));
    private static GraphReadProperty Slot(GraphReadBinding read, string name)
        => read.Properties.Single(p => p.ModelProperty.Property.Name == name);
    private static Dictionary<string, object?> Row(GraphReadBinding read)
    {
        var row = read.Properties.ToDictionary(p => p.Column.Alias, p => p.ModelProperty.Property.Name switch
        {
            nameof(Edge.Id) => (object?)"00000000-0000-0000-0000-000000000001",
            nameof(Edge.SourceId) => "00000000-0000-0000-0000-000000000002",
            nameof(Edge.TargetId) => "00000000-0000-0000-0000-000000000003",
            nameof(Edge.SourceTenant) or nameof(Edge.TargetTenant) => "tenant",
            nameof(Edge.Weight) => 123L, _ => null
        });
        row[CypherQueryPlan.IdentifiedEdgeCountAlias] = 1L;
        return row;
    }
    public sealed class Source { public string Tenant { get; set; } = ""; public Guid Id { get; set; } }
    public sealed class Target { public string Tenant { get; set; } = ""; public Guid Id { get; set; } }
    public sealed class Edge
    {
        public Guid Id { get; set; }
        public string SourceTenant { get; set; } = "";
        public Guid SourceId { get; set; }
        public string TargetTenant { get; set; } = "";
        public Guid TargetId { get; set; }
        public string? Note { get; set; }
        public decimal Weight { get; set; }
    }
}
