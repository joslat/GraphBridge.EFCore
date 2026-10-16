using GraphBridge.EntityFrameworkCore.Metadata;

namespace GraphBridge.EntityFrameworkCore.Paths;

public enum GraphTraversalDirection { Outgoing, Incoming, Both }

/// <summary>Explicit native graph metadata for an untracked traversal, not an EF entity mapping.</summary>
public sealed class GraphPathPattern
{
    public GraphPathPattern(string startLabel, IEnumerable<string> relationshipTypes)
    {
        StartLabel = GraphIdentifier.Validate(startLabel, nameof(startLabel));
        ArgumentNullException.ThrowIfNull(relationshipTypes);
        var types = new List<string>();
        foreach (var type in relationshipTypes)
        {
            if (types.Count == 16) throw new ArgumentException("Path patterns allow at most 16 relationship types.", nameof(relationshipTypes));
            types.Add(GraphIdentifier.Validate(type, nameof(relationshipTypes)));
        }
        if (types.Count == 0) throw new ArgumentException("At least one relationship type is required.", nameof(relationshipTypes));
        RelationshipTypes = Array.AsReadOnly(types.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
    }
    public string StartLabel { get; }
    public IReadOnlyList<string> RelationshipTypes { get; }
}

/// <summary>Ordered caller-projected values. StepDirections follows traversal order, not stored edge order.</summary>
public sealed record GraphPath<TNode, TRelationship>(IReadOnlyList<TNode> Nodes,
    IReadOnlyList<TRelationship> Relationships, IReadOnlyList<GraphTraversalDirection> StepDirections);

/// <summary>At most the requested number of paths. HasMorePaths means an extra path was observed in this query.</summary>
public sealed record GraphPathResults<TNode, TRelationship>(IReadOnlyList<GraphPath<TNode, TRelationship>> Paths,
    bool HasMorePaths, bool StartFound);
