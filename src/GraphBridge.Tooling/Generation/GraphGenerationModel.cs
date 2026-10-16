namespace GraphBridge.Tooling.Generation;

/// <summary>Design-time authoring input only. Runtime metadata is derived from the generated EF model.</summary>
public sealed class GraphGenerationModel
{
    public GraphGenerationModel(string @namespace, string contextName, IEnumerable<GraphNodeDefinition> nodes,
        IEnumerable<GraphIdentifiedEdgeDefinition> identifiedEdges)
    {
        Namespace = @namespace ?? throw new ArgumentNullException(nameof(@namespace));
        ContextName = contextName ?? throw new ArgumentNullException(nameof(contextName));
        Nodes = GenerationCollections.Copy(nodes);
        IdentifiedEdges = GenerationCollections.Copy(identifiedEdges);
    }
    public string Namespace { get; }
    public string ContextName { get; }
    public IReadOnlyList<GraphNodeDefinition> Nodes { get; }
    public IReadOnlyList<GraphIdentifiedEdgeDefinition> IdentifiedEdges { get; }
}

public sealed class GraphNodeDefinition
{
    public GraphNodeDefinition(string clrName, string? iri, string label, IEnumerable<GraphPropertyDefinition> properties,
        IEnumerable<string> keyProperties, GraphDeletionPolicy deletion)
    {
        ClrName = clrName ?? throw new ArgumentNullException(nameof(clrName));
        Iri = iri;
        Label = label ?? throw new ArgumentNullException(nameof(label));
        Properties = GenerationCollections.Copy(properties);
        KeyProperties = GenerationCollections.Copy(keyProperties);
        Deletion = deletion;
    }
    public string ClrName { get; }
    public string? Iri { get; }
    public string Label { get; }
    public IReadOnlyList<GraphPropertyDefinition> Properties { get; }
    public IReadOnlyList<string> KeyProperties { get; }
    public GraphDeletionPolicy Deletion { get; }
}

public sealed class GraphIdentifiedEdgeDefinition
{
    public GraphIdentifiedEdgeDefinition(string clrName, string? iri, string relationshipType,
        IEnumerable<GraphPropertyDefinition> properties, string keyProperty,
        GraphEndpointDefinition source, GraphEndpointDefinition target, GraphEdgeDirection direction)
    {
        ClrName = clrName ?? throw new ArgumentNullException(nameof(clrName));
        Iri = iri;
        RelationshipType = relationshipType ?? throw new ArgumentNullException(nameof(relationshipType));
        Properties = GenerationCollections.Copy(properties);
        KeyProperty = keyProperty ?? throw new ArgumentNullException(nameof(keyProperty));
        Source = source ?? throw new ArgumentNullException(nameof(source));
        Target = target ?? throw new ArgumentNullException(nameof(target));
        Direction = direction;
    }
    public string ClrName { get; }
    public string? Iri { get; }
    public string RelationshipType { get; }
    public IReadOnlyList<GraphPropertyDefinition> Properties { get; }
    public string KeyProperty { get; }
    public GraphEndpointDefinition Source { get; }
    public GraphEndpointDefinition Target { get; }
    public GraphEdgeDirection Direction { get; }
}

public sealed class GraphEndpointDefinition
{
    public GraphEndpointDefinition(string nodeClrName, string navigationName, IEnumerable<string> foreignKeyProperties,
        GraphDeletionPolicy deletion)
    {
        NodeClrName = nodeClrName ?? throw new ArgumentNullException(nameof(nodeClrName));
        NavigationName = navigationName ?? throw new ArgumentNullException(nameof(navigationName));
        ForeignKeyProperties = GenerationCollections.Copy(foreignKeyProperties);
        Deletion = deletion;
    }
    public string NodeClrName { get; }
    public string NavigationName { get; }
    public IReadOnlyList<string> ForeignKeyProperties { get; }
    public GraphDeletionPolicy Deletion { get; }
}

public sealed class GraphPropertyDefinition
{
    public GraphPropertyDefinition(string clrName, string? iri, string storeName, GraphScalarType scalarType,
        bool isRequired, GraphScalarConversion conversion, GraphConcurrencyMode concurrency = GraphConcurrencyMode.None)
    {
        ClrName = clrName ?? throw new ArgumentNullException(nameof(clrName));
        Iri = iri;
        StoreName = storeName ?? throw new ArgumentNullException(nameof(storeName));
        ScalarType = scalarType;
        IsRequired = isRequired;
        Conversion = conversion;
        Concurrency = concurrency;
    }
    public string ClrName { get; }
    public string? Iri { get; }
    public string StoreName { get; }
    public GraphScalarType ScalarType { get; }
    public bool IsRequired { get; }
    public GraphScalarConversion Conversion { get; }
    public GraphConcurrencyMode Concurrency { get; }
}

public enum GraphScalarType { String, Boolean, Int32, Int64, Double, Guid, DateOnly }
public enum GraphScalarConversion { Native, GuidString, DateOnlyIsoString }
public enum GraphConcurrencyMode { None, OriginalValue, IncrementingInt64 }
/// <summary>Cascade is represented for diagnostics; the initial emitter accepts Restrict only.</summary>
public enum GraphDeletionPolicy { Restrict, Cascade }
/// <summary>The initial emitter accepts SourceToTarget only; reversing roles must be explicit in authoring.</summary>
public enum GraphEdgeDirection { SourceToTarget, TargetToSource, Undirected }
public enum GraphGenerationSeverity { Info, Warning, Error }
public sealed record GraphGenerationDiagnostic(string Code, GraphGenerationSeverity Severity, string Subject, string Message);
public sealed record GraphGeneratedFile(string RelativePath, string Content);

public sealed class GraphSourceGenerationResult
{
    internal GraphSourceGenerationResult(IEnumerable<GraphGeneratedFile> files,
        IEnumerable<GraphGenerationDiagnostic> diagnostics, string? contextTypeName)
    {
        Files = Array.AsReadOnly(files.OrderBy(file => file.RelativePath, StringComparer.Ordinal).ToArray());
        Diagnostics = Array.AsReadOnly(diagnostics.Distinct().OrderBy(value => value.Subject, StringComparer.Ordinal)
            .ThenBy(value => value.Code, StringComparer.Ordinal).ThenBy(value => value.Severity)
            .ThenBy(value => value.Message, StringComparer.Ordinal).ToArray());
        ContextTypeName = contextTypeName;
    }
    public bool Success => !Diagnostics.Any(value => value.Severity == GraphGenerationSeverity.Error);
    public IReadOnlyList<GraphGeneratedFile> Files { get; }
    public IReadOnlyList<GraphGenerationDiagnostic> Diagnostics { get; }
    /// <summary>Normalized CLR full name for reflection, without C# keyword escape characters.</summary>
    public string? ContextTypeName { get; }
}

internal static class GenerationCollections
{
    internal static IReadOnlyList<T> Copy<T>(IEnumerable<T> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var copy = values.ToArray();
        if (copy.Any(value => value is null)) throw new ArgumentException("Generation input cannot contain null entries.", nameof(values));
        return Array.AsReadOnly(copy);
    }
}
