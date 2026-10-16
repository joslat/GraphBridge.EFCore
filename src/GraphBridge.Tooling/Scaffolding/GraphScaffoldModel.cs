using System.Collections.ObjectModel;
using GraphBridge.EntityFrameworkCore.Schema;
using GraphBridge.Tooling.Generation;

namespace GraphBridge.Tooling.Scaffolding;

/// <summary>Observed storage types only; absence is represented by a missing property. No values or durable element IDs are retained.</summary>
public enum GraphScaffoldValueKind { String, Boolean, Integer, Float, StringList, IntegerList, FloatList, BooleanList, EmptyList, Temporal, Spatial, Unsupported }

public sealed class GraphScaffoldNodeSample
{
    public GraphScaffoldNodeSample(IEnumerable<string> labels, IReadOnlyDictionary<string, GraphScaffoldValueKind> properties)
    {
        Labels = ScaffoldCollections.Strings(labels);
        Properties = ScaffoldCollections.Properties(properties);
    }
    public IReadOnlyList<string> Labels { get; }
    public IReadOnlyDictionary<string, GraphScaffoldValueKind> Properties { get; }
}

public sealed class GraphScaffoldRelationshipSample
{
    public GraphScaffoldRelationshipSample(string relationshipType, IEnumerable<string> sourceLabels,
        IEnumerable<string> targetLabels, IReadOnlyDictionary<string, GraphScaffoldValueKind> properties,
        bool observedParallel = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relationshipType);
        RelationshipType = relationshipType;
        SourceLabels = ScaffoldCollections.Strings(sourceLabels);
        TargetLabels = ScaffoldCollections.Strings(targetLabels);
        Properties = ScaffoldCollections.Properties(properties);
        ObservedParallel = observedParallel;
    }
    public string RelationshipType { get; }
    public IReadOnlyList<string> SourceLabels { get; }
    public IReadOnlyList<string> TargetLabels { get; }
    public IReadOnlyDictionary<string, GraphScaffoldValueKind> Properties { get; }
    /// <summary>True proves parallel edges in the bounded observation; false does not prove uniqueness.</summary>
    public bool ObservedParallel { get; }
}

public sealed class GraphScaffoldSnapshot
{
    public GraphScaffoldSnapshot(GraphSchemaCapabilities capabilities, GraphSchemaObservation schema,
        IEnumerable<string> labels, IEnumerable<string> relationshipTypes,
        IEnumerable<GraphScaffoldNodeSample> nodes, IEnumerable<GraphScaffoldRelationshipSample> relationships,
        int sampleLimit)
    {
        Capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
        Schema = schema ?? throw new ArgumentNullException(nameof(schema));
        Labels = ScaffoldCollections.Strings(labels);
        RelationshipTypes = ScaffoldCollections.Strings(relationshipTypes);
        Nodes = GenerationCollections.Copy(nodes);
        Relationships = GenerationCollections.Copy(relationships);
        if (sampleLimit is < 1 or > 10000) throw new ArgumentOutOfRangeException(nameof(sampleLimit));
        SampleLimit = sampleLimit;
    }
    public GraphSchemaCapabilities Capabilities { get; }
    public GraphSchemaObservation Schema { get; }
    public IReadOnlyList<string> Labels { get; }
    public IReadOnlyList<string> RelationshipTypes { get; }
    public IReadOnlyList<GraphScaffoldNodeSample> Nodes { get; }
    public IReadOnlyList<GraphScaffoldRelationshipSample> Relationships { get; }
    public int SampleLimit { get; }
}

/// <summary>A reviewed property decision. Ignore is explicit and reported; converters never rewrite stored values.</summary>
public sealed record GraphScaffoldPropertyOverride(string StoreName, string ClrName,
    GraphScalarType ScalarType, GraphScalarConversion Conversion = GraphScalarConversion.Native,
    bool Required = false, bool Ignore = false);

public sealed class GraphScaffoldNodeMapping
{
    public GraphScaffoldNodeMapping(string label, string clrName, IEnumerable<string> keyProperties,
        bool approveRequiredKey, IEnumerable<GraphScaffoldPropertyOverride>? properties = null)
    {
        Label = label ?? throw new ArgumentNullException(nameof(label));
        ClrName = clrName ?? throw new ArgumentNullException(nameof(clrName));
        KeyProperties = GenerationCollections.Copy(keyProperties);
        ApproveRequiredKey = approveRequiredKey;
        Properties = GenerationCollections.Copy(properties ?? []);
    }
    public string Label { get; }
    public string ClrName { get; }
    /// <summary>Ordered storage property names, never elementId() or sampled uniqueness.</summary>
    public IReadOnlyList<string> KeyProperties { get; }
    public bool ApproveRequiredKey { get; }
    public IReadOnlyList<GraphScaffoldPropertyOverride> Properties { get; }
}

public sealed class GraphScaffoldRelationshipMapping
{
    public GraphScaffoldRelationshipMapping(string relationshipType, string clrName, string sourceLabel,
        string targetLabel, string keyProperty, bool approveRequiredKey,
        IEnumerable<GraphScaffoldPropertyOverride>? properties = null)
    {
        RelationshipType = relationshipType ?? throw new ArgumentNullException(nameof(relationshipType));
        ClrName = clrName ?? throw new ArgumentNullException(nameof(clrName));
        SourceLabel = sourceLabel ?? throw new ArgumentNullException(nameof(sourceLabel));
        TargetLabel = targetLabel ?? throw new ArgumentNullException(nameof(targetLabel));
        KeyProperty = keyProperty ?? throw new ArgumentNullException(nameof(keyProperty));
        ApproveRequiredKey = approveRequiredKey;
        Properties = GenerationCollections.Copy(properties ?? []);
    }
    public string RelationshipType { get; }
    public string ClrName { get; }
    public string SourceLabel { get; }
    public string TargetLabel { get; }
    public string KeyProperty { get; }
    public bool ApproveRequiredKey { get; }
    public IReadOnlyList<GraphScaffoldPropertyOverride> Properties { get; }
}

public sealed class GraphScaffoldPolicy
{
    public GraphScaffoldPolicy(string @namespace, string contextName, IEnumerable<GraphScaffoldNodeMapping>? nodes = null,
        IEnumerable<GraphScaffoldRelationshipMapping>? relationships = null)
    {
        Namespace = @namespace ?? throw new ArgumentNullException(nameof(@namespace));
        ContextName = contextName ?? throw new ArgumentNullException(nameof(contextName));
        Nodes = GenerationCollections.Copy(nodes ?? []);
        Relationships = GenerationCollections.Copy(relationships ?? []);
    }
    public string Namespace { get; }
    public string ContextName { get; }
    public IReadOnlyList<GraphScaffoldNodeMapping> Nodes { get; }
    public IReadOnlyList<GraphScaffoldRelationshipMapping> Relationships { get; }
}

public sealed class GraphScaffoldCompilationResult
{
    internal GraphScaffoldCompilationResult(GraphGenerationModel? model, IEnumerable<GraphGenerationDiagnostic> diagnostics,
        IEnumerable<GraphGeneratedFile> files, string fingerprint)
    {
        Diagnostics = Array.AsReadOnly(diagnostics.Distinct().OrderBy(d => d.Subject, StringComparer.Ordinal)
            .ThenBy(d => d.Code, StringComparer.Ordinal).ThenBy(d => d.Message, StringComparer.Ordinal).ToArray());
        Model = Diagnostics.Any(d => d.Severity == GraphGenerationSeverity.Error) ? null : model;
        Files = Array.AsReadOnly(files.OrderBy(f => f.RelativePath, StringComparer.Ordinal).ToArray());
        SourceFingerprint = fingerprint;
    }
    public bool Success => !Diagnostics.Any(d => d.Severity == GraphGenerationSeverity.Error);
    /// <summary>Null for DTO-only proposals or invalid reviewed tracking policy. This is not an EF runtime manifest.</summary>
    public GraphGenerationModel? Model { get; }
    public IReadOnlyList<GraphGenerationDiagnostic> Diagnostics { get; }
    public IReadOnlyList<GraphGeneratedFile> Files { get; }
    public string SourceFingerprint { get; }
}

internal static class ScaffoldCollections
{
    internal static IReadOnlyList<string> Strings(IEnumerable<string> values)
        => Array.AsReadOnly(GenerationCollections.Copy(values).Select(value =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value);
            return value;
        }).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());

    internal static IReadOnlyDictionary<string, GraphScaffoldValueKind> Properties(IReadOnlyDictionary<string, GraphScaffoldValueKind> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var result = new SortedDictionary<string, GraphScaffoldValueKind>(StringComparer.Ordinal);
        foreach (var pair in values)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(pair.Key);
            if (!Enum.IsDefined(pair.Value)) throw new ArgumentOutOfRangeException(nameof(values));
            result.Add(pair.Key, pair.Value);
        }
        return new ReadOnlyDictionary<string, GraphScaffoldValueKind>(result);
    }
}
