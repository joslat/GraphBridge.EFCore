using GraphBridge.EntityFrameworkCore.Search;
using Microsoft.EntityFrameworkCore;
using Neo4j.Driver;

namespace GraphBridge.IntegrationTests;

[Collection(Neo4jIntegrationCollection.Name)]
public sealed class ProviderSearchTests(DisposableNeo4jFixture fixture)
{
    private const string NodeVector = "graphbridge_search_node_vector";
    private const string EdgeVector = "graphbridge_search_edge_vector";
    private const string NodeText = "graphbridge_search_node_text";
    private const string EdgeText = "graphbridge_search_edge_text";

    [Neo4jIntegrationFact]
    public async Task Euclidean_metric_and_eventual_fulltext_report_observed_configuration_and_match_native_results()
    {
        const string euclidean = "graphbridge_search_euclidean";
        const string eventual = "graphbridge_search_eventual";
        var run = Guid.NewGuid().ToString("N");
        try
        {
            await Seed(run);
            await Execute($"CREATE VECTOR INDEX {euclidean} IF NOT EXISTS FOR (n:GraphBridgeSearchDoc) ON (n.metricEmbedding) OPTIONS {{indexConfig: {{`vector.dimensions`:3, `vector.similarity_function`:'euclidean'}}}}");
            await Execute($"CREATE FULLTEXT INDEX {eventual} IF NOT EXISTS FOR (n:GraphBridgeSearchDoc) ON EACH [n.eventualBody] OPTIONS {{indexConfig: {{`fulltext.eventually_consistent`:true}}}}");
            await Execute("MATCH (n:GraphBridgeSearchDoc {Run:$run}) SET n.metricEmbedding=n.embedding,n.eventualBody=n.body", new { run });
            await Execute("CALL db.awaitIndex($index,60)", new { index = euclidean });
            await Execute("CALL db.awaitIndex($index,60)", new { index = eventual });
            // The caller explicitly waits in this fixture; the typed API never promises an implicit refresh.
            await Execute("CALL db.index.fulltext.awaitEventuallyConsistentIndexRefresh()");
            await using var db = Context();
            double[] vector = [0.1, 0, 0];
            var metric = await db.Database.QueryVectorNodesAsync(euclidean, vector, Key, limit: 2);
            EqualResults(await Native("CALL db.index.vector.queryNodes($index,$limit,$query) YIELD node AS entity,score RETURN entity,score ORDER BY score DESC", euclidean, vector, 2), metric);
            Assert.Equal("euclidean", metric.Provenance.SimilarityFunction, ignoreCase: true);
            Assert.Equal("b", metric.Results[0].Value);
            var nativeText = await Native("CALL db.index.fulltext.queryNodes($index,$query,{limit:$limit}) YIELD node AS entity,score RETURN entity,score ORDER BY score DESC", eventual, "graph", 2);
            Assert.NotEmpty(nativeText);
            var text = await db.Database.QueryFullTextNodesAsync(eventual, "graph", Key, limit: 2);
            EqualResults(nativeText, text);
            Assert.True(text.Provenance.EventuallyConsistent); Assert.NotEmpty(text.Results);
        }
        finally { await Cleanup(run); }
    }

    [Neo4jIntegrationFact]
    public async Task Node_and_relationship_vector_results_match_native_scores_and_limits_without_tracking()
    {
        var run = Guid.NewGuid().ToString("N");
        try
        {
            await Seed(run); await using var db = Context(); double[] vector = [1, 0, 0];
            var nodes = await db.Database.QueryVectorNodesAsync(NodeVector, vector, Key, limit: 2);
            var edges = await db.Database.QueryVectorRelationshipsAsync(EdgeVector, vector, Key, limit: 2);
            var nativeNodes = await Native("CALL db.index.vector.queryNodes($index,$limit,$query) YIELD node AS entity,score RETURN entity,score ORDER BY score DESC", NodeVector, vector, 2);
            var nativeEdges = await Native("CALL db.index.vector.queryRelationships($index,$limit,$query) YIELD relationship AS entity,score RETURN entity,score ORDER BY score DESC", EdgeVector, vector, 2);
            EqualResults(nativeNodes, nodes); EqualResults(nativeEdges, edges);
            Assert.Equal(2, nodes.Results.Count); Assert.Equal("a", nodes.Results[0].Value);
            Assert.All(nodes.Results, hit => Assert.InRange(hit.Score, 0d, 1d));
            Assert.Equal(3, nodes.Provenance.Dimensions); Assert.Equal("cosine", nodes.Provenance.SimilarityFunction, ignoreCase: true);
            Assert.Equal(fixture.ServerVersion, nodes.Provenance.ServerVersion);
            Assert.Equal(GraphSearchEntityKind.Relationship, edges.Provenance.EntityKind);
            Assert.Empty(db.ChangeTracker.Entries()); Assert.Null(db.Database.CurrentTransaction);
            var fewer = await db.Database.QueryVectorNodesAsync(NodeVector, vector, Key, limit: 1);
            Assert.Single(fewer.Results);
        }
        finally { await Cleanup(run); }
    }

    [Neo4jIntegrationFact]
    public async Task Fulltext_node_and_relationship_queries_preserve_Lucene_semantics_scores_and_empty_provenance()
    {
        var run = Guid.NewGuid().ToString("N");
        try
        {
            await Seed(run); await using var db = Context();
            foreach (var query in new[] { "graph", "body:graph AND retrieval", "\"graph retrieval\"" })
            {
                var nodes = await db.Database.QueryFullTextNodesAsync(NodeText, query, Key, limit: 2);
                var edges = await db.Database.QueryFullTextRelationshipsAsync(EdgeText, query, Key, limit: 2);
                EqualResults(await Native("CALL db.index.fulltext.queryNodes($index,$query,{limit:$limit}) YIELD node AS entity,score RETURN entity,score ORDER BY score DESC", NodeText, query, 2), nodes);
                EqualResults(await Native("CALL db.index.fulltext.queryRelationships($index,$query,{limit:$limit}) YIELD relationship AS entity,score RETURN entity,score ORDER BY score DESC", EdgeText, query, 2), edges);
                Assert.NotEmpty(nodes.Results); Assert.False(nodes.Provenance.EventuallyConsistent);
                Assert.False(string.IsNullOrWhiteSpace(nodes.Provenance.Analyzer)); Assert.Null(nodes.Provenance.Dimensions);
            }
            var empty = await db.Database.QueryFullTextNodesAsync(NodeText, "termthatdoesnotexist", Key);
            Assert.Empty(empty.Results); Assert.Equal(NodeText, empty.Provenance.IndexName);
            Assert.Empty(db.ChangeTracker.Entries());
        }
        finally { await Cleanup(run); }
    }

    [Neo4jIntegrationFact]
    public async Task Missing_wrong_kind_dimension_and_invalid_Lucene_fail_without_creating_schema_and_context_recovers()
    {
        var run = Guid.NewGuid().ToString("N");
        try
        {
            await Seed(run); await using var db = Context();
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.Database.QueryVectorNodesAsync("graphbridge_search_missing", new[] { 1d, 0, 0 }, Key));
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.Database.QueryVectorNodesAsync(NodeText, new[] { 1d, 0, 0 }, Key));
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.Database.QueryFullTextRelationshipsAsync(NodeText, "graph", Key));
            await Assert.ThrowsAsync<ArgumentException>(() => db.Database.QueryVectorNodesAsync(NodeVector, new[] { 1d, 0 }, Key));
            await Assert.ThrowsAsync<ArgumentException>(() => db.Database.QueryVectorNodesAsync(NodeVector, new[] { 0d, 0, 0 }, Key));
            await Assert.ThrowsAnyAsync<Neo4jException>(() => db.Database.QueryFullTextNodesAsync(NodeText, "\"unterminated", Key));
            Assert.Null(db.Database.CurrentTransaction);
            Assert.NotEmpty((await db.Database.QueryFullTextNodesAsync(NodeText, "graph", Key)).Results);
            await using var session = fixture.OpenSession();
            var missing = await (await session.RunAsync("SHOW INDEXES YIELD name WHERE name=$name RETURN name", new { name = "graphbridge_search_missing" })).ToListAsync();
            Assert.Empty(missing);
        }
        finally { await Cleanup(run); }
    }

    [Neo4jIntegrationFact]
    public async Task Search_borrows_transaction_and_projection_cancellation_preserves_caller_ownership()
    {
        var run = Guid.NewGuid().ToString("N");
        try
        {
            await Seed(run); await using var db = Context();
            await using var transaction = await db.Database.BeginTransactionAsync();
            await db.Database.QueryCypherAsync("MATCH (n:GraphBridgeSearchDoc {Run:$run,Key:'a'}) SET n.pending=true RETURN n.Key AS key",
                new Dictionary<string, object?> { ["run"] = run }, r => r["key"].As<string>());
            var result = await db.Database.QueryVectorNodesAsync(NodeVector, new[] { 1d, 0, 0 }, n => n.Properties.ContainsKey("pending"), limit: 1);
            Assert.True(Assert.Single(result.Results).Value); Assert.Same(transaction, db.Database.CurrentTransaction);
            var primary = new FormatException("search projection");
            Assert.Same(primary, await Assert.ThrowsAsync<FormatException>(() => db.Database.QueryFullTextNodesAsync<string>(NodeText, "graph", _ => throw primary)));
            using var cancel = new CancellationTokenSource();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => db.Database.QueryFullTextRelationshipsAsync(EdgeText, "graph", r => { cancel.Cancel(); return Key(r); }, cancellationToken: cancel.Token));
            Assert.Same(transaction, db.Database.CurrentTransaction);
            await transaction.RollbackAsync();
            Assert.False(Assert.Single((await db.Database.QueryVectorNodesAsync(NodeVector, new[] { 1d, 0, 0 }, n => n.Properties.ContainsKey("pending"), limit: 1)).Results).Value);
        }
        finally { await Cleanup(run); }
    }

    private DbContext Context() => new(new DbContextOptionsBuilder().UseNeo4j(fixture.Driver, fixture.Database).Options);
    private static string Key(IEntity entity) => entity.Properties["Key"].As<string>();
    private static void EqualResults(IReadOnlyList<(string Key, double Score)> expected, GraphSearchResults<string> actual)
    {
        Assert.Equal(expected.Select(e => e.Key), actual.Results.Select(r => r.Value));
        for (var i = 0; i < expected.Count; i++) Assert.Equal(expected[i].Score, actual.Results[i].Score, precision: 6);
        Assert.All(actual.Results, hit => Assert.False(string.IsNullOrWhiteSpace(hit.ElementId)));
    }
    private async Task<IReadOnlyList<(string Key, double Score)>> Native(string text, string index, object query, int limit)
    {
        await using var session = fixture.OpenSession();
        return await (await session.RunAsync(text, new { index, query, limit })).ToListAsync(r => (Key(r["entity"].As<IEntity>()), r["score"].As<double>()));
    }
    private async Task Seed(string run)
    {
        await Execute($"CREATE VECTOR INDEX {NodeVector} IF NOT EXISTS FOR (n:GraphBridgeSearchDoc) ON (n.embedding) OPTIONS {{indexConfig: {{`vector.dimensions`:3, `vector.similarity_function`:'cosine'}}}}");
        await Execute($"CREATE VECTOR INDEX {EdgeVector} IF NOT EXISTS FOR ()-[r:GRAPHBRIDGE_SEARCH_EDGE]-() ON (r.embedding) OPTIONS {{indexConfig: {{`vector.dimensions`:3, `vector.similarity_function`:'cosine'}}}}");
        await Execute($"CREATE FULLTEXT INDEX {NodeText} IF NOT EXISTS FOR (n:GraphBridgeSearchDoc) ON EACH [n.body] OPTIONS {{indexConfig: {{`fulltext.eventually_consistent`:false}}}}");
        await Execute($"CREATE FULLTEXT INDEX {EdgeText} IF NOT EXISTS FOR ()-[r:GRAPHBRIDGE_SEARCH_EDGE]-() ON EACH [r.body] OPTIONS {{indexConfig: {{`fulltext.eventually_consistent`:false}}}}");
        foreach (var index in new[] { NodeVector, EdgeVector, NodeText, EdgeText }) await Execute("CALL db.awaitIndex($index,60)", new { index });
        await Execute("CREATE (a:GraphBridgeSearchAnchor {Run:$run}) WITH a UNWIND $docs AS d CREATE (n:GraphBridgeSearchDoc {Run:$run,Key:d.key,body:d.body,embedding:d.embedding}) CREATE (n)-[:GRAPHBRIDGE_SEARCH_EDGE {Run:$run,Key:d.key,body:d.body,embedding:d.embedding}]->(a)",
            new { run, docs = new[] {
                new { key = "a", body = "graph graph graph retrieval", embedding = new[] { 1d, 0, 0 } },
                new { key = "b", body = "graph data", embedding = new[] { 0.8d, 0.2d, 0 } },
                new { key = "c", body = "isolation", embedding = new[] { 0d, 1, 0 } } } });
    }
    private async Task Execute(string text, object? parameters = null)
    {
        await using var session = fixture.OpenSession();
        await (parameters is null ? await session.RunAsync(text) : await session.RunAsync(text, parameters)).ConsumeAsync();
    }
    private async Task Cleanup(string run)
    {
        await Execute("MATCH (n:GraphBridgeSearchDoc)-[r:GRAPHBRIDGE_SEARCH_EDGE]->(a:GraphBridgeSearchAnchor) WHERE n.Run=$run AND a.Run=$run AND r.Run=$run DELETE r", new { run });
        await Execute("MATCH (n) WHERE n.Run=$run AND (n:GraphBridgeSearchDoc OR n:GraphBridgeSearchAnchor) DELETE n", new { run });
    }
}
