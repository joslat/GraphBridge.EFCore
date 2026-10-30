using GraphBridge.EntityFrameworkCore.FluentCypher;
using Microsoft.EntityFrameworkCore;
using static GraphBridge.UnitTests.FluentCypherCompilerTests;
using Graph = GraphBridge.EntityFrameworkCore.FluentCypher.Cypher;

namespace GraphBridge.UnitTests;

// ADR 0057 (GR-09): typed vector and full-text search as the first clause of a Fluent query.
public sealed class FluentCypherSearchTests
{
    private static readonly float[] Vector = [0.9f, 0.1f, 0f];

    [Fact]
    public void A_vector_search_starts_the_query_binds_its_node_and_score_and_parameterizes_its_input()
    {
        using var db = Context();
        var movie = Graph.Node<Movie>(); var actor = Graph.Node<Person>(); var score = Graph.Score();
        var command = db.Cypher().SearchVector(movie, "movie_embedding", Vector, 5, score)
            .Match(actor.Out<Credit>(movie))
            .Return(() => new { movie.Value.Title, actor.Value.Name, Score = score.Value })
            .OrderByDescending(() => score.Value).Limit(10).ToCommand();
        Assert.Equal("CYPHER 25 MATCH (n0:`FluentMovie`) SEARCH n0 IN (VECTOR INDEX `movie_embedding` FOR $p0 LIMIT $p1) SCORE AS v1\n"
            + "MATCH (n2:`FluentPerson`)-[r3:`FLUENT_ACTED`]->(n0:`FluentMovie`)\n"
            + "RETURN n0.`Title` AS c0, n2.`display_name` AS c1, v1 AS c2\nORDER BY c2 DESC\nLIMIT $p2", command.Text.ReplaceLineEndings("\n"));
        Assert.Equal(new double[] { 0.8999999761581421, 0.10000000149011612, 0 }, (double[])command.Parameters["p0"]!);
        Assert.Equal(5L, command.Parameters["p1"]);
    }

    [Fact]
    public void A_full_text_search_without_a_score_and_after_tags()
    {
        using var db = Context();
        var movie = Graph.Node<Movie>();
        var text = db.Cypher().SearchFullText(movie, "movie_titles", "matrix", 3)
            .Return(() => movie.Value.Title).TagWith("titles").Limit(3).ToQueryString();
        Assert.Equal("// titles\n\nCYPHER 25 MATCH (n0:`FluentMovie`) SEARCH n0 IN (FULLTEXT INDEX `movie_titles` FOR $p0 LIMIT $p1)\n"
            + "RETURN n0.`Title` AS c0\nLIMIT $p2", text.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void A_search_is_refused_after_another_clause_with_another_value_as_its_score_or_followed_by_WHERE()
    {
        using var db = Context();
        var movie = Graph.Node<Movie>(); var actor = Graph.Node<Person>();
        Assert.Equal("GBFC305", Assert.Throws<FluentCypherException>(() => db.Cypher().Match(actor.Pattern()).SearchVector(movie, "movie_embedding", Vector, 5)).Code);
        // A double value that isn't a fresh score, such as an aggregate of one.
        Assert.Equal("GBFC306", Assert.Throws<FluentCypherException>(() => db.Cypher().SearchVector(movie, "movie_embedding", Vector, 5, Graph.Max(Graph.Score().Expression))).Code);
        Assert.Equal("GBFC307", Assert.Throws<FluentCypherException>(() => db.Cypher().SearchVector(movie, "movie_embedding", Vector, 5).Where(() => movie.Value.Year > 2000)).Code);
    }

    [Theory]
    [InlineData("bad index", 5)]
    [InlineData("movie_embedding", 0)]
    [InlineData("movie_embedding", 10_001)]
    public void Invalid_index_names_and_limits_are_refused(string index, int limit)
    {
        using var db = Context();
        Assert.ThrowsAny<ArgumentException>(() => db.Cypher().SearchVector(Graph.Node<Movie>(), index, Vector, limit));
        Assert.ThrowsAny<ArgumentException>(() => db.Cypher().SearchFullText(Graph.Node<Movie>(), index, "matrix", limit));
    }

    [Fact]
    public void An_empty_or_non_finite_vector_or_an_empty_query_is_refused()
    {
        using var db = Context();
        Assert.Throws<ArgumentException>(() => db.Cypher().SearchVector(Graph.Node<Movie>(), "movie_embedding", Array.Empty<float>(), 5));
        Assert.Throws<ArgumentException>(() => db.Cypher().SearchVector(Graph.Node<Movie>(), "movie_embedding", [float.NaN], 5));
        Assert.ThrowsAny<ArgumentException>(() => db.Cypher().SearchFullText(Graph.Node<Movie>(), "movie_titles", " ", 5));
    }
}
