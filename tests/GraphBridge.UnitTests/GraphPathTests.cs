using GraphBridge.EntityFrameworkCore.Paths;
using Microsoft.EntityFrameworkCore;
using Neo4j.Driver;

namespace GraphBridge.UnitTests;

public sealed class GraphPathTests
{
    private static readonly GraphPathPattern Pattern = new("PathNode", ["CONNECTS"]);
    private static readonly IReadOnlyDictionary<string, object?> Keys = new Dictionary<string, object?> { ["Id"] = "anchor" };

    [Theory]
    [InlineData(GraphTraversalDirection.Outgoing, "(start)-[:`CONNECTS`*1..4]->()")]
    [InlineData(GraphTraversalDirection.Incoming, "(start)<-[:`CONNECTS`*1..4]-()")]
    [InlineData(GraphTraversalDirection.Both, "(start)-[:`CONNECTS`*1..4]-()")]
    public void Renderer_preserves_direction_depth_parameterization_and_same_statement_anchor_guard(GraphTraversalDirection direction, string path)
    {
        var keys = new Dictionary<string, object?> { ["Id"] = "quote' MATCH(n) DELETE n", ["Tenant"] = 7 };
        var request = new GraphPathRequest(Pattern, keys, direction, 4, 9, 1);
        keys["Id"] = "changed";
        Assert.Contains(path, request.Cypher); Assert.Contains("WITH start LIMIT 2", request.Cypher);
        Assert.Contains("size(candidates) = 1", request.Cypher); Assert.Contains("WITH path LIMIT $probe", request.Cypher);
        Assert.DoesNotContain("quote'", request.Cypher); Assert.Equal("quote' MATCH(n) DELETE n", request.Parameters["key0"]);
        Assert.Equal(7L, request.Parameters["key1"]); Assert.Equal(10L, request.Parameters["probe"]);
    }

    [Fact]
    public void Native_metadata_is_validated_snapshotted_and_canonicalized()
    {
        var types = new List<string> { "Z", "A", "Z" }; var pattern = new GraphPathPattern("PathNode", types); types.Clear();
        Assert.Equal(new[] { "A", "Z" }, pattern.RelationshipTypes);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)pattern.RelationshipTypes).Add("X"));
        Assert.Throws<ArgumentException>(() => new GraphPathPattern("bad`label", ["A"]));
        Assert.Throws<ArgumentException>(() => new GraphPathPattern("Valid", []));
        Assert.Throws<ArgumentException>(() => new GraphPathPattern("Valid", Enumerable.Repeat("A", 17)));
        Assert.Throws<ArgumentException>(() => new GraphPathPattern("Valid", ["A|B"]));
        Assert.Throws<ArgumentOutOfRangeException>(() => Request(maxDepth: 65));
        Assert.Throws<ArgumentOutOfRangeException>(() => Request(direction: (GraphTraversalDirection)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => Request(limit: 10001));
        Assert.Throws<ArgumentOutOfRangeException>(() => Request(minDepth: 5));
        foreach (var value in new object?[] { null, Guid.NewGuid(), new[] { 1, 2 }, double.NaN })
            Assert.Throws<ArgumentException>(() => new GraphPathRequest(Pattern, new Dictionary<string, object?> { ["Id"] = value }, GraphTraversalDirection.Outgoing, 4, 10, 1));
    }

    [Fact]
    public void Zero_length_path_materializes_one_node_and_no_edges_and_missing_anchor_stays_empty()
    {
        var node = Node("a"); var request = Request(maxDepth: 0, minDepth: 0);
        var result = Read(Row(1, Path([node], [])), request);
        Assert.True(result.StartFound); Assert.False(result.HasMorePaths);
        var path = Assert.Single(result.Paths); Assert.Equal("a", Assert.Single(path.Nodes)); Assert.Empty(path.Relationships); Assert.Empty(path.StepDirections);
        var empty = Read(Row(0), request); Assert.Empty(empty.Paths); Assert.False(empty.StartFound);
        var isolated = Read(Row(1), Request()); Assert.True(isolated.StartFound); Assert.Empty(isolated.Paths);
    }

    [Fact]
    public void Cycles_keep_repeated_nodes_and_parallel_physical_edges_are_distinct_paths()
    {
        var a = Node("a"); var b = Node("b");
        var cycle = Path([a, b, a], [Edge("ab", "a", "b"), Edge("ba", "b", "a")]);
        var parallel = Path([a, b], [Edge("ab2", "a", "b")]);
        var result = Read(Row(1, cycle, parallel), Request(limit: 1));
        Assert.True(result.HasMorePaths); Assert.Equal(new[] { "a", "b", "a" }, Assert.Single(result.Paths).Nodes);
        Assert.Equal(new[] { "ab", "ba" }, result.Paths[0].Relationships);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)result.Paths[0].Nodes).Clear());
    }

    [Fact]
    public void Incoming_and_mixed_paths_report_each_stored_edge_orientation()
    {
        var a = Node("a"); var b = Node("b"); var c = Node("c");
        var incoming = Path([a, b], [Edge("ba", "b", "a")]);
        Assert.Equal(GraphTraversalDirection.Incoming, Assert.Single(Read(Row(1, incoming), Request(direction: GraphTraversalDirection.Incoming)).Paths[0].StepDirections));
        var mixed = Path([a, b, c], [Edge("ba", "b", "a"), Edge("bc", "b", "c")]);
        Assert.Equal(new[] { GraphTraversalDirection.Incoming, GraphTraversalDirection.Outgoing }, Read(Row(1, mixed), Request(direction: GraphTraversalDirection.Both)).Paths[0].StepDirections);
    }

    [Fact]
    public void Malformed_depth_direction_type_repeated_relationship_and_anchor_fail_before_projection()
    {
        var a = Node("a"); var b = Node("b");
        var valid = Path([a, b], [Edge("ab", "a", "b")]);
        var badPaths = new[] {
            Path([a], []), Path([a, b], [Edge("ba", "b", "a")]),
            Path([a, b], [Edge("ab", "a", "b", "OTHER")]),
            Path([a, b, a], [Edge("repeat", "a", "b"), Edge("repeat", "b", "a")]),
            Path([a], [Edge("ab", "a", "b")]) };
        foreach (var path in badPaths)
        {
            var projected = false;
            Assert.Throws<InvalidOperationException>(() => GraphPathReader.Read(Row(1, valid, path), Request(limit: 1),
                n => { projected = true; return n; }, r => r, default));
            Assert.False(projected);
        }
        Assert.Throws<InvalidOperationException>(() => Read(Row(2), Request()));
        Assert.Throws<InvalidOperationException>(() => Read(Row(0, valid), Request()));
        Assert.Throws<InvalidOperationException>(() => Read(Row(1, valid, valid, valid), Request(limit: 1)));
    }

    [Fact]
    public async Task Public_input_and_precancellation_fail_before_driver_access()
    {
        var called = false;
        var driver = NativeQueryTests.Proxy.For<IDriver>((m, _) => { called = true; throw new InvalidOperationException(m.Name); });
        await using var db = new DbContext(new DbContextOptionsBuilder().UseNeo4j(driver).Options);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => db.Database.QueryPathsAsync(Pattern, Keys, GraphTraversalDirection.Outgoing, 65, 1, n => n, r => r));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => db.Database.QueryPathsAsync(Pattern, Keys, GraphTraversalDirection.Outgoing, 1, 1, n => n, r => r, cancellationToken: new CancellationToken(true)));
        Assert.False(called);
    }

    [Fact]
    public void Projection_cancellation_stops_before_the_next_callback_and_preserves_primary_errors()
    {
        var a = Node("a"); var b = Node("b"); var row = Row(1, Path([a, b], [Edge("ab", "a", "b")]));
        using var cancel = new CancellationTokenSource(); var nodes = 0; var edges = 0;
        Assert.ThrowsAny<OperationCanceledException>(() => GraphPathReader.Read(row, Request(),
            n => { nodes++; cancel.Cancel(); return n; }, r => { edges++; return r; }, cancel.Token));
        Assert.Equal(1, nodes); Assert.Equal(0, edges);
        var failure = new FormatException("node projector");
        Assert.Same(failure, Assert.Throws<FormatException>(() => GraphPathReader.Read<INode, IRelationship>(row, Request(), _ => throw failure, r => r, default)));
    }

    private static GraphPathRequest Request(GraphTraversalDirection direction = GraphTraversalDirection.Outgoing, int maxDepth = 4, int limit = 10, int minDepth = 1)
        => new(Pattern, Keys, direction, maxDepth, limit, minDepth);
    private static GraphPathResults<string, string> Read(IRecord row, GraphPathRequest request) => GraphPathReader.Read(row, request, n => n.ElementId, r => r.ElementId, default);
    private static IRecord Row(long count, params IPath[] paths) => NativeQueryTests.Proxy.For<IRecord>((m, a) =>
        m.Name == "get_Item" ? (string)a![0]! == "starts" ? count : paths : throw new InvalidOperationException(m.Name));
    private static INode Node(string id) => NativeQueryTests.Proxy.For<INode>((m, _) => m.Name switch {
        "get_ElementId" => id, "get_Labels" => new[] { "PathNode" }, _ => throw new InvalidOperationException(m.Name) });
    private static IRelationship Edge(string id, string from, string to, string type = "CONNECTS") => NativeQueryTests.Proxy.For<IRelationship>((m, _) => m.Name switch {
        "get_ElementId" => id, "get_StartNodeElementId" => from, "get_EndNodeElementId" => to, "get_Type" => type, _ => throw new InvalidOperationException(m.Name) });
    private static IPath Path(INode[] nodes, IRelationship[] edges) => NativeQueryTests.Proxy.For<IPath>((m, _) => m.Name switch {
        "get_Nodes" => nodes, "get_Relationships" => edges, "get_Start" => nodes[0], "get_End" => nodes[^1], _ => throw new InvalidOperationException(m.Name) });
}
