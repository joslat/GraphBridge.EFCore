using System.Reflection;

namespace GraphBridge.EntityFrameworkCore.FluentCypher;

/// <summary>A server expression with native Cypher semantics, not CLR predicate evaluation.</summary>
public sealed class CypherExpression<T>
{
    internal CypherExpression(CypherTerm term) => Term = term;
    internal CypherTerm Term { get; }
    public CypherPredicate Eq(T value) => Compare("=", new CypherLiteralTerm(value, typeof(T)));
    public CypherPredicate Ne(T value) => Compare("<>", new CypherLiteralTerm(value, typeof(T)));
    public CypherPredicate Gt(T value) => Compare(">", new CypherLiteralTerm(value, typeof(T)));
    public CypherPredicate Ge(T value) => Compare(">=", new CypherLiteralTerm(value, typeof(T)));
    public CypherPredicate Lt(T value) => Compare("<", new CypherLiteralTerm(value, typeof(T)));
    public CypherPredicate Le(T value) => Compare("<=", new CypherLiteralTerm(value, typeof(T)));
    public CypherPredicate Eq(CypherExpression<T> value) => Compare("=", Require(value));
    public CypherPredicate Ne(CypherExpression<T> value) => Compare("<>", Require(value));
    public CypherPredicate Gt(CypherExpression<T> value) => Compare(">", Require(value));
    public CypherPredicate Ge(CypherExpression<T> value) => Compare(">=", Require(value));
    public CypherPredicate Lt(CypherExpression<T> value) => Compare("<", Require(value));
    public CypherPredicate Le(CypherExpression<T> value) => Compare("<=", Require(value));
    public CypherPredicate IsNull() => new(new CypherNullTerm(Term, false));
    public CypherPredicate IsNotNull() => new(new CypherNullTerm(Term, true));
    /// <summary>Native <c>IN</c> over a parameterized list copied when this method is called. A null element or a
    /// null operand makes a non-matching row's result null, which WHERE drops.</summary>
    public CypherPredicate In(params IEnumerable<T> values)
        => new(new CypherInTerm(Term, (values ?? throw new ArgumentNullException(nameof(values))).Cast<object?>().ToArray()));
    private CypherPredicate Compare(string op, CypherTerm other) => new(new CypherComparisonTerm(Term, op, other));
    private static CypherTerm Require(CypherExpression<T> value)
        => (value ?? throw new ArgumentNullException(nameof(value))).Term;
}

/// <summary>Native, case-sensitive string operators. Values are compared exactly as stored; converted properties are refused.</summary>
public static class CypherStringExpressions
{
    public static CypherPredicate StartsWith(this CypherExpression<string> expression, string value) => Text(expression, "STARTS WITH", value);
    public static CypherPredicate EndsWith(this CypherExpression<string> expression, string value) => Text(expression, "ENDS WITH", value);
    public static CypherPredicate Contains(this CypherExpression<string> expression, string value) => Text(expression, "CONTAINS", value);
    public static CypherPredicate StartsWith(this CypherExpression<string> expression, CypherExpression<string> value) => Text(expression, "STARTS WITH", value);
    public static CypherPredicate EndsWith(this CypherExpression<string> expression, CypherExpression<string> value) => Text(expression, "ENDS WITH", value);
    public static CypherPredicate Contains(this CypherExpression<string> expression, CypherExpression<string> value) => Text(expression, "CONTAINS", value);

    private static CypherPredicate Text(CypherExpression<string> expression, string op, string value)
        => new(new CypherStringTerm(Require(expression), op,
            new CypherLiteralTerm(value ?? throw new ArgumentNullException(nameof(value)), typeof(string))));
    private static CypherPredicate Text(CypherExpression<string> expression, string op, CypherExpression<string> value)
        => new(new CypherStringTerm(Require(expression), op, Require(value)));
    private static CypherTerm Require(CypherExpression<string> expression)
        => (expression ?? throw new ArgumentNullException(nameof(expression))).Term;
}

/// <summary>Native three-valued Cypher Boolean logic; WHERE retains only true results.</summary>
public sealed class CypherPredicate
{
    internal CypherPredicate(CypherTerm term) => Term = term;
    internal CypherTerm Term { get; }
    public CypherPredicate And(CypherPredicate other)
        => new(new CypherBooleanTerm(Term, "AND", (other ?? throw new ArgumentNullException(nameof(other))).Term));
    public CypherPredicate Or(CypherPredicate other)
        => new(new CypherBooleanTerm(Term, "OR", (other ?? throw new ArgumentNullException(nameof(other))).Term));
    public CypherPredicate Not() => new(new CypherNotTerm(Term));
}

internal abstract record CypherTerm;
internal sealed record CypherPropertyTerm(CypherAlias Alias, PropertyInfo Property) : CypherTerm;
internal sealed record CypherAliasTerm(CypherAlias Alias) : CypherTerm;
internal sealed record CypherLiteralTerm(object? Value, Type Type) : CypherTerm;
/// <summary>ClrSemantics marks a comparison written as a C# lambda; the compiler accepts it only where native and C# results agree (ADR 0047).</summary>
internal sealed record CypherComparisonTerm(CypherTerm Left, string Operator, CypherTerm Right, bool ClrSemantics = false) : CypherTerm;
internal sealed record CypherBooleanTerm(CypherTerm Left, string Operator, CypherTerm Right) : CypherTerm;
internal sealed record CypherNullTerm(CypherTerm Operand, bool Negated) : CypherTerm;
/// <summary>ClrSemantics marks C# <c>!</c>; it is accepted only over operands that can never be null (ADR 0047).</summary>
internal sealed record CypherNotTerm(CypherTerm Operand, bool ClrSemantics = false) : CypherTerm;
/// <summary>ClrSemantics marks C# Contains: a null element must agree with C# Contains(null) (ADR 0047).</summary>
internal sealed record CypherInTerm(CypherTerm Operand, IReadOnlyList<object?> Values, bool ClrSemantics = false) : CypherTerm;
internal sealed record CypherStringTerm(CypherTerm Operand, string Operator, CypherTerm Argument) : CypherTerm;
internal sealed record CypherCountTerm(CypherTerm? Operand, bool Distinct) : CypherTerm;
/// <summary>A search clause's SCORE AS value (ADR 0057); it has no expression of its own.</summary>
internal sealed record CypherScoreTerm : CypherTerm;
/// <summary>min, max, sum, avg or collect over a property; collect carries a server-side slice bound.</summary>
internal sealed record CypherAggregateTerm(string Function, CypherTerm Operand, bool Distinct, int? Max) : CypherTerm;
internal sealed record CypherExistsTerm(CypherPattern Pattern, CypherTerm? Predicate) : CypherTerm;
