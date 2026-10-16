using System.Linq.Expressions;
using GraphBridge.EntityFrameworkCore.Cypher;
using Microsoft.EntityFrameworkCore.Query;

namespace GraphBridge.EntityFrameworkCore.Query;

internal sealed partial class Neo4jQueryableMethodTranslatingExpressionVisitor
{
    private static CypherExpression TranslateObjectEquals(MethodCallExpression call,
        ParameterExpression entity, GraphReadBinding binding, List<Neo4jQueryParameter> parameters)
    {
        // EF 10 EntityFinder.BuildPredicate uses object.Equals for Guid keys. Keep EF's
        // funcletized key parameter; never evaluate a predicate or capture a key at compilation.
        var left = Unbox(call.Arguments[0]);
        var right = Unbox(call.Arguments[1]);
        var leftProperty = FindProperty(left, entity, binding);
        var rightProperty = FindProperty(right, entity, binding);
        if ((leftProperty is null) == (rightProperty is null))
            throw Unsupported("object.Equals requires one mapped scalar property and one value");
        var property = leftProperty ?? rightProperty!;
        var value = leftProperty is null ? left : right;
        var type = Nullable.GetUnderlyingType(property.ModelProperty.Property.ClrType)
            ?? property.ModelProperty.Property.ClrType;
        // Floating-point Equals (NaN), arrays and arbitrary CLR equality cannot be lowered
        // to Cypher equality. Boxing is removed only inside this exact value-equality method.
        if (!(type == typeof(Guid) || type == typeof(string) || type == typeof(int)
            || type == typeof(long) || type == typeof(bool) || type.IsEnum))
            throw Unsupported("object.Equals supports Guid, string, int, long, bool and enum properties");
        if (value is not (QueryParameterExpression or ConstantExpression)
            || value is ConstantExpression { Value: not null } constant && constant.Value.GetType() != type
            || value.Type != typeof(object) && (Nullable.GetUnderlyingType(value.Type) ?? value.Type) != type)
            throw Unsupported("object.Equals requires a value with the mapped property's CLR type");
        var parameter = AddParameter(value, property.ModelProperty, parameters);
        parameters[^1] = parameters[^1] with { ExactValueType = type };
        return new CypherBinary(property.StoreProperty, CypherOperator.Equal, parameter);

        static Expression Unbox(Expression expression)
            => expression is UnaryExpression { NodeType: ExpressionType.Convert, Type: { } target } conversion
                && target == typeof(object) && conversion.Method is null ? conversion.Operand : expression;
    }
}
