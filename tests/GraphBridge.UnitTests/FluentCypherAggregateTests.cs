using GraphBridge.EntityFrameworkCore.FluentCypher;
using Microsoft.EntityFrameworkCore;
using static GraphBridge.UnitTests.FluentCypherLambdaTests;
using Graph = GraphBridge.EntityFrameworkCore.FluentCypher.Cypher;

namespace GraphBridge.UnitTests;

/// <summary>FC2-04: min, max, sum, avg and bounded collect as WITH values with native semantics.</summary>
public sealed class FluentCypherAggregateTests
{
    [Fact]
    public void Aggregates_render_as_WITH_values_and_compose_with_lambdas()
    {
        using var db = Context();
        var member = Graph.Node<Member>(); var club = Graph.Node<Club>();
        var earliest = Graph.Min(club.Property(c => c.Founded));
        var latest = Graph.Max(club.Property(c => c.Title));
        var total = Graph.Sum(club.Property(c => c.Founded));
        var mean = Graph.Avg(club.Property(c => c.Founded));
        var titles = Graph.CollectDistinct(club.Property(c => c.Title), 5);
        var command = db.Cypher().Match(member.Out<Membership>(club)).With(member, earliest, latest, total, mean, titles)
            .Where(() => total.Value != 0 && earliest.Value >= 1990)
            .Return(() => new { member.Value.Name, First = earliest.Value, Last = latest.Value, Total = total.Value, Mean = mean.Value, Titles = titles.Value })
            .OrderBy(() => earliest.Value).Limit(10).ToCommand();
        Assert.Equal("MATCH (n0:`LambdaMember`)-[r2:`LAMBDA_MEMBER_OF`]->(n1:`LambdaClub`)\n"
            + "WITH n0, min(n1.`Founded`) AS v3, max(n1.`Title`) AS v4, sum(n1.`Founded`) AS v5, avg(n1.`Founded`) AS v6, collect(DISTINCT n1.`Title`)[..$p0] AS v7\n"
            + "WHERE v5 <> $p1 AND v3 >= $p2\n"
            + "RETURN n0.`Name` AS c0, v3 AS c1, v4 AS c2, v5 AS c3, v6 AS c4, v7 AS c5\nORDER BY c1 ASC\nLIMIT $p3", command.Text.Replace("\r", ""));
        Assert.Equal(5L, command.Parameters["p0"]);
    }

    [Fact]
    public void Aggregates_returned_without_WITH_use_native_implicit_grouping()
    {
        using var db = Context();
        var member = Graph.Node<Member>(); var club = Graph.Node<Club>();
        var clubs = Graph.CountDistinct(club);
        var first = Graph.Min(club.Property(c => c.Founded));
        var titles = Graph.Collect(club.Property(c => c.Title), 3);
        var command = db.Cypher().Match(member.Out<Membership>(club))
            .Return(() => new { member.Value.Name, Clubs = clubs.Value, First = first.Value, Titles = titles.Value })
            .OrderBy(() => clubs.Value).Limit(10).ToCommand();
        Assert.Equal("MATCH (n0:`LambdaMember`)-[r2:`LAMBDA_MEMBER_OF`]->(n1:`LambdaClub`)\n"
            + "RETURN n0.`Name` AS c0, count(DISTINCT n1) AS c1, min(n1.`Founded`) AS c2, collect(n1.`Title`)[..$p0] AS c3\n"
            + "ORDER BY c1 ASC\nLIMIT $p1", command.Text.Replace("\r", ""));
        // Without WITH, an aggregate does not exist yet where WHERE runs; the message lists what is in scope.
        var error = Assert.Throws<NotSupportedException>(() => db.Cypher().Match(member.Out<Membership>(club))
            .Where(() => clubs.Value > 1).Return(() => member.Value.Name).ToQueryString());
        Assert.Equal("GBFC303", error.Data["GraphBridge.FluentDiagnostic"]);
        Assert.Contains("In scope here: n0 (Member), n1 (Club), r2 (Membership).", error.Message);
    }

    [Fact]
    public void Double_sums_read_the_native_integer_zero_of_an_empty_group()
    {
        using var db = Context();
        var member = Graph.Node<Member>();
        var total = Graph.Sum(member.Property(m => m.Score));
        var compiled = new CypherCompiler(db.Model).Compile(db.Cypher().Match(member).Return(() => total.Value));
        // Native sum starts at integer 0 and becomes a float only when it adds one.
        Assert.Equal(0.0, compiled.Read(new Dictionary<string, object> { ["c0"] = 0L }));
        Assert.Equal(1.5, compiled.Read(new Dictionary<string, object> { ["c0"] = 1.5 }));
    }

    [Fact]
    public void Min_and_max_values_are_compared_with_stored_values()
    {
        using var db = Context();
        var member = Graph.Node<Member>();
        var latest = Graph.Max(member.Property(m => m.Joined));
        var command = db.Cypher().Match(member).With(latest).Where(latest.Expression.Gt(DateTimeOffset.UnixEpoch))
            .Return(() => latest.Value).ToCommand();
        Assert.IsType<Neo4j.Driver.ZonedDateTime>(command.Parameters["p0"]);
    }

    [Fact]
    public void Aggregate_values_follow_the_agree_or_reject_rule_and_bounds()
    {
        using var db = Context();
        var member = Graph.Node<Member>(); var club = Graph.Node<Club>();
        var earliest = Graph.Min(club.Property(c => c.Founded));
        var grouped = db.Cypher().Match(member.Out<Membership>(club)).With(member, earliest);
        // min can be null (all-null group), so C# != would keep rows native <> drops.
        Assert.Equal("GBFC110", Assert.Throws<NotSupportedException>(() => grouped.Where(() => earliest.Value != 1990)
            .Return(() => member.Value.Id).ToQueryString()).Data["GraphBridge.FluentDiagnostic"]);
        var codes = Graph.Min(member.Property(m => m.Code));
        Assert.Equal("GBFC406", Assert.Throws<NotSupportedException>(() => db.Cypher().Match(member).With(codes)
            .Return(() => codes.Value).ToQueryString()).Data["GraphBridge.FluentDiagnostic"]);
        Assert.Throws<ArgumentOutOfRangeException>(() => Graph.Collect(club.Property(c => c.Title), 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Graph.Collect(club.Property(c => c.Title), 10_001));
        // Converted properties can be collected: elements are read back through the converter.
        var tokens = Graph.Collect(member.Property(m => m.Token), 3);
        Assert.Contains("collect(n0.`Token`)[..$p0] AS v1", db.Cypher().Match(member).With(tokens).Return(() => tokens.Value).ToQueryString());
    }
}
