using GraphBridge.EntityFrameworkCore.Schema;

namespace GraphBridge.EntityFrameworkCore.Migrations;

internal enum MigrationSchemaState { Absent, Ready, Waiting, Failed, Conflict }
internal sealed record MigrationSchemaObservation(MigrationSchemaState State, string? Problem = null);

internal static class GraphMigrationSchemaState
{
    internal static MigrationSchemaObservation Observe(GraphMigrationSchemaDefinition definition, GraphSchemaObservation observation)
    {
        var constraint = observation.Constraints.SingleOrDefault(value => value.Name == definition.Name);
        var index = observation.Indexes.SingleOrDefault(value => value.Name == definition.Name);
        if (definition.IsIndex)
        {
            if (constraint is not null) return Conflict("The schema name belongs to a constraint.");
            if (index is null) return new(MigrationSchemaState.Absent);
            if (!Matches(definition, index) || index.OwningConstraint is not null)
                return Conflict("Named index differs from the expected standalone definition.");
            return IndexState(index.State);
        }
        if (constraint is null)
            return index is null ? new(MigrationSchemaState.Absent) : Conflict("The constraint name is occupied by an index.");
        if (!Matches(definition, constraint)) return Conflict("Named constraint differs from the expected definition.");
        if (definition.Kind != GraphMigrationSchemaKind.UniquenessConstraint)
            return index is null ? new(MigrationSchemaState.Ready) : Conflict("Unexpected index occupies a non-index-backed constraint name.");
        if (index is null || index.OwningConstraint != definition.Name || index.Type != "RANGE"
            || !Owner(definition, index.EntityType, index.LabelsOrTypes) || !SameSet(definition.Properties, index.Properties))
            return Conflict("Uniqueness constraint has no matching observed backing index.");
        return IndexState(index.State);
    }

    internal static string? CreationConflict(GraphMigrationSchemaDefinition definition, GraphSchemaObservation observation)
    {
        if (definition.Kind is GraphMigrationSchemaKind.VectorIndex or GraphMigrationSchemaKind.FullTextIndex)
        {
            // Any index of the kind over the same schema blocks creation, whatever its options. Neo4j allows one vector index per
            // label and property; for full-text it refuses only the same order, but GraphBridge compares full-text schemas as sets.
            var equivalent = observation.Indexes.FirstOrDefault(value => value.Type == definition.DefinitionType
                && value.EntityType == definition.EntityType && SameSet(definition.LabelsOrTypes, value.LabelsOrTypes)
                && SameSet(definition.Properties, value.Properties));
            if (equivalent is not null)
                return $"An equivalent {definition.DefinitionType} index already exists under another name: '{equivalent.Name}'.";
        }
        else if (definition.IsIndex)
        {
            if (observation.Indexes.Any(value => Matches(definition, value)))
                return "An equivalent range index already exists under another name.";
        }
        else
        {
            if (observation.Constraints.Any(value => Matches(definition, value)))
                return "An equivalent constraint already exists under another name.";
            if (definition.Kind == GraphMigrationSchemaKind.UniquenessConstraint
                && (observation.Indexes.Any(value => value.Type == "RANGE"
                        && Owner(definition, value.EntityType, value.LabelsOrTypes) && SameSet(definition.Properties, value.Properties))
                    || observation.Constraints.Any(value => value.Type == definition.EntityType + "_KEY"
                        && Owner(definition, value.EntityType, value.LabelsOrTypes) && SameSet(definition.Properties, value.Properties))))
                return "An existing range index/key constraint occupies the requested uniqueness schema.";
            if (definition.Kind == GraphMigrationSchemaKind.PropertyTypeConstraint
                && observation.Constraints.Any(value => value.Type == definition.DefinitionType
                    && Owner(definition, value.EntityType, value.LabelsOrTypes) && SameSet(definition.Properties, value.Properties)))
                return "A different property-type constraint already occupies the requested schema.";
        }
        return null;
    }

    private static bool Matches(GraphMigrationSchemaDefinition definition, GraphObservedConstraint constraint)
        => Normalize(constraint.Type) == definition.DefinitionType
            && Owner(definition, constraint.EntityType, constraint.LabelsOrTypes)
            && SameSet(definition.Properties, constraint.Properties)
            && constraint.PropertyType == definition.PropertyType;
    private static bool Matches(GraphMigrationSchemaDefinition definition, GraphObservedIndex index) => definition.Kind switch
    {
        GraphMigrationSchemaKind.VectorIndex => index.Type == "VECTOR" && Owner(definition, index.EntityType, index.LabelsOrTypes)
            && index.Properties?.SequenceEqual(definition.Properties, StringComparer.Ordinal) == true
            && Config(index, "vector.dimensions") is long dimensions && dimensions == definition.VectorDimensions
            && Config(index, "vector.similarity_function") is string similarity
            && similarity.Equals(definition.VectorSimilarity.ToString(), StringComparison.OrdinalIgnoreCase),
        GraphMigrationSchemaKind.FullTextIndex => index.Type == "FULLTEXT" && index.EntityType == definition.EntityType
            // Property order doesn't change a full-text index, so labels and properties compare as sets.
            && SameSet(definition.LabelsOrTypes, index.LabelsOrTypes) && SameSet(definition.Properties, index.Properties)
            && Config(index, "fulltext.analyzer") is string analyzer
            && analyzer.Equals(definition.FullTextAnalyzer, StringComparison.OrdinalIgnoreCase)
            && Config(index, "fulltext.eventually_consistent") is bool eventual && eventual == definition.FullTextEventuallyConsistent,
        _ => index.Type == "RANGE" && Owner(definition, index.EntityType, index.LabelsOrTypes)
            && index.Properties?.SequenceEqual(definition.Properties, StringComparer.Ordinal) == true
    };
    private static object? Config(GraphObservedIndex index, string key)
        => index.IndexConfig is { } config && config.TryGetValue(key, out var value) ? value : null;
    private static bool Owner(GraphMigrationSchemaDefinition definition, string entityType, IReadOnlyList<string>? labels)
        => definition.EntityType == entityType && labels is { Count: 1 } && labels[0] == definition.LabelOrType;
    private static bool SameSet(IReadOnlyList<string> expected, IReadOnlyList<string>? actual)
        => actual is not null && expected.Count == actual.Count && new HashSet<string>(expected, StringComparer.Ordinal).SetEquals(actual);
    private static string Normalize(string type) => type switch
    {
        "UNIQUENESS" => "NODE_PROPERTY_UNIQUENESS",
        "RELATIONSHIP_UNIQUENESS" => "RELATIONSHIP_PROPERTY_UNIQUENESS",
        _ => type
    };
    private static MigrationSchemaObservation IndexState(string state) => state switch
    {
        "ONLINE" => new(MigrationSchemaState.Ready),
        "POPULATING" => new(MigrationSchemaState.Waiting),
        "FAILED" => new(MigrationSchemaState.Failed, "Index population failed; explicit operator review is required."),
        _ => Conflict("Unrecognized index state; no schema readiness is inferred.")
    };
    private static MigrationSchemaObservation Conflict(string problem) => new(MigrationSchemaState.Conflict, problem);
}
