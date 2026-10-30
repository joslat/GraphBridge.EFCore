using GraphBridge.EntityFrameworkCore.Cypher;
using GraphBridge.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;

namespace GraphBridge.EntityFrameworkCore.Query;

/// <summary>Immutable projection from finalized EF properties to their graph storage locations.</summary>
/// <remarks>
/// Association execution verifies endpoint-key uniqueness and rejects
/// multiple physical relationships for one directed endpoint/type tuple before EF shaping.
/// Identified edges instead verify a relationship-type/application-ID uniqueness constraint and
/// count physical relationships with that ID across all endpoint pairs of the relationship type.
/// The relationship cardinality check must precede payload filtering and paging; otherwise those
/// operations can conceal a duplicate. Scalar/aggregate paths must preserve the same diagnostic.
/// DISTINCT, elementId-based EF keys, and EF identity resolution do not establish graph uniqueness.
/// Diagnostics cover returned identities; they do not promise snapshot isolation or graph-wide validation.
/// Native-reference roots retain each dependent and check at most two incoming links before testing the
/// principal label. Only valid membership supplies derived FK slots; malformed membership supplies no
/// arbitrary parent to filters. Predicates and pagination can exclude malformed dependents, so these
/// checks validate returned rows only. Chains validate the immediate parent, not all ancestors.
/// </remarks>
internal sealed class GraphReadBinding
{
    private readonly GraphRowReader _fullReader;
    private GraphReadBinding(IEntityType entityType, CypherQueryRoot root, IEnumerable<GraphReadProperty> properties,
        IEnumerable<NodeBinding>? requiredUniqueEndpoints = null,
        IEnumerable<IdentifiedEdgeBinding>? requiredUniqueIdentifiedEdges = null)
    {
        EntityType = entityType;
        Root = root;
        var slots = properties.OrderBy(p => p.Index).ToArray();
        var modelProperties = entityType.GetProperties().OrderBy(p => p.GetIndex()).ToArray();
        if (!slots.Select(p => p.ModelProperty.Property).SequenceEqual(modelProperties)
            || slots.Any(p => p.Index < 0) || slots.Select(p => p.Index).Distinct().Count() != slots.Length)
            throw new InvalidOperationException("Graph read bindings must cover every finalized EF property exactly once at its EF index.");
        Properties = Array.AsReadOnly(slots);
        ValueBufferSize = slots.Length == 0 ? 0 : slots[^1].Index + 1;
        RequiredUniqueEndpoints = Array.AsReadOnly((requiredUniqueEndpoints ?? []).Distinct().ToArray());
        RequiredUniqueIdentifiedEdges = Array.AsReadOnly((requiredUniqueIdentifiedEdges ?? []).Distinct().ToArray());
        _fullReader = new GraphRowReader(this, ReadSlots(null));
    }

    public IEntityType EntityType { get; }
    public CypherQueryRoot Root { get; }
    public IReadOnlyList<GraphReadProperty> Properties { get; }
    public int ValueBufferSize { get; }
    public IReadOnlyList<NodeBinding> RequiredUniqueEndpoints { get; }
    public IReadOnlyList<IdentifiedEdgeBinding> RequiredUniqueIdentifiedEdges { get; }

    public CypherQueryPlan CreatePlan(IReadOnlyList<GraphReadProperty>? projection = null)
        => new(Root, ReadSlots(projection).Select(p => p.Column).ToArray());

    /// <summary>Prepares immutable read slots once, including hidden graph identity guards.</summary>
    public GraphRowReader PrepareRead(IReadOnlyList<GraphReadProperty>? projection = null)
        => projection is null ? _fullReader : new GraphRowReader(this, ReadSlots(projection));

    public ValueBuffer ReadRow(IReadOnlyDictionary<string, object?> row, IReadOnlyList<GraphReadProperty>? projection = null)
        => PrepareRead(projection).ReadRow(row);

    private IReadOnlyList<GraphReadProperty> ReadSlots(IReadOnlyList<GraphReadProperty>? projection)
    {
        var selected = projection ?? Properties;
        if (selected.Any(p => !Properties.Contains(p)) || selected.Select(p => p.Index).Distinct().Count() != selected.Count)
            throw new InvalidOperationException("A read projection must select distinct properties from this finalized binding.");
        // Even a payload-only scalar projection must validate endpoint key presence and conversion.
        var keys = Root is CypherAssociationRoot or CypherIdentifiedEdgeRoot or CypherNodeReferenceRoot
            ? Properties.Where(p => p.ModelProperty.Property.IsPrimaryKey()
                || p.StoreProperty.Owner is CypherPropertyOwner.Source or CypherPropertyOwner.Target)
            : [];
        return keys.Concat(selected).Distinct().ToArray();
    }

    public static GraphReadBinding FromNode(NodeBinding node)
        => new(node.EntityType, new CypherNodeRoot(node.Labels),
            node.Properties.Select(p => new GraphReadProperty(p, new CypherProperty(p.Name))));

    public static GraphReadBinding FromNodeReference(NodeReferenceBinding reference)
        => new(reference.Dependent.EntityType,
            new CypherNodeReferenceRoot(reference.Dependent.Labels, reference.RelationshipType, reference.Principal.Labels, reference.ForeignKey.IsRequired),
            reference.Dependent.StoredProperties.Select(p => new GraphReadProperty(p, new CypherProperty(p.Name)))
                .Concat(reference.ForeignKeyProperties.Select((dependent, index) => new GraphReadProperty(dependent,
                    new CypherProperty(reference.PrincipalKeyProperties[index].Name, CypherPropertyOwner.Source)))),
            [reference.Dependent, reference.Principal]);

    public static GraphReadBinding FromAssociation(AssociationEdgeBinding association)
        => new(association.EntityType,
            new CypherAssociationRoot(association.Source.Node.Labels, association.RelationshipType, association.Target.Node.Labels),
            EndpointProperties(association.Source, CypherPropertyOwner.Source)
                .Concat(EndpointProperties(association.Target, CypherPropertyOwner.Target))
                .Concat(association.PayloadProperties.Select(p =>
                    new GraphReadProperty(p, new CypherProperty(p.Name, CypherPropertyOwner.Relationship)))),
            [association.Source.Node, association.Target.Node]);

    public static GraphReadBinding FromIdentifiedEdge(IdentifiedEdgeBinding edge)
        => new(edge.EntityType,
            new CypherIdentifiedEdgeRoot(edge.Source.Node.Labels, edge.RelationshipType, edge.Target.Node.Labels, edge.KeyProperty.Name),
            EndpointProperties(edge.Source, CypherPropertyOwner.Source)
                .Concat(EndpointProperties(edge.Target, CypherPropertyOwner.Target))
                .Concat(new[] { edge.KeyProperty }.Concat(edge.PayloadProperties).Select(p =>
                    new GraphReadProperty(p, new CypherProperty(p.Name, CypherPropertyOwner.Relationship)))),
            [edge.Source.Node, edge.Target.Node], [edge]);

    private static IEnumerable<GraphReadProperty> EndpointProperties(AssociationEndpointBinding endpoint, CypherPropertyOwner owner)
        => endpoint.ForeignKeyProperties.Select((dependent, index) => new GraphReadProperty(dependent,
            new CypherProperty(endpoint.PrincipalKeyProperties[index].Name, owner)));
}

/// <summary>The dependent/model property owns decoding and the EF slot; the store property owns Cypher lookup.</summary>
internal sealed record GraphReadProperty(GraphPropertyBinding ModelProperty, CypherProperty StoreProperty)
{
    public int Index => ModelProperty.Property.GetIndex();
    public CypherColumn Column => new(StoreProperty.Name, "c" + Index, StoreProperty.Owner);
}
