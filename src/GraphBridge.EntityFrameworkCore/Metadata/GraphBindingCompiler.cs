using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore.Metadata;

namespace GraphBridge.EntityFrameworkCore.Metadata;

/// <summary>Compiles the finalized EF model into its immutable graph storage projection.</summary>
internal sealed class GraphBindingCompiler
{
    private static readonly ConditionalWeakTable<IModel, Bindings> Models = new();

    public NodeBinding GetNode(IEntityType entityType)
    {
        ArgumentNullException.ThrowIfNull(entityType);
        var bindings = Models.GetValue(entityType.Model, Compile);
        if (bindings.Associations.ContainsKey(entityType))
            throw new NotSupportedException($"GraphBridge entity '{entityType.DisplayName()}' uses association-edge storage; node queries and node mutations cannot operate on it. Relationship query/write support is a separate capability.");
        if (bindings.IdentifiedEdges.ContainsKey(entityType))
            throw new NotSupportedException($"GraphBridge entity '{entityType.DisplayName()}' uses identified-edge storage; node queries and node mutations cannot operate on it.");
        return bindings.Nodes[entityType];
    }

    public AssociationEdgeBinding GetAssociation(IEntityType entityType)
    {
        ArgumentNullException.ThrowIfNull(entityType);
        var bindings = Models.GetValue(entityType.Model, Compile);
        return bindings.Associations.TryGetValue(entityType, out var binding) ? binding
            : throw new InvalidOperationException($"GraphBridge entity '{entityType.DisplayName()}' is mapped as a node, not an association edge.");
    }

    public GraphStorageKind GetStorageKind(IEntityType entityType)
    {
        ArgumentNullException.ThrowIfNull(entityType);
        var bindings = Models.GetValue(entityType.Model, Compile);
        return bindings.IdentifiedEdges.ContainsKey(entityType) ? GraphStorageKind.IdentifiedEdge
            : bindings.Associations.ContainsKey(entityType) ? GraphStorageKind.AssociationEdge : GraphStorageKind.Node;
    }

    public IdentifiedEdgeBinding GetIdentifiedEdge(IEntityType entityType)
    {
        ArgumentNullException.ThrowIfNull(entityType);
        return Models.GetValue(entityType.Model, Compile).IdentifiedEdges.TryGetValue(entityType, out var binding)
            ? binding : throw new InvalidOperationException($"GraphBridge entity '{entityType.DisplayName()}' is not mapped as an identified edge.");
    }

    /// <summary>References declared by this dependent node only; incoming references are not included.</summary>
    public IReadOnlyList<NodeReferenceBinding> GetNodeReferences(IEntityType entityType)
    {
        ArgumentNullException.ThrowIfNull(entityType);
        var bindings = Models.GetValue(entityType.Model, Compile);
        return Array.AsReadOnly(entityType.GetForeignKeys().Where(bindings.References.ContainsKey)
            .Select(fk => bindings.References[fk]).ToArray());
    }

    public NodeReferenceBinding GetNodeReference(IForeignKey foreignKey)
    {
        ArgumentNullException.ThrowIfNull(foreignKey);
        return Models.GetValue(foreignKey.DeclaringEntityType.Model, Compile).References.TryGetValue(foreignKey, out var binding)
            ? binding : throw new InvalidOperationException("The foreign key is not explicitly mapped as a native node reference.");
    }

    private static Bindings Compile(IModel model)
    {
        GraphModelValidation.Validate(model);
        var nodes = new Dictionary<IEntityType, NodeBinding>();
        foreach (var entity in model.GetEntityTypes().Where(e => !GraphModelValidation.IsAssociation(e)))
        {
            var properties = Properties(entity);
            nodes.Add(entity, new NodeBinding(entity, (string)entity[GraphAnnotationNames.NodeLabel]!,
                Array.AsReadOnly(entity.FindPrimaryKey()!.Properties.ToArray()), Array.AsReadOnly(properties)));
        }

        var associations = new Dictionary<IEntityType, AssociationEdgeBinding>();
        var identifiedEdges = new Dictionary<IEntityType, IdentifiedEdgeBinding>();
        foreach (var entity in model.GetEntityTypes().Where(GraphModelValidation.IsAssociation))
        {
            var source = GraphModelValidation.ResolveRole(entity, GraphAnnotationNames.SourceForeignKeyProperties);
            var target = GraphModelValidation.ResolveRole(entity, GraphAnnotationNames.TargetForeignKeyProperties);
            var properties = Properties(entity);
            var key = entity.FindPrimaryKey()!.Properties;
            if (GraphModelValidation.IsIdentifiedEdge(entity))
            {
                var endpoints = source.Properties.Concat(target.Properties).ToHashSet();
                identifiedEdges.Add(entity, new IdentifiedEdgeBinding(entity,
                    (string)entity[GraphAnnotationNames.RelationshipType]!, Endpoint(source), Endpoint(target),
                    properties.Single(p => p.Property == key[0]),
                    Array.AsReadOnly(properties.Where(p => p.Property != key[0] && !endpoints.Contains(p.Property)).ToArray())));
            }
            else associations.Add(entity, new AssociationEdgeBinding(entity,
                (string)entity[GraphAnnotationNames.RelationshipType]!, Endpoint(source), Endpoint(target),
                Array.AsReadOnly(key.ToArray()),
                Array.AsReadOnly(properties.Where(p => !key.Contains(p.Property)).ToArray())));

            AssociationEndpointBinding Endpoint(IForeignKey fk)
            {
                var node = nodes[fk.PrincipalEntityType];
                return new AssociationEndpointBinding(fk, node,
                    Array.AsReadOnly(fk.Properties.Select(p => properties.Single(b => b.Property == p)).ToArray()),
                    Array.AsReadOnly(fk.PrincipalKey.Properties.Select(p => node.Properties.Single(b => b.Property == p)).ToArray()));
            }
        }
        var references = new Dictionary<IForeignKey, NodeReferenceBinding>();
        foreach (var fk in model.GetEntityTypes().SelectMany(entity => entity.GetForeignKeys()).Where(GraphModelValidation.IsNodeReference))
        {
            var dependent = nodes[fk.DeclaringEntityType];
            var principal = nodes[fk.PrincipalEntityType];
            references.Add(fk, new NodeReferenceBinding(fk, (string)fk[GraphAnnotationNames.NodeReferenceType]!, principal, dependent,
                fk.Properties.Select(property => dependent.Properties.Single(p => p.Property == property)),
                fk.PrincipalKey.Properties.Select(property => principal.Properties.Single(p => p.Property == property))));
        }
        return new Bindings(new ReadOnlyDictionary<IEntityType, NodeBinding>(nodes),
            new ReadOnlyDictionary<IEntityType, AssociationEdgeBinding>(associations),
            new ReadOnlyDictionary<IEntityType, IdentifiedEdgeBinding>(identifiedEdges),
            new ReadOnlyDictionary<IForeignKey, NodeReferenceBinding>(references));
    }

    private static GraphPropertyBinding[] Properties(IEntityType entity)
        => entity.GetProperties().OrderBy(p => p.GetIndex())
            .Select(p => new GraphPropertyBinding(p, p[GraphAnnotationNames.PropertyName] as string ?? p.Name)).ToArray();

    private sealed record Bindings(IReadOnlyDictionary<IEntityType, NodeBinding> Nodes,
        IReadOnlyDictionary<IEntityType, AssociationEdgeBinding> Associations,
        IReadOnlyDictionary<IEntityType, IdentifiedEdgeBinding> IdentifiedEdges,
        IReadOnlyDictionary<IForeignKey, NodeReferenceBinding> References);
}

internal sealed class NodeBinding
{
    internal NodeBinding(IEntityType entityType, string label, IReadOnlyList<IProperty> keyProperties,
        IReadOnlyList<GraphPropertyBinding> properties)
    {
        (EntityType, Label, KeyProperties, Properties) = (entityType, label, keyProperties, properties);
        AdditionalLabels = GraphModelValidation.AdditionalLabels(entityType);
        Labels = string.Join(':', AdditionalLabels.Prepend(label));
        var derived = entityType.GetForeignKeys().Where(GraphModelValidation.IsNodeReference).SelectMany(fk => fk.Properties).ToHashSet();
        StoredProperties = Array.AsReadOnly(properties.Where(p => !derived.Contains(p.Property)).ToArray());
    }

    public IEntityType EntityType { get; }
    /// <summary>The type's own label: its key constraints and indexes, and its upserts' MERGE, are on this label.</summary>
    public string Label { get; }
    /// <summary>Further labels each node carries (ADR 0055), in ordinal order.</summary>
    public IReadOnlyList<string> AdditionalLabels { get; }
    /// <summary>Every label, the type's own first, separated by colons: what a pattern that matches or creates the type's nodes renders.</summary>
    public string Labels { get; }
    public IReadOnlyList<IProperty> KeyProperties { get; }
    public IReadOnlyList<GraphPropertyBinding> Properties { get; }
    /// <summary>Physical node properties only; native-reference FK slots remain in Properties at their EF ordinals.</summary>
    public IReadOnlyList<GraphPropertyBinding> StoredProperties { get; }
}
