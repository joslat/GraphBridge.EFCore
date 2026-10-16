using System.Linq.Expressions;
using System.Collections;
using GraphBridge.EntityFrameworkCore.Cypher;
using GraphBridge.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;

namespace GraphBridge.EntityFrameworkCore.Query;

internal sealed class Neo4jQueryExpression(
    GraphReadBinding binding,
    CypherQueryPlan plan,
    IReadOnlyList<Neo4jQueryParameter>? parameters = null,
    IReadOnlyList<GraphReadProperty>? resultProperties = null,
    Type? aggregateType = null,
    GraphIncludeBinding? include = null) : Expression
{
    public static readonly ParameterExpression RowParameter = Expression.Parameter(typeof(ValueBuffer), "row");
    public GraphReadBinding Binding { get; } = binding;
    public CypherQueryPlan Plan { get; } = plan;
    public IReadOnlyList<Neo4jQueryParameter> Parameters { get; } = Array.AsReadOnly((parameters ?? []).ToArray());
    public IReadOnlyList<GraphReadProperty> ResultProperties { get; } = Array.AsReadOnly((resultProperties ?? binding.Properties).ToArray());
    public GraphRowReader? Reader { get; } = aggregateType is null ? binding.PrepareRead(resultProperties) : null;
    public Type? AggregateType { get; } = aggregateType;
    public GraphIncludeBinding? Include { get; } = include;
    public override Type Type => typeof(IEnumerable<ValueBuffer>);
    public override ExpressionType NodeType => ExpressionType.Extension;
    protected override Expression VisitChildren(ExpressionVisitor visitor) => this;

    public Neo4jQueryExpression With(
        CypherQueryPlan plan,
        IReadOnlyList<Neo4jQueryParameter>? parameters = null,
        IReadOnlyList<GraphReadProperty>? resultProperties = null,
        Type? aggregateType = null,
        GraphIncludeBinding? include = null)
        => new(Binding, plan, parameters ?? Parameters, resultProperties ?? ResultProperties, aggregateType ?? AggregateType, include ?? Include);
}

internal sealed record Neo4jQueryParameter(
    string Name, string? RuntimeName, object? ConstantValue, GraphPropertyBinding? Property = null,
    bool NonNegative = false, int? Maximum = null, bool Collection = false, Type? ExactValueType = null)
{
    internal const int MaxCollectionValues = 1024;
    public object? Resolve(IReadOnlyDictionary<string, object?> values)
    {
        var value = RuntimeName is null ? ConstantValue : values[RuntimeName];
        if (value is not null && ExactValueType is not null && value.GetType() != ExactValueType)
            throw new InvalidOperationException("GraphBridge object.Equals requires a value with the mapped property's CLR type; cross-type object equality is unsupported.");
        if (Collection)
        {
            // Exact materialized BCL shapes only: do not enumerate arbitrary/lazy input or lose
            // HashSet/custom collection equality semantics. Snapshot before opening transport.
            if (value is not IList list || !(value is Array { Rank: 1 }
                || value.GetType().IsGenericType && value.GetType().GetGenericTypeDefinition() == typeof(List<>)))
                throw new InvalidOperationException("GraphBridge Contains requires a non-null one-dimensional array or List<T>; custom comparers and lazy/queryable collections are unsupported.");
            if (list.Count > MaxCollectionValues)
                throw new InvalidOperationException($"GraphBridge Contains accepts at most {MaxCollectionValues} values; use an explicit bounded native query for larger sets.");
            var result = new object?[list.Count];
            for (var i = 0; i < result.Length; i++) result[i] = ConvertValue(list[i]);
            return result;
        }
        if (NonNegative)
        {
            var count = Math.Max(0, Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture));
            return Maximum is { } maximum ? Math.Min(count, maximum) : count;
        }
        return ConvertValue(value);
    }

    private object? ConvertValue(object? value)
    {
        if (value is null || Property is null)
            return value;
        var modelType = Nullable.GetUnderlyingType(Property.Property.ClrType) ?? Property.Property.ClrType;
        if (modelType.IsEnum && !modelType.IsInstanceOfType(value))
            value = Enum.ToObject(modelType, value);
        return Property.ToStore(value);
    }
}
