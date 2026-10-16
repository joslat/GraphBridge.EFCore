using GraphBridge.EntityFrameworkCore.Cypher;
using GraphBridge.EntityFrameworkCore.Metadata;
using GraphBridge.EntityFrameworkCore.Query;
using GraphBridge.EntityFrameworkCore.Schema;
using GraphBridge.EntityFrameworkCore.Update;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace GraphBridge.UnitTests;

public sealed class OptionalReferenceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Optional_identity_rejects_custom_equality_on_FK_or_principal(bool principal)
    {
        using var db = new ProviderModelContext(ProviderTestDriver.Create(), b =>
        {
            Configure(b);
            if (principal)
                b.Entity<Parent>().Property(p => p.Id).Metadata.SetValueComparer(
                    new ValueComparer<Guid>((a, z) => true, a => 0, a => a));
            else
                b.Entity<Child>().Property(c => c.ParentId).Metadata.SetValueComparer(
                    new ValueComparer<Guid?>((a, z) => true, a => 0, a => a));
        });
        Assert.Contains("custom key comparer", Assert.Throws<InvalidOperationException>(() => _ = db.Model).Message);
    }

    [Fact]
    public void Optional_model_has_nullable_derived_FK_and_versioned_zero_to_one_manifest()
    {
        var driver = ProviderTestDriver.Create();
        using var optional = new ProviderModelContext(driver, b => Configure(b));
        using var required = new ProviderModelContext(driver, b => Configure(b, required: true));
        var reference = Reference(optional);
        Assert.False(reference.ForeignKey.IsRequired);
        Assert.True(Assert.Single(reference.ForeignKeyProperties).Property.IsNullable);
        Assert.DoesNotContain(reference.Dependent.StoredProperties, p => p.Property.Name == nameof(Child.ParentId));
        var optionalManifest = GraphSchemaManifest.Create(optional.Model);
        var requiredManifest = GraphSchemaManifest.Create(required.Model);
        Assert.Equal(2, optionalManifest.SchemaVersion);
        Assert.Equal(1, requiredManifest.SchemaVersion);
        Assert.Equal(0, Assert.Single(optionalManifest.Relationships).RequiredIncomingLinks);
        Assert.Equal(1, Assert.Single(optionalManifest.Relationships).MaximumIncomingLinks);
        Assert.Equal(1, Assert.Single(requiredManifest.Relationships).RequiredIncomingLinks);
        Assert.Contains("\"maximumIncomingLinks\":1", optionalManifest.CanonicalJson);
        Assert.DoesNotContain("maximumIncomingLinks", requiredManifest.CanonicalJson);
        Assert.NotEqual(optionalManifest.Fingerprint, requiredManifest.Fingerprint);
        Assert.Empty(((ProviderTestDriver)(object)driver).Calls);
    }

    [Fact]
    public void Optional_reader_accepts_absence_but_never_hides_corrupt_present_or_partial_membership()
    {
        using var db = new ProviderModelContext(ProviderTestDriver.Create(), b => Configure(b));
        var binding = GraphReadBinding.FromNodeReference(Reference(db));
        var fk = binding.Properties.Single(p => p.ModelProperty.Property.Name == nameof(Child.ParentId));
        var row = Row(binding);
        Assert.Null(binding.ReadRow(row)[fk.Index]);
        row[CypherQueryPlan.NodeReferenceCountAlias] = 1L;
        row[CypherQueryPlan.NodeReferencePrincipalAlias] = true;
        Assert.Contains("required property", Assert.Throws<InvalidOperationException>(() => binding.ReadRow(row)).Message);
        var parent = Guid.NewGuid(); row[fk.Column.Alias] = parent.ToString("D");
        Assert.Equal(parent, binding.ReadRow(row)[fk.Index]);
        row[CypherQueryPlan.NodeReferenceCountAlias] = 2L;
        Assert.Contains("membership diagnostic", Assert.Throws<InvalidOperationException>(() => binding.ReadRow(row)).Message);
        row[CypherQueryPlan.NodeReferenceCountAlias] = 1L; row[CypherQueryPlan.NodeReferencePrincipalAlias] = false;
        Assert.Throws<InvalidOperationException>(() => binding.ReadRow(row));
        row[CypherQueryPlan.NodeReferenceCountAlias] = 0L;
        Assert.Throws<InvalidOperationException>(() => binding.ReadRow(row));
        row[fk.Column.Alias] = null;
        var title = binding.Properties.Single(p => p.ModelProperty.Property.Name == nameof(Child.Title));
        Assert.Equal("title", binding.ReadRow(row, [title])[title.Index]);
        row.Remove(fk.Column.Alias);
        Assert.Throws<KeyNotFoundException>(() => binding.ReadRow(row, [title]));
    }

    [Fact]
    public void Optional_Include_emits_empty_rows_for_absence_and_keeps_paging_before_collection_expansion()
    {
        var driver = ProviderTestDriver.Create();
        using var db = new ProviderModelContext(driver, b => Configure(b));
        var text = db.Set<Child>().Include(c => c.Parent).Take(1).ToQueryString();
        Assert.Contains("CASE WHEN `__gb_node_reference_count` = 0 THEN [] ELSE [", text);
        Assert.Contains("WITH parent LIMIT 2", text);
        var collection = db.Set<Parent>().Take(1).Include(p => p.Children).ToQueryString();
        Assert.True(collection.IndexOf("LIMIT $", StringComparison.Ordinal) < collection.IndexOf("MATCH (n)-[:", StringComparison.Ordinal));
        var root = GraphReadBinding.FromNodeReference(Reference(db));
        var include = GraphIncludeBinding.Create(root, root.EntityType.FindNavigation(nameof(Child.Parent))!, db.GetService<GraphBindingCompiler>());
        Assert.Empty(include.ReadRows(Array.Empty<object>(), () => { }));
        Assert.Empty(((ProviderTestDriver)(object)driver).Calls);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Optional_update_plans_original_guard_and_all_needed_locks_before_scalar_concurrency(bool hadParent, bool hasParent)
    {
        var driver = ProviderTestDriver.Create();
        using var db = new AssociationMutationTests.PlanContext(driver, b => Configure(b));
        var original = hadParent ? Guid.NewGuid() : (Guid?)null;
        var current = hasParent ? Guid.NewGuid() : (Guid?)null;
        var child = new Child { Id = Guid.NewGuid(), ParentId = original, Title = "old", Version = 7 };
        db.Attach(child); child.ParentId = current; child.Title = "changed";
        Assert.Equal(1, await db.SaveChangesAsync(acceptAllChangesOnSuccess: false));
        var plan = Assert.IsType<GraphMutationPlan>(db.Capture.Plan);
        var operation = Assert.Single(plan.Operations);
        Assert.Equal(GraphMutationPhase.NodeUpdate, operation.Phase);
        Assert.Equal(2 + (hadParent ? 1 : 0) + (hasParent ? 1 : 0), operation.LockCommands.Count);
        var transition = operation.LockCommands.Last();
        Assert.Contains(hadParent ? "parents[0] = p" : "size(links) = 0", transition.Text);
        Assert.Equal(hasParent, transition.Text.Contains("CREATE (q)", StringComparison.Ordinal));
        if (hadParent) Assert.Equal(original!.Value.ToString("D"), transition.Parameters["p0"]);
        if (hasParent) Assert.Equal(current!.Value.ToString("D"), transition.Parameters["q0"]);
        Assert.Equal(original, db.Entry(child).Property(c => c.ParentId).OriginalValue);
        Assert.Equal(EntityState.Modified, db.Entry(child).State);
        Assert.Single(operation.GeneratedValues);
        Assert.Empty(((ProviderTestDriver)(object)driver).Calls);
    }

    [Fact]
    public async Task Optional_insert_without_parent_asserts_absence_and_never_stores_FK()
    {
        var driver = ProviderTestDriver.Create();
        using var db = new AssociationMutationTests.PlanContext(driver, b => Configure(b));
        db.Add(new Child { Id = Guid.NewGuid(), Title = "alone" });
        Assert.Equal(1, await db.SaveChangesAsync(acceptAllChangesOnSuccess: false));
        var plan = Assert.IsType<GraphMutationPlan>(db.Capture.Plan);
        Assert.Equal(2, plan.Operations.Count);
        var properties = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(plan.Operations[0].Command.Parameters["properties"]);
        Assert.DoesNotContain(nameof(Child.ParentId), properties.Keys);
        Assert.Contains("size(links) = 0", plan.Operations[1].Command.Text);
        Assert.DoesNotContain("CREATE (q)", plan.Operations[1].Command.Text);
        Assert.Single(plan.Operations[1].LockCommands);
        Assert.Empty(((ProviderTestDriver)(object)driver).Calls);
    }

    [Fact]
    public async Task Optional_composite_FK_is_all_null_or_complete_and_mixed_nullability_mapping_rejects()
    {
        var driver = ProviderTestDriver.Create();
        using var db = new AssociationMutationTests.PlanContext(driver, Composite);
        var child = new CompositeChild { Id = Guid.NewGuid(), ParentTenant = 7, ParentCode = null };
        db.Add(child);
        Assert.Contains("entirely null or fully specified", (await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync())).Message);
        child.ParentTenant = null;
        Assert.Equal(1, await db.SaveChangesAsync(acceptAllChangesOnSuccess: false));
        using var invalid = new ProviderModelContext(driver, b => { Composite(b); b.Entity<CompositeChild>().Property(c => c.ParentTenant).IsRequired(); });
        Assert.Contains("all nullable", Assert.Throws<InvalidOperationException>(() => _ = invalid.Model).Message);
        Assert.Empty(((ProviderTestDriver)(object)driver).Calls);
    }

    private static NodeReferenceBinding Reference(DbContext db) => Assert.Single(db.GetService<GraphBindingCompiler>().GetNodeReferences(db.Model.FindEntityType(typeof(Child))!));
    private static Dictionary<string, object?> Row(GraphReadBinding binding)
    {
        var row = binding.Properties.ToDictionary(p => p.Column.Alias, p => p.ModelProperty.Property.Name switch
        {
            nameof(Child.Id) => (object?)Guid.NewGuid().ToString("D"), nameof(Child.Title) => "title", nameof(Child.Version) => 1L, _ => null
        });
        row[CypherQueryPlan.NodeReferenceCountAlias] = 0L;
        row[CypherQueryPlan.NodeReferencePrincipalAlias] = false;
        return row;
    }
    private static void Configure(ModelBuilder b, bool required = false)
    {
        b.Entity<Parent>().ToGraphNode("OptionalParent");
        b.Entity<Child>().ToGraphNode("OptionalChild").Property(c => c.Version).IsRowVersion();
        b.Entity<Child>().HasOne(c => c.Parent).WithMany(p => p.Children).HasForeignKey(c => c.ParentId)
            .IsRequired(required).OnDelete(DeleteBehavior.Restrict).ToGraphReference("OPTIONAL_CHILD");
    }
    private static void Composite(ModelBuilder b)
    {
        b.Entity<CompositeParent>().ToGraphNode("OptionalCompositeParent").HasKey(p => new { p.Tenant, p.Code });
        b.Entity<CompositeChild>().ToGraphNode("OptionalCompositeChild").HasOne<CompositeParent>().WithMany()
            .HasForeignKey(c => new { c.ParentTenant, c.ParentCode }).IsRequired(false).OnDelete(DeleteBehavior.Restrict).ToGraphReference("OPTIONAL_COMPOSITE_CHILD");
    }
    public sealed class Parent { public Guid Id { get; set; } public List<Child> Children { get; } = []; }
    public sealed class Child { public Guid Id { get; set; } public Guid? ParentId { get; set; } public Parent? Parent { get; set; } public string Title { get; set; } = ""; public long Version { get; set; } }
    public sealed class CompositeParent { public long Tenant { get; set; } public string Code { get; set; } = ""; }
    public sealed class CompositeChild { public Guid Id { get; set; } public long? ParentTenant { get; set; } public string? ParentCode { get; set; } }
}
