using GraphBridge.EntityFrameworkCore.Cypher;
using GraphBridge.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;

namespace GraphBridge.EntityFrameworkCore.Query;

/// <summary>Query-owned immutable decoding plan; every row gets a fresh EF value buffer.</summary>
internal sealed class GraphRowReader
{
    private readonly CypherQueryRoot _root;
    private readonly int _valueBufferSize;
    private readonly Slot[] _slots;

    internal GraphRowReader(GraphReadBinding binding, IReadOnlyList<GraphReadProperty> properties)
    {
        _root = binding.Root;
        _valueBufferSize = binding.ValueBufferSize;
        _slots = properties.Select(p => new Slot(p.ModelProperty, p.Index, p.Column.Alias,
            p.ModelProperty.Property.IsPrimaryKey() || p.StoreProperty.Owner is CypherPropertyOwner.Source or CypherPropertyOwner.Target,
            binding.Root is CypherIdentifiedEdgeRoot && p.ModelProperty.Property.IsPrimaryKey(),
            binding.Root is CypherNodeReferenceRoot && p.StoreProperty.Owner == CypherPropertyOwner.Source)).ToArray();
    }

    private readonly record struct Slot(GraphPropertyBinding ModelProperty, int Index, string Alias,
        bool CanonicalKey, bool NonEmptyIdentity, bool ReferenceForeignKey);

    /// <summary>Checks graph identity diagnostics and decodes all values before any EF shaper runs.</summary>
    public ValueBuffer ReadRow(IReadOnlyDictionary<string, object?> row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var absentReference = false;
        if (_root is CypherNodeReferenceRoot reference)
        {
            if (!row.TryGetValue(CypherQueryPlan.NodeReferenceCountAlias, out var referenceCount)
                || referenceCount is not long referenceValue
                || !row.TryGetValue(CypherQueryPlan.NodeReferencePrincipalAlias, out var referencePrincipal)
                || referencePrincipal is not bool principalValid
                || !(referenceValue == 1 && principalValid || !reference.Required && referenceValue == 0 && !principalValid))
                throw new InvalidOperationException("GraphBridge native-reference membership diagnostic is missing, invalid, or not satisfied; required rows need exactly one valid principal, optional rows allow zero or one.");
            absentReference = referenceValue == 0;
        }
        if (_root is CypherAssociationRoot
            && (!row.TryGetValue(CypherQueryPlan.AssociationCountAlias, out var count) || count is not long value || value != 1))
            throw new InvalidOperationException("GraphBridge association rows must report exactly one physical relationship before materialization; the cardinality diagnostic is missing, invalid, or not one.");
        if (_root is CypherIdentifiedEdgeRoot
            && (!row.TryGetValue(CypherQueryPlan.IdentifiedEdgeCountAlias, out var edgeCount) || edgeCount is not long edgeValue || edgeValue != 1))
            throw new InvalidOperationException("GraphBridge identified-edge rows must report exactly one physical relationship for the relationship type and application ID before materialization; the identity diagnostic is missing, invalid, or not one.");
        var values = new object?[_valueBufferSize];
        foreach (var slot in _slots)
        {
            var storeValue = row[slot.Alias];
            if (slot.ReferenceForeignKey && (absentReference ? storeValue is not null : storeValue is null))
                throw new InvalidOperationException("GraphBridge native-reference required property is missing from a present principal, or an absent principal returned a non-null FK slot.");
            if (slot.NonEmptyIdentity
                && storeValue is string id && string.IsNullOrWhiteSpace(id))
                throw new InvalidOperationException("GraphBridge identified-edge application ID must not be empty or whitespace.");
            var modelValue = slot.ModelProperty.FromStore(storeValue);
            if (slot.CanonicalKey
                && !Equals(storeValue, slot.ModelProperty.ToStore(modelValue)))
                throw new InvalidOperationException("GraphBridge returned key does not use its canonical store representation; different wire keys must not resolve to the same EF identity.");
            values[slot.Index] = modelValue;
        }
        return new ValueBuffer(values);
    }

}
