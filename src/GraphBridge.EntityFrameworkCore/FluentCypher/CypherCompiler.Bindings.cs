using System.Text;
using GraphBridge.EntityFrameworkCore.Metadata;
using GraphBridge.EntityFrameworkCore.Paths;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Neo4j.Driver;

namespace GraphBridge.EntityFrameworkCore.FluentCypher;

internal sealed partial class CypherCompiler
{
    private string RenderPattern(CypherPattern pattern)
    {
        if (pattern.Depth is not null && pattern.Path is null)
            throw Unsupported("GBFC201", "Bind a bounded pattern with Match(path, pattern.Repeat(min, max)) so its path contract is explicit.");
        var text = new StringBuilder(Node(pattern.Start));
        foreach (var step in pattern.Steps)
        {
            if (step.Undirected && pattern.Depth is null)
                throw Unsupported("GBFC202", "Undirected fluent steps currently require a named bounded path; scalar endpoint roles would otherwise be ambiguous.");
            // Resolve both nodes before binding the relationship's mapped roles.
            var target = Node(step.End);
            var sourceAlias = step.Outgoing ? step.Start : step.End;
            var targetAlias = step.Outgoing ? step.End : step.Start;
            var edge = BindEdge(step.Edge, sourceAlias, targetAlias, pattern.Depth is not null);
            if ((pattern.Depth is not null || step.Undirected) && edge.Source.Node.EntityType != edge.Target.Node.EntityType)
                throw Unsupported("GBFC203", "Bounded or undirected patterns require a homogeneous relationship mapping with the same source/target node type.");
            // A path constrains only its two ends, so every hop must be this mapping's: a type other mappings share (ADR 0056)
            // could otherwise walk through their relationships and node types.
            if ((pattern.Depth is not null || step.Undirected)
                && _model.GetEntityTypes().Count(e => e[GraphAnnotationNames.RelationshipType] as string == edge.RelationshipType) > 1)
                throw Unsupported("GBFC203", $"Bounded or undirected patterns require a relationship type with one mapping; '{edge.RelationshipType}' is mapped by several entity types, so a path could cross the others' relationships.");
            text.Append(step.Outgoing || step.Undirected ? "-[" : "<-[").Append(edge.Name).Append(':')
                .Append(Identifier(edge.RelationshipType));
            if (pattern.Depth is { } depth)
                text.Append('*').Append(depth.Min).Append("..").Append(depth.Max);
            text.Append(step.Outgoing && !step.Undirected ? "]->" : "]-").Append(target);
        }
        if (pattern.Path is { } path)
        {
            if (_symbols.ContainsKey(path)) throw Unsupported("GBFC204", "A path symbol can be bound only once.");
            var step = pattern.Steps.Single(); var edge = (EdgeSymbol)_symbols[step.Edge];
            var contract = new PathContract(new GraphPathPattern(((NodeSymbol)_symbols[pattern.Start]).Binding.Label, [edge.RelationshipType]),
                step.Undirected ? GraphTraversalDirection.Both : step.Outgoing ? GraphTraversalDirection.Outgoing : GraphTraversalDirection.Incoming,
                pattern.Depth!.Min, pattern.Depth.Max, ((NodeSymbol)_symbols[pattern.End]).Binding.Label);
            var symbol = Register(path, new PathSymbol(NextName(path), contract));
            _scope.Add(path);
            text.Insert(0, symbol.Name + "=");
        }
        SnapshotPattern(pattern);
        return text.ToString();
    }

    private string Node(CypherAlias alias)
    {
        if (alias.Kind != CypherAliasKind.Node) throw Unsupported("GBFC205", "A pattern endpoint must be a node symbol.");
        if (!_scope.Contains(alias))
        {
            if (_symbols.ContainsKey(alias)) throw Unsupported("GBFC206", "A dropped symbol cannot be rebound. Create a new symbol.");
            var entity = Entity(alias);
            var node = _bindings.GetNode(entity);
            Register(alias, new NodeSymbol(NextName(alias), node));
            _scope.Add(alias);
        }
        var symbol = (NodeSymbol)_symbols[alias];
        return "(" + symbol.Name + ":" + Labels(symbol.Binding.Labels) + ")";
    }

    private EdgeSymbol BindEdge(CypherAlias alias, CypherAlias source, CypherAlias target, bool repeated)
    {
        if (_scope.Contains(alias))
        {
            var previous = (EdgeSymbol)_symbols[alias];
            if (previous.Repeated || repeated)
                throw Unsupported("GBFC207", "A repeated relationship-list symbol cannot be bound by another pattern or used as a scalar edge.");
            if (previous.SourceAlias != source || previous.TargetAlias != target)
                throw Unsupported("GBFC208", "Reusing an edge symbol requires the same mapped endpoint symbols.");
            return previous;
        }
        if (_symbols.ContainsKey(alias)) throw Unsupported("GBFC206", "A dropped edge cannot be rebound.");
        var entity = Entity(alias);
        EdgeSymbol symbol;
        switch (_bindings.GetStorageKind(entity))
        {
            case GraphStorageKind.IdentifiedEdge:
                var identified = _bindings.GetIdentifiedEdge(entity);
                symbol = new(NextName(alias), entity, identified.RelationshipType, identified.Source, identified.Target,
                    source, target, [identified.KeyProperty, .. identified.PayloadProperties], repeated);
                break;
            case GraphStorageKind.AssociationEdge:
                var association = _bindings.GetAssociation(entity);
                symbol = new(NextName(alias), entity, association.RelationshipType, association.Source, association.Target,
                    source, target, association.PayloadProperties, repeated);
                break;
            default: throw Unsupported("GBFC209", "An edge symbol requires an explicitly mapped graph relationship entity.");
        }
        if (((NodeSymbol)_symbols[source]).Binding.EntityType != symbol.Source.Node.EntityType
            || ((NodeSymbol)_symbols[target]).Binding.EntityType != symbol.Target.Node.EntityType)
            throw Unsupported("GBFC210", "Pattern direction and endpoint CLR types must match the relationship's mapped source and target roles.");
        Register(alias, symbol);
        _scope.Add(alias);
        return symbol;
    }

    private IEntityType Entity(CypherAlias alias)
    {
        var entities = _model.GetEntityTypes().Where(e => e.ClrType == alias.ClrType).ToArray();
        if (entities.Length != 1) throw Unsupported("GBFC211", $"Symbol type '{alias.ClrType.Name}' must identify exactly one mapped EF entity type.");
        var entity = entities[0];
        if (entity.GetDeclaredQueryFilters().Count != 0)
            throw Unsupported("GBFC212", $"'{entity.DisplayName()}' has EF query filters. Fluent native queries cannot silently bypass them; use EF LINQ or an explicitly reviewed native query.");
        return entity;
    }

    private PropertyAccess Property(CypherPropertyTerm property)
    {
        var name = Name(property.Alias);
        var symbol = _symbols[property.Alias];
        var entity = symbol switch
        {
            NodeSymbol nodeSymbol => nodeSymbol.Binding.EntityType,
            EdgeSymbol edgeSymbol => edgeSymbol.Entity,
            _ => null
        };
        var efProperty = entity?.GetProperties().SingleOrDefault(p => p.PropertyInfo == property.Property)
            ?? throw Unsupported("GBFC213", $"'{property.Property.Name}' is not a mapped scalar property of this symbol.");
        if (symbol is NodeSymbol { Binding: var node })
        {
            var binding = node.StoredProperties.SingleOrDefault(p => p.Property == efProperty)
                ?? throw Unsupported("GBFC213", "Native-reference foreign keys are derived from edges. Match an explicit mapped relationship instead of reading a node property slot.");
            return new(name + "." + Identifier(binding.Name), binding);
        }
        var edge = (EdgeSymbol)symbol;
        var stored = edge.Properties.SingleOrDefault(p => p.Property == efProperty);
        if (stored is not null) return new(name + "." + Identifier(stored.Name), stored);
        // Follow the physical relationship: an absent optional edge has no endpoints,
        // even if its source node is still bound. Endpoint aliases need not survive WITH.
        return Endpoint(edge.Source, "startNode") ?? Endpoint(edge.Target, "endNode")
            ?? throw Unsupported("GBFC213", "Relationship property has no physical storage binding.");

        PropertyAccess? Endpoint(AssociationEndpointBinding endpoint, string function)
        {
            for (var i = 0; i < endpoint.ForeignKeyProperties.Count; i++)
                if (endpoint.ForeignKeyProperties[i].Property == efProperty)
                    return new(function + "(" + name + ")." + Identifier(endpoint.PrincipalKeyProperties[i].Name), endpoint.ForeignKeyProperties[i]);
            return null;
        }
    }
    private sealed record PropertyAccess(string Text, GraphPropertyBinding Binding);
    private abstract record Symbol(string Name);
    private sealed record NodeSymbol(string Name, NodeBinding Binding) : Symbol(Name);
    private sealed record EdgeSymbol(string Name, IEntityType Entity, string RelationshipType,
        AssociationEndpointBinding Source, AssociationEndpointBinding Target, CypherAlias SourceAlias,
        CypherAlias TargetAlias, IReadOnlyList<GraphPropertyBinding> Properties, bool Repeated) : Symbol(Name);
    /// <summary>A WITH value. Binding reads min, max and collect results like the aggregated property; List marks collect;
    /// Entity reads collected node or relationship entities (ADR 0049).</summary>
    private sealed record ValueSymbol(string Name, GraphPropertyBinding? Binding = null, bool List = false, CypherEntityReader? Entity = null) : Symbol(Name);
    private sealed record PathSymbol(string Name, PathContract Contract) : Symbol(Name);

    private sealed record PathContract(GraphPathPattern Pattern, GraphTraversalDirection Direction, int Min, int Max, string EndLabel)
    {
        internal GraphPath<INode, IRelationship> Read(object value)
        {
            if (value is not IPath path) throw new InvalidOperationException("Fluent path column is not a native path.");
            var directions = GraphPathReader.Validate(path, Pattern, Direction, Min, Max, CancellationToken.None);
            if (!path.End.Labels.Contains(EndLabel, StringComparer.Ordinal))
                throw new InvalidOperationException("Fluent path end label does not match its mapped endpoint.");
            return new(Array.AsReadOnly(path.Nodes.ToArray()), Array.AsReadOnly(path.Relationships.ToArray()), directions);
        }
    }
}
