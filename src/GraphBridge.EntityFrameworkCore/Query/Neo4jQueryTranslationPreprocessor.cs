using System.Linq.Expressions;
using GraphBridge.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;

namespace GraphBridge.EntityFrameworkCore.Query;

internal sealed class Neo4jQueryTranslationPreprocessorFactory(QueryTranslationPreprocessorDependencies dependencies)
    : IQueryTranslationPreprocessorFactory
{
    public QueryTranslationPreprocessor Create(QueryCompilationContext queryCompilationContext)
        => new Neo4jQueryTranslationPreprocessor(dependencies, queryCompilationContext);
}

internal sealed class Neo4jQueryTranslationPreprocessor(
    QueryTranslationPreprocessorDependencies dependencies, QueryCompilationContext queryCompilationContext)
    : QueryTranslationPreprocessor(dependencies, queryCompilationContext)
{
    public override Expression Process(Expression query)
    {
        INavigation? navigation = null;
        var cleaned = StripLinearInclude(query, ref navigation);
        // Preserve EF normalization, tracking metadata, query filters, navigation expansion, and
        // optimizations. Only the explicitly supported Include is removed before navigation expansion.
        var processed = base.Process(cleaned);
        return navigation is null ? processed : new Neo4jIncludeExpression(processed, navigation);
    }

    private Expression StripLinearInclude(Expression expression, ref INavigation? navigation)
    {
        if (expression is not MethodCallExpression call || call.Arguments.Count == 0)
            return expression;
        if (call.Method.DeclaringType != typeof(Queryable)
            && call.Method.DeclaringType != typeof(EntityFrameworkQueryableExtensions))
            return expression;
        if (call.Method.DeclaringType == typeof(EntityFrameworkQueryableExtensions))
        {
            if (call.Method.Name == nameof(EntityFrameworkQueryableExtensions.ThenInclude))
                throw Unsupported("nested Include");
            if (call.Method.Name == nameof(EntityFrameworkQueryableExtensions.Include))
            {
                if (navigation is not null) throw Unsupported("multiple Includes");
                var argument = call.Arguments[1];
                if (argument is UnaryExpression { NodeType: ExpressionType.Quote } quote) argument = quote.Operand;
                if (argument is not LambdaExpression { Parameters.Count: 1, Body: MemberExpression member } lambda
                    || member.Expression != lambda.Parameters[0])
                    throw Unsupported("filtered, nested, or string Include");
                var entity = QueryCompilationContext.Model.FindEntityType(lambda.Parameters[0].Type);
                navigation = entity?.FindNavigation(member.Member.Name)
                    ?? throw Unsupported("Include without a mapped ordinary navigation");
                return StripLinearInclude(call.Arguments[0], ref navigation);
            }
        }
        // Only walk the source of a linear query. Never hoist Includes out of predicates, selectors,
        // joins, or unrelated subqueries; the normal translator rejects their unsupported shapes.
        var source = StripLinearInclude(call.Arguments[0], ref navigation);
        if (source == call.Arguments[0]) return expression;
        var arguments = call.Arguments.ToArray();
        arguments[0] = source;
        return call.Update(call.Object, arguments);
    }

    private static Neo4jQueryTranslationException Unsupported(string feature)
        => Neo4jQueryRefusals.Create(feature, "Include supports one unfiltered direct native-reference navigation on an entity query.");
}
