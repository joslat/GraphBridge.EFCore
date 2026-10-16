using GraphBridge.Samples.Blogging;
using Microsoft.EntityFrameworkCore;
using Neo4j.Driver;

namespace GraphBridge.IntegrationTests;

/// <summary>Sample-specific BLOG-03 through BLOG-06 evidence using the actual BloggingContext and its native required references.</summary>
[Collection(Neo4jIntegrationCollection.Name)]
public sealed class BloggingNodeAcceptanceTests(DisposableNeo4jFixture fixture)
{
    [Neo4jIntegrationFact]
    public async Task Blog03_tracking_reuses_identity_while_fresh_contexts_read_current_persisted_values()
    {
        var id = Guid.NewGuid();
        const string original = "https://example.org/blog-03/original";
        const string changed = "https://example.org/blog-03/changed";
        try
        {
            await EnsureSchemaAsync();
            var created = new Blog { Id = id, Url = original };
            await using (var seed = Context())
            {
                seed.Blogs.Add(created);
                Assert.Equal(1, await seed.SaveChangesAsync());
                Assert.Equal(1L, created.Version);
            }

            await using var firstContext = Context();
            var first = await firstContext.Blogs.Where(blog => blog.Id == id).SingleAsync();
            Assert.NotSame(created, first);
            Assert.Equal(id, first.Id);
            Assert.Equal(original, first.Url);
            Assert.Equal(1L, first.Version);
            Assert.Same(first, await firstContext.Blogs.Where(blog => blog.Id == id).SingleAsync());
            Assert.Single(firstContext.ChangeTracker.Entries<Blog>());

            await using (var writer = Context())
            {
                var current = await writer.Blogs.Where(blog => blog.Id == id).SingleAsync();
                Assert.NotSame(first, current);
                current.Url = changed;
                Assert.Equal(1, await writer.SaveChangesAsync());
                Assert.Equal(2L, current.Version);
            }

            // Ordinary EF tracking intentionally retains a tracked instance's values; it is not an automatic refresh.
            var trackedAgain = await firstContext.Blogs.Where(blog => blog.Id == id).SingleAsync();
            Assert.Same(first, trackedAgain);
            Assert.Equal(original, trackedAgain.Url);
            Assert.Equal(1L, trackedAgain.Version);
            var detachedCurrent = await firstContext.Blogs.AsNoTracking().Where(blog => blog.Id == id).SingleAsync();
            Assert.NotSame(first, detachedCurrent);
            Assert.Equal(changed, detachedCurrent.Url);
            Assert.Equal(2L, detachedCurrent.Version);
            Assert.Single(firstContext.ChangeTracker.Entries<Blog>());

            await using var fresh = Context();
            var persisted = await fresh.Blogs.Where(blog => blog.Id == id).SingleAsync();
            Assert.NotSame(first, persisted);
            Assert.NotSame(detachedCurrent, persisted);
            Assert.Equal(changed, persisted.Url);
            Assert.Equal(2L, persisted.Version);
            Assert.Equal(EntityState.Unchanged, fresh.Entry(persisted).State);
        }
        finally { await CleanupAsync([id], []); }
    }

    [Neo4jIntegrationFact]
    public async Task Blog04_url_update_preserves_unmapped_stored_properties_and_unchanged_related_post()
    {
        var id = Guid.NewGuid();
        var postId = Guid.NewGuid();
        const string external = "external `' payload remains untouched";
        try
        {
            await EnsureSchemaAsync();
            await using (var seed = Context())
            {
                seed.Add(new Blog { Id = id, Url = "https://example.org/blog-04/original" });
                seed.Add(new Post { Id = postId, BlogId = id, Title = "Related post", Content = "Unchanged content" });
                Assert.Equal(2, await seed.SaveChangesAsync());
            }

            await using var edit = Context();
            var blog = await edit.Blogs.Where(blog => blog.Id == id).SingleAsync();
            var post = await edit.Posts.Where(post => post.Id == postId).SingleAsync();
            // Native fixture setup models an unrelated application's properties after the EF read.
            await ExecuteAsync("MATCH (blog:GraphBridgeBlog {Id:$id}) SET blog.externalNote=$note,blog.externalRank=$rank",
                new { id = id.ToString("D"), note = external, rank = 42L });
            blog.Url = "https://example.org/blog-04/changed";
            edit.ChangeTracker.DetectChanges();
            Assert.True(edit.Entry(blog).Property(value => value.Url).IsModified);
            Assert.False(edit.Entry(blog).Property(value => value.Id).IsModified);
            Assert.False(edit.Entry(blog).Property(value => value.Version).IsModified);
            Assert.Equal(EntityState.Unchanged, edit.Entry(post).State);
            Assert.Equal(1, await edit.SaveChangesAsync());
            Assert.Equal(2L, blog.Version);
            Assert.Equal(1L, post.Version);

            var properties = await BlogPropertiesAsync(id);
            Assert.Equal(id.ToString("D"), properties["Id"].As<string>());
            Assert.Equal(blog.Url, properties["url"].As<string>());
            Assert.Equal(2L, properties["Version"].As<long>());
            Assert.Equal(external, properties["externalNote"].As<string>());
            Assert.Equal(42L, properties["externalRank"].As<long>());
            Assert.Equal(new[] { "Id", "Version", "externalNote", "externalRank", "url" }, properties.Keys.Order(StringComparer.Ordinal));

            await using var fresh = Context();
            var persisted = await fresh.Blogs.Where(blog => blog.Id == id).SingleAsync();
            var related = await fresh.Posts.Where(post => post.Id == postId).SingleAsync();
            Assert.Equal(blog.Url, persisted.Url);
            Assert.Equal(2L, persisted.Version);
            Assert.Equal(id, related.BlogId);
            Assert.Equal("Related post", related.Title);
            Assert.Equal("Unchanged content", related.Content);
            Assert.Equal(1L, related.Version);
            Assert.Same(persisted, related.Blog);
            Assert.Same(related, Assert.Single(persisted.Posts));
        }
        finally { await CleanupAsync([id], [postId]); }
    }

    [Neo4jIntegrationFact]
    public async Task Blog05_second_added_blog_constraint_failure_rolls_back_first_insert_and_retains_pending_EF_state()
    {
        var existingId = Guid.NewGuid();
        var earlierId = Guid.NewGuid();
        const string existingUrl = "https://example.org/blog-05/existing";
        try
        {
            await EnsureSchemaAsync();
            await using (var seed = Context())
            {
                seed.Add(new Blog { Id = existingId, Url = existingUrl });
                Assert.Equal(1, await seed.SaveChangesAsync());
            }
            await using var edit = Context();
            Assert.Empty(await edit.Blogs.Where(blog => blog.Id == earlierId).ToListAsync());
            var earlier = new Blog { Id = earlierId, Url = "https://example.org/blog-05/must-roll-back" };
            var duplicate = new Blog { Id = existingId, Url = "https://example.org/blog-05/duplicate-refused" };
            edit.Add(earlier);
            edit.Add(duplicate);
            var failure = await Assert.ThrowsAsync<DbUpdateException>(() => edit.SaveChangesAsync());
            Assert.IsNotType<DbUpdateConcurrencyException>(failure);
            Assert.Equal("Neo.ClientError.Schema.ConstraintValidationFailed", Assert.IsAssignableFrom<Neo4jException>(failure.InnerException).Code);
            // Neo4jDatabase reports entries from its actual ordered mutation plan. This proves the new row precedes
            // the duplicate-key command, rather than counting any failed two-entry save as a second-write failure.
            Assert.Collection(failure.Entries, entry => Assert.Same(earlier, entry.Entity), entry => Assert.Same(duplicate, entry.Entity));
            Assert.Equal(EntityState.Added, edit.Entry(earlier).State);
            Assert.Equal(EntityState.Added, edit.Entry(duplicate).State);
            Assert.Equal(0L, earlier.Version);
            Assert.Equal(0L, duplicate.Version);
            Assert.Equal(0L, edit.Entry(earlier).Property(blog => blog.Version).OriginalValue);
            Assert.Null(edit.Database.CurrentTransaction);

            await using var fresh = Context();
            Assert.Empty(await fresh.Blogs.Where(blog => blog.Id == earlierId).ToListAsync());
            var original = await fresh.Blogs.Where(blog => blog.Id == existingId).SingleAsync();
            Assert.Equal(existingUrl, original.Url);
            Assert.Equal(1L, original.Version);
            var wire = await BlogPropertiesAsync(existingId);
            Assert.Equal(existingUrl, wire["url"].As<string>());
            Assert.Equal(1L, wire["Version"].As<long>());
        }
        finally { await CleanupAsync([existingId, earlierId], []); }
    }

    [Neo4jIntegrationFact]
    public async Task Blog06_two_context_stale_version_conflict_reports_exact_entry_and_preserves_winner()
    {
        var id = Guid.NewGuid();
        try
        {
            await EnsureSchemaAsync();
            await using (var seed = Context())
            {
                seed.Add(new Blog { Id = id, Url = "https://example.org/blog-06/original" });
                Assert.Equal(1, await seed.SaveChangesAsync());
            }
            await using var winnerContext = Context();
            await using var staleContext = Context();
            var winner = await winnerContext.Blogs.Where(blog => blog.Id == id).SingleAsync();
            var stale = await staleContext.Blogs.Where(blog => blog.Id == id).SingleAsync();
            Assert.NotSame(winner, stale);
            Assert.Equal(1L, winner.Version);
            Assert.Equal(1L, stale.Version);
            winner.Url = "https://example.org/blog-06/winner";
            stale.Url = "https://example.org/blog-06/stale";
            Assert.Equal(1, await winnerContext.SaveChangesAsync());
            Assert.Equal(2L, winner.Version);
            var failure = await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => staleContext.SaveChangesAsync());
            Assert.Same(stale, Assert.Single(failure.Entries).Entity);
            Assert.Equal(EntityState.Modified, staleContext.Entry(stale).State);
            Assert.True(staleContext.Entry(stale).Property(blog => blog.Url).IsModified);
            Assert.Equal("https://example.org/blog-06/stale", stale.Url);
            Assert.Equal(1L, stale.Version);
            Assert.Equal(1L, staleContext.Entry(stale).Property(blog => blog.Version).OriginalValue);
            Assert.Null(staleContext.Database.CurrentTransaction);

            await using var fresh = Context();
            var persisted = await fresh.Blogs.Where(blog => blog.Id == id).SingleAsync();
            Assert.Equal(winner.Url, persisted.Url);
            Assert.Equal(2L, persisted.Version);
            Assert.Equal(winner.Url, (await BlogPropertiesAsync(id))["url"].As<string>());
        }
        finally { await CleanupAsync([id], []); }
    }

    private BloggingContext Context() => new(fixture.Driver, fixture.Database);

    private async Task EnsureSchemaAsync()
    {
        // Same explicitly owned fixture constraints as the executable Blogging sample; no ordinary EF operation deploys them.
        await ExecuteAsync("CREATE CONSTRAINT graphbridge_blog_id IF NOT EXISTS FOR (n:GraphBridgeBlog) REQUIRE n.Id IS UNIQUE", new { });
        await ExecuteAsync("CREATE CONSTRAINT graphbridge_post_id IF NOT EXISTS FOR (n:GraphBridgePost) REQUIRE n.Id IS UNIQUE", new { });
    }

    private async Task ExecuteAsync(string cypher, object parameters)
    {
        await using var session = fixture.OpenSession();
        await (await session.RunAsync(cypher, parameters)).ConsumeAsync();
    }

    private async Task<IReadOnlyDictionary<string, object>> BlogPropertiesAsync(Guid id)
    {
        await using var session = fixture.OpenSession();
        var result = await session.RunAsync("MATCH (blog:GraphBridgeBlog {Id:$id}) RETURN properties(blog) AS properties LIMIT 2",
            new { id = id.ToString("D") });
        return (await result.SingleAsync())["properties"].As<Dictionary<string, object>>();
    }

    private async Task CleanupAsync(IEnumerable<Guid> blogIds, IEnumerable<Guid> postIds)
    {
        // Test fixture cleanup only: exact IDs allocated above, with restrictive plain DELETE and no journal changes.
        await ExecuteAsync("MATCH (blog:GraphBridgeBlog)-[link:HAS_POST]->(post:GraphBridgePost) WHERE blog.Id IN $blogIds AND post.Id IN $postIds DELETE link",
            new { blogIds = blogIds.Select(id => id.ToString("D")).ToArray(), postIds = postIds.Select(id => id.ToString("D")).ToArray() });
        await ExecuteAsync("MATCH (post:GraphBridgePost) WHERE post.Id IN $ids DELETE post",
            new { ids = postIds.Select(id => id.ToString("D")).ToArray() });
        await ExecuteAsync("MATCH (blog:GraphBridgeBlog) WHERE blog.Id IN $ids DELETE blog",
            new { ids = blogIds.Select(id => id.ToString("D")).ToArray() });
    }
}
