using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GraphBridge.EntityFrameworkCore.Migrations;

public sealed class GraphMigrationRollbackPolicy
{
    private GraphMigrationRollbackPolicy(string reason) => Reason = GraphMigrationContract.Reason(reason);
    public string Kind => "NoRollback";
    public string Reason { get; }
    public static GraphMigrationRollbackPolicy NoRollback(string reason) => new(reason);
}

/// <summary>An immutable, versioned migration contract. It does not execute, initialize or inspect a database.</summary>
public sealed class GraphMigrationPlan
{
    public GraphMigrationPlan(string id, string? predecessorId, IEnumerable<GraphMigrationStep> steps,
        GraphMigrationRollbackPolicy rollbackPolicy)
    {
        Id = GraphMigrationContract.Id(id, nameof(id));
        PredecessorId = predecessorId is null ? null : GraphMigrationContract.Id(predecessorId, nameof(predecessorId));
        if (Id == PredecessorId) throw new ArgumentException("A migration cannot be its own predecessor.", nameof(predecessorId));
        ArgumentNullException.ThrowIfNull(steps);
        Steps = Array.AsReadOnly(steps.Select(s => s ?? throw new ArgumentException("Migration steps cannot contain null.", nameof(steps))).ToArray());
        if (Steps.Count == 0 || Steps.Select(s => s.Id).Distinct(StringComparer.Ordinal).Count() != Steps.Count)
            throw new ArgumentException("A migration requires ordered steps with distinct stable IDs.", nameof(steps));
        ArgumentNullException.ThrowIfNull(rollbackPolicy);
        RollbackPolicy = rollbackPolicy;
        CanonicalJson = Canonical();
        Checksum = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(CanonicalJson))).ToLowerInvariant();
    }
    public int FormatVersion => 1;
    public string Id { get; }
    public string? PredecessorId { get; }
    public IReadOnlyList<GraphMigrationStep> Steps { get; }
    public GraphMigrationRollbackPolicy RollbackPolicy { get; }
    public string CanonicalJson { get; }
    public string Checksum { get; }
    public GraphMigrationPreview Preview() => new(this);

    private string Canonical()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("formatVersion", FormatVersion);
            writer.WriteString("id", Id);
            writer.WriteString("predecessorId", PredecessorId);
            writer.WriteStartObject("rollbackPolicy");
            writer.WriteString("kind", RollbackPolicy.Kind);
            writer.WriteString("reason", RollbackPolicy.Reason);
            writer.WriteEndObject();
            writer.WriteStartArray("steps");
            foreach (var step in Steps) WriteStep(writer, step);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteStep(Utf8JsonWriter writer, GraphMigrationStep step)
    {
        writer.WriteStartObject();
        writer.WriteString("id", step.Id);
        writer.WriteString("kind", step.Kind.ToString());
        writer.WritePropertyName("schema");
        if (step.Schema is not { } schema) writer.WriteNullValue();
        else
        {
            writer.WriteStartObject();
            writer.WriteString("name", schema.Name);
            writer.WriteString("kind", schema.Kind.ToString());
            writer.WriteString("definitionType", schema.DefinitionType);
            writer.WriteString("entityType", schema.EntityType);
            writer.WriteString("labelOrType", schema.LabelOrType);
            Strings(writer, "properties", schema.Properties);
            writer.WriteString("propertyType", schema.PropertyType);
            // Written only for the search index kinds, so plans of the earlier kinds keep their checksums.
            if (schema.Kind is GraphMigrationSchemaKind.VectorIndex or GraphMigrationSchemaKind.FullTextIndex)
            {
                Strings(writer, "labelsOrTypes", schema.LabelsOrTypes);
                if (schema.VectorDimensions is { } dimensions) writer.WriteNumber("vectorDimensions", dimensions);
                else writer.WriteNull("vectorDimensions");
                writer.WriteString("vectorSimilarity", schema.VectorSimilarity?.ToString());
                writer.WriteString("fullTextAnalyzer", schema.FullTextAnalyzer);
                if (schema.FullTextEventuallyConsistent is { } eventual) writer.WriteBoolean("fullTextEventuallyConsistent", eventual);
                else writer.WriteNull("fullTextEventuallyConsistent");
            }
            writer.WriteEndObject();
        }
        writer.WritePropertyName("scope");
        if (step.Scope is not { } scope) writer.WriteNullValue();
        else
        {
            writer.WriteStartObject();
            writer.WriteString("entityType", scope.EntityType);
            writer.WriteString("labelOrType", scope.LabelOrType);
            writer.WriteNumber("maxRows", scope.MaxRows);
            Parameters(writer, "filters", scope.Filters);
            writer.WriteEndObject();
        }
        writer.WriteString("sourceProperty", step.SourceProperty);
        writer.WriteString("targetProperty", step.TargetProperty);
        writer.WritePropertyName("value");
        TypedValue(writer, step.Value);
        writer.WriteNumber("expectedCount", step.ExpectedCount);
        writer.WriteString("destructiveReason", step.DestructiveReason);
        Strings(writer, "requiredCapabilities", step.RequiredCapabilities.Select(c => c.ToString()));
        writer.WriteStartObject("impact");
        writer.WriteString("entityType", step.Impact.EntityType);
        writer.WriteString("labelOrType", step.Impact.LabelOrType);
        if (step.Schema?.Kind is GraphMigrationSchemaKind.VectorIndex or GraphMigrationSchemaKind.FullTextIndex)
            Strings(writer, "labelsOrTypes", step.Impact.LabelsOrTypes);
        Strings(writer, "properties", step.Impact.Properties);
        if (step.Impact.MaxRows is { } maxRows) writer.WriteNumber("maxRows", maxRows);
        else writer.WriteNull("maxRows");
        writer.WriteEndObject();
        writer.WriteString("precondition", step.Precondition);
        writer.WriteString("postcondition", step.Postcondition);
        Command(writer, "command", GraphMigrationStepRenderer.Render(step));
        writer.WritePropertyName("preconditionCommand");
        if (step.Schema is not null) writer.WriteNullValue();
        else WriteCommand(writer, GraphMigrationStepRenderer.RenderPrecondition(step));
        writer.WriteEndObject();
    }

    private static void Strings(Utf8JsonWriter writer, string name, IEnumerable<string> values)
    {
        writer.WriteStartArray(name);
        foreach (var value in values) writer.WriteStringValue(value);
        writer.WriteEndArray();
    }
    private static void Command(Utf8JsonWriter writer, string name, GraphMigrationCommand command)
    {
        writer.WritePropertyName(name);
        WriteCommand(writer, command);
    }
    private static void WriteCommand(Utf8JsonWriter writer, GraphMigrationCommand command)
    {
        writer.WriteStartObject();
        writer.WriteString("cypher", command.Cypher);
        Parameters(writer, "parameters", command.Parameters);
        writer.WriteEndObject();
    }
    private static void Parameters(Utf8JsonWriter writer, string name, IReadOnlyDictionary<string, object?> parameters)
    {
        writer.WriteStartObject(name);
        foreach (var pair in parameters.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            writer.WritePropertyName(pair.Key);
            TypedValue(writer, pair.Value);
        }
        writer.WriteEndObject();
    }
    private static void TypedValue(Utf8JsonWriter writer, object? value)
    {
        writer.WriteStartObject();
        writer.WriteString("type", value switch { null => "null", string => "string", bool => "boolean", long => "int64", double => "float64", _ => throw new InvalidOperationException("Unsupported canonical migration value.") });
        switch (value)
        {
            case null: writer.WriteNull("value"); break;
            case string text: writer.WriteString("value", text); break;
            case bool flag: writer.WriteBoolean("value", flag); break;
            case long number: writer.WriteString("value", number.ToString(CultureInfo.InvariantCulture)); break;
            case double number: writer.WriteString("value", number.ToString("R", CultureInfo.InvariantCulture)); break;
        }
        writer.WriteEndObject();
    }
}

/// <summary>Pure preview. Parameter values are explicit and may contain application data; callers control display and redaction.</summary>
public sealed class GraphMigrationPreview
{
    internal GraphMigrationPreview(GraphMigrationPlan plan)
    {
        Id = plan.Id;
        PredecessorId = plan.PredecessorId;
        Checksum = plan.Checksum;
        RollbackPolicy = plan.RollbackPolicy;
        Steps = Array.AsReadOnly(plan.Steps.Select(step => new GraphMigrationStepPreview(step)).ToArray());
    }
    public string Id { get; }
    public string? PredecessorId { get; }
    public string Checksum { get; }
    public GraphMigrationRollbackPolicy RollbackPolicy { get; }
    public IReadOnlyList<GraphMigrationStepPreview> Steps { get; }
}

public sealed class GraphMigrationStepPreview
{
    internal GraphMigrationStepPreview(GraphMigrationStep step)
    {
        Id = step.Id;
        Kind = step.Kind;
        Impact = step.Impact;
        Precondition = step.Precondition;
        Postcondition = step.Postcondition;
        RequiredCapabilities = step.RequiredCapabilities;
        Command = GraphMigrationStepRenderer.Render(step);
        PreconditionCommand = step.Schema is null ? GraphMigrationStepRenderer.RenderPrecondition(step) : null;
        DestructiveReason = step.DestructiveReason;
    }
    public string Id { get; }
    public GraphMigrationStepKind Kind { get; }
    public GraphMigrationImpact Impact { get; }
    public string Precondition { get; }
    public string Postcondition { get; }
    public IReadOnlyList<GraphMigrationCapability> RequiredCapabilities { get; }
    public GraphMigrationCommand Command { get; }
    public GraphMigrationCommand? PreconditionCommand { get; }
    public string? DestructiveReason { get; }
}
