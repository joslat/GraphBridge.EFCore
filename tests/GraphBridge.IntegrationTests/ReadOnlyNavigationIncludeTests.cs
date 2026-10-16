using Microsoft.EntityFrameworkCore;
using Neo4j.Driver;

namespace GraphBridge.IntegrationTests;

[Collection(Neo4jIntegrationCollection.Name)]
public sealed class ReadOnlyNavigationIncludeTests(DisposableNeo4jFixture fixture)
{
    [Neo4jIntegrationFact]
    public Task Tracked_collection_include_fixes_readonly_inverse() => VerifyAsync(true, QueryTrackingBehavior.TrackAll);

    [Neo4jIntegrationFact]
    public Task Tracked_reference_include_fixes_readonly_reference() => VerifyAsync(false, QueryTrackingBehavior.TrackAll);

    [Neo4jIntegrationFact]
    public Task No_tracking_collection_include_fixes_readonly_inverse() => VerifyAsync(true, QueryTrackingBehavior.NoTracking);

    [Neo4jIntegrationFact]
    public Task No_tracking_reference_include_fixes_readonly_reference() => VerifyAsync(false, QueryTrackingBehavior.NoTracking);

    [Neo4jIntegrationFact]
    public Task Identity_resolution_collection_include_fixes_readonly_inverse()
        => VerifyAsync(true, QueryTrackingBehavior.NoTrackingWithIdentityResolution);

    [Neo4jIntegrationFact]
    public Task Identity_resolution_reference_include_fixes_readonly_reference()
        => VerifyAsync(false, QueryTrackingBehavior.NoTrackingWithIdentityResolution);

    private async Task VerifyAsync(bool collection, QueryTrackingBehavior tracking)
    {
        var run = Guid.NewGuid().ToString("N");
        var blogId = Guid.NewGuid();
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        try
        {
            await ExecuteAsync("CREATE CONSTRAINT graphbridge_readonly_include_blog_key IF NOT EXISTS FOR (n:GraphBridgeReadOnlyIncludeBlog) REQUIRE n.Id IS UNIQUE");
            await ExecuteAsync("CREATE CONSTRAINT graphbridge_readonly_include_post_key IF NOT EXISTS FOR (n:GraphBridgeReadOnlyIncludePost) REQUIRE n.Id IS UNIQUE");
            await using (var seed = Context())
            {
                var blog = new Blog { Id = blogId, Run = run };
                blog.Posts.Add(new Post { Id = firstId, Run = run, Title = "first" });
                blog.Posts.Add(new Post { Id = secondId, Run = run, Title = "second" });
                seed.Add(blog);
                Assert.All(blog.Posts, post => Assert.Same(blog, post.Blog));
                Assert.Equal(3, await seed.SaveChangesAsync());
            }

            await using var context = Context();
            context.ChangeTracker.QueryTrackingBehavior = tracking;
            var navigation = context.Model.FindEntityType(typeof(Post))!.FindNavigation(nameof(Post.Blog))!;
            Assert.True(navigation.FieldInfo!.IsInitOnly);
            Assert.Equal(PropertyAccessMode.Field, navigation.GetPropertyAccessMode());
            var fromQuery = 0;
            context.ChangeTracker.Tracked += (_, args) => { Assert.True(args.FromQuery); fromQuery++; };

            IReadOnlyList<Post> posts;
            if (collection)
            {
                var blog = await context.Set<Blog>().Where(value => value.Run == run).Include(value => value.Posts).SingleAsync();
                Assert.Equal(blogId, blog.Id);
                posts = blog.Posts;
                Assert.All(posts, post => Assert.Same(blog, post.Blog));
                if (tracking == QueryTrackingBehavior.TrackAll)
                    Assert.True(context.Entry(blog).Collection(value => value.Posts).IsLoaded);
            }
            else
            {
                posts = await context.Set<Post>().Where(value => value.Run == run).Include(value => value.Blog).OrderBy(value => value.Title).ToListAsync();
                Assert.Equal(2, posts.Count);
                Assert.Equal(tracking != QueryTrackingBehavior.NoTracking, ReferenceEquals(posts[0].Blog, posts[1].Blog));
                Assert.All(posts, post => Assert.Equal(tracking == QueryTrackingBehavior.NoTracking ? 1 : 2, post.Blog.Posts.Count));
                if (tracking == QueryTrackingBehavior.TrackAll)
                    Assert.False(context.Entry(posts[0].Blog).Collection(value => value.Posts).IsLoaded);
            }

            Assert.Equal(new[] { firstId, secondId }.Order(), posts.Select(post => post.Id).Order());
            Assert.Equal(new[] { "first", "second" }, posts.Select(post => post.Title).Order(StringComparer.Ordinal));
            Assert.All(posts, post =>
            {
                Assert.Equal(blogId, post.BlogId);
                Assert.Equal(blogId, post.Blog.Id);
                Assert.Contains(post, post.Blog.Posts);
            });
            if (tracking == QueryTrackingBehavior.TrackAll)
            {
                Assert.Equal(3, fromQuery);
                Assert.Equal(3, context.ChangeTracker.Entries().Count());
                Assert.All(context.ChangeTracker.Entries(), entry => Assert.Equal(EntityState.Unchanged, entry.State));
                Assert.All(posts, post => Assert.True(context.Entry(post).Reference(value => value.Blog).IsLoaded));
            }
            else
            {
                Assert.Equal(0, fromQuery);
                Assert.Empty(context.ChangeTracker.Entries());
            }
        }
        finally
        {
            await ExecuteAsync("MATCH (b:GraphBridgeReadOnlyIncludeBlog)-[r:GRAPHBRIDGE_READONLY_INCLUDE_HAS_POST]->(p:GraphBridgeReadOnlyIncludePost) WHERE b.Run=$run AND p.Run=$run DELETE r", new { run });
            await ExecuteAsync("MATCH (n) WHERE n.Run=$run AND (n:GraphBridgeReadOnlyIncludeBlog OR n:GraphBridgeReadOnlyIncludePost) DELETE n", new { run });
        }
    }

    private ReadOnlyContext Context()
        => new(new DbContextOptionsBuilder<ReadOnlyContext>().UseNeo4j(fixture.Driver, fixture.Database).Options);

    private async Task ExecuteAsync(string text, object? parameters = null)
    {
        await using var session = fixture.OpenSession();
        await (parameters is null ? await session.RunAsync(text) : await session.RunAsync(text, parameters)).ConsumeAsync();
    }

    public sealed class ReadOnlyContext(DbContextOptions<ReadOnlyContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder model)
        {
            model.Entity<Blog>().ToGraphNode("GraphBridgeReadOnlyIncludeBlog");
            model.Entity<Post>().ToGraphNode("GraphBridgeReadOnlyIncludePost")
                .HasOne(post => post.Blog).WithMany(blog => blog.Posts).HasForeignKey(post => post.BlogId)
                .OnDelete(DeleteBehavior.Restrict).ToGraphReference("GRAPHBRIDGE_READONLY_INCLUDE_HAS_POST");
            model.Entity<Post>().Navigation(post => post.Blog)
                .HasField("_blog").UsePropertyAccessMode(PropertyAccessMode.Field);
        }
    }

    public sealed class Blog
    {
        public Guid Id { get; set; }
        public string Run { get; set; } = "";
        public List<Post> Posts { get; } = [];
    }

    public sealed class Post
    {
        private readonly Blog _blog = null!;
        public Guid Id { get; set; }
        public string Run { get; set; } = "";
        public string Title { get; set; } = "";
        public Guid BlogId { get; set; }
        public Blog Blog => _blog;
    }
}
