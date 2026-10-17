using GraphBridge.EntityFrameworkCore.FluentCypher;
using Microsoft.EntityFrameworkCore;
using Graph = GraphBridge.EntityFrameworkCore.FluentCypher.Cypher;

namespace GraphBridge.UnitTests;

public sealed class FluentCypherSyntaxTests
{
    [Fact]
    public void Coactors_compile_with_distinct_typed_roles_and_immutable_pattern_branches()
    {
        using var context = new DbContext(new DbContextOptions<DbContext>());
        var actor = Graph.Node<Person>();
        var coactor = Graph.Node<Person>();
        var movie = Graph.Node<Movie>();
        var first = Graph.Edge<Credit>();
        var second = Graph.Edge<Credit>();
        var oneHop = actor.Out(first, movie);
        var twoHops = oneHop.In(second, coactor);
        var root = context.Cypher().Match(twoHops);
        var filtered = root.Where(actor.Property(p => p.Id).Eq(42)
            .And(coactor.Property(p => p.Id).Ne(actor.Property(p => p.Id))));
        var result = filtered.ReturnDistinct(() => new PersonRow(coactor.Value.Id, coactor.Value.Name))
            .OrderBy(coactor.Property(p => p.Name)).ThenBy(coactor.Property(p => p.Id)).Limit(100);

        Assert.NotSame(actor, coactor);
        Assert.Single(oneHop.Steps);
        Assert.Equal(2, twoHops.Steps.Count);
        Assert.True(twoHops.Steps[0].Outgoing);
        Assert.False(twoHops.Steps[1].Outgoing);
        Assert.Null(Assert.IsType<CypherMatchClause>(root.Clauses[0]).Predicate);
        Assert.NotNull(Assert.IsType<CypherMatchClause>(filtered.Clauses[0]).Predicate);
        Assert.True(result.Distinct);
        Assert.Equal(100, result.LimitCount);
        Assert.Equal(2, result.Orderings.Count);
    }

    [Fact]
    public void Optional_pattern_predicate_and_post_with_filter_remain_separate_clauses()
    {
        using var context = new DbContext(new DbContextOptions<DbContext>());
        var actor = Graph.Node<Person>(); var movie = Graph.Node<Movie>(); var credit = Graph.Edge<Credit>();
        var query = context.Cypher().Match(actor).OptionalMatch(actor.Out(credit, movie));
        var onPattern = query.Where(movie.Property(m => m.Title).Eq("First"));
        var onRows = query.With(actor, movie).Where(movie.Property(m => m.Title).Eq("First"));
        Assert.NotNull(Assert.IsType<CypherMatchClause>(onPattern.Clauses[1]).Predicate);
        Assert.Null(Assert.IsType<CypherMatchClause>(onRows.Clauses[1]).Predicate);
        Assert.NotNull(Assert.IsType<CypherWithClause>(onRows.Clauses[2]).Predicate);
    }

    [Fact]
    public void With_and_exists_can_express_recommendation_and_exclusion_shapes()
    {
        using var context = new DbContext(new DbContextOptions<DbContext>());
        var actor = Graph.Node<Person>(); var coactor = Graph.Node<Person>(); var movie = Graph.Node<Movie>();
        var credit = Graph.Edge<Credit>(); var other = Graph.Edge<Credit>(); var friendship = Graph.Edge<Friendship>();
        var count = Graph.CountDistinct(movie);
        var query = context.Cypher().Match(actor.Out(credit, movie).In(other, coactor))
            .Where(Graph.Exists(actor.Out(friendship, coactor)).Not())
            .With(coactor, count).Where(count.Expression.Gt(1L))
            .Return(() => new Recommendation(coactor.Value.Name, count.Value))
            .OrderByDescending(count.Expression).Limit(20);
        Assert.Equal(2, query.Query.Clauses.Count);
        Assert.IsType<CypherCountTerm>(count.Definition);
        Assert.IsType<CypherWithClause>(query.Query.Clauses[1]);
    }

    [Fact]
    public void Symbols_do_not_execute_getters_or_arbitrary_property_selector_code()
    {
        var actor = Graph.Node<Person>();
        Assert.Throws<InvalidOperationException>(() => actor.Value);
        Assert.Throws<FluentCypherException>(() => actor.Property(p => p.Name.ToUpperInvariant()));
        Assert.Throws<FluentCypherException>(() => actor.Property(p => (long)p.Id));
        Assert.Throws<FluentCypherException>(() => actor.Property(p => p.Name.Length));
        Assert.Throws<ArgumentNullException>(() => actor.Property<string>(null!));
    }

    [Fact]
    public void Native_null_tests_and_negation_are_explicit_expression_nodes()
    {
        var actor = Graph.Node<Person>();
        var value = actor.Property(p => p.Nickname);
        Assert.IsType<CypherNullTerm>(value.IsNull().Term);
        Assert.IsType<CypherNotTerm>(value.Eq("known").Not().Term);
        Assert.IsType<CypherComparisonTerm>(value.Eq((string?)null).Term);
    }

    [Fact]
    public void Invalid_clause_order_and_mutable_input_arrays_are_guarded()
    {
        using var context = new DbContext(new DbContextOptions<DbContext>());
        var actor = Graph.Node<Person>(); var other = Graph.Node<Person>();
        Assert.Throws<InvalidOperationException>(() => context.Cypher().Where(actor.Property(p => p.Id).Eq(1)));
        Assert.Throws<FluentCypherException>(() => context.Cypher().OptionalMatch(actor.Pattern()));
        Assert.Throws<ArgumentException>(() => context.Cypher().Match([]));
        var patterns = new[] { actor.Pattern() };
        var query = context.Cypher().Match(patterns);
        patterns[0] = other.Pattern();
        Assert.Same(actor, Assert.IsType<CypherMatchClause>(query.Clauses[0]).Patterns[0].Start);
        Assert.Throws<ArgumentException>(() => query.With(actor, actor));
        var result = query.Return(() => actor.Value.Name);
        Assert.Throws<InvalidOperationException>(() => result.ThenBy(actor.Property(p => p.Id)));
        Assert.Throws<InvalidOperationException>(() => result.Limit(1).OrderBy(actor.Property(p => p.Id)));
        Assert.Throws<InvalidOperationException>(() => result.Limit(1).Skip(1));
        Assert.Throws<InvalidOperationException>(() => result.Limit(1).Limit(2));
        Assert.Throws<ArgumentOutOfRangeException>(() => result.Limit(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => result.Limit(10_001));
    }

    private sealed class Person { public int Id { get; set; } public string Name { get; set; } = ""; public string? Nickname { get; set; } }
    private sealed class Movie { public int Id { get; set; } public string Title { get; set; } = ""; }
    private sealed class Credit { public int Id { get; set; } }
    private sealed class Friendship { public int Id { get; set; } }
    private sealed record PersonRow(int Id, string Name);
    private sealed record Recommendation(string Name, long Count);
}
