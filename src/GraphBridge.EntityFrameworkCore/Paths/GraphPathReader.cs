using System.Collections;
using Neo4j.Driver;

namespace GraphBridge.EntityFrameworkCore.Paths;

internal static class GraphPathReader
{
    internal static GraphPathResults<TNode, TRelationship> Read<TNode, TRelationship>(IRecord row, GraphPathRequest request,
        Func<INode, TNode> nodeProjector, Func<IRelationship, TRelationship> relationshipProjector, CancellationToken token)
    {
        if (row["starts"] is not long count || count is < 0 or > 1)
            throw new InvalidOperationException("Path anchor must identify zero or one physical node; duplicate or invalid anchor diagnostic.");
        if (row["paths"] is not IEnumerable items || items is string)
            throw new InvalidOperationException("Path results must contain a bounded collection of native paths.");
        var paths = new List<(IPath Path, IReadOnlyList<GraphTraversalDirection> Directions)>();
        foreach (var item in items)
        {
            token.ThrowIfCancellationRequested();
            if (count == 0 || paths.Count == request.ResultLimit + 1 || item is not IPath path)
                throw new InvalidOperationException("Path results violate the anchor, shape or result bound.");
            paths.Add((path, Validate(path, request, token)));
        }
        // Validate every buffered path before invoking user projectors, including the extra truncation witness.
        var projected = new List<GraphPath<TNode, TRelationship>>();
        foreach (var (path, directions) in paths.Take(request.ResultLimit))
        {
            var nodes = new List<TNode>(); var relationships = new List<TRelationship>();
            foreach (var node in path.Nodes) { token.ThrowIfCancellationRequested(); nodes.Add(nodeProjector(node)); }
            foreach (var edge in path.Relationships) { token.ThrowIfCancellationRequested(); relationships.Add(relationshipProjector(edge)); }
            token.ThrowIfCancellationRequested();
            projected.Add(new(nodes.AsReadOnly(), relationships.AsReadOnly(), directions));
        }
        return new(projected.AsReadOnly(), paths.Count > request.ResultLimit, count == 1);
    }

    private static IReadOnlyList<GraphTraversalDirection> Validate(IPath path, GraphPathRequest request, CancellationToken token)
        => Validate(path, request.Pattern, request.Direction, request.MinDepth, request.MaxDepth, token);

    internal static IReadOnlyList<GraphTraversalDirection> Validate(IPath path, GraphPathPattern pattern,
        GraphTraversalDirection direction, int minDepth, int maxDepth, CancellationToken token)
    {
        var length = path.Relationships.Count;
        if (length < minDepth || length > maxDepth || path.Nodes.Count != length + 1)
            throw new InvalidOperationException("Native path has inconsistent shape or exceeds the requested depth bounds.");
        if (path.Nodes.Any(n => string.IsNullOrWhiteSpace(n.ElementId))
            || path.Start.ElementId != path.Nodes[0].ElementId || path.End.ElementId != path.Nodes[^1].ElementId
            || !path.Start.Labels.Contains(pattern.StartLabel, StringComparer.Ordinal))
            throw new InvalidOperationException("Native path endpoints do not match traversal order or the anchor label.");
        var directions = new List<GraphTraversalDirection>(); var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < length; i++)
        {
            token.ThrowIfCancellationRequested();
            var edge = path.Relationships[i]; var from = path.Nodes[i].ElementId; var to = path.Nodes[i + 1].ElementId;
            if (string.IsNullOrWhiteSpace(edge.ElementId) || !seen.Add(edge.ElementId)
                || !pattern.RelationshipTypes.Contains(edge.Type, StringComparer.Ordinal))
                throw new InvalidOperationException("Native path repeats a physical relationship or contains an unexpected relationship type.");
            var outgoing = edge.StartNodeElementId == from && edge.EndNodeElementId == to;
            var incoming = edge.EndNodeElementId == from && edge.StartNodeElementId == to;
            if (!(direction switch { GraphTraversalDirection.Outgoing => outgoing, GraphTraversalDirection.Incoming => incoming, _ => outgoing || incoming }))
                throw new InvalidOperationException("Native path relationships do not follow the requested direction or node sequence.");
            // A self-loop has both orientations; choose the requested direction, or outgoing for Both.
            directions.Add(direction == GraphTraversalDirection.Incoming || !outgoing ? GraphTraversalDirection.Incoming : GraphTraversalDirection.Outgoing);
        }
        return directions.AsReadOnly();
    }
}
