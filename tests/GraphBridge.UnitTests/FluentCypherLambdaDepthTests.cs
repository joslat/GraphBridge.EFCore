using System.Linq.Expressions;
using GraphBridge.EntityFrameworkCore.FluentCypher;
using Microsoft.EntityFrameworkCore;
using static GraphBridge.UnitTests.FluentCypherLambdaTests;
using Graph = GraphBridge.EntityFrameworkCore.FluentCypher.Cypher;

namespace GraphBridge.UnitTests;

/// <summary>ADR 0047 wave 2: IN, ordinal string operators, negation over two-valued operands, enum ordering and
/// embedded native predicates. Each accepted shape has an explicit native twin with identical Cypher.</summary>
public sealed class FluentCypherLambdaDepthTests
{
    [Fact]
    public void Contains_becomes_IN_with_model_values_copied_at_Where()
    {
        using var db = Context();
        var member = Graph.Node<Member>();
        int[] ids = [1, 2];
        var lambda = Where(db, member, () => ids.Contains(member.Value.Id));
        Assert.Equal("n0.`Id` IN $p0", lambda.Where);
        Assert.Equal([1L, 2L], List(lambda.Parameters["p0"]));
        var explicitForm = Where(db, member, member.Property(m => m.Id).In(1, 2));
        Assert.Equal(lambda.Where, explicitForm.Where);
        Assert.Equal(List(lambda.Parameters["p0"]), List(explicitForm.Parameters["p0"]));
        ids[0] = 99; // copied when Where was called
        Assert.Equal([1L, 2L], List(lambda.Parameters["p0"]));

        var levels = new List<Level> { Level.Gold, Level.Bronze };
        Assert.Equal([2L, 0L], List(Where(db, member, () => levels.Contains(member.Value.Level)).Parameters["p0"]));
        var token = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");
        var tokens = new HashSet<Guid> { token };
        Assert.Equal([token.ToString("D")], List(Where(db, member, () => tokens.Contains(member.Value.Token)).Parameters["p0"]));
        var codes = new HashSet<string>(StringComparer.Ordinal) { "a" };
        Assert.Equal(["code:a"], List(Where(db, member, () => codes.Contains(member.Value.Code)).Parameters["p0"]));
        IEnumerable<int> typedAsSequence = new List<int> { 3 };
        Assert.Equal([3L], List(Where(db, member, () => typedAsSequence.Contains(member.Value.Id)).Parameters["p0"]));
        // A value the member type cannot represent can never be equal to it.
        long[] wide = [1, 5_000_000_000];
        Assert.Equal([1L], List(Where(db, member, () => wide.Contains(member.Value.Id)).Parameters["p0"]));
    }

    [Fact]
    public void Contains_with_a_null_element_agrees_or_is_rejected()
    {
        using var db = Context();
        var member = Graph.Node<Member>();
        string?[] names = ["Ada", null];
        // A member that is never null cannot equal the null element in either language.
        var lambda = Where(db, member, () => names.Contains(member.Value.Name));
        Assert.Equal("n0.`Name` IN $p0", lambda.Where);
        Assert.Equal(["Ada"], List(lambda.Parameters["p0"]));
        // A nullable member would be kept by C# Contains(null) and dropped by native IN; the user states the null test.
        Assert.Equal("GBFC110", CompileCode(() => Query(db, member, () => names.Contains(member.Value.Nickname))));
        string[] nonNull = ["Ada"];
        Assert.Equal("n0.`Nickname` IN $p0 OR n0.`Nickname` IS NULL",
            Where(db, member, () => nonNull.Contains(member.Value.Nickname) || member.Value.Nickname == null).Where);
        // The explicit form is native: the null element stays and a null nickname is dropped.
        var native = Where(db, member, member.Property(m => m.Nickname).In(names));
        Assert.Equal("n0.`Nickname` IN $p0", native.Where);
        Assert.Equal(["Ada", null], List(native.Parameters["p0"]));
    }

    [Fact]
    public void Ordinal_string_methods_become_native_string_operators()
    {
        using var db = Context();
        var member = Graph.Node<Member>();
        var prefix = "A";
        var lambda = Where(db, member, () => member.Value.Name.StartsWith(prefix, StringComparison.Ordinal)
            && member.Value.Name.EndsWith('e') && member.Value.Name.Contains("ov"));
        Assert.Equal("n0.`Name` STARTS WITH $p0 AND n0.`Name` ENDS WITH $p1 AND n0.`Name` CONTAINS $p2", lambda.Where);
        Assert.Equal(["A", "e", "ov"], new[] { lambda.Parameters["p0"], lambda.Parameters["p1"], lambda.Parameters["p2"] });
        var explicitForm = Where(db, member, member.Property(m => m.Name).StartsWith(prefix)
            .And(member.Property(m => m.Name).EndsWith("e")).And(member.Property(m => m.Name).Contains("ov")));
        Assert.Equal(lambda.Where, explicitForm.Where);
        var other = Graph.Node<Member>();
        var members = db.Cypher().Match(member.Pattern(), other.Pattern())
            .Where(member.Property(m => m.Name).StartsWith(other.Property(m => m.Name))).Return(() => member.Value.Id).ToQueryString();
        Assert.Contains("WHERE n0.`Name` STARTS WITH n1.`Name`", members);
    }

    [Fact]
    public void Negation_is_accepted_only_where_the_operand_can_never_be_null()
    {
        using var db = Context();
        var member = Graph.Node<Member>(); var club = Graph.Node<Club>();
        int[] ids = [1];
        string?[] names = ["Ada", null];
        Assert.Equal("NOT (n0.`Active` = $p0)", Where(db, member, () => !member.Value.Active).Where);
        Assert.Equal("NOT (n0.`Name` = $p0)", Where(db, member, () => !(member.Value.Name == "Ada")).Where);
        Assert.Equal("NOT (n0.`Nickname` IS NULL)", Where(db, member, () => !(member.Value.Nickname == null)).Where);
        Assert.Equal("NOT (n0.`Id` IN $p0)", Where(db, member, () => !ids.Contains(member.Value.Id)).Where);
        Assert.Equal("NOT (n0.`Name` IN $p0)", Where(db, member, () => !names.Contains(member.Value.Name)).Where);
        Assert.Equal("NOT (n0.`Name` STARTS WITH $p0)", Where(db, member, () => !member.Value.Name.StartsWith("A", StringComparison.Ordinal)).Where);
        Assert.Equal("NOT (n0.`Active` = $p0 AND n0.`Id` > $p1)", Where(db, member, () => !(member.Value.Active && member.Value.Id > 3)).Where);
        Assert.Equal(Where(db, member, member.Property(m => m.Name).Eq("Ada").Not()).Where, Where(db, member, () => !(member.Value.Name == "Ada")).Where);

        Assert.Equal("GBFC104", CompileCode(() => Query(db, member, () => !(member.Value.Born > 1960))));
        Assert.Equal("GBFC104", CompileCode(() => Query(db, member, () => !names.Contains(member.Value.Nickname))));
        Assert.Equal("GBFC104", CompileCode(() => Query(db, member, () => !(member.Value.Active || member.Value.Nickname == "Bob"))));
        // An OPTIONAL MATCH symbol is pattern-bound in its own WHERE and can be null afterwards.
        var optional = db.Cypher().Match(member).OptionalMatch(member.Out<Membership>(club), where: () => !(club.Value.Founded > 1990));
        Assert.Contains("NOT (n1.`Founded` > $p0)", optional.Return(() => member.Value.Id).ToQueryString());
        Assert.Equal("GBFC104", CompileCode(() => optional.With(member, club).Where(() => !(club.Value.Founded > 2000)).Return(() => member.Value.Id).ToQueryString()));
    }

    [Fact]
    public void Enum_ordering_is_translated_through_the_monotonic_enum_converter()
    {
        using var db = Context();
        var member = Graph.Node<Member>();
        var silver = Where(db, member, () => member.Value.Level >= Level.Silver);
        Assert.Equal("n0.`Level` >= $p0", silver.Where);
        Assert.Equal(1L, silver.Parameters["p0"]);
        Assert.Equal(silver.Where, Where(db, member, member.Property(m => m.Level).Ge(Level.Silver)).Where);
    }

    [Fact]
    public void Embedded_native_predicates_join_lambda_compositions()
    {
        using var db = Context();
        var member = Graph.Node<Member>(); var club = Graph.Node<Club>();
        var inline = Where(db, member, () => member.Value.Id > 3 || Graph.Is(Graph.Exists(member.Out<Membership>(club), club.Property(c => c.Founded).Gt(1990))));
        var explicitForm = Where(db, member, member.Property(m => m.Id).Gt(3)
            .Or(Graph.Exists(member.Out(Graph.Edge<Membership>(), club), club.Property(c => c.Founded).Gt(1990))));
        Assert.Equal(explicitForm.Where, inline.Where);
        Assert.StartsWith("n0.`Id` > $p0 OR EXISTS { MATCH (n0:`LambdaMember`)-[r2:`LAMBDA_MEMBER_OF`]->(n1:`LambdaClub`)", inline.Where);
        // The inner EXISTS predicate can be a lambda too; it renders like the explicit form.
        var otherClub = Graph.Node<Club>();
        Assert.Equal(Where(db, member, Graph.Exists(member.Out(Graph.Edge<Membership>(), otherClub), otherClub.Property(c => c.Founded).Gt(1990))).Where,
            Where(db, member, Graph.Exists(member.Out<Membership>(otherClub), () => otherClub.Value.Founded > 1990)).Where);
        var hasClub = Graph.Exists(member.Out<Membership>(club));
        Assert.StartsWith("NOT EXISTS { MATCH", Where(db, member, () => !Graph.Is(hasClub)).Where);
        var born = member.Property(m => m.Born).Gt(1960);
        Assert.Equal("GBFC104", CompileCode(() => Query(db, member, () => !Graph.Is(born))));
        Assert.Throws<InvalidOperationException>(() => Graph.Is(hasClub));
    }

    [Fact]
    public void Embedded_predicates_can_be_returned_as_Boolean_columns()
    {
        using var db = Context();
        var member = Graph.Node<Member>(); var club = Graph.Node<Club>();
        var text = db.Cypher().Match(member)
            .Return(() => new { member.Value.Name, Joined = Graph.Is(Graph.Exists(member.Out<Membership>(club))) }).ToQueryString();
        Assert.Contains("RETURN n0.`Name` AS c0, EXISTS { MATCH (n0:`LambdaMember`)-[r2:`LAMBDA_MEMBER_OF`]->(n1:`LambdaClub`) } AS c1", text);
        var born = member.Property(m => m.Born).Gt(1960);
        // A comparison on a nullable property can be null: bool is refused, bool? keeps the native value.
        Assert.Equal("GBFC407", CompileCode(() => db.Cypher().Match(member).Return(() => Graph.Is(born)).ToQueryString()));
        Assert.Contains("RETURN n0.`Born` > $p0 AS c0", db.Cypher().Match(member).Return(() => (bool?)Graph.Is(born)).ToQueryString());
    }

    [Fact]
    public void Rejected_membership_and_string_shapes_name_their_alternative()
    {
        using var db = Context();
        var member = Graph.Node<Member>();
        var query = db.Cypher().Match(member);
        int[] ids = [1, 2];
        var lazy = ids.Where(id => id > 0);
        var folded = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ada" };
        DateTimeOffset[] times = [DateTimeOffset.UnixEpoch];
        double[] nan = [double.NaN];
        int[]? none = null;
        string? missing = null;
        var large = Enumerable.Range(0, 1025).ToArray();
        Assert.Equal("GBFC107", Code(() => query.Where(() => lazy.Contains(member.Value.Id))));
        Assert.Equal("GBFC107", Code(() => query.Where(() => folded.Contains(member.Value.Name))));
        Assert.Equal("GBFC107", Code(() => query.Where(() => ids.Contains(member.Value.Id, EqualityComparer<int>.Default))));
        Assert.Equal("GBFC107", CompileCode(() => query.Where(() => large.Contains(member.Value.Id)).Return(() => member.Value.Id).ToQueryString()));
        Assert.Equal("GBFC112", Code(() => query.Where(() => times.Contains(member.Value.Joined))));
        Assert.Equal("GBFC105", Code(() => query.Where(() => nan.Contains(member.Value.Id))));
        Assert.Equal("GBFC102", Code(() => query.Where(() => none!.Contains(member.Value.Id))));
        Assert.Equal("GBFC106", Code(() => query.Where(() => member.Value.Name.EndsWith("e"))));
        Assert.Equal("GBFC106", Code(() => query.Where(() => member.Value.Name.StartsWith("a", StringComparison.OrdinalIgnoreCase))));
        Assert.Equal("GBFC106", Code(() => query.Where(() => member.Value.Name.Contains("a", StringComparison.CurrentCulture))));
        Assert.Equal("GBFC102", Code(() => query.Where(() => member.Value.Name.Contains(missing!))));
        Assert.Equal("GBFC101", Code(() => query.Where(() => "Ada Lovelace".Contains(member.Value.Name))));
        Assert.Equal("GBFC101", Code(() => query.Where(() => member.Value.Name.Trim().Contains("a"))));
        Assert.Equal("GBFC111", CompileCode(() => query.Where(() => member.Value.Code.Contains("x")).Return(() => member.Value.Id).ToQueryString()));
        Assert.Equal("GBFC111", CompileCode(() => query.Where(member.Property(m => m.Code).StartsWith("x")).Return(() => member.Value.Id).ToQueryString()));
        Assert.Throws<ArgumentNullException>(() => member.Property(m => m.Name).StartsWith((string)null!));
        Assert.Throws<ArgumentNullException>(() => member.Property(m => m.Id).In((IEnumerable<int>)null!));
    }

    [Fact]
    public void Embedded_predicates_never_run_application_code()
    {
        using var db = Context();
        var member = Graph.Node<Member>(); var club = Graph.Node<Club>();
        var calls = 0;
        Func<CypherPredicate> build = () => { calls++; return Graph.Exists(member.Out<Membership>(club)); };
        Assert.Equal("GBFC102", Code(() => db.Cypher().Match(member).Where(() => Graph.Is(build()))));
        Assert.Equal(0, calls);
        CypherPredicate? nothing = null;
        Assert.Equal("GBFC102", Code(() => db.Cypher().Match(member).Where(() => Graph.Is(nothing!))));
    }

    [Fact]
    public void Review_regressions_inexact_values_are_rejected_or_never_match()
    {
        using var db = Context();
        var member = Graph.Node<Member>();
        var query = db.Cypher().Match(member);
        double justAbove = 3.0000000000000004, justBelow = 2.9999999999999996, tiny = 1e-30;
        Assert.Equal("GBFC105", Code(() => query.Where(() => member.Value.Id >= justAbove)));
        Assert.Equal("GBFC105", Code(() => query.Where(() => member.Value.Id == justBelow)));
        Assert.Equal("GBFC105", Code(() => query.Where(() => member.Value.Id == tiny)));
        double[] inexact = [3.0000000000000004, 4];
        Assert.Equal([4L], List(Where(db, member, () => inexact.Contains(member.Value.Id)).Parameters["p0"]));
        // Enum.ToObject would truncate 4294967298 to Gold (2); C# never matches it.
        long beyond = 4294967298;
        Assert.Equal("GBFC105", Code(() => query.Where(() => (int)member.Value.Level == beyond)));
        long[] levels = [4294967298, 2];
        Assert.Equal([2L], List(Where(db, member, () => levels.Contains((int)member.Value.Level)).Parameters["p0"]));
    }

    [Fact]
    public void Review_regressions_no_user_code_and_no_uncoded_failures()
    {
        using var db = Context();
        var member = Graph.Node<Member>(); var other = Graph.Node<Member>();
        var spy = new SpyComparer();
        var spied = new HashSet<int>(spy) { 1 };
        Assert.Equal("GBFC107", Code(() => db.Cypher().Match(member).Where(() => spied.Contains(member.Value.Id))));
        Assert.Equal(0, spy.Calls);
        // .Value getters throw by design; inside Graph.Is they are refused rather than called.
        Assert.Equal("GBFC102", Code(() => db.Cypher().Match(member.Pattern(), other.Pattern())
            .Where(() => Graph.Is(member.Property(m => m.Id).Eq(other.Value.Id)))));
        CypherNode<Member>? absent = null;
        Assert.Equal("GBFC102", Code(() => db.Cypher().Match(member).Where(() => Graph.Is(Graph.Exists(absent!.Pattern())))));
        Assert.Equal("GBFC102", Code(() => db.Cypher().Match(member).Where(() => member.Value.Name == Failing.Value)));
        // Reference equality in C# is not value equality in Cypher.
        var blob = Graph.Node<Blob>();
        byte[] hash = [1];
        Assert.Equal("GBFC105", Assert.Throws<FluentCypherException>(() => CypherPredicateTranslator.Translate(() => blob.Value.Hash == hash))
            .Data["GraphBridge.FluentDiagnostic"]);
        Assert.IsType<CypherNullTerm>(CypherPredicateTranslator.Translate(() => blob.Value.Hash == null).Term);
    }

    [Fact]
    public void Review_regressions_converted_properties_are_not_compared_with_WITH_values()
    {
        using var db = new ProviderModelContext(ProviderTestDriver.Create(), b =>
        {
            b.Entity<Member>().ToGraphNode("LambdaMember");
            b.Entity<Member>().Property(m => m.Id).ValueGeneratedNever();
            b.Entity<Member>().Property(m => m.Born).HasConversion<string>();
            b.Entity<Club>().ToGraphNode("LambdaClub");
            b.Entity<Club>().Property(c => c.Id).ValueGeneratedNever();
            b.Entity<Membership>().HasOne<Member>().WithMany().HasForeignKey(e => e.MemberId);
            b.Entity<Membership>().HasOne<Club>().WithMany().HasForeignKey(e => e.ClubId);
            b.Entity<Membership>().Property(e => e.Id).ValueGeneratedNever();
            b.Entity<Membership>().ToGraphIdentifiedRelationship("LAMBDA_MEMBER_OF", nameof(Membership.Id), [nameof(Membership.MemberId)], [nameof(Membership.ClubId)]);
        });
        var member = Graph.Node<Member>(); var club = Graph.Node<Club>();
        var count = Graph.CountDistinct(club);
        var grouped = db.Cypher().Match(member.Out<Membership>(club)).With(member, count);
        Assert.Equal("GBFC111", CompileCode(() => grouped.Where(() => member.Value.Born == count.Value).Return(() => count.Value).ToQueryString()));
        // The built-in enum converter stores the numeric value, so it stays comparable.
        Assert.Contains("n0.`Level` = v3", grouped.Where(() => (long)member.Value.Level == count.Value).Return(() => count.Value).ToQueryString());
    }

    private sealed class SpyComparer : IEqualityComparer<int>
    {
        public int Calls { get; private set; }
        public bool Equals(int x, int y) { Calls++; return x == y; }
        public int GetHashCode(int value) => value;
        public override bool Equals(object? obj) { Calls++; return true; }
        public override int GetHashCode() => 0;
    }

    private static class Failing
    {
        public static readonly string Value = Fail();
        private static string Fail() => throw new InvalidOperationException("initializer failed");
    }

    public sealed class Blob { public int Id { get; set; } public byte[]? Hash { get; set; } }

    private static string Query(DbContext db, CypherNode<Member> member, Expression<Func<bool>> predicate)
        => db.Cypher().Match(member).Where(predicate).Return(() => member.Value.Id).ToQueryString();

    private static (string Where, IReadOnlyDictionary<string, object?> Parameters) Where(DbContext db, CypherNode<Member> member,
        Expression<Func<bool>> predicate) => Split(db.Cypher().Match(member).Where(predicate).Return(() => member.Value.Id).ToCommand());

    private static (string Where, IReadOnlyDictionary<string, object?> Parameters) Where(DbContext db, CypherNode<Member> member,
        CypherPredicate predicate) => Split(db.Cypher().Match(member).Where(predicate).Return(() => member.Value.Id).ToCommand());

    private static (string Where, IReadOnlyDictionary<string, object?> Parameters) Split(CypherCommand command)
    {
        // The outer WHERE, which can span lines when it embeds EXISTS { ... WHERE ... }.
        var text = command.Text.Replace("\r", "");
        var start = text.IndexOf("\nWHERE ", StringComparison.Ordinal) + "\nWHERE ".Length;
        return (text[start..text.IndexOf("\nRETURN ", StringComparison.Ordinal)], command.Parameters);
    }

    private static object?[] List(object? value) => Assert.IsAssignableFrom<System.Collections.IEnumerable>(value).Cast<object?>().ToArray();
    private static string? Code(Action action)
        => Assert.Throws<FluentCypherException>(action).Data["GraphBridge.FluentDiagnostic"] as string;
    private static string? CompileCode(Func<string> compile)
        => Assert.Throws<FluentCypherException>(() => compile()).Data["GraphBridge.FluentDiagnostic"] as string;
}
