using GraphBridge.EntityFrameworkCore.FluentCypher;
using Microsoft.EntityFrameworkCore;
using Graph = GraphBridge.EntityFrameworkCore.FluentCypher.Cypher;

namespace GraphBridge.UnitTests;

/// <summary>ADR 0047: C# lambdas are accepted only where the native WHERE result equals the C# meaning.</summary>
public sealed class FluentCypherLambdaTests
{
    [Fact]
    public void Lambda_and_explicit_forms_compile_to_identical_commands()
    {
        using var db = Context();
        var member = Graph.Node<Member>(); var club = Graph.Node<Club>(); var other = Graph.Node<Member>();
        var name = "Ada";
        var lambda = db.Cypher().Match(member.Out<Membership>(club).In<Membership>(other))
            .Where(() => member.Value.Name == name && other.Value.Id != member.Value.Id && club.Value.Founded >= 1990)
            .ReturnDistinct(() => other.Value.Name).OrderBy(() => other.Value.Name).Limit(10).ToCommand();
        var explicitForm = db.Cypher().Match(member.Out(Graph.Edge<Membership>(), club).In(Graph.Edge<Membership>(), other))
            .Where(member.Property(m => m.Name).Eq(name).And(other.Property(m => m.Id).Ne(member.Property(m => m.Id)))
                .And(club.Property(c => c.Founded).Ge(1990)))
            .ReturnDistinct(() => other.Value.Name).OrderBy(other.Property(m => m.Name)).Limit(10).ToCommand();
        Assert.Equal(explicitForm.Text, lambda.Text);
        Assert.Equal(explicitForm.Parameters, lambda.Parameters);
        Assert.Equal("MATCH (n0:`LambdaMember`)-[r2:`LAMBDA_MEMBER_OF`]->(n1:`LambdaClub`)<-[r4:`LAMBDA_MEMBER_OF`]-(n3:`LambdaMember`)\n"
            + "WHERE n0.`Name` = $p0 AND n3.`Id` <> n0.`Id` AND n1.`Founded` >= $p1\n"
            + "RETURN DISTINCT n3.`Name` AS c0\nORDER BY c0 ASC\nLIMIT $p2", lambda.Text.Replace("\r", ""));
    }

    [Fact]
    public void Accepted_shapes_render_native_predicates_with_parameters()
    {
        using var db = Context();
        var member = Graph.Node<Member>();
        string? missing = null;
        var token = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");
        Assert.Equal("n0.`Nickname` IS NULL", Where(db, member, () => member.Value.Nickname == null).Where);
        Assert.Equal("n0.`Nickname` IS NOT NULL", Where(db, member, () => member.Value.Nickname != null).Where);
        Assert.Equal("n0.`Nickname` IS NULL", Where(db, member, () => member.Value.Nickname == missing).Where);
        Assert.Equal("n0.`Born` > $p0", Where(db, member, () => member.Value.Born > 1960).Where);
        // A value on the left is mirrored so the symbol is always the left operand.
        var mirrored = Where(db, member, () => 1960 < member.Value.Born);
        Assert.Equal("n0.`Born` > $p0", mirrored.Where);
        Assert.Equal(1960L, mirrored.Parameters["p0"]);
        var flag = Where(db, member, () => member.Value.Active || member.Value.Name == "Ada");
        Assert.Equal("n0.`Active` = $p0 OR n0.`Name` = $p1", flag.Where);
        Assert.True(Assert.IsType<bool>(flag.Parameters["p0"]));
        // Model values reach EF converters: enum to Int64, Guid to its D string, the custom string converter.
        Assert.Equal(2L, Where(db, member, () => member.Value.Level == Level.Gold).Parameters["p0"]);
        Assert.Equal(token.ToString("D"), Where(db, member, () => member.Value.Token == token).Parameters["p0"]);
        Assert.Equal("code:x", Where(db, member, () => member.Value.Code == "x").Parameters["p0"]);
        Assert.Equal("n0.`Name` = n0.`Nickname`", Where(db, member, () => member.Value.Name == member.Value.Nickname).Where);
    }

    [Fact]
    public void WITH_values_compare_and_order_like_their_expressions()
    {
        using var db = Context();
        var member = Graph.Node<Member>(); var club = Graph.Node<Club>();
        var count = Graph.CountDistinct(club);
        var lambda = db.Cypher().Match(member.Out<Membership>(club)).With(member, count).Where(() => count.Value >= 2L)
            .Return(() => new { member.Value.Name, Clubs = count.Value }).OrderByDescending(() => count.Value)
            .ThenBy(() => member.Value.Name).Limit(5).ToCommand();
        var explicitForm = db.Cypher().Match(member.Out(Graph.Edge<Membership>(), club)).With(member, count).Where(count.Expression.Ge(2L))
            .Return(() => new { member.Value.Name, Clubs = count.Value }).OrderByDescending(count.Expression)
            .ThenBy(member.Property(m => m.Name)).Limit(5).ToCommand();
        Assert.Equal(explicitForm.Text, lambda.Text);
        Assert.Equal(explicitForm.Parameters, lambda.Parameters);
        Assert.Contains("ORDER BY c1 DESC, c0 ASC", lambda.Text);
    }

    [Fact]
    public void Inline_optional_predicate_equals_the_chained_form_and_keeps_clause_ownership()
    {
        using var db = Context();
        var member = Graph.Node<Member>(); var club = Graph.Node<Club>();
        string Text(Func<CypherQuery, CypherQuery> optional)
            => optional(db.Cypher().Match(member)).Return(() => new { member.Value.Name, club.Value.Title }).ToQueryString();
        var chained = Text(q => q.OptionalMatch(member.Out<Membership>(club)).Where(club.Property(c => c.Founded).Gt(2000)));
        Assert.Equal(chained, Text(q => q.OptionalMatch(member.Out<Membership>(club), club.Property(c => c.Founded).Gt(2000))));
        Assert.Equal(chained, Text(q => q.OptionalMatch(member.Out<Membership>(club), where: () => club.Value.Founded > 2000)));
        Assert.Contains("OPTIONAL MATCH (n0:`LambdaMember`)-[r2:`LAMBDA_MEMBER_OF`]->(n1:`LambdaClub`)\nWHERE n1.`Founded` > $p0\nRETURN", chained.Replace("\r", ""));
    }

    [Fact]
    public void Inequality_requires_operands_that_can_never_be_null()
    {
        using var db = Context();
        var member = Graph.Node<Member>(); var other = Graph.Node<Member>(); var club = Graph.Node<Club>();
        // C# keeps members without a nickname for != "Bob"; native Cypher drops them.
        Assert.Equal("GBFC110", CompileCode(() => db.Cypher().Match(member).Where(() => member.Value.Nickname != "Bob")
            .Return(() => member.Value.Id).ToQueryString()));
        // C# treats null == null as true; native Cypher does not.
        Assert.Equal("GBFC110", CompileCode(() => db.Cypher().Match(member.Pattern(), other.Pattern()).Where(() => member.Value.Nickname == other.Value.Nickname)
            .Return(() => member.Value.Id).ToQueryString()));
        // Required properties on MATCH-bound symbols agree.
        Assert.Contains("n0.`Name` <> $p0", db.Cypher().Match(member).Where(() => member.Value.Name != "Bob")
            .Return(() => member.Value.Id).ToQueryString());
        // Inside its own OPTIONAL MATCH WHERE the new symbol is pattern-bound, so a required property is never null.
        var optional = db.Cypher().Match(member).OptionalMatch(member.Out<Membership>(club), where: () => club.Value.Title != "Chess");
        Assert.Contains("n1.`Title` <> $p0", optional.Return(() => member.Value.Id).ToQueryString());
        // After its clause, an OPTIONAL MATCH symbol can be null, including after WITH.
        var forwarded = optional.With(member, club);
        Assert.Equal("GBFC110", CompileCode(() => forwarded.Where(() => club.Value.Title != "Go").Return(() => member.Value.Id).ToQueryString()));
        Assert.Contains("n1.`Title` = $p1", forwarded.Where(() => club.Value.Title == "Go").Return(() => member.Value.Id).ToQueryString());
    }

    [Fact]
    public void Rejected_shapes_fail_before_transport_with_codes()
    {
        using var db = Context();
        var member = Graph.Node<Member>(); var other = Graph.Node<Member>();
        var query = db.Cypher().Match(member.Pattern(), other.Pattern());
        var since = DateTimeOffset.UnixEpoch;
        int a = 1, b = 2;
        var token = Guid.Empty;
        Assert.Equal("GBFC104", CompileCode(() => query.Where(() => !(member.Value.Born > 1960)).Return(() => member.Value.Id).ToQueryString()));
        Assert.Equal("GBFC106", Code(() => query.Where(() => member.Value.Name.StartsWith("A"))));
        Assert.Equal("GBFC101", Code(() => query.Where(() => member.Value.Name.ToUpper() == "ADA")));
        Assert.Equal("GBFC103", Code(() => query.Where(() => member.Value == other.Value)));
        Assert.Equal("GBFC103", Code(() => query.Where(() => a == b)));
        Assert.Equal("GBFC105", Code(() => query.Where(() => member.Value.Born > 1960.5)));
        Assert.Equal("GBFC112", Code(() => query.Where(() => member.Value.Joined > since)));
        Assert.Equal("GBFC111", CompileCode(() => query.Where(() => member.Value.Token > token).Return(() => member.Value.Id).ToQueryString()));
        Assert.Equal("GBFC150", Code(() => query.Return(() => member.Value.Id).OrderBy(() => member.Value)));
        var error = Assert.Throws<NotSupportedException>(() => query.Where(() => !(member.Value.Born > 1960)).Return(() => member.Value.Id).ToQueryString());
        Assert.StartsWith("GraphBridge fluent Cypher [GBFC104]:", error.Message);
        Assert.Contains(".Not()", error.Message);
    }

    [Fact]
    public void Captured_values_are_read_once_without_running_application_code()
    {
        using var db = Context();
        var member = Graph.Node<Member>();
        var holder = new Holder();
        Assert.Equal("GBFC102", Code(() => db.Cypher().Match(member).Where(() => member.Value.Name == holder.Name)));
        Assert.Equal(0, holder.Reads);
        var name = "Ada";
        var query = db.Cypher().Match(member).Where(() => member.Value.Name == name);
        name = "Bob";
        Assert.Equal("Ada", query.Return(() => member.Value.Id).ToCommand().Parameters["p0"]);
        Assert.Equal("Bob", name);
    }

    [Fact]
    public void Lambda_and_explicit_predicates_combine_with_EXISTS()
    {
        using var db = Context();
        var member = Graph.Node<Member>(); var club = Graph.Node<Club>();
        var text = db.Cypher().Match(member).Where(() => member.Value.Active)
            .Where(Graph.Exists(member.Out<Membership>(club), club.Property(c => c.Founded).Gt(2000)))
            .Return(() => member.Value.Name).ToQueryString();
        Assert.Contains("WHERE n0.`Active` = $p0 AND EXISTS { MATCH (n0:`LambdaMember`)-[r2:`LAMBDA_MEMBER_OF`]->(n1:`LambdaClub`)", text.Replace("\r", ""));
    }

    [Fact]
    public void Anonymous_edges_are_distinct_symbols_that_keep_role_validation()
    {
        using var db = Context();
        var member = Graph.Node<Member>(); var club = Graph.Node<Club>(); var other = Graph.Node<Member>();
        var text = db.Cypher().Match(member.Out<Membership>(club).In<Membership>(other)).Return(() => other.Value.Id).ToQueryString();
        Assert.Contains("-[r2:`LAMBDA_MEMBER_OF`]->(n1:`LambdaClub`)<-[r4:`LAMBDA_MEMBER_OF`]-", text);
        Assert.Throws<NotSupportedException>(() => db.Cypher().Match(club.Out<Membership>(member)).Return(() => member.Value.Id).ToQueryString());
    }

    private static (string Where, IReadOnlyDictionary<string, object?> Parameters) Where(DbContext db, CypherNode<Member> member,
        System.Linq.Expressions.Expression<Func<bool>> predicate)
    {
        var command = db.Cypher().Match(member).Where(predicate).Return(() => member.Value.Id).ToCommand();
        var line = command.Text.Replace("\r", "").Split('\n').Single(l => l.StartsWith("WHERE ", StringComparison.Ordinal));
        return (line["WHERE ".Length..], command.Parameters);
    }

    private static string? Code(Action action)
        => Assert.Throws<NotSupportedException>(action).Data["GraphBridge.FluentDiagnostic"] as string;
    private static string? CompileCode(Func<string> compile)
        => Assert.Throws<NotSupportedException>(() => compile()).Data["GraphBridge.FluentDiagnostic"] as string;

    internal static ProviderModelContext Context() => new(ProviderTestDriver.Create(), b =>
    {
        b.Entity<Member>().ToGraphNode("LambdaMember");
        b.Entity<Member>().Property(m => m.Id).ValueGeneratedNever();
        b.Entity<Member>().Property(m => m.Code).HasConversion(v => "code:" + v, v => v.Substring(5));
        b.Entity<Club>().ToGraphNode("LambdaClub");
        b.Entity<Club>().Property(c => c.Id).ValueGeneratedNever();
        b.Entity<Membership>().HasOne<Member>().WithMany().HasForeignKey(e => e.MemberId);
        b.Entity<Membership>().HasOne<Club>().WithMany().HasForeignKey(e => e.ClubId);
        b.Entity<Membership>().Property(e => e.Id).ValueGeneratedNever();
        b.Entity<Membership>().ToGraphIdentifiedRelationship("LAMBDA_MEMBER_OF", nameof(Membership.Id), [nameof(Membership.MemberId)], [nameof(Membership.ClubId)]);
    });

    public enum Level { Bronze, Silver, Gold }
    public sealed class Member
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string? Nickname { get; set; }
        public int? Born { get; set; }
        public bool Active { get; set; }
        public Level Level { get; set; }
        public Guid Token { get; set; }
        public DateTimeOffset Joined { get; set; }
        public string Code { get; set; } = "";
        public double Score { get; set; }
    }
    public sealed class Club { public int Id { get; set; } public string Title { get; set; } = ""; public int Founded { get; set; } }
    public sealed class Membership { public int Id { get; set; } public int MemberId { get; set; } public int ClubId { get; set; } public string Role { get; set; } = ""; }
    private sealed class Holder
    {
        public int Reads { get; private set; }
        public string Name { get { Reads++; return "Ada"; } }
    }
}
