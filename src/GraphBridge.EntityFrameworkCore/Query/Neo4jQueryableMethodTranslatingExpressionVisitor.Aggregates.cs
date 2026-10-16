using System.Linq.Expressions;
using GraphBridge.EntityFrameworkCore.Cypher;
using Microsoft.EntityFrameworkCore.Query;

namespace GraphBridge.EntityFrameworkCore.Query;

internal sealed partial class Neo4jQueryableMethodTranslatingExpressionVisitor
{
    private ShapedQueryExpression TranslateIntegerExtremum(ShapedQueryExpression source,
        LambdaExpression? selector, Type resultType, CypherAggregate aggregate)
    {
        if (selector is not null) source = TranslateSelect(source, selector);
        var query = GetQuery(source);
        if (query.Include is not null || query.ResultProperties.Count != 1
            || !IsDirectScalarProjection(source.ShaperExpression, query.ResultProperties[0]))
            throw Unsupported("Min/Max require a direct int/long scalar property projection without Include");
        var property = query.ResultProperties[0];
        var modelProperty = property.ModelProperty.Property;
        var modelType = Nullable.GetUnderlyingType(modelProperty.ClrType) ?? modelProperty.ClrType;
        var resultValueType = Nullable.GetUnderlyingType(resultType) ?? resultType;
        if (HasExplicitConversion(modelProperty) || (modelType != typeof(int) && modelType != typeof(long))
            || resultValueType != modelType || (modelProperty.IsNullable && Nullable.GetUnderlyingType(resultType) is null))
            throw Unsupported("Min/Max support int/long and nullable int/long without explicit converters or nullable unwrapping");
        // Retain all existing root/pagination restrictions and the single aggregate slot.
        source = ApplyAggregate(source, null, aggregate, resultType);
        query = GetQuery(source);
        return source.UpdateQueryExpression(query.With(query.Plan with
        {
            Extremum = new(property.StoreProperty, modelProperty.IsNullable, modelType == typeof(int))
        }));
    }
}
