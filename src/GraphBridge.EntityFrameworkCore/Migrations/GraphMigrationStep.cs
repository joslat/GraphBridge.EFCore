using System.Collections.ObjectModel;
using GraphBridge.EntityFrameworkCore.Metadata;
using GraphBridge.EntityFrameworkCore.Schema;

namespace GraphBridge.EntityFrameworkCore.Migrations;

public enum GraphMigrationSchemaKind { UniquenessConstraint, ExistenceConstraint, PropertyTypeConstraint, RangeIndex, VectorIndex, FullTextIndex }
public enum GraphMigrationStepKind { CreateConstraint, CreateIndex, DropConstraint, DropIndex, RenameProperty, Backfill, AssertPrecondition }
public enum GraphMigrationCapability { NodeUniqueness, RelationshipUniqueness, PropertyExistence, PropertyType, RangeIndexes, VectorIndexes, FullTextIndexes }
public enum GraphVectorSimilarity { Cosine, Euclidean }

/// <summary>An exact named schema definition, used both for creation and expected-definition drops.</summary>
public sealed class GraphMigrationSchemaDefinition
{
    /// <summary>Constraints and RANGE indexes. Vector and full-text indexes need their options: use <see cref="VectorIndex"/> or <see cref="FullTextIndex"/>.</summary>
    public GraphMigrationSchemaDefinition(string name, GraphMigrationSchemaKind kind, string entityType,
        string labelOrType, IEnumerable<string> properties, string? propertyType = null)
        : this(name, kind, entityType, [labelOrType], properties)
    {
        if (kind is GraphMigrationSchemaKind.VectorIndex or GraphMigrationSchemaKind.FullTextIndex)
            throw new ArgumentException("Vector and full-text index definitions need their options; use VectorIndex or FullTextIndex.", nameof(kind));
        if (kind is GraphMigrationSchemaKind.ExistenceConstraint or GraphMigrationSchemaKind.PropertyTypeConstraint && Properties.Count != 1)
            throw new ArgumentException("Existence and type constraints require exactly one property.", nameof(properties));
        if (kind == GraphMigrationSchemaKind.PropertyTypeConstraint)
        {
            if (propertyType is not ("STRING" or "INTEGER" or "FLOAT" or "BOOLEAN"))
                throw new ArgumentException("Property type must be STRING, INTEGER, FLOAT or BOOLEAN.", nameof(propertyType));
        }
        else if (propertyType is not null)
            throw new ArgumentException("Only property type constraints accept a property type.", nameof(propertyType));
        PropertyType = propertyType;
    }

    private GraphMigrationSchemaDefinition(string name, GraphMigrationSchemaKind kind, string entityType,
        IEnumerable<string> labelsOrTypes, IEnumerable<string> properties)
    {
        Name = GraphMigrationContract.SchemaName(name);
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        Kind = kind;
        EntityType = GraphMigrationContract.EntityType(entityType);
        ArgumentNullException.ThrowIfNull(labelsOrTypes);
        LabelsOrTypes = Array.AsReadOnly(labelsOrTypes.Select(GraphMigrationContract.Target).ToArray());
        if (LabelsOrTypes.Count is 0 or > 16 || LabelsOrTypes.Distinct(StringComparer.Ordinal).Count() != LabelsOrTypes.Count)
            throw new ArgumentException("A schema definition requires 1 to 16 distinct labels or relationship types.", nameof(labelsOrTypes));
        LabelOrType = LabelsOrTypes[0];
        ArgumentNullException.ThrowIfNull(properties);
        Properties = Array.AsReadOnly(properties.Select(p => GraphIdentifier.Validate(p, nameof(properties))).ToArray());
        if (Properties.Count == 0 || Properties.Distinct(StringComparer.Ordinal).Count() != Properties.Count)
            throw new ArgumentException("A schema definition requires distinct ordered property names.", nameof(properties));
        if (kind == GraphMigrationSchemaKind.FullTextIndex && Properties.Count > 16)
            throw new ArgumentException("A full-text index covers at most 16 properties.", nameof(properties));
    }

    /// <summary>A named vector index over one label (or relationship type) and one property, with its dimensions and similarity function.</summary>
    public static GraphMigrationSchemaDefinition VectorIndex(string name, string entityType, string labelOrType,
        string property, int dimensions, GraphVectorSimilarity similarity)
    {
        if (dimensions is < 1 or > 4096) throw new ArgumentOutOfRangeException(nameof(dimensions), "Vector dimensions must be 1 to 4,096.");
        if (!Enum.IsDefined(similarity)) throw new ArgumentOutOfRangeException(nameof(similarity));
        return new(name, GraphMigrationSchemaKind.VectorIndex, entityType, [labelOrType], [property])
        {
            VectorDimensions = dimensions,
            VectorSimilarity = similarity
        };
    }

    /// <summary>A named full-text index over 1 to 16 labels (or relationship types) and 1 to 16 properties.</summary>
    /// <param name="analyzer">Always declared, so the result doesn't depend on the server's default analyzer setting.</param>
    public static GraphMigrationSchemaDefinition FullTextIndex(string name, string entityType, IEnumerable<string> labelsOrTypes,
        IEnumerable<string> properties, string analyzer = "standard-no-stop-words", bool eventuallyConsistent = false)
    {
        if (analyzer is null || analyzer.Length is 0 or > 64 || analyzer.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-')))
            throw new ArgumentException("An analyzer name has 1 to 64 ASCII letters, digits, underscores or hyphens.", nameof(analyzer));
        return new(name, GraphMigrationSchemaKind.FullTextIndex, entityType, labelsOrTypes, properties)
        {
            FullTextAnalyzer = analyzer,
            FullTextEventuallyConsistent = eventuallyConsistent
        };
    }

    public string Name { get; }
    public GraphMigrationSchemaKind Kind { get; }
    public string EntityType { get; }
    /// <summary>The first of <see cref="LabelsOrTypes"/>; only a full-text index has more than one.</summary>
    public string LabelOrType { get; }
    public IReadOnlyList<string> LabelsOrTypes { get; }
    public IReadOnlyList<string> Properties { get; }
    public string? PropertyType { get; }
    public int? VectorDimensions { get; private init; }
    public GraphVectorSimilarity? VectorSimilarity { get; private init; }
    public string? FullTextAnalyzer { get; private init; }
    public bool? FullTextEventuallyConsistent { get; private init; }
    public bool IsIndex => Kind is GraphMigrationSchemaKind.RangeIndex or GraphMigrationSchemaKind.VectorIndex or GraphMigrationSchemaKind.FullTextIndex;
    public string DefinitionType => Kind switch
    {
        GraphMigrationSchemaKind.UniquenessConstraint => EntityType + "_PROPERTY_UNIQUENESS",
        GraphMigrationSchemaKind.ExistenceConstraint => EntityType + "_PROPERTY_EXISTENCE",
        GraphMigrationSchemaKind.PropertyTypeConstraint => EntityType + "_PROPERTY_TYPE",
        GraphMigrationSchemaKind.RangeIndex => "RANGE",
        GraphMigrationSchemaKind.VectorIndex => "VECTOR",
        GraphMigrationSchemaKind.FullTextIndex => "FULLTEXT",
        _ => throw new InvalidOperationException("Unknown schema definition kind.")
    };

    /// <summary>Copies proposal metadata; proposal Cypher is never accepted as executable input.</summary>
    public static GraphMigrationSchemaDefinition FromProposal(GraphSchemaProposal proposal, string? propertyType = null)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        var kind = proposal.Kind switch
        {
            GraphSchemaProposalKind.UniquenessConstraint => GraphMigrationSchemaKind.UniquenessConstraint,
            GraphSchemaProposalKind.ExistenceConstraint => GraphMigrationSchemaKind.ExistenceConstraint,
            GraphSchemaProposalKind.PropertyTypeConstraint => GraphMigrationSchemaKind.PropertyTypeConstraint,
            GraphSchemaProposalKind.RangeIndex => GraphMigrationSchemaKind.RangeIndex,
            _ => throw new ArgumentException("Unsupported schema proposal kind.", nameof(proposal))
        };
        return new(proposal.Name, kind, proposal.EntityType, proposal.LabelOrType, proposal.Properties, propertyType);
    }
}

/// <summary>
/// An explicitly selected label/type and equality scope, bounded before any data mutation.
/// Candidate reads use read-committed isolation. Locks protect selected elements, not concurrent insertions
/// or elements entering the scope; quiesce application writers when a whole-label snapshot is required.
/// </summary>
public sealed class GraphMigrationDataScope
{
    public GraphMigrationDataScope(string entityType, string labelOrType,
        IReadOnlyDictionary<string, object?> filters, int maxRows)
    {
        EntityType = GraphMigrationContract.EntityType(entityType);
        LabelOrType = GraphMigrationContract.Target(labelOrType);
        ArgumentNullException.ThrowIfNull(filters);
        if (maxRows is < 1 or > 100000)
            throw new ArgumentOutOfRangeException(nameof(maxRows), "A bounded migration step permits 1 through 100000 candidates.");
        MaxRows = maxRows;
        var copied = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var pair in filters)
            copied.Add(GraphIdentifier.Validate(pair.Key, nameof(filters)), GraphMigrationContract.Value(pair.Value));
        Filters = new ReadOnlyDictionary<string, object?>(copied);
    }
    public string EntityType { get; }
    public string LabelOrType { get; }
    /// <summary>Empty means explicitly selected whole label/type; MaxRows and ExpectedCount still guard writes.</summary>
    public IReadOnlyDictionary<string, object?> Filters { get; }
    public int MaxRows { get; }
}

public sealed class GraphMigrationImpact
{
    internal GraphMigrationImpact(string entityType, IReadOnlyList<string> labelsOrTypes, IEnumerable<string> properties, int? maxRows)
    {
        EntityType = entityType;
        LabelsOrTypes = labelsOrTypes;
        LabelOrType = labelsOrTypes[0];
        Properties = Array.AsReadOnly(properties.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
        MaxRows = maxRows;
    }
    public string EntityType { get; }
    /// <summary>The first of <see cref="LabelsOrTypes"/>; only a full-text index step has more than one.</summary>
    public string LabelOrType { get; }
    public IReadOnlyList<string> LabelsOrTypes { get; }
    public IReadOnlyList<string> Properties { get; }
    public int? MaxRows { get; }
}

/// <summary>A typed, immutable step. No raw Cypher, procedures or independently committing subtransactions are accepted.</summary>
public sealed class GraphMigrationStep
{
    private GraphMigrationStep(string id, GraphMigrationStepKind kind, GraphMigrationSchemaDefinition? schema,
        GraphMigrationDataScope? scope, string? sourceProperty, string? targetProperty, object? value,
        long expectedCount, string? destructiveReason)
    {
        Id = GraphMigrationContract.Id(id, nameof(id));
        Kind = kind;
        Schema = schema;
        Scope = scope;
        SourceProperty = sourceProperty;
        TargetProperty = targetProperty;
        Value = value;
        ExpectedCount = expectedCount;
        DestructiveReason = destructiveReason;
        RequiredCapabilities = Array.AsReadOnly(schema is null ? [] : new[] { schema.Kind switch
        {
            GraphMigrationSchemaKind.RangeIndex => GraphMigrationCapability.RangeIndexes,
            GraphMigrationSchemaKind.VectorIndex => GraphMigrationCapability.VectorIndexes,
            GraphMigrationSchemaKind.FullTextIndex => GraphMigrationCapability.FullTextIndexes,
            GraphMigrationSchemaKind.ExistenceConstraint => GraphMigrationCapability.PropertyExistence,
            GraphMigrationSchemaKind.PropertyTypeConstraint => GraphMigrationCapability.PropertyType,
            GraphMigrationSchemaKind.UniquenessConstraint when schema.EntityType == "NODE" => GraphMigrationCapability.NodeUniqueness,
            _ => GraphMigrationCapability.RelationshipUniqueness
        } });
        Impact = schema is not null ? new(schema.EntityType, schema.LabelsOrTypes, schema.Properties, null)
            : new(scope!.EntityType, Array.AsReadOnly(new[] { scope.LabelOrType }),
                new[] { sourceProperty, targetProperty }.Where(p => p is not null).Select(p => p!), scope.MaxRows);
        Precondition = kind switch
        {
            GraphMigrationStepKind.CreateConstraint or GraphMigrationStepKind.CreateIndex => "Schema name is absent; equivalent differently named schema or conflicting namespace definitions require review.",
            GraphMigrationStepKind.DropConstraint or GraphMigrationStepKind.DropIndex => "The named schema object exactly matches the supplied expected definition.",
            GraphMigrationStepKind.RenameProperty => "Exactly ExpectedCount read-committed candidates within MaxRows; under candidate write locks every element still matches scope, every source exists and every destination is absent. Concurrent scope insertions are not excluded.",
            GraphMigrationStepKind.Backfill => "Exactly ExpectedCount read-committed candidates within MaxRows; under candidate write locks every element still matches scope and every destination is absent. Concurrent scope insertions are not excluded.",
            _ => "The bounded scoped count equals ExpectedCount and does not exceed MaxRows."
        };
        Postcondition = kind switch
        {
            GraphMigrationStepKind.CreateIndex => $"The exact named {schema!.DefinitionType} index exists and is ONLINE; readiness is checked separately with a finite timeout.",
            GraphMigrationStepKind.CreateConstraint => "The exact named constraint exists; a uniqueness backing index must also be ONLINE.",
            GraphMigrationStepKind.DropConstraint or GraphMigrationStepKind.DropIndex => "The named schema object is absent.",
            GraphMigrationStepKind.AssertPrecondition => "No data changed; the bounded count assertion succeeded.",
            _ => "Exactly ExpectedCount eligible elements were changed in the same transaction as step completion."
        };
    }
    public string Id { get; }
    public GraphMigrationStepKind Kind { get; }
    public GraphMigrationSchemaDefinition? Schema { get; }
    public GraphMigrationDataScope? Scope { get; }
    public string? SourceProperty { get; }
    public string? TargetProperty { get; }
    public object? Value { get; }
    public long ExpectedCount { get; }
    public string? DestructiveReason { get; }
    public GraphMigrationImpact Impact { get; }
    public IReadOnlyList<GraphMigrationCapability> RequiredCapabilities { get; }
    public string Precondition { get; }
    public string Postcondition { get; }

    public static GraphMigrationStep CreateConstraint(string id, GraphMigrationSchemaDefinition schema)
        => SchemaStep(id, GraphMigrationStepKind.CreateConstraint, schema, false, null);
    public static GraphMigrationStep CreateIndex(string id, GraphMigrationSchemaDefinition schema)
        => SchemaStep(id, GraphMigrationStepKind.CreateIndex, schema, true, null);
    public static GraphMigrationStep DropConstraint(string id, GraphMigrationSchemaDefinition schema, string destructiveReason)
        => SchemaStep(id, GraphMigrationStepKind.DropConstraint, schema, false, GraphMigrationContract.Reason(destructiveReason));
    public static GraphMigrationStep DropIndex(string id, GraphMigrationSchemaDefinition schema, string destructiveReason)
        => SchemaStep(id, GraphMigrationStepKind.DropIndex, schema, true, GraphMigrationContract.Reason(destructiveReason));
    public static GraphMigrationStep RenameProperty(string id, GraphMigrationDataScope scope, string fromProperty,
        string toProperty, long expectedCount, string destructiveReason)
    {
        GraphIdentifier.Validate(fromProperty, nameof(fromProperty));
        GraphIdentifier.Validate(toProperty, nameof(toProperty));
        if (fromProperty == toProperty) throw new ArgumentException("Rename requires distinct source and destination properties.");
        return DataStep(id, GraphMigrationStepKind.RenameProperty, scope, fromProperty, toProperty, null,
            expectedCount, GraphMigrationContract.Reason(destructiveReason));
    }
    public static GraphMigrationStep Backfill(string id, GraphMigrationDataScope scope, string property, object value, long expectedCount)
    {
        GraphIdentifier.Validate(property, nameof(property));
        ArgumentNullException.ThrowIfNull(value);
        return DataStep(id, GraphMigrationStepKind.Backfill, scope, null, property, GraphMigrationContract.Value(value), expectedCount, null);
    }
    public static GraphMigrationStep AssertPrecondition(string id, GraphMigrationDataScope scope, long expectedCount)
        => DataStep(id, GraphMigrationStepKind.AssertPrecondition, scope, null, null, null, expectedCount, null);

    private static GraphMigrationStep SchemaStep(string id, GraphMigrationStepKind kind,
        GraphMigrationSchemaDefinition schema, bool index, string? destructiveReason)
    {
        ArgumentNullException.ThrowIfNull(schema);
        if (schema.IsIndex != index) throw new ArgumentException("Schema definition does not match the step kind.", nameof(schema));
        return new(id, kind, schema, null, null, null, null, 0, destructiveReason);
    }
    private static GraphMigrationStep DataStep(string id, GraphMigrationStepKind kind, GraphMigrationDataScope scope,
        string? source, string? target, object? value, long count, string? reason)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (count < 0 || count > scope.MaxRows) throw new ArgumentOutOfRangeException(nameof(count), "ExpectedCount must be within the explicit row bound.");
        return new(id, kind, null, scope, source, target, value, count, reason);
    }
}

internal static class GraphMigrationContract
{
    internal static string Id(string value, string argument)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, argument);
        if (value.Length > 128 || value.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-' or '.')))
            throw new ArgumentException("Migration and step IDs permit at most 128 ASCII letters, digits, dots, underscores or hyphens.", argument);
        return value;
    }
    internal static string EntityType(string value) => value is "NODE" or "RELATIONSHIP" ? value
        : throw new ArgumentException("Entity type must be NODE or RELATIONSHIP.", nameof(value));
    internal static string Target(string value)
    {
        GraphIdentifier.Validate(value, nameof(value));
        if (value.StartsWith("__GraphBridgeMigration", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Migration journal and ownership labels are reserved.", nameof(value));
        return value;
    }
    internal static string SchemaName(string value)
    {
        GraphIdentifier.Validate(value, nameof(value));
        if (value.StartsWith("gb_migration_", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Migration journal schema names are reserved.", nameof(value));
        return value;
    }
    internal static string Reason(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return value;
    }
    internal static object? Value(object? value) => value switch
    {
        null or string or bool or long => value,
        int integer => (long)integer,
        double number when double.IsFinite(number) => number,
        _ => throw new ArgumentException("Migration values permit null, string, boolean, Int32/Int64 or finite Double only; convert application values explicitly.", nameof(value))
    };
}
