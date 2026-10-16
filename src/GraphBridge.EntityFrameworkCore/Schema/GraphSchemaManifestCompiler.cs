using GraphBridge.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata;

namespace GraphBridge.EntityFrameworkCore.Schema;

internal static class GraphSchemaManifestCompiler
{
    internal static GraphSchemaManifest Create(IModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        // EF Core 10.0.12 IModel.GetModelDependencies validates initialized/finalized model availability.
        // Existing GraphBindingCompiler remains the storage authority and performs mapping validation.
        _ = model.GetModelDependencies();
        var compiler = new GraphBindingCompiler();
        var nodes = new List<GraphSchemaNode>();
        var relationships = new List<GraphSchemaRelationship>();
        var caveats = new List<string>();
        foreach (var entity in model.GetEntityTypes())
        {
            switch (compiler.GetStorageKind(entity))
            {
                case GraphStorageKind.Node:
                    var node = compiler.GetNode(entity);
                    nodes.Add(new GraphSchemaNode(node.Label,
                        node.KeyProperties.Select(k => node.Properties.Single(p => p.Property == k).Name),
                        node.StoredProperties.Select(p => Property(p, "node:" + node.Label, caveats)),
                        Indexes(entity, node.StoredProperties, "node:" + node.Label, caveats, derivedNodeSlots: true),
                        entity.GetKeys().Where(key => key != entity.FindPrimaryKey()).Select(key =>
                            new GraphSchemaIndex(key.Properties.Select(p => node.Properties.Single(b => b.Property == p).Name), true))));
                    foreach (var reference in compiler.GetNodeReferences(entity))
                    {
                        var deletion = reference.ForeignKey.DeleteBehavior.ToString();
                        relationships.Add(new GraphSchemaRelationship(reference.RelationshipType, GraphSchemaRelationshipIdentity.DependentNode,
                            null, new GraphSchemaEndpoint(reference.Principal.Label, reference.PrincipalKeyProperties.Select(p => p.Name), deletion),
                            new GraphSchemaEndpoint(node.Label, node.KeyProperties.Select(key => node.StoredProperties.Single(p => p.Property == key).Name), deletion),
                            [], [], reference.ForeignKeyProperties.Select((slot, index) =>
                                new GraphSchemaDerivedForeignKey(slot.Name, reference.PrincipalKeyProperties[index].Name)), reference.ForeignKey.IsRequired));
                        caveats.Add($"relationship:{reference.RelationshipType}: each dependent node owns {(reference.ForeignKey.IsRequired ? "one required" : "zero or one optional")} incoming relationship; endpoint key uniqueness does not enforce link cardinality or endpoint labels.");
                        caveats.Add($"relationship:{reference.RelationshipType}: derived FK slots are review metadata, not stored properties; the fingerprint covers ordered principal storage keys, not logical/CLR FK slot bindings.");
                    }
                    break;
                case GraphStorageKind.AssociationEdge:
                    var association = compiler.GetAssociation(entity);
                    relationships.Add(Relationship(association.RelationshipType, GraphSchemaRelationshipIdentity.EndpointPair,
                        null, association.Source, association.Target, association.PayloadProperties, caveats));
                    break;
                case GraphStorageKind.IdentifiedEdge:
                    var edge = compiler.GetIdentifiedEdge(entity);
                    relationships.Add(Relationship(edge.RelationshipType, GraphSchemaRelationshipIdentity.ApplicationId,
                        edge.KeyProperty.Name, edge.Source, edge.Target,
                        new[] { edge.KeyProperty }.Concat(edge.PayloadProperties).ToArray(), caveats));
                    break;
                default:
                    throw new InvalidOperationException("Unknown GraphBridge storage kind.");
            }
        }
        return new GraphSchemaManifest(nodes, relationships, caveats);
    }

    private static GraphSchemaRelationship Relationship(string type, GraphSchemaRelationshipIdentity identity,
        string? keyProperty, AssociationEndpointBinding source, AssociationEndpointBinding target,
        IReadOnlyList<GraphPropertyBinding> storedProperties, List<string> caveats)
        => new(type, identity, keyProperty, Endpoint(source), Endpoint(target),
            storedProperties.Select(p => Property(p, "relationship:" + type, caveats)),
            Indexes(source.ForeignKey.DeclaringEntityType, storedProperties, "relationship:" + type, caveats));

    private static GraphSchemaEndpoint Endpoint(AssociationEndpointBinding endpoint)
        => new(endpoint.Node.Label, endpoint.PrincipalKeyProperties.Select(p => p.Name), endpoint.ForeignKey.DeleteBehavior.ToString());

    private static GraphSchemaProperty Property(GraphPropertyBinding binding, string owner, List<string> caveats)
    {
        var property = binding.Property;
        var mapping = property.GetTypeMapping();
        var providerClrType = mapping.Converter?.ProviderClrType ?? mapping.ClrType;
        var providerType = providerClrType == typeof(string) ? "STRING"
            : providerClrType == typeof(int) || providerClrType == typeof(long) ? "INTEGER"
            : providerClrType == typeof(double) ? "FLOAT"
            : providerClrType == typeof(bool) ? "BOOLEAN"
            : providerClrType == typeof(DateTimeOffset) ? "ZONED DATETIME"
            : throw new NotSupportedException("The manifest requires a supported GraphBridge scalar wire type.");
        var modelType = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;
        var conversion = property.GetValueConverter() is not null || property.GetProviderClrType() is not null ? "opaque"
            : mapping.Converter is null ? "native"
            : modelType == typeof(Guid) && providerClrType == typeof(string) ? "guid-d-string"
            : modelType.IsEnum && providerClrType == typeof(long) ? "enum-int64"
            : "opaque";
        if (conversion == "opaque")
            caveats.Add($"{owner}.{binding.Name}: opaque conversion; equal fingerprints describe storage shape, not converter semantic equivalence.");
        var generatedVersion = property.IsConcurrencyToken && property.ValueGenerated == ValueGenerated.OnAddOrUpdate;
        if (generatedVersion && (property.ClrType != typeof(long) || mapping.Converter is not null))
            throw new NotSupportedException("Provider-generated concurrency versions require an unconverted non-nullable long IsRowVersion property.");
        return new GraphSchemaProperty(binding.Name, providerType, !property.IsNullable, property.IsKey(),
            generatedVersion ? GraphSchemaConcurrency.IncrementingInt64
                : property.IsConcurrencyToken ? GraphSchemaConcurrency.OriginalValue : GraphSchemaConcurrency.None,
            property.ValueGenerated.ToString(), property.GetBeforeSaveBehavior().ToString(), property.GetAfterSaveBehavior().ToString(), conversion);
    }

    private static IReadOnlyList<GraphSchemaIndex> Indexes(IEntityType entity,
        IReadOnlyList<GraphPropertyBinding> storedProperties, string owner, List<string> caveats, bool derivedNodeSlots = false)
    {
        var names = storedProperties.ToDictionary(p => p.Property, p => p.Name);
        var indexes = new SortedDictionary<string, GraphSchemaIndex>(StringComparer.Ordinal);
        foreach (var index in entity.GetIndexes())
        {
            if (index.Properties.Any(p => !names.ContainsKey(p)))
            {
                // Endpoint foreign keys are read from matched principals, never persisted on the edge.
                // This also omits conventional FK indexes. A mixed index cannot be lowered to one element.
                caveats.Add(derivedNodeSlots
                    ? $"{owner}: indexes containing derived FK slots are excluded; FK values come from the principal node and are not stored dependent-node properties. Mixed stored/derived indexes cannot be lowered to one graph element."
                    : $"{owner}: indexes containing endpoint slots are excluded; endpoint FK values are not stored relationship properties.");
                continue;
            }
            var properties = index.Properties.Select(p => names[p]).ToArray();
            var canonicalKey = string.Join("\u001f", properties) + (index.IsUnique ? "\u001f1" : "\u001f0");
            indexes.TryAdd(canonicalKey, new GraphSchemaIndex(properties, index.IsUnique));
        }
        if (indexes.Count > 0)
            caveats.Add($"{owner}: index intent covers ordered properties and uniqueness only; sort direction and provider-specific facets are not compared.");
        return Array.AsReadOnly(indexes.Values.ToArray());
    }
}
