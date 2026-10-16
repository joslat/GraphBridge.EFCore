using System.Linq.Expressions;
using System.Numerics;
using System.Reflection;
using GraphBridge.EntityFrameworkCore.Paths;
using Neo4j.Driver;

namespace GraphBridge.EntityFrameworkCore.FluentCypher;

/// <summary>Creates symbols for the explicit, untracked Cypher query surface.</summary>
public static class Cypher
{
    public static CypherNode<T> Node<T>() where T : class => new();
    public static CypherEdge<T> Edge<T>() where T : class => new();
    public static CypherPath Path() => new();
    public static CypherValue<long> CountRows() => new(new CypherCountTerm(null, false));
    public static CypherValue<long> Count(CypherAlias alias)
        => new(new CypherCountTerm(new CypherAliasTerm(alias ?? throw new ArgumentNullException(nameof(alias))), false));
    public static CypherValue<long> CountDistinct(CypherAlias alias)
        => new(new CypherCountTerm(new CypherAliasTerm(alias ?? throw new ArgumentNullException(nameof(alias))), true));
    public static CypherValue<long> Count<T>(CypherExpression<T> expression)
        => new(new CypherCountTerm((expression ?? throw new ArgumentNullException(nameof(expression))).Term, false));
    public static CypherValue<long> CountDistinct<T>(CypherExpression<T> expression)
        => new(new CypherCountTerm((expression ?? throw new ArgumentNullException(nameof(expression))).Term, true));

    /// <summary>Native <c>min</c>; null when every value in the group is null (for example after OPTIONAL MATCH).</summary>
    public static CypherValue<T> Min<T>(CypherExpression<T> expression) => Aggregate<T>("min", Operand(expression));
    /// <summary>Native <c>max</c>; null when every value in the group is null.</summary>
    public static CypherValue<T> Max<T>(CypherExpression<T> expression) => Aggregate<T>("max", Operand(expression));
    /// <summary>Native integer <c>sum</c>; nulls are skipped and an all-null group sums to 0.</summary>
    public static CypherValue<long> Sum(CypherExpression<int> expression) => Aggregate<long>("sum", Operand(expression));
    public static CypherValue<long> Sum(CypherExpression<int?> expression) => Aggregate<long>("sum", Operand(expression));
    public static CypherValue<long> Sum(CypherExpression<long> expression) => Aggregate<long>("sum", Operand(expression));
    public static CypherValue<long> Sum(CypherExpression<long?> expression) => Aggregate<long>("sum", Operand(expression));
    /// <summary>Native floating-point <c>sum</c>; nulls are skipped and an all-null group sums to 0.</summary>
    public static CypherValue<double> Sum(CypherExpression<double> expression) => Aggregate<double>("sum", Operand(expression));
    public static CypherValue<double> Sum(CypherExpression<double?> expression) => Aggregate<double>("sum", Operand(expression));
    /// <summary>Native <c>avg</c> as a double; null when every value in the group is null.</summary>
    public static CypherValue<double?> Avg<T>(CypherExpression<T> expression) where T : struct, INumber<T> => Aggregate<double?>("avg", Operand(expression));
    public static CypherValue<double?> Avg<T>(CypherExpression<T?> expression) where T : struct, INumber<T> => Aggregate<double?>("avg", Operand(expression));
    /// <summary>Native <c>collect</c>, sliced on the server to at most <paramref name="max"/> non-null values in no guaranteed order.</summary>
    public static CypherValue<IReadOnlyList<T>> Collect<T>(CypherExpression<T> expression, int max)
        => Aggregate<IReadOnlyList<T>>("collect", Operand(expression), max: CollectBound(max));
    /// <summary>Native <c>collect(DISTINCT ...)</c>, sliced on the server to at most <paramref name="max"/> values.</summary>
    public static CypherValue<IReadOnlyList<T>> CollectDistinct<T>(CypherExpression<T> expression, int max)
        => Aggregate<IReadOnlyList<T>>("collect", Operand(expression), distinct: true, max: CollectBound(max));

    private static CypherValue<TResult> Aggregate<TResult>(string function, CypherTerm operand, bool distinct = false, int? max = null)
        => new(new CypherAggregateTerm(function, operand, distinct, max));
    private static CypherTerm Operand<T>(CypherExpression<T> expression)
        => (expression ?? throw new ArgumentNullException(nameof(expression))).Term;
    private static int CollectBound(int max)
        => max is >= 1 and <= 10_000 ? max : throw new ArgumentOutOfRangeException(nameof(max), "Collect requires a maximum between 1 and 10,000 values.");

    /// <summary>A correlated native EXISTS pattern; new symbols stay inside its scope.</summary>
    public static CypherPredicate Exists(CypherPattern pattern, CypherPredicate? predicate = null)
        => new(new CypherExistsTerm(pattern ?? throw new ArgumentNullException(nameof(pattern)), predicate?.Term));

    /// <summary>A correlated native EXISTS pattern whose inner WHERE is a C# lambda under the ADR 0047 rules.</summary>
    public static CypherPredicate Exists(CypherPattern pattern, Expression<Func<bool>> where)
        => new(new CypherExistsTerm(pattern ?? throw new ArgumentNullException(nameof(pattern)), CypherPredicateTranslator.Translate(where).Term));

    /// <summary>Embeds a native predicate, such as <see cref="Exists"/>, in a Fluent <c>Where</c> lambda, where it keeps
    /// its native semantics. It is recognized during translation and is never evaluated.</summary>
    public static bool Is(CypherPredicate predicate) => throw CypherAlias.EvaluationError();
}

/// <summary>An opaque query symbol. Its identity is independent of CLR type and variable name.</summary>
public abstract class CypherAlias
{
    internal CypherAlias(Type clrType, CypherAliasKind kind) { ClrType = clrType; Kind = kind; }
    internal Type ClrType { get; }
    internal CypherAliasKind Kind { get; }
    internal virtual CypherTerm? Definition => null;
    internal static InvalidOperationException EvaluationError()
        => new("Fluent Cypher symbols and markers are translated, never evaluated on the client. Use them only inside Fluent lambdas such as Return, Where and OrderBy.");
}

/// <summary>Any mapped node symbol. Lets an edge type be named without restating the endpoint node type.</summary>
public abstract class CypherNode : CypherAlias
{
    internal CypherNode(Type clrType) : base(clrType, CypherAliasKind.Node) { }
}

/// <summary>A mapped node symbol; it is not an entity instance or an EF tracking entry.</summary>
public sealed class CypherNode<T> : CypherNode where T : class
{
    internal CypherNode() : base(typeof(T)) { }
    public T Value => throw EvaluationError();
    public CypherExpression<TProperty> Property<TProperty>(Expression<Func<T, TProperty>> property)
        => CypherPropertySelector.Create(this, property);
    public CypherPattern Out<TEdge, TTarget>(CypherEdge<TEdge> edge, CypherNode<TTarget> target)
        where TEdge : class where TTarget : class => new CypherPattern(this).Out(edge, target);
    public CypherPattern In<TEdge, TSource>(CypherEdge<TEdge> edge, CypherNode<TSource> source)
        where TEdge : class where TSource : class => new CypherPattern(this).In(edge, source);
    /// <summary>Outgoing step through a new anonymous edge symbol. Use Graph.Edge when the edge is referenced later.</summary>
    public CypherPattern Out<TEdge>(CypherNode target) where TEdge : class => new CypherPattern(this).Out<TEdge>(target);
    /// <summary>Incoming step through a new anonymous edge symbol. Use Graph.Edge when the edge is referenced later.</summary>
    public CypherPattern In<TEdge>(CypherNode source) where TEdge : class => new CypherPattern(this).In<TEdge>(source);
    public CypherPattern Pattern() => new(this);
    public CypherPattern Undirected<TEdge>(CypherEdge<TEdge> edge, CypherNode<T> other) where TEdge : class
        => new CypherPattern(this).Undirected(edge, other);
}

/// <summary>A mapped relationship symbol, distinct from its two endpoint nodes.</summary>
public sealed class CypherEdge<T> : CypherAlias where T : class
{
    internal CypherEdge() : base(typeof(T), CypherAliasKind.Edge) { }
    public T Value => throw EvaluationError();
    public CypherExpression<TProperty> Property<TProperty>(Expression<Func<T, TProperty>> property)
        => CypherPropertySelector.Create(this, property);
}

/// <summary>A value introduced by an explicit WITH projection.</summary>
public sealed class CypherValue<T> : CypherAlias
{
    private readonly CypherTerm _definition;
    internal CypherValue(CypherTerm definition) : base(typeof(T), CypherAliasKind.Value) => _definition = definition;
    internal override CypherTerm Definition => _definition;
    public T Value => throw EvaluationError();
    public CypherExpression<T> Expression => new(new CypherAliasTerm(this));
}

/// <summary>A bounded native path symbol. Values retain native node/edge properties and never track EF entities.</summary>
public sealed class CypherPath : CypherAlias
{
    internal CypherPath() : base(typeof(GraphPath<INode, IRelationship>), CypherAliasKind.Path) { }
    public GraphPath<INode, IRelationship> Value => throw EvaluationError();
}

internal enum CypherAliasKind { Node, Edge, Value, Path }

internal static class CypherPropertySelector
{
    public static CypherExpression<TProperty> Create<TEntity, TProperty>(CypherAlias alias,
        Expression<Func<TEntity, TProperty>> selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        if (selector.Body is not MemberExpression { Member: PropertyInfo property } member
            || member.Expression != selector.Parameters[0] || property.GetIndexParameters().Length != 0)
            throw CypherDiagnostics.Create("GBFC213", "A Cypher property selector must name one direct mapped CLR property, without conversion or computation.");
        return new(new CypherPropertyTerm(alias, property));
    }
}
