using GraphBridge.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Neo4j.Driver;

namespace GraphBridge.UnitTests;

public sealed class NodeReferenceBindingTests : IDisposable
{
    private readonly IDriver _driver = ProviderTestDriver.Create();
    public void Dispose() => Assert.Empty(((ProviderTestDriver)(object)_driver).Calls);

    [Theory]
    [InlineData(DeleteBehavior.Restrict)]
    [InlineData(DeleteBehavior.NoAction)]
    public void Finalized_reference_binding_preserves_EF_identity_direction_and_derived_FK_slots(DeleteBehavior deletion)
    {
        using var context = Context(model => Configure(model).OnDelete(deletion));
        var compiler = context.GetService<GraphBindingCompiler>();
        var postType = context.Model.FindEntityType(typeof(Post))!;
        var blogType = context.Model.FindEntityType(typeof(Blog))!;
        var reference = Assert.Single(compiler.GetNodeReferences(postType));
        Assert.Empty(compiler.GetNodeReferences(blogType));
        Assert.Equal("HAS_POST", reference.RelationshipType);
        Assert.Same(postType.GetForeignKeys().Single(), reference.ForeignKey);
        Assert.Same(reference, compiler.GetNodeReference(reference.ForeignKey));
        Assert.Same(compiler.GetNode(blogType), reference.Principal);
        Assert.Same(compiler.GetNode(postType), reference.Dependent);
        Assert.Equal(GraphStorageKind.Node, compiler.GetStorageKind(postType));
        Assert.Equal(deletion, reference.ForeignKey.DeleteBehavior);
        Assert.Equal(nameof(Post.Blog), reference.ForeignKey.DependentToPrincipal!.Name);
        Assert.Equal(nameof(Blog.Posts), reference.ForeignKey.PrincipalToDependent!.Name);
        Assert.Equal(nameof(Post.BlogId), Assert.Single(reference.ForeignKeyProperties).Property.Name);
        Assert.Equal("blogId", Assert.Single(reference.PrincipalKeyProperties).Name);
        Assert.Contains(reference.Dependent.Properties, property => property.Property.Name == nameof(Post.BlogId));
        Assert.DoesNotContain(reference.Dependent.StoredProperties, property => property.Property.Name == nameof(Post.BlogId));
        Assert.Equal(reference.Dependent.EntityType.GetProperties().OrderBy(p => p.GetIndex()), reference.Dependent.Properties.Select(p => p.Property));
        Assert.Equal(reference.Principal.Properties, reference.Principal.StoredProperties);
        Assert.Throws<NotSupportedException>(() => ((IList<GraphPropertyBinding>)reference.ForeignKeyProperties).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<GraphPropertyBinding>)reference.PrincipalKeyProperties).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<GraphPropertyBinding>)reference.Dependent.StoredProperties).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<NodeReferenceBinding>)compiler.GetNodeReferences(postType)).Clear());
        using var reused = new DbContext(new DbContextOptionsBuilder().UseNeo4j(_driver).UseModel(context.Model).Options);
        Assert.Same(reference, reused.GetService<GraphBindingCompiler>().GetNodeReference(reference.ForeignKey));
    }

    [Fact]
    public void Composite_reference_slots_preserve_declared_order_and_principal_store_names()
    {
        using var context = Context(model =>
        {
            model.Entity<CompositeBlog>().ToGraphNode("CompositeBlog").HasKey(blog => new { blog.Tenant, blog.Code });
            model.Entity<CompositeBlog>().Property(blog => blog.Tenant).HasGraphPropertyName("tenant");
            model.Entity<CompositeBlog>().Property(blog => blog.Code).HasGraphPropertyName("code");
            model.Entity<CompositePost>().ToGraphNode("CompositePost").HasOne<CompositeBlog>().WithMany()
                .HasForeignKey(post => new { post.BlogTenant, post.BlogCode }).IsRequired().OnDelete(DeleteBehavior.Restrict)
                .ToGraphReference("HAS_COMPOSITE_POST");
        });
        var binding = Assert.Single(context.GetService<GraphBindingCompiler>().GetNodeReferences(context.Model.FindEntityType(typeof(CompositePost))!));
        Assert.Equal(new[] { "BlogTenant", "BlogCode" }, binding.ForeignKeyProperties.Select(p => p.Property.Name));
        Assert.Equal(new[] { "tenant", "code" }, binding.PrincipalKeyProperties.Select(p => p.Name));
        Assert.Equal(3L, binding.ForeignKeyProperties[0].ToStore(3L));
        Assert.Equal("A", binding.ForeignKeyProperties[1].ToStore("A"));
        Assert.Equal(nameof(CompositePost.Id), Assert.Single(binding.Dependent.StoredProperties).Property.Name);
    }

    [Fact]
    public void Public_generic_and_nongeneric_extensions_annotate_only_the_FK_and_validate_identifiers()
    {
        var model = new ModelBuilder();
        var typed = Configure(model);
        Assert.Same(typed, typed.ToGraphReference("HAS_POST"));
        ReferenceCollectionBuilder untyped = typed;
        Assert.Same(untyped, untyped.ToGraphReference("HAS_POST"));
        Assert.Equal("HAS_POST", typed.Metadata[GraphAnnotationNames.NodeReferenceType]);
        Assert.Null(model.Entity<Post>().Metadata.FindAnnotation(GraphAnnotationNames.NodeReferenceType));
        Assert.Throws<ArgumentException>(() => typed.ToGraphReference("HAS`POST"));
        Assert.Throws<ArgumentException>(() => untyped.ToGraphReference(""));
        Assert.Throws<ArgumentNullException>(() => GraphModelBuilderExtensions.ToGraphReference(null!, "HAS_POST"));
    }

    [Fact]
    public void Ordinary_scalar_BlogId_is_stored_until_an_explicit_FK_mapping_exists()
    {
        using var context = Context(model => model.Entity<PlainPost>().ToGraphNode("PlainPost"));
        var compiler = context.GetService<GraphBindingCompiler>();
        var node = compiler.GetNode(context.Model.FindEntityType(typeof(PlainPost))!);
        Assert.Empty(compiler.GetNodeReferences(node.EntityType));
        Assert.Contains(node.StoredProperties, p => p.Property.Name == nameof(PlainPost.BlogId));
        Assert.Equal(node.Properties, node.StoredProperties);
    }

    [Fact]
    public void Explicit_shadow_FK_retains_EF_slot_but_no_dependent_storage_property()
    {
        using var context = Context(model =>
        {
            model.Entity<PlainBlog>().ToGraphNode("PlainBlog");
            model.Entity<ShadowPost>().ToGraphNode("ShadowPost").HasOne<PlainBlog>().WithMany()
                .HasForeignKey("BlogId").IsRequired().OnDelete(DeleteBehavior.Restrict).ToGraphReference("HAS_SHADOW_POST");
        });
        var binding = Assert.Single(context.GetService<GraphBindingCompiler>().GetNodeReferences(context.Model.FindEntityType(typeof(ShadowPost))!));
        Assert.True(Assert.Single(binding.ForeignKeyProperties).Property.IsShadowProperty());
        Assert.Equal(nameof(ShadowPost.Id), Assert.Single(binding.Dependent.StoredProperties).Property.Name);
    }

    [Fact]
    public void Missing_mapping_and_wrong_annotation_target_are_rejected()
    {
        Invalid(model => Configure(model).Metadata.RemoveAnnotation(GraphAnnotationNames.NodeReferenceType), "relationship storage");
        Invalid(model =>
        {
            Configure(model);
            model.Entity<Post>().HasAnnotation(GraphAnnotationNames.NodeReferenceType, "HAS_POST");
        }, "belongs on an EF foreign key");
        Invalid(model => Configure(model).HasAnnotation(GraphAnnotationNames.NodeReferenceType, 123), "relationship type identifier");
    }

    [Theory]
    [InlineData(DeleteBehavior.Cascade)]
    [InlineData(DeleteBehavior.ClientCascade)]
    [InlineData(DeleteBehavior.ClientSetNull)]
    [InlineData(DeleteBehavior.ClientNoAction)]
    [InlineData(DeleteBehavior.SetNull)]
    public void Cascade_and_null_setting_deletion_policies_are_not_silently_lowered(DeleteBehavior deletion)
        => Invalid(model => Configure(model).OnDelete(deletion), "Restrict or NoAction");

    [Fact]
    public void Unique_references_are_rejected()
    {
        Invalid(model =>
        {
            model.Entity<PlainBlog>().ToGraphNode("PlainBlog");
            var relationship = model.Entity<PlainPost>().ToGraphNode("PlainPost").HasOne<PlainBlog>().WithOne()
                .HasForeignKey<PlainPost>(post => post.BlogId).IsRequired().OnDelete(DeleteBehavior.Restrict);
            relationship.HasAnnotation(GraphAnnotationNames.NodeReferenceType, "HAS_POST");
        }, "many-to-one");
    }

    [Fact]
    public void Alternate_principal_key_and_dependent_key_FK_slots_are_rejected()
    {
        Invalid(model =>
        {
            Configure(model).HasPrincipalKey(blog => blog.AlternateId);
        }, "principal primary key");
        Invalid(model =>
        {
            Configure(model);
            model.Entity<Post>().HasKey(post => new { post.Id, post.BlogId });
        }, "disjoint from dependent keys");
        Invalid(model =>
        {
            Configure(model);
            model.Entity<Post>().HasAlternateKey(post => post.BlogId);
        }, "disjoint from dependent keys");
    }

    [Fact]
    public void Derived_FK_cannot_declare_generation_concurrency_or_a_stored_property_name()
    {
        Invalid(model => { Configure(model); model.Entity<Post>().Property(post => post.BlogId).ValueGeneratedOnAdd(); }, "stable non-generated");
        Invalid(model => { Configure(model); model.Entity<Post>().Property(post => post.BlogId).IsConcurrencyToken(); }, "concurrency tokens");
        Invalid(model => { Configure(model); model.Entity<Post>().Property(post => post.BlogId).HasGraphPropertyName("blog_id"); }, "derived from principal keys");
    }

    [Fact]
    public void Principal_key_OnUpdateSometimes_generation_is_not_mistaken_for_stable_identity()
    {
        Invalid(model =>
        {
            Configure(model);
            model.Entity<Blog>().Property(blog => blog.Id).Metadata.ValueGenerated = ValueGenerated.OnUpdateSometimes;
        }, "principal keys must be stable");
    }

    [Fact]
    public void Derived_FK_metadata_name_does_not_reserve_a_physical_node_property_name()
    {
        using var context = Context(model =>
        {
            Configure(model);
            model.Entity<Post>().Property(post => post.Title).HasGraphPropertyName(nameof(Post.BlogId));
        });
        var node = context.GetService<GraphBindingCompiler>().GetNode(context.Model.FindEntityType(typeof(Post))!);
        var stored = Assert.Single(node.StoredProperties, property => property.Name == nameof(Post.BlogId));
        Assert.Equal(nameof(Post.Title), stored.Property.Name);
        Assert.Equal(2, node.Properties.Count(property => property.Name == nameof(Post.BlogId)));
        Invalid(model =>
        {
            Configure(model);
            model.Entity<Post>().Property(post => post.Title).HasGraphPropertyName(nameof(Post.Id));
        }, "store property name 'Id' is mapped more than once");
    }

    [Fact]
    public void More_than_one_reference_per_dependent_is_rejected()
    {
        Invalid(model =>
        {
            Configure(model);
            model.Entity<Post>().HasOne<Blog>().WithMany().HasForeignKey("SecondBlogId").IsRequired()
                .OnDelete(DeleteBehavior.Restrict).ToGraphReference("SECOND_HAS_POST");
        }, "only one native node-reference");
    }

    [Fact]
    public void Relationship_type_ownership_is_global_across_references()
    {
        Invalid(model =>
        {
            Configure(model);
            model.Entity<PlainPost>().ToGraphNode("PlainPost").HasOne<Blog>().WithMany().HasForeignKey(post => post.BlogId)
                .IsRequired().OnDelete(DeleteBehavior.Restrict).ToGraphReference("HAS_POST");
        }, "globally unique mapping");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Relationship_type_cannot_be_shared_with_association_or_identified_storage(bool identified)
    {
        Invalid(model =>
        {
            Configure(model);
            model.Entity<PlainBlog>().ToGraphNode("OtherBlog");
            var join = model.Entity<Credit>();
            join.HasOne<Blog>().WithMany().HasForeignKey(credit => credit.BlogId);
            join.HasOne<PlainBlog>().WithMany().HasForeignKey(credit => credit.OtherBlogId);
            if (identified)
                join.ToGraphIdentifiedRelationship("HAS_POST", nameof(Credit.Id), [nameof(Credit.BlogId)], [nameof(Credit.OtherBlogId)]);
            else
            {
                join.Ignore(credit => credit.Id);
                join.HasKey(credit => new { credit.BlogId, credit.OtherBlogId });
                join.ToGraphRelationship("HAS_POST", [nameof(Credit.BlogId)], [nameof(Credit.OtherBlogId)]);
            }
        }, "globally unique mapping");
    }

    [Fact]
    public void Edge_endpoint_FKs_cannot_be_relabelled_as_node_references()
    {
        Invalid(model =>
        {
            model.Entity<PlainBlog>().ToGraphNode("PlainBlog");
            model.Entity<PlainPost>().ToGraphNode("PlainPost");
            var join = model.Entity<Credit>();
            join.HasOne<PlainBlog>().WithMany().HasForeignKey(credit => credit.BlogId)
                .OnDelete(DeleteBehavior.Restrict).ToGraphReference("REFERENCE");
            join.HasOne<PlainPost>().WithMany().HasForeignKey(credit => credit.OtherBlogId);
            join.ToGraphIdentifiedRelationship("CREDIT", nameof(Credit.Id), [nameof(Credit.BlogId)], [nameof(Credit.OtherBlogId)]);
        }, "cannot be configured on association");
    }

    [Fact]
    public void Self_reference_and_longer_cycles_are_rejected_while_acyclic_chains_bind()
    {
        Invalid(model =>
        {
            model.Entity<PlainPost>().ToGraphNode("Self").HasOne<PlainPost>().WithMany().HasForeignKey(post => post.BlogId)
                .IsRequired().OnDelete(DeleteBehavior.Restrict).ToGraphReference("SELF");
        }, "cyclic native node references");
        Invalid(model => ConfigureChain(model, cycle: true), "cyclic native node references");
        using var context = Context(model => ConfigureChain(model, cycle: false));
        var compiler = context.GetService<GraphBindingCompiler>();
        Assert.Single(compiler.GetNodeReferences(context.Model.FindEntityType(typeof(CycleA))!));
        Assert.Single(compiler.GetNodeReferences(context.Model.FindEntityType(typeof(CycleB))!));
        Assert.Empty(compiler.GetNodeReferences(context.Model.FindEntityType(typeof(CycleC))!));
    }

    [Fact]
    public void FK_inherits_principal_converter_but_different_resolved_converters_fail()
    {
        var converter = new ValueConverter<Guid, string>(id => id.ToString("N"), text => Guid.ParseExact(text, "N"));
        foreach (var configureDependent in new[] { false, true })
        {
            using var context = Context(model =>
            {
                Configure(model);
                model.Entity<Blog>().Property(blog => blog.Id).HasConversion(converter);
                if (configureDependent) model.Entity<Post>().Property(post => post.BlogId).HasConversion(converter);
            });
            var binding = Assert.Single(context.GetService<GraphBindingCompiler>().GetNodeReferences(context.Model.FindEntityType(typeof(Post))!));
            var id = Guid.Parse("11000000-0000-0000-0000-000000000001");
            Assert.Equal(id.ToString("N"), Assert.Single(binding.ForeignKeyProperties).ToStore(id));
            Assert.Equal(Assert.Single(binding.PrincipalKeyProperties).ToStore(id), Assert.Single(binding.ForeignKeyProperties).ToStore(id));
        }
        Invalid(model =>
        {
            Configure(model);
            model.Entity<Blog>().Property(blog => blog.Id).HasConversion(converter);
            model.Entity<Post>().Property(post => post.BlogId).HasConversion(new ValueConverter<Guid, string>(id => id.ToString("D"), text => Guid.ParseExact(text, "D")));
        }, "same resolved converter instance");
    }

    private ProviderModelContext Context(Action<ModelBuilder> configure) => new(_driver, configure);
    private void Invalid(Action<ModelBuilder> configure, string expected)
    {
        using var context = Context(configure);
        Assert.Contains(expected, Assert.Throws<InvalidOperationException>(() => context.Model).Message);
    }

    private static ReferenceCollectionBuilder<Blog, Post> Configure(ModelBuilder model)
    {
        model.Entity<Blog>().ToGraphNode("Blog").Property(blog => blog.Id).HasGraphPropertyName("blogId");
        model.Entity<Post>().ToGraphNode("Post");
        return model.Entity<Post>().HasOne(post => post.Blog).WithMany(blog => blog.Posts).HasForeignKey(post => post.BlogId)
            .IsRequired().OnDelete(DeleteBehavior.Restrict).ToGraphReference("HAS_POST");
    }

    private static void ConfigureChain(ModelBuilder model, bool cycle)
    {
        model.Entity<CycleA>().ToGraphNode("A");
        model.Entity<CycleB>().ToGraphNode("B");
        model.Entity<CycleC>().ToGraphNode("C");
        model.Entity<CycleA>().HasOne<CycleB>().WithMany().HasForeignKey(value => value.OtherId).IsRequired().OnDelete(DeleteBehavior.Restrict).ToGraphReference("B_TO_A");
        model.Entity<CycleB>().HasOne<CycleC>().WithMany().HasForeignKey(value => value.OtherId).IsRequired().OnDelete(DeleteBehavior.Restrict).ToGraphReference("C_TO_B");
        if (cycle) model.Entity<CycleC>().HasOne<CycleA>().WithMany().HasForeignKey(value => value.OtherId).IsRequired().OnDelete(DeleteBehavior.Restrict).ToGraphReference("A_TO_C");
    }

    public sealed class Blog
    {
        public Guid Id { get; set; }
        public Guid AlternateId { get; set; }
        public List<Post> Posts { get; } = [];
    }
    public sealed class Post
    {
        public Guid Id { get; set; }
        public Guid BlogId { get; set; }
        public string Title { get; set; } = "";
        public Blog Blog { get; set; } = null!;
    }
    public sealed class PlainBlog { public Guid Id { get; set; } }
    public sealed class PlainPost { public Guid Id { get; set; } public Guid BlogId { get; set; } }
    public sealed class OptionalPost { public Guid Id { get; set; } public Guid? BlogId { get; set; } }
    public sealed class ShadowPost { public Guid Id { get; set; } }
    public sealed class CompositeBlog { public long Tenant { get; set; } public string Code { get; set; } = ""; }
    public sealed class CompositePost { public Guid Id { get; set; } public long BlogTenant { get; set; } public string BlogCode { get; set; } = ""; }
    public sealed class Credit { public Guid Id { get; set; } public Guid BlogId { get; set; } public Guid OtherBlogId { get; set; } }
    public sealed class CycleA { public Guid Id { get; set; } public Guid OtherId { get; set; } }
    public sealed class CycleB { public Guid Id { get; set; } public Guid OtherId { get; set; } }
    public sealed class CycleC { public Guid Id { get; set; } public Guid OtherId { get; set; } }
}
