using Microsoft.EntityFrameworkCore.Metadata;

namespace GraphBridge.EntityFrameworkCore.Metadata;

/// <summary>A relationship whose EF identity is a type-wide application key, independent of its endpoints.</summary>
internal sealed class IdentifiedEdgeBinding(
    IEntityType entityType, string relationshipType, AssociationEndpointBinding source,
    AssociationEndpointBinding target, GraphPropertyBinding keyProperty,
    IReadOnlyList<GraphPropertyBinding> payloadProperties)
{
    public IEntityType EntityType { get; } = entityType;
    public string RelationshipType { get; } = relationshipType;
    public AssociationEndpointBinding Source { get; } = source;
    public AssociationEndpointBinding Target { get; } = target;
    public GraphPropertyBinding KeyProperty { get; } = keyProperty;
    public IReadOnlyList<GraphPropertyBinding> PayloadProperties { get; } = payloadProperties;
}
