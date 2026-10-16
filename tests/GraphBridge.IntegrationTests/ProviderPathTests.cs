using GraphBridge.EntityFrameworkCore.Paths;
using Microsoft.EntityFrameworkCore;
using Neo4j.Driver;

namespace GraphBridge.IntegrationTests;

[Collection(Neo4jIntegrationCollection.Name)]
public sealed class ProviderPathTests(DisposableNeo4jFixture fixture)
{
    private const string Label = "GraphBridgePathNode";
    private const string Edge = "GRAPHBRIDGE_PATH_EDGE";
    private static readonly GraphPathPattern Pattern = new(Label, [Edge]);

    [Neo4jIntegrationFact]
    public async Task Directed_and_mixed_cycle_paths_match_native_order_and_preserve_parallel_edges_and_self_loops()
    {
        var run = Guid.NewGuid().ToString("N");
        try
        {
            await Seed(run); await using var db = Context();
            foreach (var direction in Enum.GetValues<GraphTraversalDirection>())
            {
                var actual = await db.Database.QueryPathsAsync(Pattern, Keys(run, "a"), direction, 3, 500, NodeKey, EdgeKey);
                var expected = await Native(run, "a", direction, 1, 3);
                Assert.Equal(expected.Select(p => Signature(p, direction)).Order(StringComparer.Ordinal), actual.Paths.Select(Signature).Order(StringComparer.Ordinal));
                Assert.True(actual.StartFound); Assert.False(actual.HasMorePaths);
                Assert.Contains(actual.Paths, p => p.Nodes.Count > p.Nodes.Distinct().Count());
                Assert.All(actual.Paths, p =>
                {
                    Assert.InRange(p.Relationships.Count, 1, 3);
                    Assert.Equal(p.Relationships.Count, p.Relationships.Distinct().Count());
                    Assert.Equal(p.Nodes.Count - 1, p.Relationships.Count);
                    Assert.DoesNotContain("other", p.Relationships);
                });
                if (direction == GraphTraversalDirection.Outgoing)
                {
                    Assert.Contains(actual.Paths, p => p.Relationships.SequenceEqual(new[] { "ab1" }));
                    Assert.Contains(actual.Paths, p => p.Relationships.SequenceEqual(new[] { "ab2" }));
                    Assert.Contains(actual.Paths, p => p.Relationships.SequenceEqual(new[] { "aa" }));
                }
            }
            Assert.Empty(db.ChangeTracker.Entries()); Assert.Null(db.Database.CurrentTransaction);
        }
        finally { await Cleanup(run); }
    }

    [Neo4jIntegrationFact]
    public async Task Depth_result_probe_zero_hops_missing_and_isolated_anchors_have_distinct_outcomes()
    {
        var run = Guid.NewGuid().ToString("N");
        try
        {
            await Seed(run); await using var db = Context();
            var truncated = await db.Database.QueryPathsAsync(Pattern, Keys(run, "a"), GraphTraversalDirection.Outgoing, 3, 1, NodeKey, EdgeKey);
            Assert.Single(truncated.Paths); Assert.True(truncated.HasMorePaths);
            var oneHop = await db.Database.QueryPathsAsync(Pattern, Keys(run, "a"), GraphTraversalDirection.Outgoing, 1, 3, NodeKey, EdgeKey);
            Assert.Equal(3, oneHop.Paths.Count); Assert.False(oneHop.HasMorePaths);
            Assert.All(oneHop.Paths, p => Assert.Single(p.Relationships));
            var twoOnly = await db.Database.QueryPathsAsync(Pattern, Keys(run, "a"), GraphTraversalDirection.Outgoing, 2, 500, NodeKey, EdgeKey, minDepth: 2);
            Assert.NotEmpty(twoOnly.Paths); Assert.All(twoOnly.Paths, p => Assert.Equal(2, p.Relationships.Count));
            var zero = await db.Database.QueryPathsAsync(Pattern, Keys(run, "a"), GraphTraversalDirection.Outgoing, 0, 1, NodeKey, EdgeKey, minDepth: 0);
            Assert.Equal("a", Assert.Single(Assert.Single(zero.Paths).Nodes)); Assert.Empty(zero.Paths[0].Relationships); Assert.False(zero.HasMorePaths);
            var isolated = await db.Database.QueryPathsAsync(Pattern, Keys(run, "isolated"), GraphTraversalDirection.Both, 4, 10, NodeKey, EdgeKey);
            Assert.True(isolated.StartFound); Assert.Empty(isolated.Paths); Assert.False(isolated.HasMorePaths);
            var missing = await db.Database.QueryPathsAsync(Pattern, Keys(run, "missing"), GraphTraversalDirection.Both, 4, 10, NodeKey, EdgeKey);
            Assert.False(missing.StartFound); Assert.Empty(missing.Paths);
            var empty = await db.Database.QueryPathsAsync(new GraphPathPattern("GraphBridgePathEmpty", [Edge]), Keys(run, "a"), GraphTraversalDirection.Outgoing, 0, 1, NodeKey, EdgeKey, minDepth: 0);
            Assert.False(empty.StartFound); Assert.Empty(empty.Paths);
        }
        finally { await Cleanup(run); }
    }

    [Neo4jIntegrationFact]
    public async Task Composite_anchor_parameters_and_type_allowlist_are_exact_and_duplicate_anchor_fails_before_projection()
    {
        var run = Guid.NewGuid().ToString("N");
        try
        {
            await Seed(run);
            const string hostile = "quote' MATCH (n) DELETE n //";
            await Execute($"MATCH (n:{Label} {{Run:$run,Key:'a'}}) SET n.Key=$key", new { run, key = hostile });
            await using var db = Context();
            var both = new GraphPathPattern(Label, [Edge, "GRAPHBRIDGE_PATH_OTHER"]);
            var paths = await db.Database.QueryPathsAsync(both, Keys(run, hostile), GraphTraversalDirection.Outgoing, 1, 20, NodeKey, EdgeKey);
            Assert.Equal(4, paths.Paths.Count); Assert.Contains(paths.Paths, p => p.Relationships.Contains("other"));
            Assert.All(paths.Paths, p => Assert.Equal(hostile, p.Nodes[0]));
            await Execute($"CREATE (n:{Label} {{Run:$run,Key:$key}})", new { run, key = hostile });
            var projected = false;
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => db.Database.QueryPathsAsync(both, Keys(run, hostile), GraphTraversalDirection.Outgoing, 2, 20,
                n => { projected = true; return NodeKey(n); }, EdgeKey));
            Assert.Contains("duplicate", error.Message); Assert.False(projected); Assert.Null(db.Database.CurrentTransaction);
            Assert.Empty(db.ChangeTracker.Entries());
        }
        finally { await Cleanup(run); }
    }

    [Neo4jIntegrationFact]
    public async Task Path_values_survive_disposal_and_borrowed_projection_errors_cancellation_and_rollback_preserve_ownership()
    {
        var run = Guid.NewGuid().ToString("N");
        try
        {
            await Seed(run);
            GraphPathResults<string, string> buffered;
            await using (var db = Context())
            {
                buffered = await db.Database.QueryPathsAsync(Pattern, Keys(run, "a"), GraphTraversalDirection.Outgoing, 2, 20, NodeKey, EdgeKey);
                await using var transaction = await db.Database.BeginTransactionAsync();
                await db.Database.QueryCypherAsync($"MATCH (n:{Label} {{Run:$run,Key:'b'}}) SET n.pending=true RETURN n.Key AS key",
                    new Dictionary<string, object?> { ["run"] = run }, r => r["key"].As<string>());
                var pending = await db.Database.QueryPathsAsync(Pattern, Keys(run, "a"), GraphTraversalDirection.Outgoing, 1, 20,
                    n => n.Properties.ContainsKey("pending"), EdgeKey);
                Assert.Contains(pending.Paths, p => p.Nodes.Contains(true)); Assert.Same(transaction, db.Database.CurrentTransaction);
                var primary = new FormatException("path projector");
                Assert.Same(primary, await Assert.ThrowsAsync<FormatException>(() => db.Database.QueryPathsAsync<string, string>(Pattern, Keys(run, "a"), GraphTraversalDirection.Outgoing, 2, 20, _ => throw primary, EdgeKey)));
                using var cancel = new CancellationTokenSource();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => db.Database.QueryPathsAsync(Pattern, Keys(run, "a"), GraphTraversalDirection.Outgoing, 2, 20,
                    n => { cancel.Cancel(); return NodeKey(n); }, EdgeKey, cancellationToken: cancel.Token));
                Assert.Same(transaction, db.Database.CurrentTransaction); await transaction.RollbackAsync();
                var fresh = await db.Database.QueryPathsAsync(Pattern, Keys(run, "a"), GraphTraversalDirection.Outgoing, 1, 20,
                    n => n.Properties.ContainsKey("pending"), EdgeKey);
                Assert.All(fresh.Paths, p => Assert.DoesNotContain(true, p.Nodes));
            }
            Assert.NotEmpty(buffered.Paths); Assert.All(buffered.Paths, p => Assert.Equal("a", p.Nodes[0]));
            Assert.Throws<NotSupportedException>(() => ((IList<string>)buffered.Paths[0].Nodes).Clear());
        }
        finally { await Cleanup(run); }
    }

    private DbContext Context() => new(new DbContextOptionsBuilder().UseNeo4j(fixture.Driver, fixture.Database).Options);
    private static Dictionary<string, object?> Keys(string run, string key) => new() { ["Run"] = run, ["Key"] = key };
    private static string NodeKey(INode node) => node["Key"].As<string>();
    private static string EdgeKey(IRelationship edge) => edge["Key"].As<string>();
    private static string Signature(GraphPath<string, string> path) => string.Join(">", path.Nodes) + "|" + string.Join(">", path.Relationships) + "|" + string.Join(",", path.StepDirections);
    private static string Signature(IPath path, GraphTraversalDirection direction)
    {
        var directions = path.Relationships.Select((r, i) => direction != GraphTraversalDirection.Incoming && r.StartNodeElementId == path.Nodes[i].ElementId && r.EndNodeElementId == path.Nodes[i + 1].ElementId
            ? GraphTraversalDirection.Outgoing : GraphTraversalDirection.Incoming);
        // A self-loop has both orientations; the documented convention uses the requested direction, or outgoing for Both.
        return string.Join(">", path.Nodes.Select(NodeKey)) + "|" + string.Join(">", path.Relationships.Select(EdgeKey)) + "|" + string.Join(",", directions);
    }
    private async Task<IReadOnlyList<IPath>> Native(string run, string key, GraphTraversalDirection direction, int min, int max)
    {
        var rel = $"[:{Edge}*{min}..{max}]";
        var arrows = direction == GraphTraversalDirection.Outgoing ? "-" + rel + "->" : direction == GraphTraversalDirection.Incoming ? "<-" + rel + "-" : "-" + rel + "-";
        await using var session = fixture.OpenSession();
        return await (await session.RunAsync($"CYPHER 5 MATCH p=(n:{Label} {{Run:$run,Key:$key}}){arrows}() RETURN p", new { run, key })).ToListAsync(r => r["p"].As<IPath>());
    }
    private async Task Seed(string run)
    {
        await Execute($"CREATE (a:{Label} {{Run:$run,Key:'a'}}),(b:{Label} {{Run:$run,Key:'b'}}),(c:{Label} {{Run:$run,Key:'c'}}),(d:{Label} {{Run:$run,Key:'d'}}),(i:{Label} {{Run:$run,Key:'isolated'}}) "
            + $"CREATE (a)-[:{Edge} {{Key:'ab1'}}]->(b),(a)-[:{Edge} {{Key:'ab2'}}]->(b),(b)-[:{Edge} {{Key:'bc'}}]->(c),(c)-[:{Edge} {{Key:'ca'}}]->(a),(a)-[:{Edge} {{Key:'aa'}}]->(a),(d)-[:{Edge} {{Key:'da'}}]->(a),(a)-[:GRAPHBRIDGE_PATH_OTHER {{Key:'other'}}]->(c)", new { run });
    }
    private async Task Execute(string text, object? parameters = null)
    {
        await using var session = fixture.OpenSession();
        await (parameters is null ? await session.RunAsync(text) : await session.RunAsync(text, parameters)).ConsumeAsync();
    }
    private async Task Cleanup(string run)
    {
        await Execute($"MATCH (a:{Label})-[r]->(b:{Label}) WHERE a.Run=$run AND b.Run=$run AND type(r) IN [$edge,'GRAPHBRIDGE_PATH_OTHER'] DELETE r", new { run, edge = Edge });
        await Execute($"MATCH (n:{Label} {{Run:$run}}) DELETE n", new { run });
    }
}
