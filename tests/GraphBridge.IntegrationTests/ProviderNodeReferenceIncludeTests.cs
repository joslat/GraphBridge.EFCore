using Microsoft.EntityFrameworkCore;
using Neo4j.Driver;

namespace GraphBridge.IntegrationTests;

[Collection(Neo4jIntegrationCollection.Name)]
public sealed class ProviderNodeReferenceIncludeTests(DisposableNeo4jFixture fixture)
{
    [Neo4jIntegrationFact]
    public async Task Collection_Include_pages_roots_before_expansion_and_keeps_complete_and_empty_collections()
    {
        var scope = Scope.New();
        try
        {
            await SeedAsync(scope);
            await using var context = Context();
            var query = context.Set<Blog>().Where(blog => blog.Run == scope.Run).Include(blog => blog.Posts)
                .OrderBy(blog => blog.Name).Skip(1).Take(1);
            var text = query.ToQueryString();
            Assert.DoesNotContain(scope.Run, text);
            var result = Assert.Single(await query.ToListAsync());
            Assert.Equal(scope.BlogB, result.Id);
            Assert.Equal(new[] { "b-1", "b-2", "b-3" }, result.Posts.Select(post => post.Title).Order(StringComparer.Ordinal));
            Assert.Equal(3, result.Posts.Select(post => post.Id).Distinct().Count());
            Assert.True(context.Entry(result).Collection(blog => blog.Posts).IsLoaded);
            Assert.All(result.Posts, post =>
            {
                Assert.Same(result, post.Blog);
                Assert.Equal(result.Id, post.BlogId);
                Assert.True(context.Entry(post).Reference(value => value.Blog).IsLoaded);
            });
            Assert.Equal(4, context.ChangeTracker.Entries().Count());
            var empty = await context.Set<Blog>().Where(blog => blog.Id == scope.EmptyBlog).Include(blog => blog.Posts).SingleAsync();
            Assert.Empty(empty.Posts);
            Assert.True(context.Entry(empty).Collection(blog => blog.Posts).IsLoaded);
            Assert.Equal(5, context.ChangeTracker.Entries().Count());
        }
        finally { await CleanupAsync(scope); }
    }

    [Neo4jIntegrationFact]
    public async Task Reference_Include_uses_EF_identity_fixup_and_loaded_flags_without_marking_inverse_collection_complete()
    {
        var scope = Scope.New();
        try
        {
            await SeedAsync(scope);
            await using var context = Context();
            var fromQuery = 0;
            context.ChangeTracker.Tracked += (_, args) => { Assert.True(args.FromQuery); fromQuery++; };
            var posts = await context.Set<Post>().Where(post => post.Run == scope.Run).Include(post => post.Blog).OrderBy(post => post.Title).ToListAsync();
            Assert.Equal(4, posts.Count);
            Assert.Equal(6, fromQuery);
            var b = posts.Single(post => post.Title == "b-1").Blog;
            Assert.Equal(scope.BlogB, b.Id);
            Assert.Equal(3, b.Posts.Count);
            Assert.All(posts.Where(post => post.BlogId == scope.BlogB), post => Assert.Same(b, post.Blog));
            Assert.All(posts, post =>
            {
                Assert.True(context.Entry(post).Reference(value => value.Blog).IsLoaded);
                Assert.Equal(EntityState.Unchanged, context.Entry(post).State);
            });
            Assert.False(context.Entry(b).Collection(blog => blog.Posts).IsLoaded);
        }
        finally { await CleanupAsync(scope); }
    }

    [Neo4jIntegrationFact]
    public async Task Tracked_Include_retains_pending_values_and_adds_missing_children_through_EF_fixup()
    {
        var scope = Scope.New();
        try
        {
            await SeedAsync(scope);
            await using var context = Context();
            var blog = await context.Set<Blog>().SingleAsync(value => value.Id == scope.BlogB);
            var post = await context.Set<Post>().SingleAsync(value => value.Id == scope.PostB);
            blog.Name = "pending root";
            post.Title = "pending child";
            context.ChangeTracker.DetectChanges();
            Assert.False(context.Entry(blog).Collection(value => value.Posts).IsLoaded);
            var included = await context.Set<Blog>().Include(value => value.Posts).SingleAsync(value => value.Id == scope.BlogB);
            Assert.Same(blog, included);
            Assert.Same(post, included.Posts.Single(value => value.Id == scope.PostB));
            Assert.Equal("pending root", included.Name);
            Assert.Equal("pending child", post.Title);
            Assert.Equal("b", context.Entry(blog).Property(value => value.Name).OriginalValue);
            Assert.Equal("b-1", context.Entry(post).Property(value => value.Title).OriginalValue);
            Assert.Equal(3, included.Posts.Count);
            Assert.True(context.Entry(blog).Collection(value => value.Posts).IsLoaded);
            Assert.Equal(4, context.ChangeTracker.Entries().Count());
        }
        finally { await CleanupAsync(scope); }
    }

    [Neo4jIntegrationFact]
    public async Task Include_does_not_mark_an_unresolved_locally_changed_reference_loaded()
    {
        var scope = Scope.New();
        try
        {
            await SeedAsync(scope);
            foreach (var collection in new[] { false, true })
            {
                await using var context = Context();
                var post = await context.Set<Post>().SingleAsync(value => value.Id == scope.PostB);
                post.BlogId = scope.EmptyBlog;
                context.ChangeTracker.DetectChanges();
                Assert.Null(post.Blog);
                Assert.False(context.Entry(post).Reference(value => value.Blog).IsLoaded);
                if (collection)
                {
                    var original = await context.Set<Blog>().Include(value => value.Posts).SingleAsync(value => value.Id == scope.BlogB);
                    Assert.DoesNotContain(post, original.Posts);
                    Assert.True(context.Entry(original).Collection(value => value.Posts).IsLoaded);
                }
                else
                    Assert.Same(post, await context.Set<Post>().Include(value => value.Blog).SingleAsync(value => value.Id == scope.PostB));
                Assert.Equal(scope.EmptyBlog, post.BlogId);
                Assert.Equal(scope.BlogB, context.Entry(post).Property(value => value.BlogId).OriginalValue);
                Assert.Null(post.Blog);
                Assert.False(context.Entry(post).Reference(value => value.Blog).IsLoaded);
            }
        }
        finally { await CleanupAsync(scope); }
    }

    [Neo4jIntegrationFact]
    public async Task No_tracking_modes_fix_included_navigations_without_using_the_context_tracker()
    {
        var scope = Scope.New();
        try
        {
            await SeedAsync(scope);
            await using var context = Context();
            var tracked = await context.Set<Blog>().SingleAsync(blog => blog.Id == scope.BlogB);
            foreach (var identityResolution in new[] { false, true })
            {
                var blogs = context.Set<Blog>().Where(blog => blog.Run == scope.Run).Include(blog => blog.Posts).AsQueryable();
                blogs = identityResolution ? blogs.AsNoTrackingWithIdentityResolution() : blogs.AsNoTracking();
                var roots = await blogs.OrderBy(blog => blog.Name).ToListAsync();
                Assert.Equal(3, roots.Count);
                Assert.Empty(roots.Single(blog => blog.Id == scope.EmptyBlog).Posts);
                var b = roots.Single(blog => blog.Id == scope.BlogB);
                Assert.NotSame(tracked, b);
                Assert.Equal(3, b.Posts.Count);
                Assert.All(b.Posts, post => Assert.Same(b, post.Blog));
                var posts = context.Set<Post>().Where(post => post.BlogId == scope.BlogB).Include(post => post.Blog).AsQueryable();
                posts = identityResolution ? posts.AsNoTrackingWithIdentityResolution() : posts.AsNoTracking();
                var children = await posts.OrderBy(post => post.Title).ToListAsync();
                Assert.Equal(3, children.Count);
                Assert.All(children, post => Assert.Contains(post, post.Blog.Posts));
                if (identityResolution)
                {
                    Assert.Same(children[0].Blog, children[1].Blog);
                    Assert.Equal(3, children[0].Blog.Posts.Count);
                }
                else
                {
                    Assert.NotSame(children[0].Blog, children[1].Blog);
                    Assert.Single(children[0].Blog.Posts);
                }
                Assert.Empty(tracked.Posts);
                Assert.Single(context.ChangeTracker.Entries());
            }
        }
        finally { await CleanupAsync(scope); }
    }

    [Neo4jIntegrationFact]
    public async Task Collection_Include_validates_every_included_row_before_tracking_the_current_root()
    {
        foreach (var corruption in new[] { "parallel", "second-parent", "payload-type", "wrong-child-label" })
        {
            var scope = Scope.New();
            try
            {
                await SeedAsync(scope);
                if (corruption == "parallel")
                    await ExecuteAsync("MATCH (b:GraphBridgeIncludeBlog {Id:$blog}), (p:GraphBridgeIncludePost {Id:$post}) CREATE (b)-[:GRAPHBRIDGE_INCLUDE_HAS_POST]->(p)",
                        new { blog = Wire(scope.BlogB), post = Wire(scope.PostB) });
                if (corruption == "second-parent")
                    await ExecuteAsync("MATCH (b:GraphBridgeIncludeBlog {Id:$blog}), (p:GraphBridgeIncludePost {Id:$post}) CREATE (b)-[:GRAPHBRIDGE_INCLUDE_HAS_POST]->(p)",
                        new { blog = Wire(scope.BlogA), post = Wire(scope.PostB) });
                if (corruption == "payload-type")
                    await ExecuteAsync("MATCH (p:GraphBridgeIncludePost {Id:$post}) SET p.Version='invalid long'", new { post = Wire(scope.PostB) });
                if (corruption == "wrong-child-label")
                    await ExecuteAsync("MATCH (p:GraphBridgeIncludePost {Id:$post}) SET p:GraphBridgeIncludeForeign REMOVE p:GraphBridgeIncludePost", new { post = Wire(scope.PostB) });
                await using var context = Context();
                await Assert.ThrowsAsync<InvalidOperationException>(() => context.Set<Blog>().Where(blog => blog.Id == scope.BlogB).Include(blog => blog.Posts).Take(1).ToListAsync());
                Assert.Empty(context.ChangeTracker.Entries());
                // Diagnostics are scoped to returned roots/children, not unrelated parts of the graph.
                var empty = await context.Set<Blog>().Where(blog => blog.Id == scope.EmptyBlog).Include(blog => blog.Posts).SingleAsync();
                Assert.Empty(empty.Posts);
            }
            finally { await CleanupAsync(scope); }
        }
    }

    [Neo4jIntegrationFact]
    public async Task Reference_Include_preserves_orphan_diagnostics_and_validates_parent_payload_before_any_shaping()
    {
        foreach (var corruption in new[] { "orphan", "wrong-label", "missing-parent-payload" })
        {
            var scope = Scope.New();
            try
            {
                await SeedAsync(scope);
                if (corruption == "orphan")
                    await ExecuteAsync("MATCH ()-[r:GRAPHBRIDGE_INCLUDE_HAS_POST]->(:GraphBridgeIncludePost {Id:$post}) DELETE r", new { post = Wire(scope.PostB) });
                if (corruption == "wrong-label")
                    await ExecuteAsync("MATCH (b:GraphBridgeIncludeBlog {Id:$blog}) SET b:GraphBridgeIncludeForeign REMOVE b:GraphBridgeIncludeBlog", new { blog = Wire(scope.BlogB) });
                if (corruption == "missing-parent-payload")
                    await ExecuteAsync("MATCH (b:GraphBridgeIncludeBlog {Id:$blog}) REMOVE b.Name", new { blog = Wire(scope.BlogB) });
                await using var context = Context();
                await Assert.ThrowsAsync<InvalidOperationException>(() => context.Set<Post>().Where(post => post.Id == scope.PostB).Include(post => post.Blog).ToListAsync());
                Assert.Empty(context.ChangeTracker.Entries());
            }
            finally { await CleanupAsync(scope); }
        }
    }

    [Neo4jIntegrationFact]
    public async Task Include_uses_the_active_transaction_and_supports_early_disposal_and_cancellation()
    {
        var scope = Scope.New();
        try
        {
            await SeedAsync(scope);
            await using (var edit = Context())
            {
                await using var transaction = await edit.Database.BeginTransactionAsync();
                var blog = await edit.Set<Blog>().Include(value => value.Posts).SingleAsync(value => value.Id == scope.BlogB);
                blog.Posts.Single(post => post.Id == scope.PostB).Title = "pending transaction";
                Assert.Equal(1, await edit.SaveChangesAsync());
                var pending = await edit.Set<Blog>().AsNoTracking().Include(value => value.Posts).SingleAsync(value => value.Id == scope.BlogB);
                Assert.Equal("pending transaction", pending.Posts.Single(post => post.Id == scope.PostB).Title);
                Assert.Same(transaction, edit.Database.CurrentTransaction);
                await transaction.RollbackAsync();
            }
            await using var context = Context();
            var query = context.Set<Blog>().Where(blog => blog.Run == scope.Run).Include(blog => blog.Posts).OrderBy(blog => blog.Name);
            await using (var enumerator = query.AsAsyncEnumerable().GetAsyncEnumerator())
            {
                Assert.True(await enumerator.MoveNextAsync());
                Assert.Single(enumerator.Current.Posts);
            }
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => query.ToListAsync(cancelled.Token));
            var roots = await query.AsNoTracking().ToListAsync();
            Assert.Equal("b-1", roots.Single(blog => blog.Id == scope.BlogB).Posts.Single(post => post.Id == scope.PostB).Title);
        }
        finally { await CleanupAsync(scope); }
    }

    [Neo4jIntegrationFact]
    public async Task Include_requires_child_key_constraints_even_if_no_root_matches()
    {
        await SchemaAsync();
        await ExecuteAsync("DROP CONSTRAINT graphbridge_include_post_key");
        try
        {
            await using var context = Context();
            var absent = Guid.NewGuid();
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => context.Set<Blog>().Where(blog => blog.Id == absent).Include(blog => blog.Posts).ToListAsync());
            Assert.Contains("GraphBridgeIncludePost", error.Message);
            Assert.Contains("uniqueness constraint", error.Message);
            Assert.Empty(context.ChangeTracker.Entries());
        }
        finally { await SchemaAsync(); }
    }

    private IncludeContext Context() => new(new DbContextOptionsBuilder<IncludeContext>().UseNeo4j(fixture.Driver, fixture.Database).Options);
    private async Task SeedAsync(Scope scope)
    {
        await SchemaAsync();
        var a = new Blog { Id = scope.BlogA, Run = scope.Run, Name = "a" };
        var b = new Blog { Id = scope.BlogB, Run = scope.Run, Name = "b" };
        var empty = new Blog { Id = scope.EmptyBlog, Run = scope.Run, Name = "c" };
        a.Posts.Add(new Post { Run = scope.Run, Title = "a-1" });
        b.Posts.Add(new Post { Id = scope.PostB, Run = scope.Run, Title = "b-1" });
        b.Posts.Add(new Post { Run = scope.Run, Title = "b-2" });
        b.Posts.Add(new Post { Run = scope.Run, Title = "b-3" });
        await using var context = Context();
        context.AddRange(a, b, empty);
        Assert.Equal(7, await context.SaveChangesAsync());
    }
    private async Task SchemaAsync()
    {
        await ExecuteAsync("CREATE CONSTRAINT graphbridge_include_blog_key IF NOT EXISTS FOR (n:GraphBridgeIncludeBlog) REQUIRE n.Id IS UNIQUE");
        await ExecuteAsync("CREATE CONSTRAINT graphbridge_include_post_key IF NOT EXISTS FOR (n:GraphBridgeIncludePost) REQUIRE n.Id IS UNIQUE");
    }
    private async Task ExecuteAsync(string text, object? parameters = null)
    {
        await using var session = fixture.OpenSession();
        await (parameters is null ? await session.RunAsync(text) : await session.RunAsync(text, parameters)).ConsumeAsync();
    }
    private async Task CleanupAsync(Scope scope)
    {
        await ExecuteAsync("MATCH (b)-[r:GRAPHBRIDGE_INCLUDE_HAS_POST]->(p) WHERE b.Run=$run AND p.Run=$run DELETE r", new { run = scope.Run });
        await ExecuteAsync("MATCH (n) WHERE n.Run=$run AND (n:GraphBridgeIncludeBlog OR n:GraphBridgeIncludePost OR n:GraphBridgeIncludeForeign) DELETE n", new { run = scope.Run });
    }
    private static string Wire(Guid id) => id.ToString("D");
    private sealed record Scope(string Run, Guid BlogA, Guid BlogB, Guid EmptyBlog, Guid PostB)
    {
        public static Scope New() => new(Guid.NewGuid().ToString("N"), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
    }
    public sealed class IncludeContext(DbContextOptions<IncludeContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder model)
        {
            model.Entity<Blog>().ToGraphNode("GraphBridgeIncludeBlog");
            model.Entity<Post>().ToGraphNode("GraphBridgeIncludePost").Property(post => post.Version).IsRowVersion();
            model.Entity<Post>().HasOne(post => post.Blog).WithMany(blog => blog.Posts).HasForeignKey(post => post.BlogId)
                .OnDelete(DeleteBehavior.Restrict).ToGraphReference("GRAPHBRIDGE_INCLUDE_HAS_POST");
        }
    }
    public sealed class Blog
    {
        public Guid Id { get; set; }
        public string Run { get; set; } = "";
        public string Name { get; set; } = "";
        public List<Post> Posts { get; } = [];
    }
    public sealed class Post
    {
        public Guid Id { get; set; }
        public string Run { get; set; } = "";
        public string Title { get; set; } = "";
        public long Version { get; set; }
        public Guid BlogId { get; set; }
        public Blog Blog { get; set; } = null!;
    }
}
