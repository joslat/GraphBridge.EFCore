using Microsoft.EntityFrameworkCore;
using GraphBridge.EntityFrameworkCore.Schema;
using Neo4j.Driver;

namespace GraphBridge.IntegrationTests;

[Collection(Neo4jIntegrationCollection.Name)]
public sealed class ProviderNodeReferenceQueryTests(DisposableNeo4jFixture fixture)
{
    [Neo4jIntegrationFact]
    public async Task Native_FK_rows_use_EF_tracking_fixup_and_fresh_context_save_read_delete()
    {
        var scope = Scope.New();
        try
        {
            await SeedAsync(scope);
            await using (var context = Context())
            {
                var queryEvents = 0;
                context.ChangeTracker.Tracked += (_, args) => { Assert.True(args.FromQuery); queryEvents++; };
                var post = await context.Set<Post>().SingleAsync(value => value.Id == scope.PostId);
                Assert.Equal(scope.BlogId, post.BlogId);
                Assert.Null(post.Blog);
                var blog = await context.Set<Blog>().SingleAsync(value => value.Id == scope.BlogId);
                Assert.Same(blog, post.Blog);
                Assert.Same(post, Assert.Single(blog.Posts));
                Assert.False(context.Entry(blog).Collection(value => value.Posts).IsLoaded);
                // Pinned EF fixup marks a populated reference loaded; it does not imply collection Include.
                Assert.True(context.Entry(post).Reference(value => value.Blog).IsLoaded);
                Assert.Equal(2, queryEvents);
                Assert.Equal(scope.BlogId, context.Entry(post).Property(value => value.BlogId).OriginalValue);
                post.Title = "saved after read";
                Assert.Same(post, await context.Set<Post>().SingleAsync(value => value.Id == scope.PostId));
                Assert.Equal("saved after read", post.Title);
                Assert.Equal(1, await context.SaveChangesAsync());
                Assert.Equal(2L, post.Version);
            }
            await using (var fresh = Context())
            {
                // The opposite load order also uses ordinary EF fixup.
                var blog = await fresh.Set<Blog>().SingleAsync(value => value.Id == scope.BlogId);
                var post = await fresh.Set<Post>().SingleAsync(value => value.Id == scope.PostId);
                Assert.Same(blog, post.Blog);
                Assert.Equal("saved after read", post.Title);
                Assert.Equal(2L, post.Version);
                fresh.Remove(post);
                Assert.Equal(1, await fresh.SaveChangesAsync());
            }
            await using var afterDelete = Context();
            Assert.Empty(await afterDelete.Set<Post>().Where(value => value.Id == scope.PostId).ToListAsync());
            Assert.Single(await afterDelete.Set<Blog>().Where(value => value.Id == scope.BlogId).ToListAsync());
        }
        finally { await CleanupAsync(scope); }
    }

    [Neo4jIntegrationFact]
    public async Task Derived_FK_filter_scalar_projection_paging_and_no_tracking_ignore_unrelated_scalar_FK_property()
    {
        var scope = Scope.New();
        try
        {
            await SeedAsync(scope, "a");
            await SeedPostAsync(scope with { PostId = Guid.NewGuid() }, "b");
            await SeedPostAsync(scope with { PostId = Guid.NewGuid() }, "c");
            await ExecuteAsync("MATCH (p:GraphBridgeReadReferencePost {Run:$run}) SET p.BlogId=$decoy", new { run = scope.Run, decoy = Guid.NewGuid().ToString("D") });
            await using var context = Context();
            var id = scope.BlogId;
            var query = context.Set<Post>().Where(post => post.Run == scope.Run && post.BlogId == id);
            var page = query.OrderBy(post => post.Title).Skip(1).Take(1).Select(post => post.Title);
            var cypher = page.ToQueryString();
            Assert.Contains("MATCH (n:`GraphBridgeReadReferencePost`)", cypher);
            Assert.Contains("s.`blog_key`", cypher);
            Assert.DoesNotContain("n.`BlogId`", cypher);
            Assert.DoesNotContain(scope.Run, cypher);
            Assert.Equal("b", Assert.Single(await page.ToListAsync()));
            Assert.Equal(new[] { "a", "b", "c" }, await query.OrderBy(post => post.Title).Select(post => post.Title).ToListAsync());
            Assert.All(await query.Select(post => post.BlogId).ToListAsync(), value => Assert.Equal(id, value));
            Assert.Empty(context.ChangeTracker.Entries());
            var blog = await context.Set<Blog>().SingleAsync(value => value.Id == scope.BlogId);
            foreach (var identityResolution in new[] { false, true })
            {
                var untracked = identityResolution ? query.AsNoTrackingWithIdentityResolution() : query.AsNoTracking();
                var first = await untracked.OrderBy(post => post.Title).ToListAsync();
                var second = await untracked.OrderBy(post => post.Title).ToListAsync();
                Assert.Equal(3, first.Count);
                Assert.NotSame(first[0], second[0]);
                Assert.All(first, post => { Assert.Equal(scope.BlogId, post.BlogId); Assert.Null(post.Blog); });
                Assert.Empty(blog.Posts);
                Assert.Empty(context.ChangeTracker.Entries<Post>());
            }
            id = Guid.NewGuid();
            Assert.Empty(await query.ToListAsync());
        }
        finally { await CleanupAsync(scope); }
    }

    [Neo4jIntegrationFact]
    public async Task Missing_ambiguous_wrong_label_and_malformed_parent_keys_fail_entity_and_scalar_reads_before_tracking()
    {
        foreach (var corruption in new[] { "orphan", "parallel", "second-parent", "wrong-label", "missing-key", "noncanonical-key", "wrong-key-type" })
        {
            var scope = Scope.New();
            try
            {
                await SeedAsync(scope);
                if (corruption == "orphan")
                    await ExecuteAsync("MATCH ()-[r:GRAPHBRIDGE_READ_HAS_POST]->(:GraphBridgeReadReferencePost {Run:$run}) DELETE r", new { run = scope.Run });
                if (corruption == "parallel") await SeedLinkAsync(scope);
                if (corruption == "second-parent")
                    await ExecuteAsync("MATCH (p:GraphBridgeReadReferencePost {Run:$run}) CREATE (:GraphBridgeReadReferenceBlog {blog_key:$id,Run:$run})-[:GRAPHBRIDGE_READ_HAS_POST]->(p)",
                        new { run = scope.Run, id = Guid.NewGuid().ToString("D") });
                if (corruption == "wrong-label")
                    await ExecuteAsync("MATCH (b:GraphBridgeReadReferenceBlog {Run:$run}) SET b:GraphBridgeReadReferenceForeign REMOVE b:GraphBridgeReadReferenceBlog", new { run = scope.Run });
                if (corruption == "missing-key")
                    await ExecuteAsync("MATCH (b:GraphBridgeReadReferenceBlog {Run:$run}) REMOVE b.blog_key", new { run = scope.Run });
                if (corruption == "noncanonical-key")
                    await ExecuteAsync("MATCH (b:GraphBridgeReadReferenceBlog {Run:$run}) SET b.blog_key=$id", new { run = scope.Run, id = scope.BlogId.ToString("D").ToUpperInvariant() });
                if (corruption == "wrong-key-type")
                    await ExecuteAsync("MATCH (b:GraphBridgeReadReferenceBlog {Run:$run}) SET b.blog_key=42", new { run = scope.Run });
                await using var context = Context();
                // Predicate addresses dependent data: no parent-filter semantics can hide the malformed row.
                var query = context.Set<Post>().Where(post => post.Run == scope.Run && post.Title == "before").Take(1);
                await Assert.ThrowsAsync<InvalidOperationException>(() => query.ToListAsync());
                await Assert.ThrowsAsync<InvalidOperationException>(() => query.Select(post => post.Title).ToListAsync());
                Assert.Empty(context.ChangeTracker.Entries());
            }
            finally { await CleanupAsync(scope); }
        }
    }

    [Neo4jIntegrationFact]
    public async Task Corruption_is_checked_before_reusing_an_existing_tracked_identity()
    {
        var scope = Scope.New();
        try
        {
            await SeedAsync(scope);
            await using var context = Context();
            var query = context.Set<Post>().Where(post => post.Id == scope.PostId);
            var tracked = Assert.Single(await query.ToListAsync());
            await SeedLinkAsync(scope);
            await Assert.ThrowsAsync<InvalidOperationException>(() => query.ToListAsync());
            Assert.Same(tracked, Assert.Single(context.ChangeTracker.Entries<Post>()).Entity);
            Assert.Equal("before", tracked.Title);
            Assert.Equal(EntityState.Unchanged, context.Entry(tracked).State);
        }
        finally { await CleanupAsync(scope); }
    }

    [Neo4jIntegrationFact]
    public async Task Both_endpoint_constraints_are_required_even_for_empty_scalar_results()
    {
        await SchemaAsync();
        foreach (var name in new[] { "graphbridge_read_reference_blog_key", "graphbridge_read_reference_post_key" })
        {
            await ExecuteAsync($"DROP CONSTRAINT {name}");
            try
            {
                await using var context = Context();
                var absent = Guid.NewGuid();
                var query = context.Set<Post>().Where(post => post.Id == absent).Select(post => post.Title);
                var error = await Assert.ThrowsAsync<InvalidOperationException>(() => query.ToListAsync());
                Assert.Contains("uniqueness constraint", error.Message);
                Assert.Empty(context.ChangeTracker.Entries());
            }
            finally { await SchemaAsync(); }
        }
    }

    [Neo4jIntegrationFact]
    public async Task Root_queries_borrow_transactions_and_release_early_or_cancelled_enumerations()
    {
        var scope = Scope.New();
        try
        {
            await SeedAsync(scope);
            await using (var context = Context())
            {
                await using var transaction = await context.Database.BeginTransactionAsync();
                var post = await context.Set<Post>().SingleAsync(value => value.Id == scope.PostId);
                post.Title = "inside transaction";
                Assert.Equal(1, await context.SaveChangesAsync());
                var pending = await context.Set<Post>().AsNoTracking().SingleAsync(value => value.Id == scope.PostId);
                Assert.Equal("inside transaction", pending.Title);
                Assert.Same(transaction, context.Database.CurrentTransaction);
                await transaction.RollbackAsync();
            }
            await using var fresh = Context();
            var query = fresh.Set<Post>().Where(post => post.Id == scope.PostId);
            await using (var enumerator = query.AsAsyncEnumerable().GetAsyncEnumerator())
            {
                Assert.True(await enumerator.MoveNextAsync());
                Assert.Equal("before", enumerator.Current.Title);
            }
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => query.ToListAsync(cancellation.Token));
            Assert.Equal("before", Assert.Single(await query.AsNoTracking().ToListAsync()).Title);
        }
        finally { await CleanupAsync(scope); }
    }

    [Neo4jIntegrationFact]
    public async Task Corrupt_multi_parent_FK_filters_do_not_choose_an_arbitrary_parent()
    {
        var scope = Scope.New();
        var other = Guid.NewGuid();
        try
        {
            await SeedAsync(scope);
            await ExecuteAsync("MATCH (p:GraphBridgeReadReferencePost {Run:$run}) CREATE (:GraphBridgeReadReferenceBlog {blog_key:$id,Run:$run})-[:GRAPHBRIDGE_READ_HAS_POST]->(p)",
                new { run = scope.Run, id = other.ToString("D") });
            await using var context = Context();
            foreach (var parentId in new[] { scope.BlogId, other })
                Assert.Empty(await context.Set<Post>().Where(post => post.Run == scope.Run && post.BlogId == parentId).ToListAsync());
            // FK filters exclude an undefined FK. Addressing the dependent directly still reports corruption.
            await Assert.ThrowsAsync<InvalidOperationException>(() => context.Set<Post>().Where(post => post.Id == scope.PostId).Select(post => post.Title).ToListAsync());
            Assert.Empty(context.ChangeTracker.Entries());
        }
        finally { await CleanupAsync(scope); }
    }

    [Neo4jIntegrationFact]
    public async Task Composite_shadow_FKs_materialize_from_renamed_principal_keys_and_remain_EF_original_values()
    {
        var run = Guid.NewGuid().ToString("N");
        var options = new DbContextOptionsBuilder<ProviderNodeReferenceUpdateTests.CompositeContext>().UseNeo4j(fixture.Driver, fixture.Database).Options;
        try
        {
            await ExecuteAsync("CREATE CONSTRAINT graphbridge_reference_composite_parent IF NOT EXISTS FOR (n:GraphBridgeReferenceCompositeParent) REQUIRE (n.tenant_key,n.code_key) IS UNIQUE");
            await ExecuteAsync("CREATE CONSTRAINT graphbridge_reference_composite_child IF NOT EXISTS FOR (n:GraphBridgeReferenceCompositeChild) REQUIRE n.Id IS UNIQUE");
            var parent = new ProviderNodeReferenceUpdateTests.CompositeParent { Tenant = 17L, Code = run, Run = run };
            var child = new ProviderNodeReferenceUpdateTests.CompositeChild { Run = run, Title = "shadow" };
            await using (var seed = new ProviderNodeReferenceUpdateTests.CompositeContext(options))
            {
                seed.AddRange(parent, child);
                seed.Entry(child).Property<long>("OwnerTenant").CurrentValue = parent.Tenant;
                seed.Entry(child).Property<string>("OwnerCode").CurrentValue = parent.Code;
                Assert.Equal(2, await seed.SaveChangesAsync());
            }
            await using var fresh = new ProviderNodeReferenceUpdateTests.CompositeContext(options);
            var query = fresh.Set<ProviderNodeReferenceUpdateTests.CompositeChild>()
                .Where(value => value.Run == run && EF.Property<long>(value, "OwnerTenant") == 17L && EF.Property<string>(value, "OwnerCode") == run);
            var loaded = Assert.Single(await query.ToListAsync());
            Assert.NotSame(child, loaded);
            Assert.Equal(17L, fresh.Entry(loaded).Property<long>("OwnerTenant").OriginalValue);
            Assert.Equal(run, fresh.Entry(loaded).Property<string>("OwnerCode").OriginalValue);
            Assert.Equal(run, Assert.Single(await query.Select(value => EF.Property<string>(value, "OwnerCode")).ToListAsync()));
            loaded.Title = "updated from shadow FK read";
            Assert.Equal(1, await fresh.SaveChangesAsync());
            fresh.Remove(loaded);
            Assert.Equal(1, await fresh.SaveChangesAsync());
        }
        finally
        {
            await ExecuteAsync("MATCH (p:GraphBridgeReferenceCompositeParent)-[r:GRAPHBRIDGE_COMPOSITE_OWNER]->(c:GraphBridgeReferenceCompositeChild) WHERE p.Run=$run AND c.Run=$run DELETE r", new { run });
            await ExecuteAsync("MATCH (n) WHERE n.Run=$run AND (n:GraphBridgeReferenceCompositeParent OR n:GraphBridgeReferenceCompositeChild) DELETE n", new { run });
        }
    }

    [Neo4jIntegrationFact]
    public async Task Schema_inspection_reports_application_enforced_cardinality_without_claiming_data_integrity()
    {
        var scope = Scope.New();
        try
        {
            await SeedAsync(scope);
            await using var context = Context();
            var before = await context.Database.InspectGraphSchemaAsync();
            var relationship = Assert.Single(before.Manifest.Relationships);
            Assert.Equal(GraphSchemaRelationshipIdentity.DependentNode, relationship.Identity);
            Assert.Equal(1, relationship.RequiredIncomingLinks);
            Assert.Empty(relationship.Properties);
            Assert.DoesNotContain(before.Manifest.Nodes.Single(node => node.Label == "GraphBridgeReadReferencePost").Properties,
                property => property.Name == nameof(Post.BlogId));
            Assert.Contains(before.Comparison.Issues, issue => issue.Code == "DependentNodeCardinalityApplicationEnforced" && issue.Status == GraphSchemaStatus.Warn);
            Assert.DoesNotContain(before.Comparison.Proposals, proposal => proposal.EntityType == "RELATIONSHIP");
            await ExecuteAsync("MATCH ()-[r:GRAPHBRIDGE_READ_HAS_POST]->(:GraphBridgeReadReferencePost {Run:$run}) DELETE r", new { run = scope.Run });
            var after = await context.Database.InspectGraphSchemaAsync();
            Assert.Equal(before.Manifest.Fingerprint, after.Manifest.Fingerprint);
            Assert.Contains(after.Comparison.Issues, issue => issue.Code == "DependentNodeShapeNotValidated" && issue.Status == GraphSchemaStatus.Warn);
            Assert.DoesNotContain(after.Comparison.Proposals, proposal => proposal.EntityType == "RELATIONSHIP");
            await Assert.ThrowsAsync<InvalidOperationException>(() => context.Set<Post>().Where(post => post.Id == scope.PostId).ToListAsync());
            Assert.Empty(context.ChangeTracker.Entries());
        }
        finally { await CleanupAsync(scope); }
    }

    private QueryContext Context() => new(new DbContextOptionsBuilder<QueryContext>().UseNeo4j(fixture.Driver, fixture.Database).Options);
    private async Task SchemaAsync()
    {
        await ExecuteAsync("CREATE CONSTRAINT graphbridge_read_reference_blog_key IF NOT EXISTS FOR (n:GraphBridgeReadReferenceBlog) REQUIRE n.blog_key IS UNIQUE");
        await ExecuteAsync("CREATE CONSTRAINT graphbridge_read_reference_post_key IF NOT EXISTS FOR (n:GraphBridgeReadReferencePost) REQUIRE n.post_key IS UNIQUE");
    }
    private async Task SeedAsync(Scope scope, string title = "before")
    {
        await SchemaAsync();
        await ExecuteAsync("CREATE (:GraphBridgeReadReferenceBlog {blog_key:$id,Run:$run})", new { id = scope.BlogId.ToString("D"), run = scope.Run });
        await SeedPostAsync(scope, title);
    }
    private async Task SeedPostAsync(Scope scope, string title)
    {
        await ExecuteAsync("CREATE (:GraphBridgeReadReferencePost {post_key:$id,Run:$run,Title:$title,Version:1})", new { id = scope.PostId.ToString("D"), run = scope.Run, title });
        await SeedLinkAsync(scope);
    }
    private Task SeedLinkAsync(Scope scope) => ExecuteAsync(
        "MATCH (b:GraphBridgeReadReferenceBlog {blog_key:$blog}), (p:GraphBridgeReadReferencePost {post_key:$post}) CREATE (b)-[:GRAPHBRIDGE_READ_HAS_POST]->(p)",
        new { blog = scope.BlogId.ToString("D"), post = scope.PostId.ToString("D") });
    private async Task ExecuteAsync(string text, object? parameters = null)
    {
        await using var session = fixture.OpenSession();
        await (parameters is null ? await session.RunAsync(text) : await session.RunAsync(text, parameters)).ConsumeAsync();
    }
    private async Task CleanupAsync(Scope scope)
    {
        await ExecuteAsync("MATCH (b)-[r:GRAPHBRIDGE_READ_HAS_POST]->(p:GraphBridgeReadReferencePost) WHERE b.Run=$run AND p.Run=$run DELETE r", new { run = scope.Run });
        await ExecuteAsync("MATCH (n) WHERE n.Run=$run AND (n:GraphBridgeReadReferenceBlog OR n:GraphBridgeReadReferencePost OR n:GraphBridgeReadReferenceForeign) DELETE n", new { run = scope.Run });
    }
    private sealed record Scope(string Run, Guid BlogId, Guid PostId)
    {
        public static Scope New() => new(Guid.NewGuid().ToString("N"), Guid.Parse("abcdef01-" + Guid.NewGuid().ToString("D")[9..]), Guid.NewGuid());
    }
    public sealed class QueryContext(DbContextOptions<QueryContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder model)
        {
            model.Entity<Blog>().ToGraphNode("GraphBridgeReadReferenceBlog").Property(blog => blog.Id).HasGraphPropertyName("blog_key");
            model.Entity<Post>().ToGraphNode("GraphBridgeReadReferencePost").Property(post => post.Id).HasGraphPropertyName("post_key");
            model.Entity<Post>().Property(post => post.Version).IsRowVersion();
            model.Entity<Post>().HasOne(post => post.Blog).WithMany(blog => blog.Posts).HasForeignKey(post => post.BlogId)
                .OnDelete(DeleteBehavior.Restrict).ToGraphReference("GRAPHBRIDGE_READ_HAS_POST");
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
        public Guid Id { get; set; }
        public Guid BlogId { get; set; }
        public Blog Blog { get; set; } = null!;
        public string Run { get; set; } = "";
        public string Title { get; set; } = "";
        public long Version { get; set; }
    }
}
