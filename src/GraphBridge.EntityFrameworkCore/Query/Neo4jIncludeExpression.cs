using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Metadata;

namespace GraphBridge.EntityFrameworkCore.Query;

/// <summary>One reviewed Include preserved across EF's ordinary preprocessing pipeline.</summary>
internal sealed class Neo4jIncludeExpression(Expression source, INavigation navigation) : Expression
{
    public Expression Source { get; } = source;
    public INavigation Navigation { get; } = navigation;
    public override ExpressionType NodeType => ExpressionType.Extension;
    public override Type Type => Source.Type;
    protected override Expression VisitChildren(ExpressionVisitor visitor)
    {
        var visited = visitor.Visit(Source);
        return visited == Source ? this : new Neo4jIncludeExpression(visited, Navigation);
    }
}
