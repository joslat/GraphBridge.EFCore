namespace GraphBridge.EntityFrameworkCore.Metadata;

internal sealed record GraphSchemaConstraint(string EntityType, string Type,
    IReadOnlyList<string> LabelsOrTypes, IReadOnlyList<string> Properties);

/// <summary>Validates observed schema; it never infers key existence or creates constraints.</summary>
internal static class GraphKeyConstraintValidator
{
    public static void ValidateIdentifiedEdges(IEnumerable<IdentifiedEdgeBinding> bindings,
        IEnumerable<GraphSchemaConstraint> constraints)
    {
        var observed = constraints.ToArray();
        foreach (var binding in bindings.Distinct())
            if (!observed.Any(c => c.EntityType == "RELATIONSHIP"
                && c.Type is "RELATIONSHIP_PROPERTY_UNIQUENESS" or "RELATIONSHIP_UNIQUENESS" or "RELATIONSHIP_KEY"
                && c.LabelsOrTypes.SequenceEqual(new[] { binding.RelationshipType })
                && c.Properties.SequenceEqual(new[] { binding.KeyProperty.Name })))
                throw new InvalidOperationException($"GraphBridge requires an existing relationship uniqueness constraint for :{binding.RelationshipType} ({binding.KeyProperty.Name}). Deploy the reviewed constraint explicitly; runtime operations never create schema.");
    }

    public static void Validate(IEnumerable<NodeBinding> bindings, IEnumerable<GraphSchemaConstraint> constraints)
    {
        var observed = constraints.ToArray();
        foreach (var binding in bindings.Distinct())
        {
            var keys = binding.KeyProperties.Select(key => binding.Properties.Single(p => p.Property == key).Name)
                .ToHashSet(StringComparer.Ordinal);
            if (!observed.Any(c => c.EntityType == "NODE"
                && c.Type is "NODE_PROPERTY_UNIQUENESS" or "UNIQUENESS" or "NODE_KEY"
                && c.LabelsOrTypes.SequenceEqual(new[] { binding.Label })
                && c.Properties.Count == keys.Count
                && keys.SetEquals(c.Properties)))
                throw new InvalidOperationException($"GraphBridge requires an existing uniqueness constraint for :{binding.Label} ({string.Join(", ", keys)}). Deploy the reviewed constraint explicitly; runtime operations never create schema.");
        }
    }
}
