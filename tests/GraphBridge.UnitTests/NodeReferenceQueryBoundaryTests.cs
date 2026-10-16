using GraphBridge.EntityFrameworkCore.Diagnostics;
using GraphBridge.EntityFrameworkCore.Schema;
using Microsoft.EntityFrameworkCore;

namespace GraphBridge.UnitTests;

public sealed class NodeReferenceQueryBoundaryTests
{
    [Fact]
    public async Task Bound_reference_model_translates_roots_Includes_and_witness_but_rejects_aggregates_before_transport()
    {
        var driver = ProviderTestDriver.Create();
        await using var context = new ProviderModelContext(driver, Configure);
        var blog = new ReferenceBlog { Id = Guid.NewGuid() };
        var post = new ReferencePost { Id = Guid.NewGuid(), Blog = blog };
        context.Add(post);
        Assert.Equal(blog.Id, post.BlogId);
        Assert.Same(post, Assert.Single(blog.Posts));
        Assert.Equal(2, context.ChangeTracker.Entries().Count());

        Assert.Contains("HAS_POST", context.Set<ReferencePost>().ToQueryString());
        Assert.Contains("ReferenceBlog", context.Set<ReferenceBlog>().ToQueryString());
        var manifest = context.Database.GetGraphSchemaManifest();
        Assert.Equal(GraphSchemaRelationshipIdentity.DependentNode, Assert.Single(manifest.Relationships).Identity);
        await Assert.ThrowsAsync<Neo4jQueryTranslationException>(() => context.Set<ReferencePost>().CountAsync());
        await Assert.ThrowsAsync<Neo4jQueryTranslationException>(() => context.Set<ReferencePost>().LongCountAsync());
        await Assert.ThrowsAsync<Neo4jQueryTranslationException>(() => context.Set<ReferencePost>().AnyAsync());
        Assert.Contains("__gb_include_rows", context.Set<ReferenceBlog>().Include(value => value.Posts).ToQueryString());
        Assert.Contains("__gb_include_rows", context.Set<ReferencePost>().Include(value => value.Blog).ToQueryString());
        Assert.Throws<Neo4jQueryTranslationException>(() => context.Set<ReferenceBlog>().Include(value => value.Posts.Where(p => p.Id == post.Id)).ToQueryString());
        Assert.Throws<Neo4jQueryTranslationException>(() => context.Set<ReferenceBlog>().Include(value => value.Posts).ThenInclude(value => value.Blog).ToQueryString());
        Assert.All(context.ChangeTracker.Entries(), entry => Assert.Equal(EntityState.Added, entry.State));
        Assert.Equal(blog.Id, post.BlogId);
        Assert.Empty(((ProviderTestDriver)(object)driver).Calls);
    }

    private static void Configure(ModelBuilder model)
    {
        model.Entity<ReferenceBlog>().ToGraphNode("ReferenceBlog");
        model.Entity<ReferencePost>().ToGraphNode("ReferencePost")
            .HasOne(post => post.Blog).WithMany(blog => blog.Posts)
            .HasForeignKey(post => post.BlogId).OnDelete(DeleteBehavior.Restrict)
            .ToGraphReference("HAS_POST");
    }

    public sealed class ReferenceBlog
    {
        public Guid Id { get; set; }
        public List<ReferencePost> Posts { get; set; } = [];
    }
    public sealed class ReferencePost
    {
        public Guid Id { get; set; }
        public Guid BlogId { get; set; }
        public ReferenceBlog Blog { get; set; } = null!;
    }
}
