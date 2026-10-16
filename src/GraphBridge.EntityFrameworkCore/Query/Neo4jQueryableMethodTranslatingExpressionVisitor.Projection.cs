using System.Linq.Expressions;
using GraphBridge.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Storage;

namespace GraphBridge.EntityFrameworkCore.Query;

internal sealed partial class Neo4jQueryableMethodTranslatingExpressionVisitor
{
    // Only result construction is local. Selected values use existing store conversion
    // and EF slots; predicates/method calls never fall back to client execution.
    private sealed class ProjectionTranslator(ParameterExpression entity, GraphReadBinding binding,
        Expression entityShaper) : ExpressionVisitor
    {
        internal HashSet<GraphReadProperty> Properties { get; } = [];

        public override Expression? Visit(Expression? expression)
        {
            if (expression is null) return null;
            if (expression == entity)
            {
                Properties.UnionWith(binding.Properties);
                return entityShaper;
            }
            // Preserve explicit projection conversions in the shaper, rather than
            // unboxing an int directly as nullable/different primitive/enum storage.
            if (expression is UnaryExpression unary)
            {
                var source = Nullable.GetUnderlyingType(unary.Operand.Type) ?? unary.Operand.Type;
                var target = Nullable.GetUnderlyingType(unary.Type) ?? unary.Type;
                if (unary.Method is null && unary.NodeType is ExpressionType.Convert or ExpressionType.ConvertChecked
                    && (source == target || target == typeof(object)))
                    return unary.Update(Visit(unary.Operand)!);
                throw Unsupported("Projection conversion");
            }
            if (FindProperty(expression, entity, binding) is { } property)
            {
                Properties.Add(property);
                return Expression.Convert(Expression.MakeIndex(Neo4jQueryExpression.RowParameter,
                    typeof(ValueBuffer).GetProperty("Item")!, [Expression.Constant(property.Index)]), expression.Type);
            }
            return expression switch
            {
                NewExpression creation => base.VisitNew(creation),
                MemberInitExpression initialization => base.VisitMemberInit(initialization),
                ConstantExpression constant when constant.Value is null || Neo4jTypeMappingSource.IsSupportedModelScalar(constant.Type) => constant,
                // EF can parameterize an entire row-independent DTO constructor when
                // it contains captured values (ExpressionTreeFuncletizer). Read that
                // execution's parameter, never embed its object in the cached shaper
                // or send it to Neo4j. This does not admit row-dependent client code.
                QueryParameterExpression parameter
                    => Expression.Convert(Expression.Property(Expression.Property(QueryCompilationContext.QueryContextParameter,
                        nameof(QueryContext.Parameters)), "Item", Expression.Constant(parameter.Name)), parameter.Type),
                _ => throw Unsupported("Projection expression " + expression.NodeType)
            };
        }

        protected override MemberBinding VisitMemberBinding(MemberBinding binding)
            => binding is MemberAssignment ? base.VisitMemberBinding(binding)
                : throw Unsupported("Collection or nested member mutation in a projection initializer");
    }
}
