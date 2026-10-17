using GraphBridge.EntityFrameworkCore.FluentCypher;
using Microsoft.EntityFrameworkCore;
using Graph = GraphBridge.EntityFrameworkCore.FluentCypher.Cypher;
using Person = GraphBridge.UnitTests.FluentCypherCompilerTests.Person;
using Movie = GraphBridge.UnitTests.FluentCypherCompilerTests.Movie;
using Credit = GraphBridge.UnitTests.FluentCypherCompilerTests.Credit;
using Friendship = GraphBridge.UnitTests.FluentCypherCompilerTests.Friendship;

namespace GraphBridge.UnitTests;

public sealed class FluentCypherReviewTests
{
    [Fact]
    public void Optional_edge_FKs_follow_the_relationship_and_survive_endpoint_scope_removal()
    {
        using var db = FluentCypherCompilerTests.Context(model =>
        {
            model.Entity<Person>().Property(p => p.Id).HasGraphPropertyName("person_key");
            model.Entity<Movie>().Property(p => p.Id).HasGraphPropertyName("movie_key");
        });
        var person = Graph.Node<Person>(); var movie = Graph.Node<Movie>(); var edge = Graph.Edge<Credit>();
        var query = db.Cypher().Match(person).OptionalMatch(person.Out(edge, movie)).With(edge)
            .Return(() => new { Source = (int?)edge.Value.PersonId, Target = (int?)edge.Value.MovieId });
        var compiled = query.Compile();
        Assert.Contains("RETURN startNode(r2).`person_key` AS c0, endNode(r2).`movie_key` AS c1", compiled.Command.Text);
        var absent = compiled.Read(new Dictionary<string, object> { ["c0"] = null!, ["c1"] = null! });
        Assert.Null(absent.Source); Assert.Null(absent.Target);
    }

    [Fact]
    public void EXISTS_predicate_can_be_reused_without_exporting_local_symbols()
    {
        using var db = FluentCypherCompilerTests.Context();
        var person = Graph.Node<Person>(); var friend = Graph.Node<Person>(); var edge = Graph.Edge<Friendship>();
        var exists = Graph.Exists(person.Out(edge, friend), friend.Property(p => p.Name).Eq("Ada"));
        var query = db.Cypher().Match(person).Where(exists.Or(exists.Not()));
        var command = query.Return(() => person.Value.Name).ToCommand();
        Assert.Equal(2, command.Text.Split("EXISTS {").Length - 1);
        Assert.Equal(2, command.Parameters.Count);
        Assert.Throws<FluentCypherException>(() => query.Return(() => friend.Value.Name).ToQueryString());
    }

    [Fact]
    public void Nested_and_sibling_EXISTS_reuse_is_lexically_scoped()
    {
        using var db = FluentCypherCompilerTests.Context();
        var person = Graph.Node<Person>(); var friend = Graph.Node<Person>(); var second = Graph.Node<Person>();
        var inner = Graph.Exists(friend.Out(Graph.Edge<Friendship>(), second));
        var outer = Graph.Exists(person.Out(Graph.Edge<Friendship>(), friend), inner.Or(inner.Not()));
        var query = db.Cypher().Match(person).Where(outer.And(outer));
        Assert.Equal(6, query.Return(() => person.Value.Id).ToQueryString().Split("EXISTS {").Length - 1);
        Assert.Throws<FluentCypherException>(() => query.With(friend).Return(() => friend.Value.Name).ToQueryString());
    }

    [Fact]
    public void Unsupported_capture_receiver_rejects_without_reflection_error_or_user_code_execution()
    {
        using var db = FluentCypherCompilerTests.Context(); var person = Graph.Node<Person>();
        var calls = new CaptureSource();
        Assert.Throws<FluentCypherException>(() => db.Cypher().Match(person)
            .Return(() => calls.GetHolder().Alias.Value.Name).ToQueryString());
        Assert.Equal(0, calls.Calls);
        CaptureHolder? missing = null;
        Assert.Throws<FluentCypherException>(() => db.Cypher().Match(person)
            .Return(() => missing!.Alias.Value.Name).ToQueryString());
    }

    private sealed class CaptureSource
    {
        public int Calls { get; private set; }
        public CaptureHolder GetHolder() { Calls++; throw new InvalidOperationException("Must not run"); }
    }
    private sealed class CaptureHolder { public readonly CypherNode<Person> Alias = Graph.Node<Person>(); }
}
