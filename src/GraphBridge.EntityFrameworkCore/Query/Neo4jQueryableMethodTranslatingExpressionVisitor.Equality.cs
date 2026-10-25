using System.Linq.Expressions;
using GraphBridge.EntityFrameworkCore.Cypher;
using Microsoft.EntityFrameworkCore.Query;

namespace GraphBridge.EntityFrameworkCore.Query;

internal sealed partial class Neo4jQueryableMethodTranslatingExpressionVisitor
{
    // EF 10 EntityFinder.BuildPredicate uses object.Equals for Guid keys. Keep EF's
    // funcletized key parameter; never evaluate a predicate or capture a key at compilation.
    private static CypherExpression TranslateObjectEquals(MethodCallExpression call,
        ParameterExpression entity, GraphReadBinding binding, List<Neo4jQueryParameter> parameters)
        => TranslateValueEquals("object.Equals", call.Arguments[0], call.Arguments[1], entity, binding, parameters);

    // PL-45: x.Equals(y) reaches the provider untouched (EF 10 doesn't normalize it), as string.Equals(string),
    // Guid.Equals(Guid), int.Equals(object), Enum.Equals(object) and the like. Overloads that take another
    // parameter type, such as a comparison, are not value equality and stay refused.
    private static bool IsInstanceEquals(MethodCallExpression call)
        => call is { Object: { } instance, Method: { Name: nameof(object.Equals), IsStatic: false } method, Arguments: [_] }
            && method.ReturnType == typeof(bool)
            && method.GetParameters()[0].ParameterType is var parameterType
            && (parameterType == typeof(object) || parameterType == instance.Type);

    private static CypherExpression TranslateInstanceEquals(MethodCallExpression call,
        ParameterExpression entity, GraphReadBinding binding, List<Neo4jQueryParameter> parameters)
        => TranslateValueEquals("x.Equals(y)", call.Object!, call.Arguments[0], entity, binding, parameters);

    private static CypherExpression TranslateValueEquals(string method, Expression leftOperand, Expression rightOperand,
        ParameterExpression entity, GraphReadBinding binding, List<Neo4jQueryParameter> parameters)
    {
        var left = Unbox(leftOperand);
        var right = Unbox(rightOperand);
        var leftProperty = FindProperty(left, entity, binding);
        var rightProperty = FindProperty(right, entity, binding);
        if ((leftProperty is null) == (rightProperty is null))
            throw Unsupported($"{method} requires one mapped scalar property and one value");
        if (IsVector(leftProperty) || IsVector(rightProperty))
            throw RefuseVector("Predicate on a vector property other than a null test");
        var property = leftProperty ?? rightProperty!;
        var value = leftProperty is null ? left : right;
        var type = Nullable.GetUnderlyingType(property.ModelProperty.Property.ClrType)
            ?? property.ModelProperty.Property.ClrType;
        // Floating-point Equals (NaN), arrays and arbitrary CLR equality cannot be lowered
        // to Cypher equality. Boxing is removed only inside these value-equality methods.
        if (!(type == typeof(Guid) || type == typeof(string) || type == typeof(int)
            || type == typeof(long) || type == typeof(bool) || type.IsEnum))
            throw Unsupported($"{method} supports Guid, string, int, long, bool and enum properties");
        if (value is not (QueryParameterExpression or ConstantExpression)
            || value is ConstantExpression { Value: not null } constant && constant.Value.GetType() != type
            || value.Type != typeof(object) && (Nullable.GetUnderlyingType(value.Type) ?? value.Type) != type)
            throw Unsupported($"{method} requires a value with the mapped property's CLR type");
        var parameter = AddParameter(value, property.ModelProperty, parameters);
        parameters[^1] = parameters[^1] with { ExactValueType = type };
        return new CypherBinary(property.StoreProperty, CypherOperator.Equal, parameter);

        // An enum's Equals is declared on System.Enum, so its receiver may arrive boxed to Enum or ValueType.
        static Expression Unbox(Expression expression)
            => expression is UnaryExpression { NodeType: ExpressionType.Convert, Type: { } target } conversion
                && (target == typeof(object) || target == typeof(Enum) || target == typeof(ValueType))
                && conversion.Method is null ? conversion.Operand : expression;
    }
}
