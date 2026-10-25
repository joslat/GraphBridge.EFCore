using GraphBridge.EntityFrameworkCore.Storage;
using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.ChangeTracking.Internal;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace GraphBridge.EntityFrameworkCore.Metadata;

internal static class GraphModelValidation
{
    public static void Validate(IModel model)
    {
        var labels = new HashSet<string>(StringComparer.Ordinal);
        var edgeOwners = new HashSet<(IEntityType Source, string Type, IEntityType Target)>();
        var identifiedTypes = model.GetEntityTypes().Where(IsIdentifiedEdge)
            .Select(e => e[GraphAnnotationNames.RelationshipType] as string).ToHashSet(StringComparer.Ordinal);
        var identifiedOwners = new HashSet<string>(StringComparer.Ordinal);
        var referenceTypes = new HashSet<string>(StringComparer.Ordinal);
        var entityEdgeTypes = model.GetEntityTypes().Where(IsAssociation)
            .Select(entity => entity[GraphAnnotationNames.RelationshipType] as string).ToHashSet(StringComparer.Ordinal);
        foreach (var entity in model.GetEntityTypes())
        {
            if (entity.FindAnnotation(GraphAnnotationNames.NodeReferenceType) is not null)
                Fail(entity, "ToGraphReference metadata belongs on an EF foreign key, not an entity type");
            if (entity.BaseType is not null || entity.GetDerivedTypes().Any())
                Fail(entity, "inheritance is unsupported");
            if (entity.IsOwned())
                Fail(entity, "owned entities are unsupported; use an explicit scalar converter");
            if (entity.GetComplexProperties().Any())
                Fail(entity, "complex properties are unsupported; use an explicit scalar converter");
            if (entity.FindPrimaryKey() is null)
                Fail(entity, "a stable application primary key is required; keyless entities are unsupported");

            if (IsAssociation(entity))
            {
                if (entity.GetForeignKeys().Any(IsNodeReference))
                    Fail(entity, "native node references cannot be configured on association or identified-edge endpoint foreign keys");
                if (entity.FindAnnotation(GraphAnnotationNames.NodeLabel) is not null)
                    Fail(entity, "node and association-edge storage are mutually exclusive");
                var (source, target, relationshipType) = ValidateAssociation(entity);
                if (IsIdentifiedEdge(entity))
                {
                    if (!identifiedOwners.Add(relationshipType))
                        Fail(entity, "an identified relationship type must have exactly one EF entity owner across all endpoint types");
                }
                else if (identifiedTypes.Contains(relationshipType))
                    Fail(entity, "a relationship type cannot be shared by identified-edge and endpoint-pair association mappings");
                else if (!edgeOwners.Add((source.PrincipalEntityType, relationshipType, target.PrincipalEntityType)))
                    Fail(entity, "the directed source node / relationship type / target node tuple is already mapped by another association entity");
            }
            else
            {
                ValidateNodeRelationships(entity);
                foreach (var reference in entity.GetForeignKeys().Where(IsNodeReference))
                {
                    var type = ValidateNodeReference(reference);
                    if (entityEdgeTypes.Contains(type) || !referenceTypes.Add(type))
                        Fail(entity, "a native node-reference relationship type must have one globally unique mapping and cannot be shared with association or identified-edge storage");
                }
                if (entity.IsPropertyBag)
                    Fail(entity, "property-bag entities are unsupported unless explicitly mapped as an association edge");
                if (entity.FindAnnotation(GraphAnnotationNames.SourceForeignKeyProperties) is not null
                    || entity.FindAnnotation(GraphAnnotationNames.TargetForeignKeyProperties) is not null
                    || entity.FindAnnotation(GraphAnnotationNames.IdentifiedEdgeKey) is not null)
                    Fail(entity, "endpoint roles require an explicit ToGraphRelationship mapping");
                var label = entity[GraphAnnotationNames.NodeLabel] as string;
                if (label is null)
                    Fail(entity, "an explicit ToGraphNode(label) mapping is required");
                GraphIdentifier.Validate(label, "label");
                if (!labels.Add(label))
                    Fail(entity, $"node label '{label}' is already mapped by another entity type");
            }

            var names = new HashSet<string>(StringComparer.Ordinal);
            var derivedReferenceSlots = entity.GetForeignKeys().Where(IsNodeReference).SelectMany(fk => fk.Properties).ToHashSet();
            foreach (var property in entity.GetProperties())
            {
                var generatedVersion = property.IsConcurrencyToken && property.ValueGenerated == ValueGenerated.OnAddOrUpdate;
                if (!generatedVersion && (property.GetBeforeSaveBehavior() == PropertySaveBehavior.Ignore
                    || property.GetAfterSaveBehavior() == PropertySaveBehavior.Ignore))
                    Fail(entity, $"property '{property.Name}' uses an unsupported Ignore save behavior; only provider-generated concurrency versions support ignored writes");
                var name = property[GraphAnnotationNames.PropertyName] as string ?? property.Name;
                GraphIdentifier.Validate(name, "propertyName");
                if (!derivedReferenceSlots.Contains(property) && !names.Add(name))
                    Fail(entity, $"store property name '{name}' is mapped more than once");
                var vector = property.FindAnnotation(GraphAnnotationNames.VectorDimensions)?.Value;
                if (vector is not null)
                {
                    // ADR 0054: an opted-in embedding is payload only, stored as a list of floats without a converter.
                    if (vector is not int dimensions || dimensions is < 1 or > 4096 || !Neo4jTypeMappingSource.IsVectorType(property.ClrType))
                        Fail(entity, $"vector property '{property.Name}' must be float[] or ReadOnlyMemory<float> with 1 to 4,096 dimensions");
                    if (property.IsKey() || property.IsForeignKey() || property.IsConcurrencyToken || property.ValueGenerated != ValueGenerated.Never)
                        Fail(entity, $"vector property '{property.Name}' is payload only; it can't be a key, a foreign key, a concurrency token or a generated value");
                    if (property.GetValueConverter() is not null || property.GetProviderClrType() is not null)
                        Fail(entity, $"vector property '{property.Name}' is stored as a list of floats and can't also have a value converter");
                    continue;
                }
                if (!Neo4jTypeMappingSource.IsSupportedModelScalar(property.ClrType)
                    && property.GetValueConverter() is null && property.GetProviderClrType() is null)
                    Fail(entity, $"property '{property.Name}' of CLR type '{property.ClrType.Name}' requires an explicit HasConversion mapping");

                var mapping = property.FindTypeMapping();
                if (mapping is not null)
                {
                    if (mapping.Converter?.ConvertsNulls == true)
                        Fail(entity, $"property '{property.Name}' uses a converter with ConvertsNulls=true; null-converting mappings are unsupported");
                    // EF 10.0.12 Property/RuntimeProperty resolve the normal key comparer from this
                    // same finalized mapping. A property-level override can collapse distinct wire
                    // identities (for example case-insensitive string keys), which Cypher does not.
                    if ((property.IsPrimaryKey() || property.IsForeignKey())
                        && !UsesMappingKeyComparer(property, mapping.KeyComparer))
                        Fail(entity, $"identity property '{property.Name}' uses a custom key comparer; graph identity requires the resolved type-mapping key comparer");
                    var storeType = mapping.Converter?.ProviderClrType ?? mapping.ClrType;
                    if (storeType == typeof(DateTimeOffset)
                        && (property.IsKey() || property.IsForeignKey() || property.IsConcurrencyToken))
                        Fail(entity, $"native temporal property '{property.Name}' is payload-only; keys, foreign keys and concurrency tokens require an explicit non-temporal representation");
                    if (!Neo4jTypeMappingSource.IsNativeScalar(storeType))
                        Fail(entity, $"property '{property.Name}' converts to unsupported store type '{storeType.Name}'");
                }
            }
        }
        ValidateReferenceCycles(model);
    }

    // EF wraps a non-nullable mapping comparer for nullable FK slots. Accept only
    // that exact wrapper over the same mapping comparer, never arbitrary wrappers
    // or subclasses whose equality could collapse distinct graph identities.
#pragma warning disable EF1001 // Pinned EF nullable comparer wrapper; audited in EF_SERVICE_MAP.md.
    private static bool UsesMappingKeyComparer(IProperty property, ValueComparer mappingComparer)
    {
        var comparer = property.GetKeyValueComparer();
        if (ReferenceEquals(comparer, mappingComparer)) return true;
        return Nullable.GetUnderlyingType(property.ClrType) is not null
            && comparer is IInfrastructure<ValueComparer> wrapper
            && comparer.GetType().IsGenericType
            && comparer.GetType().GetGenericTypeDefinition() == typeof(NullableValueComparer<>)
            && ReferenceEquals(wrapper.Instance, mappingComparer);
    }
#pragma warning restore EF1001

    internal static bool IsAssociation(IEntityType entity)
        => entity.FindAnnotation(GraphAnnotationNames.RelationshipType) is not null;

    internal static bool IsIdentifiedEdge(IEntityType entity)
        => entity.FindAnnotation(GraphAnnotationNames.IdentifiedEdgeKey) is not null;

    internal static bool IsNodeReference(IForeignKey foreignKey)
        => foreignKey.FindAnnotation(GraphAnnotationNames.NodeReferenceType) is not null;

    internal static IForeignKey ResolveRole(IEntityType entity, string annotation)
    {
        var names = entity[annotation] as IReadOnlyList<string>;
        if (names is null || names.Count == 0
            || names.Any(string.IsNullOrWhiteSpace) || names.Distinct(StringComparer.Ordinal).Count() != names.Count)
            Fail(entity, $"'{annotation}' must identify one complete foreign key by distinct property names in declared order");
        var matches = entity.GetForeignKeys().Where(fk => fk.Properties.Select(p => p.Name).SequenceEqual(names)).ToArray();
        if (matches.Length != 1)
            Fail(entity, $"'{annotation}' must identify exactly one complete foreign key in declared property order");
        return matches[0];
    }

    private static (IForeignKey Source, IForeignKey Target, string Type) ValidateAssociation(IEntityType entity)
    {
        var relationshipType = entity[GraphAnnotationNames.RelationshipType] as string;
        if (relationshipType is null)
            Fail(entity, "ToGraphRelationship requires a relationship type identifier");
        GraphIdentifier.Validate(relationshipType, "relationshipType");
        if (entity.GetForeignKeys().Count() != 2)
            Fail(entity, "association storage requires exactly two explicitly selected endpoint foreign keys");
        var source = ResolveRole(entity, GraphAnnotationNames.SourceForeignKeyProperties);
        var target = ResolveRole(entity, GraphAnnotationNames.TargetForeignKeyProperties);
        if (source == target || source.Properties.Intersect(target.Properties).Any())
            Fail(entity, "source and target endpoint foreign-key property slots must be disjoint");
        var endpointProperties = source.Properties.Concat(target.Properties).ToHashSet();
        var keyProperties = entity.FindPrimaryKey()!.Properties;
        if (IsIdentifiedEdge(entity))
        {
            if (entity[GraphAnnotationNames.IdentifiedEdgeKey] is not string keyName || string.IsNullOrWhiteSpace(keyName)
                || keyProperties.Count != 1 || keyProperties[0].Name != keyName || endpointProperties.Contains(keyProperties[0]))
                Fail(entity, "identified-edge mapping requires exactly one explicit application primary-key property separate from all endpoint foreign keys");
            var id = keyProperties[0];
            if (id.IsNullable || id.ValueGenerated == ValueGenerated.OnAddOrUpdate || id.IsConcurrencyToken)
                Fail(entity, "identified-edge application key must be required and stable, not a generated concurrency version");
            if (id.ClrType != typeof(Guid) && id.ClrType != typeof(string)
                && id.ClrType != typeof(int) && id.ClrType != typeof(long))
                Fail(entity, "identified-edge application keys currently require Guid, string, int or long CLR identity");
            if (entity.Model.GetEntityTypes().SelectMany(e => e.GetSkipNavigations()).Any(s => s.JoinEntityType == entity))
                Fail(entity, "identified edges cannot back skip navigations; use explicit edge reference and inverse collection navigations for parallel edges");
        }
        else if (keyProperties.Count != endpointProperties.Count || !endpointProperties.SetEquals(keyProperties))
            Fail(entity, "association primary key must be exactly the union of source and target foreign-key slots; independently identified edges are unsupported");
        if (entity.GetKeys().Any(key => key != entity.FindPrimaryKey()))
            Fail(entity, "association alternate keys are unsupported");
        if (entity.GetReferencingForeignKeys().Any() || entity.GetSkipNavigations().Any())
            Fail(entity, "an association edge cannot be another relationship's endpoint or own skip navigations");
        foreach (var navigation in entity.GetNavigations())
            if (!navigation.IsOnDependent || (navigation.ForeignKey != source && navigation.ForeignKey != target))
                Fail(entity, "only association-to-endpoint reference navigations are supported on an association");
        foreach (var fk in new[] { source, target })
        {
            if (fk.IsOwnership || fk.IsUnique)
                Fail(entity, "association endpoints require non-owning many-to-one foreign keys");
            if (!fk.IsRequired || fk.Properties.Any(p => p.IsNullable))
                Fail(entity, "association endpoint foreign keys must be required and nonnullable");
            if (IsAssociation(fk.PrincipalEntityType) || fk.PrincipalEntityType[GraphAnnotationNames.NodeLabel] is not string)
                Fail(entity, "association endpoints must reference explicitly mapped graph nodes");
            if (fk.PrincipalKey != fk.PrincipalEntityType.FindPrimaryKey())
                Fail(entity, "association endpoints must reference the node primary key; alternate-key endpoints are unsupported");
            if (fk.Properties.Any(p => p.ValueGenerated != ValueGenerated.Never))
                Fail(entity, "association endpoint foreign keys cannot have independently generated values");
            for (var index = 0; index < fk.Properties.Count; index++)
                ValidateEndpointConversion(entity, fk.Properties[index], fk.PrincipalKey.Properties[index]);
        }
        return (source, target, relationshipType);
    }

    private static void ValidateNodeRelationships(IEntityType entity)
    {
        if (entity.GetForeignKeys().Any(fk => !IsNodeReference(fk)))
            Fail(entity, "relationship storage requires explicit ToGraphReference metadata for node foreign keys or an explicitly mapped association join");
        if (entity.GetForeignKeys().Count(IsNodeReference) > 1)
            Fail(entity, "only one native node-reference foreign key per dependent node is currently supported");
        foreach (var navigation in entity.GetNavigations())
            if (!IsNodeReference(navigation.ForeignKey)
                && (navigation.IsOnDependent || !IsAssociation(navigation.ForeignKey.DeclaringEntityType)))
                Fail(entity, "relationship storage is unsupported until an explicit association mapping is configured");
        foreach (var skip in entity.GetSkipNavigations())
        {
            if (!IsAssociation(skip.JoinEntityType))
                Fail(entity, "relationship storage is unsupported until an explicit association mapping is configured");
            var source = ResolveRole(skip.JoinEntityType, GraphAnnotationNames.SourceForeignKeyProperties);
            var target = ResolveRole(skip.JoinEntityType, GraphAnnotationNames.TargetForeignKeyProperties);
            if (skip.Inverse is null || skip.Inverse.JoinEntityType != skip.JoinEntityType
                || !((skip.ForeignKey == source && skip.Inverse.ForeignKey == target)
                    || (skip.ForeignKey == target && skip.Inverse.ForeignKey == source)))
                Fail(entity, "skip navigations must use the two explicitly selected association endpoint roles");
        }
    }

    private static string ValidateNodeReference(IForeignKey foreignKey)
    {
        var entity = foreignKey.DeclaringEntityType;
        var type = foreignKey[GraphAnnotationNames.NodeReferenceType] as string;
        if (type is null)
            Fail(entity, "ToGraphReference requires a relationship type identifier");
        GraphIdentifier.Validate(type, "relationshipType");
        if (foreignKey.IsUnique || foreignKey.IsOwnership)
            Fail(entity, "native node references require non-owning many-to-one foreign keys");
        if (foreignKey.IsRequired ? foreignKey.Properties.Any(property => property.IsNullable)
            : foreignKey.Properties.Any(property => !property.IsNullable))
            Fail(entity, "native node-reference FK slots must be all nonnullable for required references or all nullable for optional references");
        if (foreignKey.DeleteBehavior is not (DeleteBehavior.Restrict or DeleteBehavior.NoAction))
            Fail(entity, "native node references require explicit Restrict or NoAction deletion; database/client cascade and null-setting policies are unsupported");
        if (IsAssociation(foreignKey.PrincipalEntityType)
            || foreignKey.PrincipalEntityType[GraphAnnotationNames.NodeLabel] is not string)
            Fail(entity, "native node-reference principals must be explicitly mapped graph nodes");
        if (foreignKey.PrincipalKey != foreignKey.PrincipalEntityType.FindPrimaryKey())
            Fail(entity, "native node references must target the principal primary key; alternate-key references are unsupported");
        if (foreignKey.Properties.Any(property => property.IsKey() || property.IsConcurrencyToken
            || property.ValueGenerated != ValueGenerated.Never))
            Fail(entity, "native node-reference FK slots must be stable non-generated properties disjoint from dependent keys and concurrency tokens");
        if (foreignKey.Properties.Any(property => property.FindAnnotation(GraphAnnotationNames.PropertyName) is not null))
            Fail(entity, "native node-reference FK slots are derived from principal keys and cannot declare a stored HasGraphPropertyName mapping");
        if (foreignKey.PrincipalKey.Properties.Any(property => property.IsConcurrencyToken
            || property.ValueGenerated is not (ValueGenerated.Never or ValueGenerated.OnAdd)))
            Fail(entity, "native node-reference principal keys must be stable and cannot be concurrency versions");
        if (entity.GetForeignKeys().Where(other => other != foreignKey)
            .Any(other => other.Properties.Intersect(foreignKey.Properties).Any()))
            Fail(entity, "native node-reference FK slots must not overlap other foreign keys");
        for (var index = 0; index < foreignKey.Properties.Count; index++)
            ValidateEndpointConversion(entity, foreignKey.Properties[index], foreignKey.PrincipalKey.Properties[index], allowNullableDependent: true);
        return type;
    }

    private static void ValidateReferenceCycles(IModel model)
    {
        // One dependency per node makes each walk linear. Completed walks are memoized; no recursive stack is needed.
        var completed = new HashSet<IEntityType>();
        foreach (var start in model.GetEntityTypes())
        {
            var path = new HashSet<IEntityType>();
            var current = start;
            while (!completed.Contains(current))
            {
                if (!path.Add(current)) Fail(start, "cyclic native node references, including self references, are unsupported");
                var next = current.GetForeignKeys().SingleOrDefault(IsNodeReference);
                if (next is null) break;
                current = next.PrincipalEntityType;
            }
            completed.UnionWith(path);
        }
    }

    private static void ValidateEndpointConversion(IEntityType entity, IProperty dependent, IProperty principal, bool allowNullableDependent = false)
    {
        var dependentMapping = dependent.GetTypeMapping();
        var principalMapping = principal.GetTypeMapping();
        var dependentConverter = dependentMapping.Converter;
        var principalConverter = principalMapping.Converter;
        var dependentWire = dependentConverter?.ProviderClrType ?? dependentMapping.ClrType;
        var principalWire = principalConverter?.ProviderClrType ?? principalMapping.ClrType;
        // int and long both use the driver's Int64 wire representation, with checked conversion on reads.
        if (dependentWire == typeof(int)) dependentWire = typeof(long);
        if (principalWire == typeof(int)) principalWire = typeof(long);
        var dependentType = allowNullableDependent ? Nullable.GetUnderlyingType(dependent.ClrType) ?? dependent.ClrType : dependent.ClrType;
        if (dependentType != principal.ClrType || dependentWire != principalWire)
            Fail(entity, $"endpoint property '{dependent.Name}' has an incompatible model or wire type for principal key '{principal.Name}'");
        if (!ReferenceEquals(dependentConverter, principalConverter))
            Fail(entity, $"endpoint property '{dependent.Name}' and principal key '{principal.Name}' require the same resolved converter instance; reuse one ValueConverter instance for matching endpoint wire semantics");
    }

    [DoesNotReturn]
    private static void Fail(IEntityType entity, string message)
        => throw new InvalidOperationException($"GraphBridge model '{entity.DisplayName()}': {message}.");
}
