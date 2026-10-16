using Microsoft.EntityFrameworkCore;

namespace GraphBridge.IntegrationTests;

public sealed partial class ProviderNodeReferenceUpdateTests
{
    [Neo4jIntegrationFact]
    public async Task Bulk_required_reference_preserves_membership_and_deletes_only_its_owned_FK_link()
    {
        var run = Guid.NewGuid().ToString("N");
        try
        {
            var (blog, post) = await SeedAsync(run);
            await using var context = Context();
            Assert.Equal(1, await context.Set<Post>().Where(p => p.BlogId == blog.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.Title, "bulk")));
            Assert.Equal("bulk", (await PostPropertiesAsync(post.Id))["Title"]);
            Assert.Equal(2L, (await PostPropertiesAsync(post.Id))["Version"]);
            Assert.Equal(Wire(blog.Id), await ParentIdAsync(post.Id));
            await ExecuteAsync("MATCH (p:GraphBridgeReferencePost {Id:$id}) CREATE (p)-[:GRAPHBRIDGE_REFERENCE_EXTERNAL]->(:GraphBridgeReferenceForeign {Run:$run})",
                new { id = Wire(post.Id), run });
            await Assert.ThrowsAsync<DbUpdateException>(() => context.Set<Post>().Where(p => p.Id == post.Id).ExecuteDeleteAsync());
            Assert.Equal(1L, await LinkCountAsync(run)); // Removal of the modeled FK was rolled back too.
            await ExecuteAsync("MATCH (:GraphBridgeReferencePost {Id:$id})-[r:GRAPHBRIDGE_REFERENCE_EXTERNAL]->() DELETE r", new { id = Wire(post.Id) });
            Assert.Equal(1, await context.Set<Post>().Where(p => p.Id == post.Id).ExecuteDeleteAsync());
            Assert.Equal(0L, await LinkCountAsync(run));
            Assert.Equal(1, await context.Set<Blog>().CountAsync(b => b.Id == blog.Id));
        }
        finally { await CleanupAsync(run); }
    }

    [Neo4jIntegrationFact]
    public async Task Bulk_required_reference_rejects_duplicate_membership_before_payload_updates()
    {
        var run = Guid.NewGuid().ToString("N");
        try
        {
            var (blog, post) = await SeedAsync(run);
            await ExecuteAsync("MATCH (b:GraphBridgeReferenceBlog {Id:$blog}), (p:GraphBridgeReferencePost {Id:$post}) CREATE (b)-[:GRAPHBRIDGE_HAS_POST]->(p)",
                new { blog = Wire(blog.Id), post = Wire(post.Id) });
            await using var context = Context();
            await Assert.ThrowsAsync<InvalidOperationException>(() => context.Set<Post>().Where(p => p.Run == run).Take(1)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.Title, "bad")));
            Assert.Equal("before", (await PostPropertiesAsync(post.Id))["Title"]);
            Assert.Equal(2L, await LinkCountAsync(run));
        }
        finally { await CleanupAsync(run); }
    }
}
