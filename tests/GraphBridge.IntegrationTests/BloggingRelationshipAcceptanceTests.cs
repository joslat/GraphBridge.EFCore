using GraphBridge.Samples.Blogging;
using Microsoft.EntityFrameworkCore;
using Neo4j.Driver;

namespace GraphBridge.IntegrationTests;

/// <summary>BLOG-07/08 acceptance through the actual sample model, including independent root fixup and eager loading.</summary>
[Collection(Neo4jIntegrationCollection.Name)]
public sealed class BloggingRelationshipAcceptanceTests(DisposableNeo4jFixture fixture)
{
    [Neo4jIntegrationFact]
    public async Task Blog07_navigation_graph_creates_three_EF_entries_and_two_directed_native_links_then_fixes_up_fresh_roots()
    {
        var blogId = Guid.NewGuid();
        var postIds = new[] { Guid.NewGuid(), Guid.NewGuid() };
        try
        {
            await EnsureSchemaAsync();
            var blog = new Blog { Id = blogId, Url = "https://example.org/blog-07" };
            blog.Posts.Add(new Post { Id = postIds[0], Title = "First post", Content = "Original content" });
            blog.Posts.Add(new Post { Id = postIds[1], Title = "Second post", Content = null });
            await using (var create = Context())
            {
                create.Blogs.Add(blog);
                Assert.Equal(3, create.ChangeTracker.Entries().Count());
                Assert.All(blog.Posts, post => { Assert.Same(blog, post.Blog); Assert.Equal(blogId, post.BlogId); });
                Assert.Equal(3, await create.SaveChangesAsync());
                Assert.Equal(1L, blog.Version);
                Assert.All(blog.Posts, post => Assert.Equal(1L, post.Version));
                Assert.All(create.ChangeTracker.Entries(), entry => Assert.Equal(EntityState.Unchanged, entry.State));
            }

            await AssertNativeGraphAsync(blogId, postIds);
            await using (var fresh = Context())
            {
                var parent = await fresh.Blogs.Where(value => value.Id == blogId).SingleAsync();
                var children = await fresh.Posts.Where(value => value.BlogId == blogId).OrderBy(value => value.Title).ToListAsync();
                Assert.Equal(2, children.Count);
                Assert.Equal(2, parent.Posts.Count);
                Assert.All(children, child => { Assert.Equal(blogId, child.BlogId); Assert.Same(parent, child.Blog); Assert.Contains(child, parent.Posts); });
                Assert.All(children, child => Assert.DoesNotContain(child, blog.Posts));
                Assert.False(fresh.Entry(parent).Collection(value => value.Posts).IsLoaded);
                // Normal EF fixup is not Include and must not claim that the collection was eagerly loaded.
                var firstId = postIds[0];
                var first = children.Single(value => value.Id == firstId);
                Assert.Same(first, await fresh.Posts.Where(value => value.Id == firstId).SingleAsync());
                first.Title = "Updated through the actual Blogging model";
                Assert.Equal(1, await fresh.SaveChangesAsync());
                Assert.Equal(2L, first.Version);
                Assert.Equal(1L, parent.Version);
            }
            await AssertNativeGraphAsync(blogId, postIds);

            await using (var delete = Context())
            {
                // Materialize dependents before principal to exercise inverse fixup as well as deletion ordering.
                var children = await delete.Posts.Where(value => value.BlogId == blogId).ToListAsync();
                var parent = await delete.Blogs.Where(value => value.Id == blogId).SingleAsync();
                Assert.Equal(2, parent.Posts.Count);
                Assert.All(children, child => Assert.Same(parent, child.Blog));
                delete.RemoveRange(children);
                delete.Remove(parent);
                Assert.Equal(3, await delete.SaveChangesAsync());
            }
            Assert.Equal(0L, await ScopedNodeCountAsync([blogId, .. postIds]));
        }
        finally { await CleanupAsync(blogId, postIds); }
    }

    [Neo4jIntegrationFact]
    public async Task Blog07_principal_delete_remains_restrictive_and_scalar_only_legacy_post_is_not_silently_adopted()
    {
        var blogId = Guid.NewGuid();
        var postIds = new[] { Guid.NewGuid(), Guid.NewGuid() };
        try
        {
            await EnsureSchemaAsync();
            await using (var seed = Context())
            {
                var blog = new Blog { Id = blogId, Url = "https://example.org/blog-07/restrict" };
                blog.Posts.Add(new Post { Id = postIds[0], Title = "Required child" });
                seed.Add(blog);
                Assert.Equal(2, await seed.SaveChangesAsync());
            }
            await using (var remove = Context())
            {
                var blog = await remove.Blogs.Where(value => value.Id == blogId).SingleAsync();
                remove.Remove(blog);
                await Assert.ThrowsAsync<DbUpdateException>(() => remove.SaveChangesAsync());
                Assert.Equal(EntityState.Deleted, remove.Entry(blog).State);
            }
            Assert.Equal(2L, await ScopedNodeCountAsync([blogId, postIds[0]]));
            await AssertNativeGraphAsync(blogId, [postIds[0]]);

            // Deliberate legacy fixture: the previous sample persisted a scalar BlogId and no owned relationship.
            var legacyId = postIds[1];
            await ExecuteAsync("CREATE (:GraphBridgePost {Id:$id,BlogId:$blogId,title:$title,Version:$version})",
                new { id = legacyId.ToString("D"), blogId = blogId.ToString("D"), title = "Legacy scalar reference", version = 1L });
            await using var read = Context();
            await Assert.ThrowsAsync<InvalidOperationException>(() => read.Posts.Where(value => value.Id == legacyId).SingleAsync());
            Assert.Empty(read.ChangeTracker.Entries());
            await using var session = fixture.OpenSession();
            var cursor = await session.RunAsync("MATCH (post:GraphBridgePost {Id:$id}) OPTIONAL MATCH ()-[link:HAS_POST]->(post) RETURN count(link) AS count",
                new { id = legacyId.ToString("D") });
            Assert.Equal(0L, (await cursor.SingleAsync())["count"].As<long>());
        }
        finally { await CleanupAsync(blogId, postIds); }
    }

    [Neo4jIntegrationFact]
    public async Task Blog08_actual_sample_Includes_both_directions_with_root_paging_empty_collections_and_no_tracking()
    {
        var firstId = Guid.NewGuid();
        var middleId = Guid.NewGuid();
        var emptyId = Guid.NewGuid();
        var firstPostId = Guid.NewGuid();
        var middlePostIds = new[] { Guid.NewGuid(), Guid.NewGuid() };
        try
        {
            await EnsureSchemaAsync();
            await using (var seed = Context())
            {
                var first = new Blog { Id = firstId, Url = "https://example.org/blog-08/a" };
                first.Posts.Add(new Post { Id = firstPostId, Title = "First blog post" });
                var middle = new Blog { Id = middleId, Url = "https://example.org/blog-08/b" };
                middle.Posts.Add(new Post { Id = middlePostIds[0], Title = "Middle first", Content = "Included content" });
                middle.Posts.Add(new Post { Id = middlePostIds[1], Title = "Middle second", Content = null });
                seed.AddRange(first, middle, new Blog { Id = emptyId, Url = "https://example.org/blog-08/c" });
                Assert.Equal(6, await seed.SaveChangesAsync());
            }

            await using (var collection = Context())
            {
                var query = collection.Blogs.Where(blog => blog.Id == firstId || blog.Id == middleId || blog.Id == emptyId)
                    .Include(blog => blog.Posts).OrderBy(blog => blog.Url).Skip(1).Take(1);
                Assert.DoesNotContain("https://", query.ToQueryString());
                var middle = Assert.Single(await query.ToListAsync());
                Assert.Equal(middleId, middle.Id);
                Assert.Equal(middlePostIds.Order(), middle.Posts.Select(post => post.Id).Order());
                Assert.True(collection.Entry(middle).Collection(blog => blog.Posts).IsLoaded);
                Assert.All(middle.Posts, post =>
                {
                    Assert.Same(middle, post.Blog);
                    Assert.Equal(middleId, post.BlogId);
                    Assert.True(collection.Entry(post).Reference(value => value.Blog).IsLoaded);
                });
                Assert.Equal(3, collection.ChangeTracker.Entries().Count());
                var empty = await collection.Blogs.Where(blog => blog.Id == emptyId).Include(blog => blog.Posts).SingleAsync();
                Assert.Empty(empty.Posts);
                Assert.True(collection.Entry(empty).Collection(blog => blog.Posts).IsLoaded);
                Assert.Equal(4, collection.ChangeTracker.Entries().Count());
            }

            await using (var reference = Context())
            {
                var posts = await reference.Posts.Where(post => post.BlogId == middleId).Include(post => post.Blog)
                    .OrderBy(post => post.Title).ToListAsync();
                Assert.Equal(2, posts.Count);
                var parent = posts[0].Blog;
                Assert.Equal(middleId, parent.Id);
                Assert.Equal(2, parent.Posts.Count);
                Assert.All(posts, post =>
                {
                    Assert.Same(parent, post.Blog);
                    Assert.Contains(post, parent.Posts);
                    Assert.True(reference.Entry(post).Reference(value => value.Blog).IsLoaded);
                });
                Assert.False(reference.Entry(parent).Collection(blog => blog.Posts).IsLoaded);
                Assert.Equal(3, reference.ChangeTracker.Entries().Count());
            }

            await using (var detached = Context())
            {
                var blogs = await detached.Blogs.Where(blog => blog.Id == firstId || blog.Id == middleId || blog.Id == emptyId)
                    .Include(blog => blog.Posts).AsNoTracking().OrderBy(blog => blog.Url).ToListAsync();
                Assert.Equal(3, blogs.Count);
                Assert.Empty(blogs.Single(blog => blog.Id == emptyId).Posts);
                var middle = blogs.Single(blog => blog.Id == middleId);
                Assert.Equal(2, middle.Posts.Count);
                Assert.All(middle.Posts, post => Assert.Same(middle, post.Blog));
                var posts = await detached.Posts.Where(post => post.BlogId == middleId).Include(post => post.Blog)
                    .AsNoTracking().OrderBy(post => post.Title).ToListAsync();
                Assert.Equal(2, posts.Count);
                Assert.All(posts, post =>
                {
                    Assert.Equal(middleId, post.Blog.Id);
                    Assert.Contains(post, post.Blog.Posts);
                });
                Assert.Empty(detached.ChangeTracker.Entries());
            }
        }
        finally
        {
            await CleanupAsync(firstId, [firstPostId]);
            await CleanupAsync(middleId, middlePostIds);
            await CleanupAsync(emptyId, []);
        }
    }

    private BloggingContext Context() => new(fixture.Driver, fixture.Database);

    private async Task EnsureSchemaAsync()
    {
        await ExecuteAsync("CREATE CONSTRAINT graphbridge_blog_id IF NOT EXISTS FOR (n:GraphBridgeBlog) REQUIRE n.Id IS UNIQUE", new { });
        await ExecuteAsync("CREATE CONSTRAINT graphbridge_post_id IF NOT EXISTS FOR (n:GraphBridgePost) REQUIRE n.Id IS UNIQUE", new { });
    }

    private async Task AssertNativeGraphAsync(Guid blogId, IReadOnlyList<Guid> postIds)
    {
        await using var session = fixture.OpenSession();
        var cursor = await session.RunAsync(
            "MATCH (blog:GraphBridgeBlog {Id:$id}) OPTIONAL MATCH (blog)-[link]->(post) "
            + "RETURN type(link) AS type,labels(post) AS labels,properties(post) AS properties,properties(link) AS linkProperties LIMIT $limit",
            new { id = blogId.ToString("D"), limit = (long)postIds.Count + 1 });
        var rows = await cursor.ToListAsync();
        Assert.Equal(postIds.Count, rows.Count);
        var actualIds = new List<string>();
        foreach (var row in rows)
        {
            Assert.Equal("HAS_POST", row["type"].As<string>());
            Assert.Equal("GraphBridgePost", Assert.Single(row["labels"].As<List<string>>()));
            var properties = row["properties"].As<Dictionary<string, object>>();
            Assert.False(properties.ContainsKey("BlogId"));
            Assert.Contains("title", properties.Keys);
            Assert.Contains("Version", properties.Keys);
            Assert.Empty(row["linkProperties"].As<Dictionary<string, object>>());
            actualIds.Add(properties["Id"].As<string>());
        }
        Assert.Equal(postIds.Select(id => id.ToString("D")).Order(StringComparer.Ordinal), actualIds.Order(StringComparer.Ordinal));
    }

    private async Task<long> ScopedNodeCountAsync(IEnumerable<Guid> ids)
    {
        await using var session = fixture.OpenSession();
        var cursor = await session.RunAsync("MATCH (n) WHERE (n:GraphBridgeBlog OR n:GraphBridgePost) AND n.Id IN $ids RETURN count(n) AS count",
            new { ids = ids.Select(id => id.ToString("D")).ToArray() });
        return (await cursor.SingleAsync())["count"].As<long>();
    }

    private async Task ExecuteAsync(string cypher, object parameters)
    {
        await using var session = fixture.OpenSession();
        await (await session.RunAsync(cypher, parameters)).ConsumeAsync();
    }

    private async Task CleanupAsync(Guid blogId, IEnumerable<Guid> postIds)
    {
        var ids = postIds.Select(id => id.ToString("D")).ToArray();
        await ExecuteAsync("MATCH (blog:GraphBridgeBlog {Id:$blogId})-[link:HAS_POST]->(post:GraphBridgePost) WHERE post.Id IN $ids DELETE link",
            new { blogId = blogId.ToString("D"), ids });
        await ExecuteAsync("MATCH (post:GraphBridgePost) WHERE post.Id IN $ids DELETE post", new { ids });
        await ExecuteAsync("MATCH (blog:GraphBridgeBlog {Id:$id}) DELETE blog", new { id = blogId.ToString("D") });
    }
}
