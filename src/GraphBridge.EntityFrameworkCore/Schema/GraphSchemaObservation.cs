using System.Collections;

namespace GraphBridge.EntityFrameworkCore.Schema;

public sealed class GraphObservedConstraint
{
    public GraphObservedConstraint(string name, string type, string entityType,
        IEnumerable<string>? labelsOrTypes, IEnumerable<string>? properties, string? propertyType = null)
    {
        Name = GraphSchemaObservationParser.NonEmpty(name, nameof(name));
        Type = GraphSchemaObservationParser.NonEmpty(type, nameof(type));
        EntityType = GraphSchemaObservationParser.NonEmpty(entityType, nameof(entityType));
        LabelsOrTypes = GraphSchemaObservationParser.Copy(labelsOrTypes);
        Properties = GraphSchemaObservationParser.Copy(properties);
        PropertyType = propertyType;
    }

    public string Name { get; }
    public string Type { get; }
    public string EntityType { get; }
    public IReadOnlyList<string>? LabelsOrTypes { get; }
    public IReadOnlyList<string>? Properties { get; }
    public string? PropertyType { get; }
}

public sealed class GraphObservedIndex
{
    public GraphObservedIndex(string name, string state, string type, string entityType,
        IEnumerable<string>? labelsOrTypes, IEnumerable<string>? properties, string? owningConstraint = null)
    {
        Name = GraphSchemaObservationParser.NonEmpty(name, nameof(name));
        State = GraphSchemaObservationParser.NonEmpty(state, nameof(state));
        Type = GraphSchemaObservationParser.NonEmpty(type, nameof(type));
        EntityType = GraphSchemaObservationParser.NonEmpty(entityType, nameof(entityType));
        LabelsOrTypes = GraphSchemaObservationParser.Copy(labelsOrTypes);
        Properties = GraphSchemaObservationParser.Copy(properties);
        OwningConstraint = owningConstraint;
    }

    public string Name { get; }
    public string State { get; }
    public string Type { get; }
    public string EntityType { get; }
    public IReadOnlyList<string>? LabelsOrTypes { get; }
    public IReadOnlyList<string>? Properties { get; }
    public string? OwningConstraint { get; }
}

/// <summary>A copied schema observation. It does not assert that stored data complies with the EF model.</summary>
public sealed class GraphSchemaObservation
{
    public GraphSchemaObservation(IEnumerable<GraphObservedConstraint> constraints,
        IEnumerable<GraphObservedIndex> indexes, string? graphTypeSpecification = null)
    {
        ArgumentNullException.ThrowIfNull(constraints);
        ArgumentNullException.ThrowIfNull(indexes);
        Constraints = Array.AsReadOnly(constraints.OrderBy(value => value.Name, StringComparer.Ordinal).ToArray());
        Indexes = Array.AsReadOnly(indexes.OrderBy(value => value.Name, StringComparer.Ordinal).ToArray());
        if (Constraints.Select(value => value.Name).Distinct(StringComparer.Ordinal).Count() != Constraints.Count
            || Indexes.Select(value => value.Name).Distinct(StringComparer.Ordinal).Count() != Indexes.Count)
            throw new ArgumentException("A schema observation cannot contain duplicate constraint or index names.");
        GraphTypeSpecification = graphTypeSpecification;
    }

    public IReadOnlyList<GraphObservedConstraint> Constraints { get; }
    public IReadOnlyList<GraphObservedIndex> Indexes { get; }
    /// <summary>Raw SHOW CURRENT GRAPH TYPE specification; null means it was not observed.</summary>
    public string? GraphTypeSpecification { get; }
}

/// <summary>Strict decoding of the selected SHOW columns; unknown schema kinds are preserved for advisory reporting.</summary>
public static class GraphSchemaObservationParser
{
    public static GraphObservedConstraint ParseConstraint(IReadOnlyDictionary<string, object?> row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return new(Text(row, "name"), Text(row, "type"), Text(row, "entityType"),
            Strings(row, "labelsOrTypes"), Strings(row, "properties"), OptionalText(row, "propertyType"));
    }

    public static GraphObservedIndex ParseIndex(IReadOnlyDictionary<string, object?> row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return new(Text(row, "name"), Text(row, "state"), Text(row, "type"), Text(row, "entityType"),
            Strings(row, "labelsOrTypes"), Strings(row, "properties"), OptionalText(row, "owningConstraint"));
    }

    internal static string NonEmpty(string value, string argument)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, argument);
        return value;
    }

    internal static IReadOnlyList<string>? Copy(IEnumerable<string>? values)
        => values is null ? null : Array.AsReadOnly(values.Select(value => NonEmpty(value, nameof(values))).ToArray());

    private static string Text(IReadOnlyDictionary<string, object?> row, string name)
        => row.TryGetValue(name, out var value) && value is string text && !string.IsNullOrWhiteSpace(text)
            ? text : throw Malformed(name, "nonempty string");

    private static string? OptionalText(IReadOnlyDictionary<string, object?> row, string name)
        => !row.TryGetValue(name, out var value) || value is null ? null
            : value is string text ? text : throw Malformed(name, "string or null");

    private static IReadOnlyList<string>? Strings(IReadOnlyDictionary<string, object?> row, string name)
    {
        if (!row.TryGetValue(name, out var value))
            throw Malformed(name, "list of strings or null");
        if (value is null)
            return null;
        if (value is not IEnumerable values || value is string)
            throw Malformed(name, "list of strings or null");
        var result = new List<string>();
        foreach (var item in values)
        {
            if (item is not string text || string.IsNullOrWhiteSpace(text))
                throw Malformed(name, "list of nonempty strings");
            result.Add(text);
        }
        return result.AsReadOnly();
    }

    private static InvalidOperationException Malformed(string name, string expected)
        => new($"GraphBridge schema observation column '{name}' must be a {expected}.");
}
