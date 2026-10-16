using System.Linq.Expressions;
using GraphBridge.EntityFrameworkCore.Cypher;
using GraphBridge.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;

namespace GraphBridge.EntityFrameworkCore.Query;

internal sealed partial class Neo4jQueryableMethodTranslatingExpressionVisitor
{
    private static bool IsContainsMethod(MethodCallExpression call)
        => call.Method.Name == nameof(Enumerable.Contains) && call.Arguments.Count == 2
            && call.Method.DeclaringType is { } type && (type == typeof(Enumerable) || type == typeof(Queryable));

    private static CypherExpression TranslateCollectionContains(MethodCallExpression call,
        ParameterExpression entity, GraphReadBinding binding, List<Neo4jQueryParameter> parameters)
    {
        var property = FindProperty(call.Arguments[1], entity, binding)
            ?? throw Unsupported("Contains item must be one directly mapped scalar property");
        RequireContainsEquality(property.ModelProperty);
        var collection = StripIdentityConversion(call.Arguments[0]);
        // EF 10 normalizes ICollection<T>.Contains to Queryable.Contains(AsQueryable(source), item).
        if (collection is MethodCallExpression { Method.DeclaringType: { } declaring, Arguments.Count: 1 } asQueryable
            && declaring == typeof(Queryable) && asQueryable.Method.Name == nameof(Queryable.AsQueryable))
            collection = StripIdentityConversion(asQueryable.Arguments[0]);
        if (collection is NewArrayExpression { NodeType: ExpressionType.NewArrayInit } array
            && array.Expressions.All(e => StripIdentityConversion(e) is ConstantExpression))
        {
            if (array.Expressions.Count > Neo4jQueryParameter.MaxCollectionValues)
                throw Unsupported($"Contains inline collections larger than {Neo4jQueryParameter.MaxCollectionValues}");
            var values = Array.CreateInstance(array.Type.GetElementType()!, array.Expressions.Count);
            for (var i = 0; i < values.Length; i++) values.SetValue(((ConstantExpression)StripIdentityConversion(array.Expressions[i])).Value, i);
            collection = Expression.Constant(values, array.Type);
        }
        var parameter = AddParameter(collection, property.ModelProperty, parameters);
        parameters[^1] = parameters[^1] with { Collection = true };
        return new CypherIn(property.StoreProperty, parameter);
    }

    protected override ShapedQueryExpression TranslateContains(ShapedQueryExpression source, Expression item)
    {
        var query = GetQuery(source);
        if (query.ResultProperties.Count != 1 || query.Include is not null
            || !IsDirectScalarProjection(source.ShaperExpression, query.ResultProperties[0]))
            throw Unsupported("Contains requires a direct scalar Select without Include; entity membership is unsupported");
        RequireBeforePagination(query, "Contains");
        var property = query.ResultProperties[0];
        RequireContainsEquality(property.ModelProperty);
        var parameters = query.Parameters.ToList();
        var equals = new CypherBinary(property.StoreProperty, CypherOperator.Equal, AddParameter(item, property.ModelProperty, parameters));
        var predicate = query.Plan.Predicate is null ? equals : new CypherBinary(query.Plan.Predicate, CypherOperator.And, equals);
        source = source.UpdateQueryExpression(query.With(query.Plan with { Predicate = predicate }, parameters));
        // Preserve existing aggregate restrictions for relationship identity/cardinality diagnostics.
        return ApplyAggregate(source, null, CypherAggregate.Any, typeof(bool));
    }

    private static bool IsDirectScalarProjection(Expression shaper, GraphReadProperty property)
    {
        // A DTO can select exactly one column too. It must not inherit that column's
        // equality (notably a non-null DTO containing a null scalar versus null).
        while (shaper is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked, Method: null } convert)
            shaper = convert.Operand;
        return shaper is IndexExpression { Object: var row, Arguments: [ConstantExpression { Value: int index }] }
            && row == Neo4jQueryExpression.RowParameter && index == property.Index;
    }

    private static void RequireContainsEquality(GraphPropertyBinding property)
    {
        var type = Nullable.GetUnderlyingType(property.Property.ClrType) ?? property.Property.ClrType;
        if (HasExplicitConversion(property.Property)
            || !(type == typeof(string) || type == typeof(int) || type == typeof(long) || type == typeof(bool)
                || type == typeof(Guid) || type.IsEnum))
            throw Unsupported("Contains supports string, int, long, bool, Guid and enum properties without explicit converters; floating-point/custom equality requires an explicit native query");
    }
}
