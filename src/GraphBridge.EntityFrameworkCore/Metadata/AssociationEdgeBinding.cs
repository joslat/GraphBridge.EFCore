using Microsoft.EntityFrameworkCore.Metadata;

namespace GraphBridge.EntityFrameworkCore.Metadata;

internal enum GraphStorageKind { Node, AssociationEdge, IdentifiedEdge }

/// <summary>Immutable association storage projection; the directed edge runs from Source to Target.</summary>
internal sealed class AssociationEdgeBinding
{
    internal AssociationEdgeBinding(IEntityType entityType, string relationshipType,
        AssociationEndpointBinding source, AssociationEndpointBinding target,
        IReadOnlyList<IProperty> keyProperties, IReadOnlyList<GraphPropertyBinding> payloadProperties)
        => (EntityType, RelationshipType, Source, Target, KeyProperties, PayloadProperties)
            = (entityType, relationshipType, source, target, keyProperties, payloadProperties);

    public IEntityType EntityType { get; }
    public string RelationshipType { get; }
    public AssociationEndpointBinding Source { get; }
    public AssociationEndpointBinding Target { get; }
    public IReadOnlyList<IProperty> KeyProperties { get; }
    public IReadOnlyList<GraphPropertyBinding> PayloadProperties { get; }
}

internal sealed class AssociationEndpointBinding
{
    internal AssociationEndpointBinding(IForeignKey foreignKey, NodeBinding node,
        IReadOnlyList<GraphPropertyBinding> foreignKeyProperties, IReadOnlyList<GraphPropertyBinding> principalKeyProperties)
        => (ForeignKey, Node, ForeignKeyProperties, PrincipalKeyProperties)
            = (foreignKey, node, foreignKeyProperties, principalKeyProperties);

    public IForeignKey ForeignKey { get; }
    public NodeBinding Node { get; }
    public IReadOnlyList<GraphPropertyBinding> ForeignKeyProperties { get; }
    public IReadOnlyList<GraphPropertyBinding> PrincipalKeyProperties { get; }
}
