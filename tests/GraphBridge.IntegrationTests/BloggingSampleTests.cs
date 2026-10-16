using GraphBridge.Samples.Blogging;
using Microsoft.EntityFrameworkCore;
using Neo4j.Driver;

namespace GraphBridge.IntegrationTests;

[Collection(Neo4jIntegrationCollection.Name)]
public sealed class BloggingSampleTests(DisposableNeo4jFixture fixture)
{
    [Neo4jIntegrationFact]
    public async Task Code_first_sample_runs_real_EF_CRUD_and_rollback()
    {
        await using var setup = fixture.OpenSession();
        await (await setup.RunAsync("CREATE CONSTRAINT graphbridge_blog_id IF NOT EXISTS FOR (n:GraphBridgeBlog) REQUIRE n.Id IS UNIQUE")).ConsumeAsync();
        await (await setup.RunAsync("CREATE CONSTRAINT graphbridge_post_id IF NOT EXISTS FOR (n:GraphBridgePost) REQUIRE n.Id IS UNIQUE")).ConsumeAsync();

        var result = await BloggingWorkflow.RunAsync(fixture.Driver, fixture.Database);
        Assert.Equal(3, result.Added);
        Assert.Equal(1, result.Updated);
        Assert.Equal(3, result.Deleted);
        Assert.Equal(2L, result.Version);
        Assert.True(result.RollbackVerified);
        Assert.True(result.ReferenceFixupVerified);
        Assert.True(result.IncludeVerified);
        Assert.Matches("^[a-f0-9]{64}$", result.SourceFingerprint);
        Assert.Equal(result.SourceFingerprint, result.TargetFingerprint);
        await using var independent = new BloggingContext(fixture.Driver, fixture.Database);
        Assert.Equal(independent.Database.GetGraphSchemaManifest().Fingerprint, result.TargetFingerprint);
        Assert.Contains("WHERE", result.Cypher);
        Assert.Contains("$p0", result.Cypher);
        Assert.DoesNotContain("https://", result.Cypher);
    }

    [Neo4jIntegrationFact]
    public async Task Canceled_workflow_refuses_to_start_transport()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var observer = new BloggingCancellationObservation(cancellation);
        var driver = BloggingCancellationProxy.Wrap(fixture.Driver, observer);
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            BloggingWorkflow.RunAsync(driver, fixture.Database, cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(0, observer.Calls);
    }

    [Neo4jIntegrationFact]
    public async Task Cancellation_after_acknowledged_creation_cleans_its_nodes_and_links_and_preserves_token()
    {
        await using var session = fixture.OpenSession();
        await (await session.RunAsync("CREATE CONSTRAINT graphbridge_blog_id IF NOT EXISTS FOR (n:GraphBridgeBlog) REQUIRE n.Id IS UNIQUE")).ConsumeAsync();
        await (await session.RunAsync("CREATE CONSTRAINT graphbridge_post_id IF NOT EXISTS FOR (n:GraphBridgePost) REQUIRE n.Id IS UNIQUE")).ConsumeAsync();
        using var cancellation = new CancellationTokenSource();
        var observer = new BloggingCancellationObservation(cancellation);
        var driver = BloggingCancellationProxy.Wrap(fixture.Driver, observer);
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            BloggingWorkflow.RunAsync(driver, fixture.Database, cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.True(observer.Committed);
        Assert.Equal(3, observer.CreatedIds.Count);
        Assert.False(error.Data.Contains("GraphBridge.BloggingCleanupError"));
        var cursor = await session.RunAsync("MATCH (n) WHERE (n:GraphBridgeBlog OR n:GraphBridgePost) AND n.Id IN $ids RETURN count(n) AS count",
            new { ids = observer.CreatedIds.Select(id => id.ToString("D")).ToArray() });
        Assert.Equal(0L, (await cursor.SingleAsync())["count"].As<long>());
    }
}
