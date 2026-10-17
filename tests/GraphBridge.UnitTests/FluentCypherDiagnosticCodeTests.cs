using GraphBridge.EntityFrameworkCore.FluentCypher;
using Microsoft.EntityFrameworkCore;
using static GraphBridge.UnitTests.FluentCypherLambdaTests;
using Graph = GraphBridge.EntityFrameworkCore.FluentCypher.Cypher;

namespace GraphBridge.UnitTests;

/// <summary>FC2-07b: every Fluent refusal carries a stable code in its message and Exception.Data, by family:
/// 1xx predicates, 2xx patterns and symbols, 3xx clauses and scope, 4xx projection and ordering.</summary>
public sealed class FluentCypherDiagnosticCodeTests
{
    [Fact]
    public void Pattern_clause_and_projection_refusals_are_coded()
    {
        using var db = Context();
        var member = Graph.Node<Member>(); var club = Graph.Node<Club>();
        var rows = Graph.CountRows();
        Assert.Equal("GBFC210", Code(() => db.Cypher().Match(club.Out<Membership>(member)).Return(() => member.Value.Id).ToQueryString()));
        Assert.Equal("GBFC211", Code(() => db.Cypher().Match(Graph.Node<FluentCypherLambdaDepthTests.Blob>().Pattern()).Return(() => rows.Value).ToQueryString()));
        Assert.Equal("GBFC213", Code(() => member.Property(m => m.Name.Length)));
        Assert.Equal("GBFC206", Code(() => db.Cypher().Match(member.Pattern()).With(rows).Match(member.Pattern()).Return(() => rows.Value).ToQueryString()));
        Assert.Equal("GBFC301", Code(() => db.Cypher().OptionalMatch(member.Pattern())));
        Assert.Equal("GBFC303", Code(() => db.Cypher().Match(member.Pattern(), club.Pattern()).With(member).Return(() => club.Value.Title).ToQueryString()));
        Assert.Equal("GBFC403", Code(() => db.Cypher().Match(member).Return(() => member.Value.Name.ToUpperInvariant()).ToQueryString()));
        Assert.Equal("GBFC404", Code(() => db.Cypher().Match(member).Return(() => member.Value).ToQueryString()));
        Assert.Equal("GBFC405", Code(() => db.Cypher().Match(member).Return(() => member.Value.Id).OrderBy(member.Property(m => m.Name)).ToQueryString()));
        var error = Assert.Throws<FluentCypherException>(() => db.Cypher().Match(member).Return(() => member.Value).ToQueryString());
        Assert.StartsWith("GraphBridge fluent Cypher [GBFC404]:", error.Message);
    }

    [Fact]
    public void Refusals_are_typed_and_still_caught_as_NotSupportedException()
    {
        using var db = Context();
        var member = Graph.Node<Member>();
        var caught = Assert.ThrowsAny<NotSupportedException>(() => db.Cypher().Match(member).Return(() => member.Value).ToQueryString());
        Assert.Equal("GBFC404", Coded(Assert.IsType<FluentCypherException>(caught)));
        Assert.Throws<ArgumentException>(() => new FluentCypherException("message", " "));
        Assert.Throws<ArgumentNullException>(() => new FluentCypherException("message", null!));
    }

    private static string Code(Action action) => Coded(Assert.Throws<FluentCypherException>(action));
    private static string Code(Func<object> action) => Coded(Assert.Throws<FluentCypherException>(() => action()));

    // The typed property, Exception.Data and the message carry the same code.
    private static string Coded(FluentCypherException error)
    {
        Assert.Equal(error.Code, error.Data["GraphBridge.FluentDiagnostic"]);
        Assert.Contains($"[{error.Code}]", error.Message);
        return error.Code;
    }
}
