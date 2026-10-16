using Microsoft.EntityFrameworkCore;
using Neo4j.Driver;

namespace GraphBridge.IntegrationTests;

[Collection(Neo4jIntegrationCollection.Name)]
public sealed class ExplainQueryTests(DisposableNeo4jFixture fixture)
{
    [Neo4jIntegrationFact]
    public async Task Generated_LINQ_Cypher_can_be_explained_with_explicit_store_parameters()
    {
        await using var db = new GraphBridge.Samples.Blogging.BloggingContext(fixture.Driver, fixture.Database);
        var id = Guid.NewGuid();
        var statement = db.Blogs.Where(blog => blog.Id == id).ToQueryString();
        Assert.Contains("$p0", statement);
        Assert.DoesNotContain(id.ToString("D"), statement);
        var explanation = await db.Database.ExplainCypherAsync(statement,
            new Dictionary<string, object?> { ["p0"] = id.ToString("D") });
        Assert.NotEmpty(explanation.Plan.OperatorType);
        Assert.Empty(db.ChangeTracker.Entries());
        Assert.Null(db.Database.CurrentTransaction);
    }

    [Neo4jIntegrationFact]
    public async Task Explain_returns_real_read_and_write_plans_without_creating_data()
    {
        await using var db = Context();
        var id = Guid.NewGuid().ToString("N");
        var parameters = new Dictionary<string, object?> { ["id"] = id };
        var write = await db.Database.ExplainCypherAsync("CREATE (n:GraphBridgeExplainOnly {id:$id}) RETURN n", parameters);
        Assert.NotEmpty(write.Plan.OperatorType);
        Assert.NotEmpty(write.Plan.Children);
        var statement = "MATCH (n:GraphBridgeExplainOnly {id:$id}) RETURN n";
        var first = await db.Database.ExplainCypherAsync(statement, parameters);
        var second = await db.Database.ExplainCypherAsync(statement, new Dictionary<string, object?> { ["id"] = "changed" });
        Assert.Matches("^[a-f0-9]{64}$", first.StatementFingerprint);
        Assert.Equal(first.StatementFingerprint, second.StatementFingerprint);
        Assert.NotEqual(write.StatementFingerprint, first.StatementFingerprint);
        Assert.Null(db.Database.CurrentTransaction);
        Assert.Empty(db.ChangeTracker.Entries());
        var count = await db.Database.QueryCypherAsync("MATCH (n:GraphBridgeExplainOnly {id:$id}) RETURN count(n) AS count",
            parameters, r => r["count"].As<long>());
        Assert.Equal(0L, Assert.Single(count));
    }

    [Neo4jIntegrationFact]
    public async Task Explain_preserves_borrowed_transaction_and_pending_write_until_caller_rolls_back()
    {
        var id = Guid.NewGuid().ToString("N");
        var parameters = new Dictionary<string, object?> { ["id"] = id };
        await using var db = Context();
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            await db.Database.QueryCypherAsync("CREATE (n:GraphBridgeExplainOnly {id:$id}) RETURN n.id AS id", parameters, r => r["id"].As<string>());
            await db.Database.ExplainCypherAsync("MATCH (n:GraphBridgeExplainOnly {id:$id}) DELETE n", parameters);
            Assert.Same(transaction, db.Database.CurrentTransaction);
            var stillThere = await db.Database.QueryCypherAsync("MATCH (n:GraphBridgeExplainOnly {id:$id}) RETURN count(n) AS count", parameters, r => r["count"].As<long>());
            Assert.Equal(1L, Assert.Single(stillThere));
            await transaction.RollbackAsync();
        }
        await using var fresh = Context();
        Assert.Equal(0L, Assert.Single(await fresh.Database.QueryCypherAsync("MATCH (n:GraphBridgeExplainOnly {id:$id}) RETURN count(n) AS count", parameters, r => r["count"].As<long>())));
    }

    [Neo4jIntegrationFact]
    public async Task Invalid_statement_plan_cap_and_cancellation_leave_context_usable()
    {
        await using var db = Context();
        var empty = new Dictionary<string, object?>();
        await Assert.ThrowsAnyAsync<Neo4jException>(() => db.Database.ExplainCypherAsync("RETURN )", empty));
        // EXPLAIN permits missing parameter values on some Neo4j versions; do not infer execution validation.
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.Database.ExplainCypherAsync("MATCH (n:GraphBridgeExplainOnly) RETURN n", empty, maxPlanNodes: 1));
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => db.Database.ExplainCypherAsync("RETURN 1", empty, cancellationToken: canceled.Token));
        Assert.Null(db.Database.CurrentTransaction);
        Assert.NotNull(await db.Database.ExplainCypherAsync("RETURN 1", empty));
    }

    private DbContext Context() => new(new DbContextOptionsBuilder().UseNeo4j(fixture.Driver, fixture.Database).Options);
}
