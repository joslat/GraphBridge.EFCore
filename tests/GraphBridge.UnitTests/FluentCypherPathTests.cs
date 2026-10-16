using GraphBridge.EntityFrameworkCore.Paths;
using Microsoft.EntityFrameworkCore;
using Neo4j.Driver;
using Graph = GraphBridge.EntityFrameworkCore.FluentCypher.Cypher;
using Person = GraphBridge.UnitTests.FluentCypherCompilerTests.Person;
using Friendship = GraphBridge.UnitTests.FluentCypherCompilerTests.Friendship;

namespace GraphBridge.UnitTests;

public sealed class FluentCypherPathTests
{
    [Theory]
    [InlineData(GraphTraversalDirection.Outgoing, "-[r2:`FLUENT_FRIEND`*0..3]->")]
    [InlineData(GraphTraversalDirection.Incoming, "<-[r2:`FLUENT_FRIEND`*0..3]-")]
    [InlineData(GraphTraversalDirection.Both, "-[r2:`FLUENT_FRIEND`*0..3]-")]
    public void Bounded_paths_render_native_direction_and_read_untracked_ordered_values(GraphTraversalDirection direction, string expected)
    {
        using var db = FluentCypherCompilerTests.Context();
        var start = Graph.Node<Person>(); var end = Graph.Node<Person>(); var edge = Graph.Edge<Friendship>(); var path = Graph.Path();
        var pattern = direction switch { GraphTraversalDirection.Outgoing => start.Out(edge, end), GraphTraversalDirection.Incoming => start.In(edge, end), _ => start.Undirected(edge, end) };
        var compiled = db.Cypher().Match(path, pattern.Repeat(0, 3)).Where(start.Property(p => p.Id).Eq(1)).Return(() => path.Value).Limit(20).Compile();
        Assert.Contains("MATCH path3=(n0:`FluentPerson`)" + expected + "(n1:`FluentPerson`)", compiled.Command.Text);
        var a = Node("a"); var b = Node("b");
        var relation = direction == GraphTraversalDirection.Incoming ? Edge("ba", "b", "a") : Edge("ab", "a", "b");
        var result = compiled.Read(new Dictionary<string, object> { ["c0"] = Path([a, b], [relation]) });
        Assert.Equal(new[] { "a", "b" }, result.Nodes.Select(n => n.ElementId));
        Assert.Equal(direction == GraphTraversalDirection.Both ? GraphTraversalDirection.Outgoing : direction, Assert.Single(result.StepDirections));
        Assert.Throws<NotSupportedException>(() => ((IList<INode>)result.Nodes).Clear());
        Assert.Empty(db.ChangeTracker.Entries());
    }

    [Fact]
    public void Bounded_paths_reject_invalid_depth_scalar_edge_access_and_heterogeneous_repetition()
    {
        using var db = FluentCypherCompilerTests.Context();
        var start = Graph.Node<Person>(); var end = Graph.Node<Person>(); var edge = Graph.Edge<Friendship>(); var path = Graph.Path();
        var step = start.Out(edge, end);
        Assert.Throws<ArgumentOutOfRangeException>(() => step.Repeat(-1, 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => step.Repeat(1, 65));
        Assert.Throws<ArgumentOutOfRangeException>(() => step.Repeat(3, 2));
        Assert.Throws<NotSupportedException>(() => step.Repeat(1, 2).Out(Graph.Edge<Friendship>(), start));
        Assert.Throws<NotSupportedException>(() => db.Cypher().Match(step.Repeat(1, 2)).Return(() => end.Value.Id).ToQueryString());
        Assert.Throws<NotSupportedException>(() => db.Cypher().Match(path, step.Repeat(1, 2)).Return(() => edge.Value.SourceId).ToQueryString());
        var movie = Graph.Node<FluentCypherCompilerTests.Movie>();
        Assert.Throws<NotSupportedException>(() => db.Cypher().Match(path, start.Out(Graph.Edge<FluentCypherCompilerTests.Credit>(), movie).Repeat(1, 2)).Return(() => path.Value).ToQueryString());
    }

    [Fact]
    public void Path_reader_rejects_repeated_edges_wrong_depth_and_malformed_wire_shapes()
    {
        using var db = FluentCypherCompilerTests.Context();
        var start = Graph.Node<Person>(); var end = Graph.Node<Person>(); var path = Graph.Path();
        var compiled = db.Cypher().Match(path, start.Out(Graph.Edge<Friendship>(), end).Repeat(1, 2)).Return(() => path.Value).Compile();
        var a = Node("a"); var b = Node("b");
        Assert.Throws<InvalidOperationException>(() => Read("not a path"));
        Assert.Throws<InvalidOperationException>(() => Read(Path([a], [])));
        Assert.Throws<InvalidOperationException>(() => Read(Path([a, b, a], [Edge("same", "a", "b"), Edge("same", "b", "a")])));
        Assert.Throws<InvalidOperationException>(() => Read(Path([a, b], [Edge("wrong", "b", "a")])));
        Assert.Throws<InvalidOperationException>(() => Read(Path([a, Node("x", "OtherLabel")], [Edge("ax", "a", "x")])));
        GraphPath<INode, IRelationship> Read(object value) => compiled.Read(new Dictionary<string, object> { ["c0"] = value });
    }

    [Fact]
    public void Zero_hops_and_optional_missing_paths_have_explicit_shapes()
    {
        using var db = FluentCypherCompilerTests.Context();
        var start = Graph.Node<Person>(); var end = Graph.Node<Person>(); var path = Graph.Path();
        var compiled = db.Cypher().Match(start).OptionalMatch(path, start.Out(Graph.Edge<Friendship>(), end).Repeat(0, 0)).Return(() => path.Value).Compile();
        var result = compiled.Read(new Dictionary<string, object> { ["c0"] = Path([Node("a")], []) });
        Assert.Single(result.Nodes); Assert.Empty(result.Relationships); Assert.Empty(result.StepDirections);
        Assert.Null(compiled.Read(new Dictionary<string, object> { ["c0"] = null! }));
    }

    // The bidirectional friendship query discussed in dotnet/efcore#33795, written with the fluent surface.
    [Fact]
    public void Undirected_single_hop_path_compiles_to_native_undirected_cypher_and_reads_both_stored_directions()
    {
        using var db = FluentCypherCompilerTests.Context();
        var person = Graph.Node<Person>(); var friend = Graph.Node<Person>(); var path = Graph.Path();
        var compiled = db.Cypher()
            .Match(path, person.Undirected(Graph.Edge<Friendship>(), friend).Repeat(1, 1))
            .Where(() => person.Value.Name == "Alison")
            .Return(() => path.Value)
            .Limit(25)
            .Compile();
        Assert.Equal("MATCH path3=(n0:`FluentPerson`)-[r2:`FLUENT_FRIEND`*1..1]-(n1:`FluentPerson`)\nWHERE n0.`display_name` = $p0\nRETURN path3 AS c0\nLIMIT $p1",
            compiled.Command.Text.Replace("\r", ""));
        Assert.Equal("Alison", compiled.Command.Parameters["p0"]);
        var alison = Node("alison"); var bob = Node("bob");
        var stored = compiled.Read(new Dictionary<string, object> { ["c0"] = Path([alison, bob], [Edge("alison-bob", "alison", "bob")]) });
        Assert.Equal(GraphTraversalDirection.Outgoing, Assert.Single(stored.StepDirections));
        var against = compiled.Read(new Dictionary<string, object> { ["c0"] = Path([alison, bob], [Edge("bob-alison", "bob", "alison")]) });
        Assert.Equal(GraphTraversalDirection.Incoming, Assert.Single(against.StepDirections));
        Assert.Empty(db.ChangeTracker.Entries());
    }

    [Fact]
    public void Undirected_step_without_a_named_bounded_path_is_refused_with_GBFC202()
    {
        using var db = FluentCypherCompilerTests.Context();
        var person = Graph.Node<Person>(); var friend = Graph.Node<Person>();
        var error = Assert.Throws<NotSupportedException>(() => db.Cypher()
            .Match(person.Undirected(Graph.Edge<Friendship>(), friend))
            .Return(() => friend.Value.Name).ToQueryString());
        Assert.Contains("GBFC202", error.Message);
    }

    [Fact]
    public void Undirected_paths_return_typed_endpoint_values_but_not_repeated_edge_properties()
    {
        using var db = FluentCypherCompilerTests.Context();
        var person = Graph.Node<Person>(); var friend = Graph.Node<Person>(); var edge = Graph.Edge<Friendship>(); var path = Graph.Path();
        var query = db.Cypher()
            .Match(path, person.Undirected(edge, friend).Repeat(1, 1))
            .Where(() => person.Value.Name == "Alison");
        Assert.Equal("MATCH path3=(n0:`FluentPerson`)-[r2:`FLUENT_FRIEND`*1..1]-(n1:`FluentPerson`)\nWHERE n0.`display_name` = $p0\nRETURN n1.`display_name` AS c0",
            query.Return(() => friend.Value.Name).ToQueryString().Replace("\r", ""));
        // A repeated edge variable is a list in Cypher, so its scalar properties are refused even for one hop.
        Assert.Throws<NotSupportedException>(() => query.Return(() => edge.Value.SourceId).ToQueryString());
    }

    private static INode Node(string id, string label = "FluentPerson") => NativeQueryTests.Proxy.For<INode>((m, _) => m.Name switch
    { "get_ElementId" => id, "get_Labels" => new[] { label }, _ => throw new InvalidOperationException(m.Name) });
    private static IRelationship Edge(string id, string from, string to) => NativeQueryTests.Proxy.For<IRelationship>((m, _) => m.Name switch
    { "get_ElementId" => id, "get_StartNodeElementId" => from, "get_EndNodeElementId" => to, "get_Type" => "FLUENT_FRIEND", _ => throw new InvalidOperationException(m.Name) });
    private static IPath Path(INode[] nodes, IRelationship[] edges) => NativeQueryTests.Proxy.For<IPath>((m, _) => m.Name switch
    { "get_Nodes" => nodes, "get_Relationships" => edges, "get_Start" => nodes[0], "get_End" => nodes[^1], _ => throw new InvalidOperationException(m.Name) });
}
