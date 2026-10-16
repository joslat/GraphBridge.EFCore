using Microsoft.EntityFrameworkCore.Metadata;

namespace GraphBridge.EntityFrameworkCore.Metadata;

/// <summary>
/// One finalized EF node FK projected to a native principal-to-dependent relationship.
/// It is not a tracked join entity: EF tracks the two nodes and owns reference/collection fixup.
/// FK slots are derived from the principal's stored key, never stored on either the relationship or dependent node.
/// </summary>
internal sealed class NodeReferenceBinding
{
    internal NodeReferenceBinding(IForeignKey foreignKey, string relationshipType, NodeBinding principal, NodeBinding dependent,
        IEnumerable<GraphPropertyBinding> foreignKeyProperties, IEnumerable<GraphPropertyBinding> principalKeyProperties)
    {
        ForeignKey = foreignKey;
        RelationshipType = relationshipType;
        Principal = principal;
        Dependent = dependent;
        ForeignKeyProperties = Array.AsReadOnly(foreignKeyProperties.ToArray());
        PrincipalKeyProperties = Array.AsReadOnly(principalKeyProperties.ToArray());
    }
    public IForeignKey ForeignKey { get; }
    public string RelationshipType { get; }
    public NodeBinding Principal { get; }
    public NodeBinding Dependent { get; }
    /// <summary>Dependent EF slots in declared FK order; their Name is metadata, not a dependent-node storage location.</summary>
    public IReadOnlyList<GraphPropertyBinding> ForeignKeyProperties { get; }
    public IReadOnlyList<GraphPropertyBinding> PrincipalKeyProperties { get; }
}
