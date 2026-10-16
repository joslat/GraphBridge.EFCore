using GraphBridge.EntityFrameworkCore.Cypher;
using GraphBridge.EntityFrameworkCore.Diagnostics;
using GraphBridge.EntityFrameworkCore.Metadata;
using GraphBridge.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Neo4j.Driver;

namespace GraphBridge.UnitTests;

public sealed class NodeReferenceReadTests : IDisposable
{
    private readonly IDriver _driver = ProviderTestDriver.Create();
    public void Dispose() => Assert.Empty(((ProviderTestDriver)(object)_driver).Calls);

    [Fact]
    public void Binding_derives_FK_from_principal_key_and_preserves_every_finalized_EF_slot()
    {
        using var context = Context();
        var read = Binding<Post>(context);
        Assert.Equal(new CypherNodeReferenceRoot("ReadPost", "READ_HAS_POST", "ReadBlog"), read.Root);
        Assert.Equal(read.EntityType.GetProperties().OrderBy(p => p.GetIndex()), read.Properties.Select(p => p.ModelProperty.Property));
        Assert.Equal(new CypherProperty("blog_key", CypherPropertyOwner.Source), Slot(read, nameof(Post.BlogId)).StoreProperty);
        Assert.Equal(new CypherProperty("post_key"), Slot(read, nameof(Post.Id)).StoreProperty);
        Assert.Equal(new[] { "ReadPost", "ReadBlog" }, read.RequiredUniqueEndpoints.Select(n => n.Label));
        Assert.Throws<NotSupportedException>(() => ((IList<NodeBinding>)read.RequiredUniqueEndpoints).Clear());
        Assert.Empty(read.RequiredUniqueIdentifiedEdges);
        var values = read.ReadRow(Row(read));
        Assert.Equal(Guid.Parse("11000000-0000-0000-0000-000000000001"), values[Slot(read, nameof(Post.BlogId)).Index]);
        Assert.Equal(Guid.Parse("22000000-0000-0000-0000-000000000002"), values[Slot(read, nameof(Post.Id)).Index]);
        Assert.Equal("title", values[Slot(read, nameof(Post.Title)).Index]);
        Assert.Null(values[Slot(read, nameof(Post.Note)).Index]);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("null")]
    [InlineData("zero")]
    [InlineData("two")]
    [InlineData("negative")]
    [InlineData("integer")]
    [InlineData("text")]
    public void Invalid_membership_count_rejects_before_any_property_conversion(string fault)
    {
        using var context = Context();
        var read = Binding<Post>(context);
        var row = Row(read);
        row[Slot(read, nameof(Post.Id)).Column.Alias] = "bad Guid";
        row[CypherQueryPlan.NodeReferenceCountAlias] = fault switch
        {
            "null" => null, "zero" => 0L, "two" => 2L, "negative" => -1L,
            "integer" => (object)1, "text" => "1", _ => 1L
        };
        if (fault == "missing") row.Remove(CypherQueryPlan.NodeReferenceCountAlias);
        Assert.Contains("membership diagnostic", Assert.Throws<InvalidOperationException>(() => read.ReadRow(row)).Message);
        Assert.Empty(context.ChangeTracker.Entries());
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("null")]
    [InlineData("false")]
    [InlineData("integer")]
    [InlineData("text")]
    public void Missing_wrong_label_or_invalid_principal_diagnostic_rejects_before_conversion(string fault)
    {
        using var context = Context();
        var read = Binding<Post>(context);
        var row = Row(read);
        row[Slot(read, nameof(Post.BlogId)).Column.Alias] = "bad Guid";
        row[CypherQueryPlan.NodeReferencePrincipalAlias] = fault switch
        {
            "null" => null, "false" => false, "integer" => (object)1L, "text" => "true", _ => true
        };
        if (fault == "missing") row.Remove(CypherQueryPlan.NodeReferencePrincipalAlias);
        Assert.Contains("membership diagnostic", Assert.Throws<InvalidOperationException>(() => read.ReadRow(row)).Message);
    }

    [Theory]
    [InlineData(nameof(Post.Id))]
    [InlineData(nameof(Post.BlogId))]
    public void Scalar_projection_forces_dependent_and_principal_keys_and_checks_presence_and_canonical_wire_values(string name)
    {
        using var context = Context();
        var read = Binding<Post>(context);
        var title = Slot(read, nameof(Post.Title));
        var selected = new[] { title };
        var plan = read.CreatePlan(selected);
        Assert.Equal(3, plan.Columns.Count);
        Assert.Contains(plan.Columns, c => c.Name == "post_key" && c.Owner == CypherPropertyOwner.Node);
        Assert.Contains(plan.Columns, c => c.Name == "blog_key" && c.Owner == CypherPropertyOwner.Source);
        var row = Row(read);
        row.Remove(Slot(read, nameof(Post.Note)).Column.Alias);
        Assert.Equal("title", read.ReadRow(row, selected)[title.Index]);
        var key = Slot(read, name).Column.Alias;
        row[key] = null;
        Assert.Contains("required property", Assert.Throws<InvalidOperationException>(() => read.ReadRow(row, selected)).Message);
        row[key] = "ABCDEF00-1234-5678-90AB-CDEF00123456";
        Assert.Contains("canonical store representation", Assert.Throws<InvalidOperationException>(() => read.ReadRow(row, selected)).Message);
        row[key] = "malformed Guid";
        Assert.Throws<FormatException>(() => read.ReadRow(row, selected));
        row.Remove(key);
        Assert.Throws<KeyNotFoundException>(() => read.ReadRow(row, selected));
    }

    [Fact]
    public void Scalar_and_entity_LINQ_use_principal_FK_parameter_and_preserve_diagnostics_before_paging()
    {
        using var context = Context();
        var blogId = Guid.NewGuid();
        var text = context.Set<Post>().AsNoTracking().Where(p => p.BlogId == blogId && p.Title != "secret")
            .OrderBy(p => p.BlogId).ThenBy(p => p.Title).Skip(1).Take(2).Select(p => p.Title).ToQueryString();
        Assert.StartsWith("MATCH (n:`ReadPost`)\nCALL (n)", text);
        Assert.Contains("OPTIONAL MATCH (parent)-[candidate:`READ_HAS_POST`]->(n)", text);
        Assert.Contains("WITH parent LIMIT 2\n  RETURN collect(parent) AS parents", text);
        Assert.Contains("CASE WHEN size(parents) = 1 THEN parents[0] ELSE null END AS parent", text);
        Assert.Contains("coalesce(parent:`ReadBlog`, false)", text);
        Assert.Contains("CASE WHEN parent:`ReadBlog` THEN parent ELSE null END AS s", text);
        Assert.Contains("s.`blog_key` = $", text);
        Assert.Contains("n.`post_key` AS", text);
        Assert.Contains("s.`blog_key` AS", text);
        Assert.Contains("ORDER BY s.`blog_key`", text);
        Assert.Contains("\nSKIP $", text);
        Assert.Contains("\nLIMIT $", text);
        Assert.True(text.IndexOf("WITH parent LIMIT 2", StringComparison.Ordinal) < text.IndexOf("parent:`ReadBlog`", StringComparison.Ordinal));
        Assert.True(text.IndexOf(CypherQueryPlan.NodeReferenceCountAlias, StringComparison.Ordinal) < text.IndexOf("\nWHERE", StringComparison.Ordinal));
        Assert.Contains(CypherQueryPlan.NodeReferencePrincipalAlias, text);
        Assert.DoesNotContain("n.`BlogId`", text);
        Assert.DoesNotContain("DISTINCT", text);
        Assert.DoesNotContain("elementId", text);
        Assert.DoesNotContain(blogId.ToString(), text);
        Assert.DoesNotContain("secret", text);
        Assert.Contains(CypherQueryPlan.NodeReferenceCountAlias, context.Set<Post>().ToQueryString());
    }

    [Fact]
    public void Composite_shadow_FKs_use_ordered_renamed_principal_keys_and_inherited_converter()
    {
        var converter = new ValueConverter<Guid, string>(id => id.ToString("N"), text => Guid.ParseExact(text, "N"));
        using var context = new ProviderModelContext(_driver, model =>
        {
            model.Entity<CompositeParent>().ToGraphNode("ReadCompositeParent").HasKey(p => new { p.Tenant, p.Code });
            model.Entity<CompositeParent>().Property(p => p.Tenant).HasGraphPropertyName("tenant_key");
            model.Entity<CompositeParent>().Property(p => p.Code).HasGraphPropertyName("code_key").HasConversion(converter);
            var dependent = model.Entity<ShadowChild>().ToGraphNode("ReadShadowChild");
            dependent.Property<long>("ParentTenant");
            dependent.Property<Guid>("ParentCode");
            dependent.HasOne<CompositeParent>().WithMany().HasForeignKey("ParentTenant", "ParentCode")
                .IsRequired().OnDelete(DeleteBehavior.Restrict).ToGraphReference("READ_COMPOSITE_CHILD");
        });
        var read = Binding<ShadowChild>(context);
        var tenant = Slot(read, "ParentTenant");
        var code = Slot(read, "ParentCode");
        var title = Slot(read, nameof(ShadowChild.Title));
        Assert.True(tenant.ModelProperty.Property.IsShadowProperty());
        Assert.True(code.ModelProperty.Property.IsShadowProperty());
        Assert.Equal(new CypherProperty("tenant_key", CypherPropertyOwner.Source), tenant.StoreProperty);
        Assert.Equal(new CypherProperty("code_key", CypherPropertyOwner.Source), code.StoreProperty);
        var id = Guid.Parse("abcdef00-1234-5678-90ab-cdef00123456");
        var row = new Dictionary<string, object?>
        {
            [CypherQueryPlan.NodeReferenceCountAlias] = 1L,
            [CypherQueryPlan.NodeReferencePrincipalAlias] = true,
            [Slot(read, nameof(ShadowChild.Id)).Column.Alias] = Guid.NewGuid().ToString("D"),
            [tenant.Column.Alias] = 12L, [code.Column.Alias] = id.ToString("N"), [title.Column.Alias] = "shadow"
        };
        Assert.Equal(4, read.CreatePlan([title]).Columns.Count);
        Assert.Equal(id, read.ReadRow(row, [title])[code.Index]);
        row[code.Column.Alias] = id.ToString("N").ToUpperInvariant();
        Assert.Contains("canonical store representation", Assert.Throws<InvalidOperationException>(() => read.ReadRow(row, [title])).Message);
        var text = context.Set<ShadowChild>().Where(p => EF.Property<Guid>(p, "ParentCode") == id).ToQueryString();
        Assert.Contains("s.`code_key` = $", text);
        Assert.DoesNotContain("n.`ParentCode`", text);
    }

    [Fact]
    public void Principal_root_remains_ordinary_node_and_chain_reads_only_immediate_parent()
    {
        using var context = Context(model =>
        {
            model.Entity<Comment>().ToGraphNode("ReadComment").HasOne<Post>().WithMany().HasForeignKey(c => c.PostId)
                .IsRequired().OnDelete(DeleteBehavior.Restrict).ToGraphReference("READ_HAS_COMMENT");
        });
        var text = context.Set<Blog>().ToQueryString();
        Assert.StartsWith("MATCH (n:`ReadBlog`)\nRETURN", text);
        Assert.DoesNotContain("CALL", text);
        var read = Binding<Comment>(context);
        Assert.Equal(new[] { "ReadComment", "ReadPost" }, read.RequiredUniqueEndpoints.Select(n => n.Label));
        var child = context.Set<Comment>().ToQueryString();
        Assert.Contains("READ_HAS_COMMENT", child);
        Assert.DoesNotContain("READ_HAS_POST", child);
        Assert.DoesNotContain("ReadBlog", child);
    }

    [Fact]
    public void Dependent_aggregates_reject_before_transport_including_direct_renderer_paths()
    {
        using var context = Context();
        var posts = context.Set<Post>();
        Assert.Contains("membership diagnostics", Assert.Throws<Neo4jQueryTranslationException>(() => posts.Count()).Message);
        Assert.Throws<Neo4jQueryTranslationException>(() => posts.LongCount(p => p.Title == "x"));
        Assert.Throws<Neo4jQueryTranslationException>(() => posts.Any());
        Assert.Throws<Neo4jQueryTranslationException>(() => posts.Select(p => p.Title).Count());
        var plan = Binding<Post>(context).CreatePlan();
        Assert.Throws<NotSupportedException>(() => CypherQueryRenderer.Render(plan with { Aggregate = CypherAggregate.Count }));
        Assert.Throws<NotSupportedException>(() => CypherQueryRenderer.Render(plan with { Aggregate = CypherAggregate.Any }));
    }

    [Fact]
    public void Renderer_validates_all_new_identifiers_owners_and_reserved_output_aliases()
    {
        var root = new CypherNodeReferenceRoot("Post", "HAS_POST", "Blog");
        var plan = new CypherQueryPlan(root, [new("title", "c0")]);
        foreach (var invalid in new[] { root with { DependentLabel = "bad`" }, root with { PrincipalLabel = "bad`" }, root with { RelationshipType = "bad`" } })
            Assert.Throws<InvalidOperationException>(() => CypherQueryRenderer.Render(plan with { Root = invalid }));
        foreach (var owner in new[] { CypherPropertyOwner.Relationship, CypherPropertyOwner.Target })
            Assert.Throws<InvalidOperationException>(() => CypherQueryRenderer.Render(plan with { Columns = [new("title", "c0", owner)] }));
        foreach (var alias in new[] { CypherQueryPlan.NodeReferenceCountAlias, CypherQueryPlan.NodeReferencePrincipalAlias })
            Assert.Throws<InvalidOperationException>(() => CypherQueryRenderer.Render(plan with { Columns = [new("title", alias)] }));
        Assert.Throws<InvalidOperationException>(() => CypherQueryRenderer.Render(plan with { Columns = [new("title", "c0"), new("key", "c0", CypherPropertyOwner.Source)] }));
    }

    private ProviderModelContext Context(Action<ModelBuilder>? extra = null) => new(_driver, model =>
    {
        model.Entity<Blog>().ToGraphNode("ReadBlog").Property(p => p.Id).HasGraphPropertyName("blog_key");
        model.Entity<Post>().ToGraphNode("ReadPost").Property(p => p.Id).HasGraphPropertyName("post_key");
        model.Entity<Post>().HasOne(p => p.Blog).WithMany(b => b.Posts).HasForeignKey(p => p.BlogId)
            .IsRequired().OnDelete(DeleteBehavior.Restrict).ToGraphReference("READ_HAS_POST");
        extra?.Invoke(model);
    });

    private static GraphReadBinding Binding<T>(ProviderModelContext context)
        => GraphReadBinding.FromNodeReference(Assert.Single(context.GetService<GraphBindingCompiler>()
            .GetNodeReferences(context.Model.FindEntityType(typeof(T))!)));

    private static GraphReadProperty Slot(GraphReadBinding read, string name)
        => read.Properties.Single(p => p.ModelProperty.Property.Name == name);

    private static Dictionary<string, object?> Row(GraphReadBinding read)
    {
        var row = read.Properties.ToDictionary(p => p.Column.Alias, p => p.ModelProperty.Property.Name switch
        {
            nameof(Post.Id) => (object?)"22000000-0000-0000-0000-000000000002",
            nameof(Post.BlogId) => "11000000-0000-0000-0000-000000000001",
            nameof(Post.Title) => "title", _ => null
        });
        row[CypherQueryPlan.NodeReferenceCountAlias] = 1L;
        row[CypherQueryPlan.NodeReferencePrincipalAlias] = true;
        return row;
    }

    public sealed class Blog { public Guid Id { get; set; } public List<Post> Posts { get; } = []; }
    public sealed class Post
    {
        public Guid Id { get; set; }
        public Guid BlogId { get; set; }
        public string Title { get; set; } = "";
        public string? Note { get; set; }
        public Blog Blog { get; set; } = null!;
    }
    public sealed class Comment { public Guid Id { get; set; } public Guid PostId { get; set; } }
    public sealed class CompositeParent { public long Tenant { get; set; } public Guid Code { get; set; } }
    public sealed class ShadowChild { public Guid Id { get; set; } public string Title { get; set; } = ""; }
}
