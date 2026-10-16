namespace GraphBridge.EntityFrameworkCore.Search;

public enum GraphSearchKind { Vector, FullText }
public enum GraphSearchEntityKind { Node, Relationship }

/// <summary>An untracked caller-projected value and the server's unmodified score.</summary>
/// <param name="ElementId">Diagnostic store identity, not a durable application key.</param>
public sealed record GraphScoredResult<T>(T Value, double Score, string ElementId);

/// <summary>Observed server/index configuration for this execution, including empty results.</summary>
/// <remarks>Metadata is an observation, not a schema lock or a certification of a deployment.</remarks>
public sealed record GraphSearchProvenance(
    GraphSearchKind Kind, GraphSearchEntityKind EntityKind, string IndexName,
    string ServerVersion, string Edition, string IndexProvider,
    IReadOnlyList<string> LabelsOrTypes, IReadOnlyList<string> Properties,
    int? Dimensions, string? SimilarityFunction, string? Analyzer, bool EventuallyConsistent,
    int RequestedLimit);

/// <summary>Bounded immutable result collection; projected values themselves remain caller-owned.</summary>
public sealed record GraphSearchResults<T>(IReadOnlyList<GraphScoredResult<T>> Results, GraphSearchProvenance Provenance);
