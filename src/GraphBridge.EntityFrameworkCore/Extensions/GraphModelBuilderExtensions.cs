using GraphBridge.EntityFrameworkCore.Metadata;
using GraphBridge.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Microsoft.EntityFrameworkCore;

public static class GraphModelBuilderExtensions
{
    /// <summary>
    /// Declares a required or optional one-to-many node reference stored as a native relationship from principal to dependent.
    /// The FK is derived from the matched principal key, not copied to the dependent node. Configure Restrict or NoAction deletion.
    /// Root queries derive FK values from the native relationship; native persistence uses the dependent EF entry.
    /// Optional references support parent transitions; required FK reassignment and dependent-root aggregates remain unsupported.
    /// One direct typed Include is supported in either direction within the documented native-reference model restrictions.
    /// </summary>
    public static ReferenceCollectionBuilder ToGraphReference(this ReferenceCollectionBuilder builder, string relationshipType)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.HasAnnotation(GraphAnnotationNames.NodeReferenceType, GraphIdentifier.Validate(relationshipType, nameof(relationshipType)));
    }

    /// <inheritdoc cref="ToGraphReference(ReferenceCollectionBuilder, string)" />
    public static ReferenceCollectionBuilder<TPrincipal, TDependent> ToGraphReference<TPrincipal, TDependent>(
        this ReferenceCollectionBuilder<TPrincipal, TDependent> builder, string relationshipType)
        where TPrincipal : class where TDependent : class
    {
        ArgumentNullException.ThrowIfNull(builder);
        ToGraphReference((ReferenceCollectionBuilder)builder, relationshipType);
        return builder;
    }

    /// <summary>
    /// Maps an association join to a directed relationship from the source FK's principal to the target FK's principal.
    /// Each property array must identify one complete EF foreign key in its declared order.
    /// This metadata does not enable relationship queries or persistence until those provider capabilities are supported.
    /// </summary>
    public static EntityTypeBuilder ToGraphRelationship(this EntityTypeBuilder builder, string relationshipType,
        string[] sourceForeignKeyProperties, string[] targetForeignKeyProperties)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (builder.Metadata.FindAnnotation(GraphAnnotationNames.IdentifiedEdgeKey) is not null)
            throw new InvalidOperationException("Identified-edge and endpoint-pair association mappings are mutually exclusive.");
        var type = GraphIdentifier.Validate(relationshipType, nameof(relationshipType));
        var source = CopyRole(sourceForeignKeyProperties, nameof(sourceForeignKeyProperties));
        var target = CopyRole(targetForeignKeyProperties, nameof(targetForeignKeyProperties));
        builder.HasAnnotation(GraphAnnotationNames.RelationshipType, type);
        builder.HasAnnotation(GraphAnnotationNames.SourceForeignKeyProperties, source);
        builder.HasAnnotation(GraphAnnotationNames.TargetForeignKeyProperties, target);
        return builder;
    }

    /// <summary>
    /// Maps an entity to a directed relationship with a separate, single application primary key.
    /// Parallel edges are distinct by that key. Existing endpoint FKs cannot be rebound.
    /// </summary>
    public static EntityTypeBuilder ToGraphIdentifiedRelationship(this EntityTypeBuilder builder, string relationshipType,
        string keyProperty, string[] sourceForeignKeyProperties, string[] targetForeignKeyProperties)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyProperty);
        if (builder.Metadata.FindAnnotation(GraphAnnotationNames.RelationshipType) is not null
            && builder.Metadata.FindAnnotation(GraphAnnotationNames.IdentifiedEdgeKey) is null)
            throw new InvalidOperationException("Identified-edge and endpoint-pair association mappings are mutually exclusive.");
        builder.HasAnnotation(GraphAnnotationNames.RelationshipType, GraphIdentifier.Validate(relationshipType, nameof(relationshipType)));
        builder.HasAnnotation(GraphAnnotationNames.SourceForeignKeyProperties, CopyRole(sourceForeignKeyProperties, nameof(sourceForeignKeyProperties)));
        builder.HasAnnotation(GraphAnnotationNames.TargetForeignKeyProperties, CopyRole(targetForeignKeyProperties, nameof(targetForeignKeyProperties)));
        builder.HasAnnotation(GraphAnnotationNames.IdentifiedEdgeKey, keyProperty);
        return builder;
    }

    /// <inheritdoc cref="ToGraphIdentifiedRelationship(EntityTypeBuilder, string, string, string[], string[])" />
    public static EntityTypeBuilder<TEntity> ToGraphIdentifiedRelationship<TEntity>(this EntityTypeBuilder<TEntity> builder,
        string relationshipType, string keyProperty, string[] sourceForeignKeyProperties, string[] targetForeignKeyProperties)
        where TEntity : class
    {
        ToGraphIdentifiedRelationship((EntityTypeBuilder)builder, relationshipType, keyProperty,
            sourceForeignKeyProperties, targetForeignKeyProperties);
        return builder;
    }

    /// <inheritdoc cref="ToGraphRelationship(EntityTypeBuilder, string, string[], string[])" />
    public static EntityTypeBuilder<TEntity> ToGraphRelationship<TEntity>(this EntityTypeBuilder<TEntity> builder,
        string relationshipType, string[] sourceForeignKeyProperties, string[] targetForeignKeyProperties)
        where TEntity : class
    {
        ToGraphRelationship((EntityTypeBuilder)builder, relationshipType, sourceForeignKeyProperties, targetForeignKeyProperties);
        return builder;
    }

    private static IReadOnlyList<string> CopyRole(string[] properties, string argumentName)
    {
        ArgumentNullException.ThrowIfNull(properties, argumentName);
        if (properties.Length == 0 || properties.Any(string.IsNullOrWhiteSpace)
            || properties.Distinct(StringComparer.Ordinal).Count() != properties.Length)
            throw new ArgumentException("Endpoint role must contain distinct, nonempty EF foreign-key property names.", argumentName);
        return Array.AsReadOnly(properties.ToArray());
    }

    public static EntityTypeBuilder ToGraphNode(this EntityTypeBuilder builder, string label)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.HasAnnotation(GraphAnnotationNames.NodeLabel, GraphIdentifier.Validate(label, nameof(label)));
        return builder;
    }

    public static EntityTypeBuilder<TEntity> ToGraphNode<TEntity>(this EntityTypeBuilder<TEntity> builder, string label)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.HasAnnotation(GraphAnnotationNames.NodeLabel, GraphIdentifier.Validate(label, nameof(label)));
        return builder;
    }

    /// <summary>
    /// Further labels every node of the type carries besides its <c>ToGraphNode</c> label, such as neo4j-graphrag's
    /// <c>__Entity__</c> (ADR 0055). They are written on create and upsert, and every match of the type requires them.
    /// </summary>
    public static EntityTypeBuilder HasAdditionalGraphLabels(this EntityTypeBuilder builder, params string[] labels)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.HasAnnotation(GraphAnnotationNames.AdditionalNodeLabels, AdditionalLabels(labels));
        return builder;
    }

    /// <inheritdoc cref="HasAdditionalGraphLabels(EntityTypeBuilder, string[])"/>
    public static EntityTypeBuilder<TEntity> HasAdditionalGraphLabels<TEntity>(this EntityTypeBuilder<TEntity> builder, params string[] labels)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.HasAnnotation(GraphAnnotationNames.AdditionalNodeLabels, AdditionalLabels(labels));
        return builder;
    }

    private static string AdditionalLabels(string[] labels)
    {
        ArgumentNullException.ThrowIfNull(labels);
        if (labels.Length == 0) throw new ArgumentException("Name at least one additional label.", nameof(labels));
        foreach (var label in labels) GraphIdentifier.Validate(label, nameof(labels));
        if (labels.Distinct(StringComparer.Ordinal).Count() != labels.Length) throw new ArgumentException("Additional labels must be distinct.", nameof(labels));
        return string.Join(':', labels);
    }

    public static PropertyBuilder HasGraphPropertyName(this PropertyBuilder builder, string name)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.HasAnnotation(GraphAnnotationNames.PropertyName, GraphIdentifier.Validate(name, nameof(name)));
        return builder;
    }

    public static PropertyBuilder<TProperty> HasGraphPropertyName<TProperty>(this PropertyBuilder<TProperty> builder, string name)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.HasAnnotation(GraphAnnotationNames.PropertyName, GraphIdentifier.Validate(name, nameof(name)));
        return builder;
    }

    /// <summary>
    /// Maps a <c>float[]</c> or <c>ReadOnlyMemory&lt;float&gt;</c> property as an embedding with exactly <paramref name="dimensions"/> values,
    /// stored as a list of floats (ADR 0054). Writes refuse a vector of another length, with a NaN or infinite value, or all zeros.
    /// Queries can return it and test it for null; find similar entities with typed vector search.
    /// </summary>
    public static PropertyBuilder IsGraphVector(this PropertyBuilder builder, int dimensions)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (!Neo4jTypeMappingSource.IsVectorType(builder.Metadata.ClrType))
            throw new ArgumentException($"IsGraphVector maps float[] or ReadOnlyMemory<float> properties; '{builder.Metadata.Name}' is {builder.Metadata.ClrType.Name}.", nameof(builder));
        if (dimensions is < 1 or > 4096)
            throw new ArgumentOutOfRangeException(nameof(dimensions), "Vector dimensions must be 1 to 4,096.");
        builder.HasAnnotation(GraphAnnotationNames.VectorDimensions, dimensions);
        return builder;
    }

    /// <inheritdoc cref="IsGraphVector(PropertyBuilder, int)" />
    public static PropertyBuilder<TProperty> IsGraphVector<TProperty>(this PropertyBuilder<TProperty> builder, int dimensions)
    {
        IsGraphVector((PropertyBuilder)builder, dimensions);
        return builder;
    }
}
