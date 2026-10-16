using System.Linq.Expressions;
using GraphBridge.EntityFrameworkCore.Metadata;

namespace GraphBridge.EntityFrameworkCore.Query;

internal enum Neo4jBulkValueKind { Constant, Copy, Add, Subtract }
internal sealed record Neo4jBulkAssignment(GraphReadProperty Target, Neo4jBulkValueKind Kind,
    GraphReadProperty? Source = null, string? Parameter = null);

internal sealed record Neo4jBulkTarget(NodeBinding? Node = null, NodeReferenceBinding? Reference = null,
    AssociationEndpointBinding? Source = null, AssociationEndpointBinding? Target = null,
    GraphPropertyBinding? RelationshipLock = null);

internal sealed class Neo4jBulkExpression(Neo4jQueryExpression source, Neo4jBulkTarget target,
    IReadOnlyList<Neo4jBulkAssignment>? assignments) : Expression
{
    internal const int MaxRows = 10_000;
    internal const string IdentityAlias = "__gb_bulk_id";
    public Neo4jQueryExpression Source { get; } = source;
    public Neo4jBulkTarget Target { get; } = target;
    public IReadOnlyList<NodeBinding> RequiredNodes { get; } = Array.AsReadOnly(source.Binding.RequiredUniqueEndpoints
        .Concat(target.Node is null ? [] : new[] { target.Node }).Distinct().ToArray());
    public IReadOnlyList<Neo4jBulkAssignment>? Assignments { get; } = assignments is null
        ? null : Array.AsReadOnly(assignments.ToArray());
    public override Type Type => typeof(int);
    public override ExpressionType NodeType => ExpressionType.Extension;
    protected override Expression VisitChildren(ExpressionVisitor visitor) => this;
}
