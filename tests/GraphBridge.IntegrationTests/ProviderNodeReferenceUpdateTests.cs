using Microsoft.EntityFrameworkCore;
using Neo4j.Driver;

namespace GraphBridge.IntegrationTests;

/// <summary>Ordinary EF FK writes inspected through the driver while reference reads remain separately gated.</summary>
[Collection(Neo4jIntegrationCollection.Name)]
public sealed partial class ProviderNodeReferenceUpdateTests(DisposableNeo4jFixture fixture)
{
    [Neo4jIntegrationFact]
    public async Task New_blog_and_two_posts_count_three_EF_entries_and_store_only_native_FKs()
    {
        var run = Guid.NewGuid().ToString("N");
        try
        {
            await SchemaAsync();
            await using var context = Context();
            var blog = new Blog { Run = run };
            blog.Posts.Add(new Post { Run = run, Title = "O'Reilly `$value` MATCH (n) DELETE n" });
            blog.Posts.Add(new Post { Run = run, Title = "second" });
            context.Add(blog);
            Assert.Equal(3, context.ChangeTracker.Entries().Count());
            Assert.All(blog.Posts, post => Assert.Equal(blog.Id, post.BlogId));
            Assert.Equal(3, await context.SaveChangesAsync());
            Assert.All(context.ChangeTracker.Entries(), entry => Assert.Equal(EntityState.Unchanged, entry.State));
            Assert.All(blog.Posts, post => Assert.Equal(1L, post.Version));
            Assert.Equal(1L, blog.Version);
            Assert.Equal(0, await context.SaveChangesAsync());
            Assert.Equal(3L, await NodeCountAsync(run));
            Assert.Equal(2L, await LinkCountAsync(run));
            foreach (var post in blog.Posts)
            {
                var stored = await PostPropertiesAsync(post.Id);
                Assert.Equal(post.Title, stored["Title"]);
                Assert.False(stored.ContainsKey("BlogId"));
                Assert.Equal(blog.Id.ToString("D"), await ParentIdAsync(post.Id));
            }
        }
        finally { await CleanupAsync(run); }
    }

    [Neo4jIntegrationFact]
    public async Task Existing_parent_add_payload_update_and_owned_link_delete_preserve_unrelated_properties()
    {
        var run = Guid.NewGuid().ToString("N");
        try
        {
            await SchemaAsync();
            var blog = new Blog { Run = run };
            await using (var seed = Context()) { seed.Add(blog); Assert.Equal(1, await seed.SaveChangesAsync()); }
            var post = new Post { Run = run, BlogId = blog.Id, Title = "before" };
            await using (var create = Context()) { create.Add(post); Assert.Equal(1, await create.SaveChangesAsync()); }
            await ExecuteAsync("MATCH (n:GraphBridgeReferencePost {Id:$id}) SET n.external='preserve'", new { id = Wire(post.Id) });
            await ExecuteAsync("MATCH ()-[r:GRAPHBRIDGE_HAS_POST]->(n:GraphBridgeReferencePost {Id:$id}) SET r.external='link payload'", new { id = Wire(post.Id) });
            await using (var edit = Context())
            {
                edit.Attach(post);
                post.Title = "after";
                Assert.Equal(1, await edit.SaveChangesAsync());
                Assert.Equal(2L, post.Version);
                var properties = await PostPropertiesAsync(post.Id);
                Assert.Equal("after", properties["Title"]);
                Assert.Equal("preserve", properties["external"]);
                Assert.False(properties.ContainsKey("BlogId"));
                Assert.Equal("link payload", await ScalarAsync<string>(
                    "MATCH ()-[r:GRAPHBRIDGE_HAS_POST]->(n:GraphBridgeReferencePost {Id:$id}) RETURN r.external AS value", new { id = Wire(post.Id) }));
                edit.Remove(post);
                Assert.Equal(1, await edit.SaveChangesAsync());
                Assert.Equal(EntityState.Detached, edit.Entry(post).State);
            }
            Assert.Equal(1L, await NodeCountAsync(run));
            Assert.Equal(0L, await LinkCountAsync(run));
        }
        finally { await CleanupAsync(run); }
    }

    [Neo4jIntegrationFact]
    public async Task Missing_parent_rolls_back_all_created_nodes_and_retains_EF_versions()
    {
        var run = Guid.NewGuid().ToString("N");
        try
        {
            await SchemaAsync();
            await using var context = Context();
            var earlier = new Blog { Run = run };
            var post = new Post { Run = run, BlogId = Guid.NewGuid(), Title = "must roll back" };
            context.AddRange(earlier, post);
            var error = await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => context.SaveChangesAsync());
            Assert.Same(post, Assert.Single(error.Entries).Entity);
            Assert.All(context.ChangeTracker.Entries(), entry => Assert.Equal(EntityState.Added, entry.State));
            Assert.Equal(0L, earlier.Version);
            Assert.Equal(0L, post.Version);
            Assert.Null(context.Database.CurrentTransaction);
            Assert.Equal(0L, await NodeCountAsync(run));
        }
        finally { await CleanupAsync(run); }
    }

    [Neo4jIntegrationFact]
    public async Task Corrupt_membership_rejects_updates_and_deletes_without_mutating_any_graph_element()
    {
        foreach (var corruption in new[] { "orphan", "parallel", "second-parent", "wrong-label" })
        {
            var run = Guid.NewGuid().ToString("N");
            try
            {
                var (blog, post) = await SeedAsync(run);
                if (corruption is "orphan" or "wrong-label")
                    await ExecuteAsync("MATCH ()-[r:GRAPHBRIDGE_HAS_POST]->(n:GraphBridgeReferencePost {Id:$id}) DELETE r", new { id = Wire(post.Id) });
                if (corruption == "parallel")
                    await ExecuteAsync("MATCH (b:GraphBridgeReferenceBlog {Id:$blog}), (p:GraphBridgeReferencePost {Id:$post}) CREATE (b)-[:GRAPHBRIDGE_HAS_POST]->(p)", new { blog = Wire(blog.Id), post = Wire(post.Id) });
                if (corruption is "second-parent" or "wrong-label")
                {
                    // Fixed fixture labels and an exact allocated Run scope; never production data.
                    var label = corruption == "second-parent" ? "GraphBridgeReferenceBlog" : "GraphBridgeReferenceForeign";
                    await ExecuteAsync($"MATCH (p:GraphBridgeReferencePost {{Id:$post}}) CREATE (b:{label} {{Id:$id,Run:$run,Version:1}})-[:GRAPHBRIDGE_HAS_POST]->(p)",
                        new { post = Wire(post.Id), id = Wire(Guid.NewGuid()), run });
                }
                var count = await LinkCountAsync(run);
                foreach (var deletion in new[] { false, true })
                {
                    await using var context = Context();
                    var candidate = Copy(post);
                    context.Attach(candidate);
                    if (deletion) context.Remove(candidate); else candidate.Title = "must not persist";
                    var error = await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => context.SaveChangesAsync());
                    Assert.Same(candidate, Assert.Single(error.Entries).Entity);
                    // The node and its parent are locked; the membership check names itself (GR-69).
                    Assert.Equal(("reference", "affected 0"), ((string?)error.Data["GraphBridge.MutationStep"], (string?)error.Data["GraphBridge.MutationResult"]));
                    Assert.Equal(deletion ? EntityState.Deleted : EntityState.Modified, context.Entry(candidate).State);
                    Assert.Equal(1L, candidate.Version);
                    Assert.Null(context.Database.CurrentTransaction);
                    Assert.Equal(count, await LinkCountAsync(run));
                    var stored = await PostPropertiesAsync(post.Id);
                    Assert.Equal("before", stored["Title"]);
                    Assert.Equal(1L, stored["Version"]);
                }
            }
            finally { await CleanupAsync(run); }
        }
    }

    [Neo4jIntegrationFact]
    public async Task Principal_delete_and_unrelated_dependent_edge_fail_restrictively_and_restore_owned_link()
    {
        var run = Guid.NewGuid().ToString("N");
        try
        {
            var (blog, post) = await SeedAsync(run);
            await using (var deleteParent = Context())
            {
                deleteParent.Attach(blog);
                deleteParent.Remove(blog);
                await Assert.ThrowsAsync<DbUpdateException>(() => deleteParent.SaveChangesAsync());
                Assert.Equal(EntityState.Deleted, deleteParent.Entry(blog).State);
            }
            await ExecuteAsync("MATCH (p:GraphBridgeReferencePost {Id:$post}), (b:GraphBridgeReferenceBlog {Id:$blog}) CREATE (p)-[:GRAPHBRIDGE_REFERENCE_EXTERNAL]->(b)",
                new { post = Wire(post.Id), blog = Wire(blog.Id) });
            await using (var deletePost = Context())
            {
                deletePost.Attach(post);
                deletePost.Remove(post);
                await Assert.ThrowsAsync<DbUpdateException>(() => deletePost.SaveChangesAsync());
                Assert.Equal(EntityState.Deleted, deletePost.Entry(post).State);
            }
            Assert.Equal(2L, await NodeCountAsync(run));
            Assert.Equal(1L, await LinkCountAsync(run));
            Assert.Equal(blog.Id.ToString("D"), await ParentIdAsync(post.Id));
            Assert.Equal(1L, await ScalarAsync<long>("MATCH (:GraphBridgeReferencePost {Id:$id})-[r:GRAPHBRIDGE_REFERENCE_EXTERNAL]->() RETURN count(r) AS value", new { id = Wire(post.Id) }));
        }
        finally { await CleanupAsync(run); }
    }

    [Neo4jIntegrationFact]
    public async Task Stale_version_cannot_update_or_delete_a_reference_node()
    {
        var run = Guid.NewGuid().ToString("N");
        try
        {
            var (_, post) = await SeedAsync(run);
            var stale = Copy(post);
            await using (var winner = Context())
            {
                winner.Attach(post);
                post.Title = "winner";
                Assert.Equal(1, await winner.SaveChangesAsync());
            }
            foreach (var deletion in new[] { false, true })
            {
                await using var loser = Context();
                var candidate = Copy(stale);
                loser.Attach(candidate);
                if (deletion) loser.Remove(candidate); else candidate.Title = "loser";
                var error = await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => loser.SaveChangesAsync());
                Assert.Same(candidate, Assert.Single(error.Entries).Entity);
                Assert.Equal(1L, candidate.Version);
                Assert.Equal(1L, await LinkCountAsync(run));
                var stored = await PostPropertiesAsync(post.Id);
                Assert.Equal("winner", stored["Title"]);
                Assert.Equal(2L, stored["Version"]);
            }
        }
        finally { await CleanupAsync(run); }
    }

    [Neo4jIntegrationFact]
    public async Task Navigation_reassignment_rejects_before_pending_new_parent_is_persisted()
    {
        var run = Guid.NewGuid().ToString("N");
        try
        {
            var (blog, post) = await SeedAsync(run);
            await using var context = Context();
            context.Attach(post);
            var replacement = new Blog { Run = run };
            context.Add(replacement);
            post.Blog = replacement;
            context.ChangeTracker.DetectChanges();
            Assert.Equal(replacement.Id, post.BlogId);
            await Assert.ThrowsAsync<NotSupportedException>(() => context.SaveChangesAsync());
            Assert.Equal(EntityState.Added, context.Entry(replacement).State);
            Assert.Equal(EntityState.Modified, context.Entry(post).State);
            Assert.Equal(0L, replacement.Version);
            Assert.Equal(2L, await NodeCountAsync(run));
            Assert.Equal(Wire(blog.Id), await ParentIdAsync(post.Id));
        }
        finally { await CleanupAsync(run); }
    }

    [Neo4jIntegrationFact]
    public async Task Owned_links_participate_in_explicit_transaction_rollback()
    {
        var run = Guid.NewGuid().ToString("N");
        try
        {
            await SchemaAsync();
            await using var context = Context();
            await using var transaction = await context.Database.BeginTransactionAsync();
            var blog = new Blog { Run = run };
            blog.Posts.Add(new Post { Run = run });
            context.Add(blog);
            Assert.Equal(2, await context.SaveChangesAsync());
            Assert.Same(transaction, context.Database.CurrentTransaction);
            await transaction.RollbackAsync();
            Assert.Equal(0L, await NodeCountAsync(run));
            Assert.Equal(0L, await LinkCountAsync(run));
            Assert.All(context.ChangeTracker.Entries(), entry => Assert.Equal(EntityState.Unchanged, entry.State));
        }
        finally { await CleanupAsync(run); }
    }

    [Neo4jIntegrationFact]
    public async Task Acyclic_reference_chain_deletes_dependents_before_principals()
    {
        var run = Guid.NewGuid().ToString("N");
        try
        {
            await SchemaAsync();
            await using var context = Context();
            var blog = new Blog { Run = run };
            var post = new Post { Run = run, Blog = blog };
            var comment = new Comment { Run = run, Post = post };
            context.Add(comment);
            Assert.Equal(3, await context.SaveChangesAsync());
            Assert.Equal(3L, await NodeCountAsync(run));
            context.Remove(comment);
            context.Remove(post);
            context.Remove(blog);
            Assert.Equal(3, await context.SaveChangesAsync());
            Assert.Empty(context.ChangeTracker.Entries());
            Assert.Equal(0L, await NodeCountAsync(run));
        }
        finally { await CleanupAsync(run); }
    }

    [Neo4jIntegrationFact]
    public async Task Competing_adds_for_one_dependent_key_cannot_create_two_parent_links()
    {
        var run = Guid.NewGuid().ToString("N");
        try
        {
            await SchemaAsync();
            var parents = new[] { new Blog { Run = run }, new Blog { Run = run } };
            await using (var seed = Context()) { seed.AddRange(parents); Assert.Equal(2, await seed.SaveChangesAsync()); }
            var id = Guid.NewGuid();
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            async Task<bool> AddAsync(Blog parent)
            {
                await using var writer = Context();
                var post = new Post { Id = id, BlogId = parent.Id, Run = run };
                writer.Add(post);
                await start.Task;
                try { Assert.Equal(1, await writer.SaveChangesAsync()); return true; }
                catch (DbUpdateException error)
                {
                    Assert.Same(post, Assert.Single(error.Entries).Entity);
                    Assert.Equal(EntityState.Added, writer.Entry(post).State);
                    Assert.Equal(0L, post.Version);
                    return false;
                }
            }
            var tasks = parents.Select(AddAsync).ToArray();
            start.SetResult();
            var outcomes = await Task.WhenAll(tasks);
            Assert.Single(outcomes, success => success);
            Assert.Equal(3L, await NodeCountAsync(run));
            Assert.Equal(1L, await LinkCountAsync(run));
            Assert.Contains(await ParentIdAsync(id), parents.Select(parent => Wire(parent.Id)));
        }
        finally { await CleanupAsync(run); }
    }

    [Neo4jIntegrationFact]
    public async Task Dependent_only_save_requires_principal_key_constraint_without_implicit_schema_changes()
    {
        var run = Guid.NewGuid().ToString("N");
        try
        {
            await SchemaAsync();
            var blog = new Blog { Run = run };
            await using (var seed = Context()) { seed.Add(blog); Assert.Equal(1, await seed.SaveChangesAsync()); }
            await ExecuteAsync("DROP CONSTRAINT graphbridge_reference_blog_key");
            try
            {
                await using var context = Context();
                var post = new Post { Run = run, BlogId = blog.Id };
                context.Add(post);
                var error = await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync());
                Assert.Contains("GraphBridgeReferenceBlog", error.Message);
                Assert.Contains("uniqueness constraint", error.Message);
                Assert.Equal(EntityState.Added, context.Entry(post).State);
                Assert.Equal(0L, post.Version);
                Assert.Equal(1L, await NodeCountAsync(run));
                Assert.Equal(0L, await LinkCountAsync(run));
                Assert.Equal(0L, await ScalarAsync<long>("SHOW CONSTRAINTS YIELD name WHERE name=$name RETURN count(*) AS value",
                    new { name = "graphbridge_reference_blog_key" }));
            }
            finally { await SchemaAsync(); }
        }
        finally { await CleanupAsync(run); }
    }

    [Neo4jIntegrationFact]
    public async Task Concurrent_link_add_and_principal_delete_leave_only_a_complete_graph_or_no_graph()
    {
        for (var iteration = 0; iteration < 3; iteration++)
        {
            var run = Guid.NewGuid().ToString("N");
            try
            {
                await SchemaAsync();
                var blog = new Blog { Run = run };
                await using (var seed = Context()) { seed.Add(blog); Assert.Equal(1, await seed.SaveChangesAsync()); }
                var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                async Task<bool> WriteAsync(bool deletion)
                {
                    await using var writer = Context();
                    if (deletion) writer.Remove(new Blog { Id = blog.Id, Run = run, Version = blog.Version });
                    else writer.Add(new Post { BlogId = blog.Id, Run = run });
                    await start.Task;
                    try { Assert.Equal(1, await writer.SaveChangesAsync()); return true; }
                    catch (DbUpdateException)
                    {
                        Assert.Null(writer.Database.CurrentTransaction);
                        return false;
                    }
                }
                var add = WriteAsync(false);
                var delete = WriteAsync(true);
                start.SetResult();
                var results = await Task.WhenAll(add, delete);
                Assert.Single(results, success => success);
                Assert.Equal(results[0] ? 2L : 0L, await NodeCountAsync(run));
                Assert.Equal(results[0] ? 1L : 0L, await LinkCountAsync(run));
            }
            finally { await CleanupAsync(run); }
        }
    }

    [Neo4jIntegrationFact]
    public async Task Composite_renamed_principal_keys_and_shadow_FKs_support_native_add_update_delete()
    {
        var run = Guid.NewGuid().ToString("N");
        try
        {
            await ExecuteAsync("CREATE CONSTRAINT graphbridge_reference_composite_parent IF NOT EXISTS FOR (n:GraphBridgeReferenceCompositeParent) REQUIRE (n.tenant_key,n.code_key) IS UNIQUE");
            await ExecuteAsync("CREATE CONSTRAINT graphbridge_reference_composite_child IF NOT EXISTS FOR (n:GraphBridgeReferenceCompositeChild) REQUIRE n.Id IS UNIQUE");
            await using var context = new CompositeContext(new DbContextOptionsBuilder<CompositeContext>().UseNeo4j(fixture.Driver, fixture.Database).Options);
            var parent = new CompositeParent { Tenant = 17L, Code = run, Run = run };
            var child = new CompositeChild { Run = run, Title = "before" };
            context.AddRange(child, parent);
            context.Entry(child).Property<long>("OwnerTenant").CurrentValue = parent.Tenant;
            context.Entry(child).Property<string>("OwnerCode").CurrentValue = parent.Code;
            Assert.Equal(2, await context.SaveChangesAsync());
            Assert.Equal(1L, await ScalarAsync<long>(
                "MATCH (:GraphBridgeReferenceCompositeParent {tenant_key:$tenant,code_key:$code})-[r:GRAPHBRIDGE_COMPOSITE_OWNER]->(c:GraphBridgeReferenceCompositeChild {Id:$id}) RETURN count(r) AS value",
                new { tenant = 17L, code = run, id = Wire(child.Id) }));
            var stored = await ScalarAsync<Dictionary<string, object>>(
                "MATCH (c:GraphBridgeReferenceCompositeChild {Id:$id}) RETURN properties(c) AS value", new { id = Wire(child.Id) });
            Assert.False(stored.ContainsKey("OwnerTenant"));
            Assert.False(stored.ContainsKey("OwnerCode"));
            child.Title = "after";
            Assert.Equal(1, await context.SaveChangesAsync());
            Assert.Equal(2L, child.Version);
            Assert.Equal("after", await ScalarAsync<string>("MATCH (c:GraphBridgeReferenceCompositeChild {Id:$id}) RETURN c.Title AS value", new { id = Wire(child.Id) }));
            context.Remove(child);
            context.Remove(parent);
            Assert.Equal(2, await context.SaveChangesAsync());
            Assert.Equal(0L, await ScalarAsync<long>("MATCH (n) WHERE n.Run=$run RETURN count(n) AS value", new { run }));
        }
        finally { await CleanupAsync(run); }
    }

    private ReferenceContext Context() => new(new DbContextOptionsBuilder<ReferenceContext>().UseNeo4j(fixture.Driver, fixture.Database).Options);
    private async Task<(Blog, Post)> SeedAsync(string run)
    {
        await SchemaAsync();
        var blog = new Blog { Run = run };
        var post = new Post { Run = run, Blog = blog, Title = "before" };
        await using var context = Context();
        context.Add(post);
        Assert.Equal(2, await context.SaveChangesAsync());
        // Return independent entities so Attach(post) does not accidentally attach a principal graph.
        return (new Blog { Id = blog.Id, Run = run, Version = blog.Version }, Copy(post));
    }
    private static Post Copy(Post value) => new() { Id = value.Id, BlogId = value.BlogId, Run = value.Run, Title = value.Title, Version = value.Version };
    private static string Wire(Guid value) => value.ToString("D");
    private async Task SchemaAsync()
    {
        await ExecuteAsync("CREATE CONSTRAINT graphbridge_reference_blog_key IF NOT EXISTS FOR (n:GraphBridgeReferenceBlog) REQUIRE n.Id IS UNIQUE");
        await ExecuteAsync("CREATE CONSTRAINT graphbridge_reference_post_key IF NOT EXISTS FOR (n:GraphBridgeReferencePost) REQUIRE n.Id IS UNIQUE");
        await ExecuteAsync("CREATE CONSTRAINT graphbridge_reference_comment_key IF NOT EXISTS FOR (n:GraphBridgeReferenceComment) REQUIRE n.Id IS UNIQUE");
    }
    private async Task ExecuteAsync(string text, object? parameters = null)
    {
        await using var session = fixture.OpenSession();
        await (parameters is null ? await session.RunAsync(text) : await session.RunAsync(text, parameters)).ConsumeAsync();
    }
    private async Task<T> ScalarAsync<T>(string text, object parameters)
    {
        await using var session = fixture.OpenSession();
        return (await (await session.RunAsync(text, parameters)).SingleAsync())["value"].As<T>();
    }
    private Task<string> ParentIdAsync(Guid post) => ScalarAsync<string>("MATCH (b)-[:GRAPHBRIDGE_HAS_POST]->(:GraphBridgeReferencePost {Id:$id}) RETURN b.Id AS value", new { id = Wire(post) });
    private Task<Dictionary<string, object>> PostPropertiesAsync(Guid post) => ScalarAsync<Dictionary<string, object>>(
        "MATCH (p:GraphBridgeReferencePost {Id:$id}) RETURN properties(p) AS value", new { id = Wire(post) });
    private Task<long> LinkCountAsync(string run) => ScalarAsync<long>(
        "MATCH ()-[r:GRAPHBRIDGE_HAS_POST]->(:GraphBridgeReferencePost {Run:$run}) RETURN count(r) AS value", new { run });
    private Task<long> NodeCountAsync(string run) => ScalarAsync<long>(
        "MATCH (n) WHERE n.Run=$run AND (n:GraphBridgeReferenceBlog OR n:GraphBridgeReferencePost OR n:GraphBridgeReferenceComment) RETURN count(n) AS value", new { run });
    private async Task CleanupAsync(string run)
    {
        // Every node/relationship was created by this suite under this random scope. No DETACH DELETE or global cleanup.
        await ExecuteAsync("MATCH (a)-[r]->(b) WHERE a.Run=$run AND b.Run=$run AND type(r) IN ['GRAPHBRIDGE_HAS_POST','GRAPHBRIDGE_HAS_COMMENT','GRAPHBRIDGE_REFERENCE_EXTERNAL','GRAPHBRIDGE_COMPOSITE_OWNER'] DELETE r", new { run });
        await ExecuteAsync("MATCH (n) WHERE n.Run=$run AND (n:GraphBridgeReferenceBlog OR n:GraphBridgeReferencePost OR n:GraphBridgeReferenceComment OR n:GraphBridgeReferenceForeign OR n:GraphBridgeReferenceCompositeParent OR n:GraphBridgeReferenceCompositeChild) DELETE n", new { run });
    }
    public sealed class ReferenceContext(DbContextOptions<ReferenceContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder model)
        {
            model.Entity<Blog>().ToGraphNode("GraphBridgeReferenceBlog").Property(value => value.Version).IsRowVersion();
            model.Entity<Post>().ToGraphNode("GraphBridgeReferencePost").Property(value => value.Version).IsRowVersion();
            model.Entity<Post>().HasOne(value => value.Blog).WithMany(value => value.Posts).HasForeignKey(value => value.BlogId)
                .OnDelete(DeleteBehavior.Restrict).ToGraphReference("GRAPHBRIDGE_HAS_POST");
            model.Entity<Comment>().ToGraphNode("GraphBridgeReferenceComment");
            model.Entity<Comment>().HasOne(value => value.Post).WithMany().HasForeignKey(value => value.PostId)
                .OnDelete(DeleteBehavior.Restrict).ToGraphReference("GRAPHBRIDGE_HAS_COMMENT");
        }
    }
    public sealed class CompositeContext(DbContextOptions<CompositeContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder model)
        {
            model.Entity<CompositeParent>().ToGraphNode("GraphBridgeReferenceCompositeParent").HasKey(value => new { value.Tenant, value.Code });
            model.Entity<CompositeParent>().Property(value => value.Tenant).HasGraphPropertyName("tenant_key");
            model.Entity<CompositeParent>().Property(value => value.Code).HasGraphPropertyName("code_key");
            model.Entity<CompositeChild>().ToGraphNode("GraphBridgeReferenceCompositeChild").Property(value => value.Version).IsRowVersion();
            model.Entity<CompositeChild>().HasOne<CompositeParent>().WithMany().HasForeignKey("OwnerTenant", "OwnerCode")
                .IsRequired().OnDelete(DeleteBehavior.Restrict).ToGraphReference("GRAPHBRIDGE_COMPOSITE_OWNER");
        }
    }
    public sealed class CompositeParent
    {
        public long Tenant { get; set; }
        public string Code { get; set; } = "";
        public string Run { get; set; } = "";
    }
    public sealed class CompositeChild
    {
        public Guid Id { get; set; }
        public string Run { get; set; } = "";
        public string Title { get; set; } = "";
        public long Version { get; set; }
    }
    public sealed class Blog
    {
        public Guid Id { get; set; }
        public string Run { get; set; } = "";
        public long Version { get; set; }
        public List<Post> Posts { get; } = [];
    }
    public sealed class Post
    {
        public Guid Id { get; set; }
        public string Run { get; set; } = "";
        public Guid BlogId { get; set; }
        public Blog Blog { get; set; } = null!;
        public string Title { get; set; } = "";
        public long Version { get; set; }
    }
    public sealed class Comment
    {
        public Guid Id { get; set; }
        public string Run { get; set; } = "";
        public Guid PostId { get; set; }
        public Post Post { get; set; } = null!;
    }
}
