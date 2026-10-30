namespace GraphBridge.EntityFrameworkCore.Metadata;

public static class GraphAnnotationNames
{
    public const string NodeLabel = "GraphBridge:NodeLabel";
    /// <summary>Further labels every node of the type carries, as one string with the labels separated by colons (ADR 0055).</summary>
    public const string AdditionalNodeLabels = "GraphBridge:AdditionalNodeLabels";
    public const string PropertyName = "GraphBridge:PropertyName";
    public const string RelationshipType = "GraphBridge:RelationshipType";
    public const string SourceForeignKeyProperties = "GraphBridge:SourceForeignKeyProperties";
    public const string TargetForeignKeyProperties = "GraphBridge:TargetForeignKeyProperties";
    public const string IdentifiedEdgeKey = "GraphBridge:IdentifiedEdgeKey";
    public const string NodeReferenceType = "GraphBridge:NodeReferenceType";
    public const string VectorDimensions = "GraphBridge:VectorDimensions";
}
