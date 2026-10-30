using System.Collections;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using GraphBridge.EntityFrameworkCore.Cypher;
using GraphBridge.EntityFrameworkCore.Metadata;
using GraphBridge.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Storage;

namespace GraphBridge.EntityFrameworkCore.FluentCypher;

/// <summary>Returns a node or relationship symbol as its mapped entity, untracked (ADR 0049).</summary>
/// <remarks>The query projects the mapped properties only. Values are decoded with the same slots and checks as LINQ,
/// then EF's materializer creates the instance. Nothing is attached and no identity resolution takes place.</remarks>
internal sealed class CypherEntityReader
{
    internal const string ReservedPrefix = "__gb_";
    private const string SourceKey = "__gb_source", TargetKey = "__gb_target", ReferenceKey = "__gb_reference",
        PrincipalKey = "__gb_principal", Parent = "__gb_parent";
    private static readonly ConditionalWeakTable<IEntityType, Func<MaterializationContext, object>> Materializers = new();

    private readonly DbContext _context;
    private readonly GraphReadBinding _binding;
    private readonly NodeReferenceBinding? _reference;
    private readonly Slot[] _slots;
    private Func<MaterializationContext, object>? _materializer;

    private CypherEntityReader(DbContext context, GraphReadBinding binding, NodeReferenceBinding? reference)
    {
        if (binding.Properties.FirstOrDefault(p => p.StoreProperty.Name.StartsWith(ReservedPrefix, StringComparison.Ordinal)) is { } reserved)
            throw CypherDiagnostics.Create("GBFC408", $"'{binding.EntityType.DisplayName()}' stores property '{reserved.StoreProperty.Name}' under the reserved '{ReservedPrefix}' prefix, so it cannot be returned as an entity. Return its scalar members instead.");
        (_context, _binding, _reference) = (context, binding, reference);
        var identifiedEdge = binding.Root is CypherIdentifiedEdgeRoot;
        _slots = binding.Properties.Select(p =>
        {
            var owner = p.StoreProperty.Owner;
            var key = p.ModelProperty.Property.IsPrimaryKey();
            return new Slot(p.ModelProperty, p.Index, owner, p.StoreProperty.Name,
                key || owner is CypherPropertyOwner.Source or CypherPropertyOwner.Target,
                identifiedEdge && key, reference is not null && owner == CypherPropertyOwner.Source);
        }).ToArray();
    }

    private readonly record struct Slot(GraphPropertyBinding ModelProperty, int Index, CypherPropertyOwner Owner, string Name,
        bool CanonicalKey, bool NonEmptyIdentity, bool ReferenceForeignKey);

    internal static CypherEntityReader ForNode(DbContext context, GraphBindingCompiler bindings, NodeBinding node)
        => bindings.GetNodeReferences(node.EntityType).SingleOrDefault() is { } reference
            ? new(context, GraphReadBinding.FromNodeReference(reference), reference)
            : new(context, GraphReadBinding.FromNode(node), null);

    internal static CypherEntityReader ForEdge(DbContext context, GraphBindingCompiler bindings, IEntityType entity)
        => bindings.GetStorageKind(entity) == GraphStorageKind.IdentifiedEdge
            ? new(context, GraphReadBinding.FromIdentifiedEdge(bindings.GetIdentifiedEdge(entity)), null)
            : new(context, GraphReadBinding.FromAssociation(bindings.GetAssociation(entity)), null);

    /// <summary>The store name of a property kept on the returned node or relationship itself; null for a key read from an endpoint or parent.</summary>
    internal string? StoredName(System.Reflection.PropertyInfo member)
        => _slots.FirstOrDefault(s => s.ModelProperty.Property.PropertyInfo == member
            && s.Owner is CypherPropertyOwner.Node or CypherPropertyOwner.Relationship).Name;

    /// <summary>A map projection of the mapped properties. A map projection over null is null, so an absent OPTIONAL MATCH symbol stays null.</summary>
    internal string Render(string symbol)
    {
        var entries = Keys(CypherPropertyOwner.Node).Concat(Keys(CypherPropertyOwner.Relationship)).ToList();
        if (_reference is { } reference)
            // Mirrors the LINQ membership diagnostic: count physical links first (at most two), then test the parent's label.
            entries.Add(ReferenceKey + ": COLLECT { MATCH (" + Parent + ")-[:" + Identifier(reference.RelationshipType) + "]->(" + symbol + ") RETURN "
                + Parent + " {" + string.Join(", ", Keys(CypherPropertyOwner.Source).Append(PrincipalKey + ": (" + Parent + ":"
                + Labels(reference.Principal.Labels) + ")")) + "} LIMIT 2 }");
        else
        {
            if (Endpoint(CypherPropertyOwner.Source, "startNode") is { } source) entries.Add(SourceKey + ": " + source);
            if (Endpoint(CypherPropertyOwner.Target, "endNode") is { } target) entries.Add(TargetKey + ": " + target);
        }
        return symbol + " {" + string.Join(", ", entries) + "}";

        IEnumerable<string> Keys(CypherPropertyOwner owner)
            => _slots.Where(s => s.Owner == owner).Select(s => "." + Identifier(s.Name)).Distinct();

        // Cypher allows a map projection only on a variable, so an endpoint's keys are a map literal over startNode/endNode.
        string? Endpoint(CypherPropertyOwner owner, string function)
        {
            var names = _slots.Where(s => s.Owner == owner).Select(s => Identifier(s.Name)).Distinct().ToArray();
            return names.Length == 0 ? null
                : "{" + string.Join(", ", names.Select(name => name + ": " + function + "(" + symbol + ")." + name)) + "}";
        }
    }

    internal object? Read(object? value)
    {
        if (value is null) return null;
        if (value is not IReadOnlyDictionary<string, object> map)
            throw new InvalidOperationException("A Fluent Cypher entity column did not return a map of its mapped properties.");
        IReadOnlyDictionary<string, object>? parent = null;
        var absentParent = false;
        if (_reference is { } reference)
        {
            if (!map.TryGetValue(ReferenceKey, out var candidates) || candidates is not IList parents) throw GraphRowReader.ReferenceMembershipError();
            parent = parents.Count == 1 && parents[0] is IReadOnlyDictionary<string, object> only
                && only.TryGetValue(PrincipalKey, out var principal) && principal is true ? only : null;
            if (parent is null && !(parents.Count == 0 && !reference.ForeignKey.IsRequired)) throw GraphRowReader.ReferenceMembershipError();
            absentParent = parents.Count == 0;
        }
        var values = new object?[_binding.ValueBufferSize];
        foreach (var slot in _slots)
        {
            var stored = slot.Owner switch
            {
                CypherPropertyOwner.Source when _reference is not null => parent is null ? null : Get(parent, slot.Name),
                CypherPropertyOwner.Source => Get(Nested(map, SourceKey), slot.Name),
                CypherPropertyOwner.Target => Get(Nested(map, TargetKey), slot.Name),
                _ => Get(map, slot.Name)
            };
            if (slot.ReferenceForeignKey && (absentParent ? stored is not null : stored is null)) throw GraphRowReader.ReferenceSlotError();
            values[slot.Index] = GraphRowReader.Decode(slot.ModelProperty, stored, slot.CanonicalKey, slot.NonEmptyIdentity);
        }
        return Materializer()(new MaterializationContext(new ValueBuffer(values), _context));
    }

    internal object ReadList(object value)
    {
        var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(_binding.EntityType.ClrType))!;
        foreach (var element in (IEnumerable)value) list.Add(Read(element));
        return list;
    }

    private static object? Get(IReadOnlyDictionary<string, object> map, string name) => map.TryGetValue(name, out var value) ? value : null;

    private static IReadOnlyDictionary<string, object> Nested(IReadOnlyDictionary<string, object> map, string name)
        => map.TryGetValue(name, out var value) && value is IReadOnlyDictionary<string, object> nested ? nested
            : throw new InvalidOperationException("A Fluent Cypher relationship entity is missing its endpoint keys.");

    /// <summary>EF builds the instance as for a no-tracking LINQ query: constructor binding, service properties, interceptors.</summary>
    private Func<MaterializationContext, object> Materializer()
        => _materializer ??= Materializers.GetValue(_binding.EntityType, entity =>
        {
            var parameter = Expression.Parameter(typeof(MaterializationContext), "materializationContext");
            var body = _context.GetService<IStructuralTypeMaterializerSource>().CreateMaterializeExpression(
                new StructuralTypeMaterializerSourceParameters(entity, "instance", entity.ClrType, IsNullable: false,
                    QueryTrackingBehavior.NoTracking), parameter);
            return Expression.Lambda<Func<MaterializationContext, object>>(Expression.Convert(body, typeof(object)), parameter).Compile();
        });

    // A node type's labels, separated by colons (ADR 0055), each validated and quoted.
    private static string Labels(string labels) => string.Join(':', labels.Split(':').Select(Identifier));

    private static string Identifier(string value) => CypherQueryRenderer.Identifier(value);
}
