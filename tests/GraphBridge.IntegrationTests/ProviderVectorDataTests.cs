using GraphBridge.EntityFrameworkCore.Search;
using GraphBridge.Samples.VectorSearch;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.VectorData;
using Neo4j.Driver;

namespace GraphBridge.IntegrationTests;

/// <summary>PL-09, public issue #1: the VectorData adapter on real vector indexes, compared with direct GraphBridge search.</summary>
[Collection(Neo4jIntegrationCollection.Name)]
public sealed class ProviderVectorDataTests(DisposableNeo4jFixture fixture)
{
    private const string NodeVector = "graphbridge_vectordata_node";
    private const string EdgeVector = "graphbridge_vectordata_edge";
    private const string EmptyVector = "graphbridge_vectordata_empty";
    private const string EmptyEdgeVector = "graphbridge_vectordata_empty_edge";
    private const string EuclideanVector = "graphbridge_vectordata_euclidean";
    private static readonly float[] Query = [1f, 0.1f, 0f];

    public sealed record Hit(string Key);
    private sealed class PoolContext(DbContextOptions<PoolContext> options) : DbContext(options);

    [Neo4jIntegrationFact]
    public async Task Node_and_relationship_adapters_match_direct_search_for_every_input_type()
    {
        var run = Guid.NewGuid().ToString("N");
        try
        {
            await Seed(run);
            await using var db = Context();
            var vector = Query.Select(v => (double)v).ToArray();
            var direct = await db.Database.QueryVectorNodesAsync(NodeVector, vector, Key, limit: 3);
            var directEdges = await db.Database.QueryVectorRelationshipsAsync(EdgeVector, vector, Key, limit: 3);
            var nodes = db.Database.AsNodeVectorSearchable(NodeVector, n => new Hit(Key(n)));
            Same(direct, await Collect(nodes.SearchAsync(Query, 3)));
            Same(direct, await Collect(nodes.SearchAsync(new ReadOnlyMemory<float>(Query), 3)));
            Same(direct, await Collect(nodes.SearchAsync(new Embedding<float>(Query), 3)));
            Same(directEdges, await Collect(db.Database.AsRelationshipVectorSearchable(EdgeVector, r => new Hit(Key(r))).SearchAsync(Query, 3)));
            Assert.Equal(new[] { "a", "b", "c" }, direct.Results.Select(r => r.Value));
            Assert.Single(await Collect(nodes.SearchAsync(Query, 1)));
            Assert.Empty(await Collect(db.Database.AsNodeVectorSearchable(EmptyVector, n => new Hit(Key(n))).SearchAsync(Query, 5)));
            Assert.Empty(await Collect(db.Database.AsRelationshipVectorSearchable(EmptyEdgeVector, r => new Hit(Key(r))).SearchAsync(Query, 5)));
            // A limit above the index size returns what exists. The search is approximate, so near-ties (a and b here)
            // may come back in a different order at a larger limit; compare with the direct search at the same limit.
            var all = await Collect(nodes.SearchAsync(Query, 10_000));
            Same(await db.Database.QueryVectorNodesAsync(NodeVector, vector, Key, limit: 10_000), all);
            Assert.Equal(new[] { "a", "b", "c" }, all.Select(r => r.Record.Key).Order());
            Assert.Equal(all.Select(r => r.Score).OrderByDescending(s => s), all.Select(r => r.Score));
            Assert.Empty(db.ChangeTracker.Entries());
            Assert.Null(db.Database.CurrentTransaction);
        }
        finally { await Cleanup(run); }
    }

    [Neo4jIntegrationFact]
    public async Task Scores_follow_the_documented_cosine_and_euclidean_formulas()
    {
        var run = Guid.NewGuid().ToString("N");
        try
        {
            await Seed(run);
            await using var db = Context();
            // a = [1,0,0], c = [0,1,0]; the query is a, so cos is 1 and 0, and the squared distance is 0 and 2.
            var cosine = await Collect(db.Database.AsNodeVectorSearchable(NodeVector, n => new Hit(Key(n))).SearchAsync(new[] { 1f, 0, 0 }, 3));
            Assert.Equal(1.0, cosine.Single(r => r.Record.Key == "a").Score!.Value, precision: 6);
            Assert.Equal(0.5, cosine.Single(r => r.Record.Key == "c").Score!.Value, precision: 6);
            var euclidean = await Collect(db.Database.AsNodeVectorSearchable(EuclideanVector, n => new Hit(Key(n))).SearchAsync(new[] { 1f, 0, 0 }, 3));
            Assert.Equal(1.0, euclidean.Single(r => r.Record.Key == "a").Score!.Value, precision: 6);
            Assert.Equal(1.0 / 3, euclidean.Single(r => r.Record.Key == "c").Score!.Value, precision: 6);
            var direct = await db.Database.QueryVectorNodesAsync(EuclideanVector, [1d, 0, 0], Key, limit: 3);
            Same(direct, euclidean);
        }
        finally { await Cleanup(run); }
    }

    [Neo4jIntegrationFact]
    public async Task Caller_transaction_survives_every_failure_and_nothing_is_committed_by_the_adapter()
    {
        var run = Guid.NewGuid().ToString("N");
        try
        {
            await Seed(run);
            await using var db = Context();
            var nodes = db.Database.AsNodeVectorSearchable(NodeVector, n => new Hit(Key(n)));
            await using var transaction = await db.Database.BeginTransactionAsync();
            await db.Database.QueryCypherAsync("MATCH (n:GraphBridgeVectorDataDoc {Run:$run,Key:'a'}) SET n.pending = true RETURN n.Key AS key",
                new Dictionary<string, object?> { ["run"] = run }, r => r["key"].As<string>());
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Collect(nodes.SearchAsync(Query, 1, cancellationToken: cancelled.Token)));
            }
            await Assert.ThrowsAsync<FormatException>(() => Collect(db.Database.AsNodeVectorSearchable<Hit>(NodeVector, _ => throw new FormatException()).SearchAsync(Query, 1)));
            await Assert.ThrowsAsync<InvalidOperationException>(() => Collect(db.Database.AsNodeVectorSearchable("graphbridge_vectordata_missing", n => new Hit(Key(n))).SearchAsync(Query, 1)));
            using (var cancel = new CancellationTokenSource())
            {
                await using var results = nodes.SearchAsync(Query, 3).WithCancellation(cancel.Token).GetAsyncEnumerator();
                Assert.True(await results.MoveNextAsync());
                cancel.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await results.MoveNextAsync());
            }
            Assert.Same(transaction, db.Database.CurrentTransaction);
            Assert.NotEmpty(await Collect(nodes.SearchAsync(Query, 3)));
            await transaction.RollbackAsync();
            // The pending write was never committed by any adapter call.
            Assert.Empty(await db.Database.QueryCypherAsync("MATCH (n:GraphBridgeVectorDataDoc {Run:$run}) WHERE n.pending RETURN n.Key AS key",
                new Dictionary<string, object?> { ["run"] = run }, r => r["key"].As<string>()));
        }
        finally { await Cleanup(run); }
    }

    [Neo4jIntegrationFact]
    public async Task Existing_preflight_still_rejects_missing_indexes_wrong_dimensions_and_zero_cosine_vectors()
    {
        var run = Guid.NewGuid().ToString("N");
        try
        {
            await Seed(run);
            await using var db = Context();
            var nodes = db.Database.AsNodeVectorSearchable(NodeVector, n => new Hit(Key(n)));
            await Assert.ThrowsAsync<InvalidOperationException>(() => Collect(
                db.Database.AsNodeVectorSearchable("graphbridge_vectordata_missing", n => new Hit(Key(n))).SearchAsync(Query, 1)));
            await Assert.ThrowsAsync<InvalidOperationException>(() => Collect(
                db.Database.AsRelationshipVectorSearchable(NodeVector, r => new Hit(Key(r))).SearchAsync(Query, 1)));
            await Assert.ThrowsAsync<ArgumentException>(() => Collect(nodes.SearchAsync(new[] { 1f, 0f }, 1)));
            await Assert.ThrowsAsync<ArgumentException>(() => Collect(nodes.SearchAsync(new[] { 0f, 0f, 0f }, 1)));
            Assert.Null(db.Database.CurrentTransaction);
            await using var session = fixture.OpenSession();
            Assert.Empty(await (await session.RunAsync("SHOW INDEXES YIELD name WHERE name = $name RETURN name",
                new { name = "graphbridge_vectordata_missing" })).ToListAsync());
            Assert.NotEmpty(await Collect(nodes.SearchAsync(Query, 1)));
        }
        finally { await Cleanup(run); }
    }

    [Neo4jIntegrationFact]
    public async Task Adapter_borrows_caller_transactions_and_keeps_failures_cancellation_and_the_concurrency_guard()
    {
        var run = Guid.NewGuid().ToString("N");
        try
        {
            await Seed(run);
            await using var db = Context();
            var nodes = db.Database.AsNodeVectorSearchable(NodeVector, n => new Hit(Key(n)));
            await using (var transaction = await db.Database.BeginTransactionAsync())
            {
                Assert.NotEmpty(await Collect(nodes.SearchAsync(Query, 2)));
                Assert.Same(transaction, db.Database.CurrentTransaction); // neither committed nor disposed by the adapter
                await transaction.RollbackAsync();
            }
            Assert.Null(db.Database.CurrentTransaction);

            var primary = new FormatException("projector");
            Assert.Same(primary, await Assert.ThrowsAsync<FormatException>(() => Collect(
                db.Database.AsNodeVectorSearchable<Hit>(NodeVector, _ => throw primary).SearchAsync(Query, 1))));

            using (var cancel = new CancellationTokenSource())
            {
                await using var results = nodes.SearchAsync(Query, 3).WithCancellation(cancel.Token).GetAsyncEnumerator();
                Assert.True(await results.MoveNextAsync());
                cancel.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await results.MoveNextAsync());
            }

            // A second search on the same context while the first is still reading is refused by the existing guard.
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var blocking = db.Database.AsNodeVectorSearchable(NodeVector, n => { entered.Set(); release.Wait(TimeSpan.FromSeconds(30)); return new Hit(Key(n)); });
            var first = Task.Run(() => Collect(blocking.SearchAsync(Query, 1)));
            Assert.True(entered.Wait(TimeSpan.FromSeconds(30)));
            await Assert.ThrowsAsync<InvalidOperationException>(() => Collect(nodes.SearchAsync(Query, 1)));
            release.Set();
            Assert.Single(await first);
            Assert.Single(await Collect(nodes.SearchAsync(Query, 1)));
            Assert.Null(db.Database.CurrentTransaction);
        }
        finally { await Cleanup(run); }
    }

    [Neo4jIntegrationFact]
    public async Task Pooled_and_disposed_contexts_never_run_a_stale_adapter()
    {
        var run = Guid.NewGuid().ToString("N");
        try
        {
            await Seed(run);
            var builder = new DbContextOptionsBuilder<PoolContext>();
            builder.UseNeo4j(fixture.Driver, fixture.Database);
            var pool = new PooledDbContextFactory<PoolContext>(builder.Options);
            var first = await pool.CreateDbContextAsync();
            var kept = first.Database.AsNodeVectorSearchable(NodeVector, n => new Hit(Key(n)));
            Assert.NotEmpty(await Collect(kept.SearchAsync(Query, 1)));
            await first.DisposeAsync();
            await using var second = await pool.CreateDbContextAsync();
            Assert.Same(first, second);
            Assert.Contains("earlier lease", (await Assert.ThrowsAsync<InvalidOperationException>(() => Collect(kept.SearchAsync(Query, 1)))).Message);
            Assert.NotEmpty(await Collect(second.Database.AsNodeVectorSearchable(NodeVector, n => new Hit(Key(n))).SearchAsync(Query, 1)));

            var disposed = Context();
            var stale = disposed.Database.AsNodeVectorSearchable(NodeVector, n => new Hit(Key(n)));
            await disposed.DisposeAsync();
            await Assert.ThrowsAsync<ObjectDisposedException>(() => Collect(stale.SearchAsync(Query, 1)));
        }
        finally { await Cleanup(run); }
    }

    [Neo4jIntegrationFact]
    public async Task Sample_consumer_finds_movies_through_IVectorSearchable_only()
    {
        try
        {
            await Execute(MovieSearchSetup.CreateIndex);
            await Execute(MovieSearchSetup.AwaitIndex);
            await Execute(MovieSearchSetup.Seed, new { movies = MovieSearchSetup.Movies });
            await using var services = new ServiceCollection()
                .AddDbContext<MoviesContext>(options => options.UseNeo4j(fixture.Driver, fixture.Database))
                .AddMovieSearch()
                .BuildServiceProvider();
            await using var scope = services.CreateAsyncScope();
            var matches = await scope.ServiceProvider.GetRequiredService<MovieFinder>().FindAsync(new float[] { 1, 0, 0, 0 });
            Assert.Equal(new[] { "Interstellar", "Arrival", "Her" }, matches.Select(m => m.Record.Title));
            var db = scope.ServiceProvider.GetRequiredService<MoviesContext>();
            var direct = await db.Database.QueryVectorNodesAsync(MovieSearchSetup.Index, [1d, 0, 0, 0], n => n["title"].As<string>(), limit: 3);
            Assert.Equal(direct.Results.Select(r => r.Value), matches.Select(m => m.Record.Title));
            Assert.Equal(direct.Results.Select(r => (double?)r.Score), matches.Select(m => m.Score));
        }
        finally
        {
            await Execute(MovieSearchSetup.DeleteMovies);
            await Execute(MovieSearchSetup.DropIndex);
        }
    }

    private DbContext Context() => new(new DbContextOptionsBuilder().UseNeo4j(fixture.Driver, fixture.Database).Options);
    private static string Key(IEntity entity) => entity.Properties["Key"].As<string>();
    private static void Same(GraphSearchResults<string> expected, IReadOnlyList<VectorSearchResult<Hit>> actual)
    {
        Assert.Equal(expected.Results.Select(r => r.Value), actual.Select(r => r.Record.Key));
        Assert.Equal(expected.Results.Select(r => (double?)r.Score), actual.Select(r => r.Score));
    }
    private static async Task<List<VectorSearchResult<Hit>>> Collect(IAsyncEnumerable<VectorSearchResult<Hit>> source)
    {
        var results = new List<VectorSearchResult<Hit>>();
        await foreach (var result in source) results.Add(result);
        return results;
    }
    private async Task Seed(string run)
    {
        await Execute($"CREATE VECTOR INDEX {NodeVector} IF NOT EXISTS FOR (n:GraphBridgeVectorDataDoc) ON (n.embedding) OPTIONS {{indexConfig: {{`vector.dimensions`:3, `vector.similarity_function`:'cosine'}}}}");
        await Execute($"CREATE VECTOR INDEX {EdgeVector} IF NOT EXISTS FOR ()-[r:GRAPHBRIDGE_VECTORDATA_EDGE]-() ON (r.embedding) OPTIONS {{indexConfig: {{`vector.dimensions`:3, `vector.similarity_function`:'cosine'}}}}");
        await Execute($"CREATE VECTOR INDEX {EmptyVector} IF NOT EXISTS FOR (n:GraphBridgeVectorDataNone) ON (n.embedding) OPTIONS {{indexConfig: {{`vector.dimensions`:3, `vector.similarity_function`:'cosine'}}}}");
        await Execute($"CREATE VECTOR INDEX {EmptyEdgeVector} IF NOT EXISTS FOR ()-[r:GRAPHBRIDGE_VECTORDATA_NONE]-() ON (r.embedding) OPTIONS {{indexConfig: {{`vector.dimensions`:3, `vector.similarity_function`:'cosine'}}}}");
        await Execute($"CREATE VECTOR INDEX {EuclideanVector} IF NOT EXISTS FOR (n:GraphBridgeVectorDataDoc) ON (n.metric) OPTIONS {{indexConfig: {{`vector.dimensions`:3, `vector.similarity_function`:'euclidean'}}}}");
        foreach (var index in new[] { NodeVector, EdgeVector, EmptyVector, EmptyEdgeVector, EuclideanVector }) await Execute("CALL db.awaitIndex($index,60)", new { index });
        await Execute("CREATE (a:GraphBridgeVectorDataAnchor {Run:$run}) WITH a UNWIND $docs AS d CREATE (n:GraphBridgeVectorDataDoc {Run:$run,Key:d.key,embedding:d.embedding,metric:d.embedding}) CREATE (n)-[:GRAPHBRIDGE_VECTORDATA_EDGE {Run:$run,Key:d.key,embedding:d.embedding}]->(a)",
            new { run, docs = new[] {
                new { key = "a", embedding = new[] { 1d, 0, 0 } },
                new { key = "b", embedding = new[] { 0.8d, 0.2d, 0 } },
                new { key = "c", embedding = new[] { 0d, 1, 0 } } } });
    }
    private async Task Execute(string text, object? parameters = null)
    {
        await using var session = fixture.OpenSession();
        await (parameters is null ? await session.RunAsync(text) : await session.RunAsync(text, parameters)).ConsumeAsync();
    }
    private async Task Cleanup(string run)
    {
        await Execute("MATCH (n:GraphBridgeVectorDataDoc)-[r:GRAPHBRIDGE_VECTORDATA_EDGE]->(a:GraphBridgeVectorDataAnchor) WHERE n.Run=$run AND a.Run=$run AND r.Run=$run DELETE r", new { run });
        await Execute("MATCH (n) WHERE n.Run=$run AND (n:GraphBridgeVectorDataDoc OR n:GraphBridgeVectorDataAnchor) DELETE n", new { run });
    }
}
