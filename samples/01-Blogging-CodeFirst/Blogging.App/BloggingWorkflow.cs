using Microsoft.EntityFrameworkCore;
using Neo4j.Driver;

namespace GraphBridge.Samples.Blogging;

public sealed record BloggingResult(int Added, int Updated, int Deleted, long Version, string Cypher,
    bool RollbackVerified, bool ReferenceFixupVerified, bool IncludeVerified,
    string SourceFingerprint, string TargetFingerprint);

/// <summary>All application reads and writes use EF; setup constraints and native integration assertions are separate.</summary>
public static class BloggingWorkflow
{
    public static async Task<BloggingResult> RunAsync(IDriver driver, string database, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var blog = new Blog { Url = "https://example.org/graphbridge" };
        var post = new Post { Title = "EF tracking, native relationships", Content = "Original content" };
        var second = new Post { Title = "One state owner", Content = null };
        blog.Posts.Add(post);
        blog.Posts.Add(second);
        await using var create = new BloggingContext(driver, database);
        var sourceFingerprint = create.Database.GetGraphSchemaManifest().Fingerprint;
        create.Blogs.Add(blog);

        try
        {
            var added = await create.SaveChangesAsync(cancellationToken);
            if (added != 3 || blog.Version != 1 || post.Version != 1 || second.Version != 1
                || post.BlogId != blog.Id || second.BlogId != blog.Id || !ReferenceEquals(post.Blog, blog) || !ReferenceEquals(second.Blog, blog))
                throw new InvalidOperationException("Blog/Post graph creation did not preserve three EF entries, generated versions and navigation fixup.");
            await using var edit = new BloggingContext(driver, database);
            var query = edit.Blogs.Where(b => b.Id == blog.Id).Include(b => b.Posts).OrderBy(b => b.Url).Take(10);
            var cypher = query.ToQueryString();
            var loaded = await query.SingleAsync(cancellationToken);
            var posts = loaded.Posts;
            if (posts.Count != 2 || posts.Any(p => p.BlogId != loaded.Id || !ReferenceEquals(p.Blog, loaded))
                || posts.Any(p => ReferenceEquals(p, post) || ReferenceEquals(p, second)))
                throw new InvalidOperationException("Fresh-context collection Include did not restore the required FK and EF navigation fixup.");
            if (!edit.Entry(loaded).Collection(b => b.Posts).IsLoaded || edit.ChangeTracker.Entries().Count() != 3
                || posts.Any(p => !edit.Entry(p).Reference(value => value.Blog).IsLoaded))
                throw new InvalidOperationException("Collection Include did not mark its navigations loaded with exactly three tracked entries.");
            loaded.Url = "https://example.org/graphbridge/updated";
            var updated = await edit.SaveChangesAsync(cancellationToken);

            await using (var pending = new BloggingContext(driver, database))
            await using (var transaction = await pending.Database.BeginTransactionAsync(cancellationToken))
            {
                var draft = await pending.Posts.Where(p => p.Id == post.Id).SingleAsync(cancellationToken);
                draft.Content = "This edit must roll back";
                if (await pending.SaveChangesAsync(cancellationToken) != 1) throw new InvalidOperationException("The post update must count one EF entry.");
                await transaction.RollbackAsync(CancellationToken.None);
            }

            await using var verify = new BloggingContext(driver, database);
            var persistedPosts = await verify.Posts.Where(p => p.BlogId == blog.Id).Include(p => p.Blog).OrderBy(p => p.Title).ToListAsync(cancellationToken);
            var persisted = persistedPosts.Single(p => p.Id == post.Id);
            var persistedSecond = persistedPosts.Single(p => p.Id == second.Id);
            if (persisted.Content != "Original content" || persisted.Version != 1 || persistedSecond.Content is not null || persistedSecond.Version != 1)
                throw new InvalidOperationException("The sample's explicit rollback did not preserve its original post.");
            var persistedBlog = persisted.Blog;
            if (persistedBlog.Id != blog.Id || persistedBlog.Posts.Count != 2 || !ReferenceEquals(persistedSecond.Blog, persistedBlog))
                throw new InvalidOperationException("Reference Include did not restore EF identity and navigation fixup.");
            if (persistedPosts.Count != 2 || verify.ChangeTracker.Entries().Count() != 3
                || persistedPosts.Any(p => !verify.Entry(p).Reference(value => value.Blog).IsLoaded)
                || verify.Entry(persistedBlog).Collection(b => b.Posts).IsLoaded)
                throw new InvalidOperationException("Reference Include must load references without claiming the inverse collection is complete.");
            var version = persistedBlog.Version;
            verify.Remove(persisted);
            verify.Remove(persistedSecond);
            verify.Remove(persistedBlog);
            var deleted = await verify.SaveChangesAsync(cancellationToken);
            if (updated != 1 || deleted != 3 || version != 2)
                throw new InvalidOperationException("The sample must update one entry and delete two dependents plus their principal.");
            await using var absent = new BloggingContext(driver, database);
            if ((await absent.Blogs.Where(b => b.Id == blog.Id).ToListAsync(cancellationToken)).Count != 0)
                throw new InvalidOperationException("The sample's deleted Blog remained in storage.");
            foreach (var id in new[] { post.Id, second.Id })
                if ((await absent.Posts.Where(p => p.Id == id).ToListAsync(cancellationToken)).Count != 0)
                    throw new InvalidOperationException("A sample Post remained after dependent-first deletion.");
            var targetFingerprint = absent.Database.GetGraphSchemaManifest().Fingerprint;
            if (sourceFingerprint != targetFingerprint) throw new InvalidOperationException("The code-first storage mapping changed during the workflow.");
            return new BloggingResult(added, updated, deleted, version, cypher, true, true, true, sourceFingerprint, targetFingerprint);
        }
        catch (Exception primary)
        {
            // Cleanup uses normal dependent-owned link deletion, restricted to this invocation's node IDs.
            try
            {
                await using var cleanup = new BloggingContext(driver, database);
                foreach (var id in new[] { post.Id, second.Id })
                    foreach (var item in await cleanup.Posts.Where(p => p.Id == id).ToListAsync()) cleanup.Remove(item);
                foreach (var item in await cleanup.Blogs.Where(b => b.Id == blog.Id).ToListAsync()) cleanup.Remove(item);
                await cleanup.SaveChangesAsync();
            }
            catch (Exception cleanupError) { primary.Data["GraphBridge.BloggingCleanupError"] = cleanupError; }
            throw;
        }
    }
}
