using System.Linq.Expressions;
using GraphBridge.EntityFrameworkCore.Cypher;
using GraphBridge.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Storage;

namespace GraphBridge.EntityFrameworkCore.Query;

internal sealed class Neo4jShapedQueryCompilingExpressionVisitorFactory(
    ShapedQueryCompilingExpressionVisitorDependencies dependencies) : IShapedQueryCompilingExpressionVisitorFactory
{
    public ShapedQueryCompilingExpressionVisitor Create(QueryCompilationContext queryCompilationContext)
        => new Neo4jShapedQueryCompilingExpressionVisitor(dependencies, queryCompilationContext);
}

internal sealed class Neo4jShapedQueryCompilingExpressionVisitor(
    ShapedQueryCompilingExpressionVisitorDependencies dependencies,
    QueryCompilationContext queryCompilationContext)
    : ShapedQueryCompilingExpressionVisitor(dependencies, queryCompilationContext)
{
    protected override Expression VisitExtension(Expression extensionExpression)
        => extensionExpression is Neo4jBulkExpression bulk
            ? Expression.Call(typeof(Neo4jBulkExecutor).GetMethod(nameof(Neo4jBulkExecutor.ExecuteAsync))!,
                Expression.Convert(QueryCompilationContext.QueryContextParameter, typeof(Neo4jQueryContext)),
                Expression.Constant(bulk))
            : base.VisitExtension(extensionExpression);

    protected override Expression VisitShapedQuery(ShapedQueryExpression shapedQueryExpression)
    {
        if (shapedQueryExpression.QueryExpression is not Neo4jQueryExpression query)
            throw Neo4jQueryRefusals.Create("a query root that is not an explicitly mapped node or relationship entity",
                "Query a DbSet of a mapped node or relationship entity, or use a reviewed native query.");

        var body = InjectStructuralTypeMaterializers(shapedQueryExpression.ShaperExpression);
        var delegateType = typeof(Func<,,>).MakeGenericType(typeof(QueryContext), typeof(ValueBuffer), body.Type);
        var shaper = Expression.Lambda(delegateType, body,
            QueryCompilationContext.QueryContextParameter, Neo4jQueryExpression.RowParameter).Compile();
        Func<QueryContext, ValueBuffer, object>? includedShaper = null;
        if (query.Include is { } include)
        {
            var includedBody = InjectStructuralTypeMaterializers(new StructuralTypeShaperExpression(
                include.Included.EntityType, Neo4jQueryExpression.RowParameter, nullable: false));
            includedShaper = Expression.Lambda<Func<QueryContext, ValueBuffer, object>>(
                Expression.Convert(includedBody, typeof(object)), QueryCompilationContext.QueryContextParameter,
                Neo4jQueryExpression.RowParameter).Compile();
        }
        var enumerableType = typeof(Neo4jQueryingEnumerable<>).MakeGenericType(body.Type);
        // Store immutable command text inside EF's compiled delegate, not in a second cache.
        // Values still resolve from the current QueryContext for every enumeration.
        var command = query.Include is { } commandInclude
            ? CypherIncludeRenderer.Render(commandInclude.CreatePlan(query.Plan))
            : CypherQueryRenderer.Render(query.Plan);
        return Expression.New(
            enumerableType.GetConstructors().Single(),
            Expression.Convert(QueryCompilationContext.QueryContextParameter, typeof(Neo4jQueryContext)),
            Expression.Constant(query),
            Expression.Constant(command),
            Expression.Constant(shaper, delegateType),
            Expression.Constant(QueryCompilationContext.QueryTrackingBehavior == QueryTrackingBehavior.NoTrackingWithIdentityResolution),
            Expression.Constant(includedShaper, typeof(Func<QueryContext, ValueBuffer, object>)),
            Expression.Constant(QueryCompilationContext.QueryTrackingBehavior));
    }
}
