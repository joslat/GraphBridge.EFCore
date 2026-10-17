![GraphBridge](https://raw.githubusercontent.com/joslat/GraphBridge.EFCore/main/docs/images/graphbridge-logo-small.png)

# GraphBridge.EntityFrameworkCore.VectorData

[![NuGet](https://img.shields.io/nuget/vpre/GraphBridge.EntityFrameworkCore.VectorData?label=nuget&color=0b6ae6)](https://www.nuget.org/packages/GraphBridge.EntityFrameworkCore.VectorData)
![Microsoft.Extensions.VectorData](https://img.shields.io/badge/Microsoft.Extensions.VectorData-IVectorSearchable-512bd4)
[![License: MIT](https://img.shields.io/badge/license-MIT-0b6ae6)](https://github.com/joslat/GraphBridge.EFCore/blob/main/LICENSE)
![Developer preview](https://img.shields.io/badge/status-developer%20preview-e8a317)

Optional adapter for [GraphBridge](https://www.nuget.org/packages/GraphBridge.EntityFrameworkCore), the Entity Framework Core provider for Neo4j. It lets retrieval code search an existing Neo4j node or relationship vector index through `Microsoft.Extensions.VectorData`'s `IVectorSearchable<TRecord>`, without depending on Neo4j types or GraphBridge methods. The core provider does not take this dependency.

```xml
<PackageReference Include="GraphBridge.EntityFrameworkCore.VectorData" Version="<version on this page>" />
```

## Bind an index, then search through the interface

```csharp
public sealed record MovieHit(string Title, long Released);

// Application setup: the only code that knows GraphBridge, the index and the stored properties.
services.AddScoped<IVectorSearchable<MovieHit>>(provider => provider.GetRequiredService<MoviesContext>().Database
    .AsNodeVectorSearchable("movie_taste", movie => new MovieHit(movie["title"].As<string>(), movie["released"].As<long>())));

// Application code: depends only on the interface.
public sealed class MovieFinder(IVectorSearchable<MovieHit> search)
{
    public IAsyncEnumerable<VectorSearchResult<MovieHit>> FindAsync(ReadOnlyMemory<float> embedding)
        => search.SearchAsync(embedding, top: 5);
}
```

`AsRelationshipVectorSearchable` does the same for relationship indexes.

## What you get

| | |
|---|---|
| **Inputs** | `ReadOnlyMemory<float>`, `float[]` and `Embedding<float>`, copied exactly and never normalized |
| **Scores** | Neo4j's native similarity, unchanged: between 0 and 1, (1 + cos) / 2 for cosine and 1 / (1 + d²) for Euclidean |
| **Behavior** | The same index check, limits (1–10,000 results, 1–4,096 coordinates), transaction, cancellation and concurrency rules as GraphBridge's own vector search; results are never tracked |
| **Clear refusals** | Every non-default `VectorSearchOptions` setting (`Filter`, `VectorProperty`, `Skip`, `IncludeVectors`, `ScoreThreshold`) and every other input type fails before any database work |

## Boundaries

- Search only. The index must already exist; nothing creates schema, writes records or generates embeddings.
- No pre-filtering. EF global query filters and tenant rules do not apply; use an index that holds only what the caller may search.
- Scores are not portable between vector stores, and `VectorSearchResult` has no provenance; GraphBridge's `QueryVectorNodesAsync` keeps the full observed index provenance.
- The adapter borrows its context. Register it per scope, never as a singleton. With context pooling, an adapter kept beyond its scope fails clearly instead of running on another scope's context.
- Requires `Microsoft.Extensions.VectorData.Abstractions` 10.11.0 or later.

The design was proposed by Luis Quintanilla in [issue #1](https://github.com/joslat/GraphBridge.EFCore/issues/1). Read the [VectorData guide](https://joslat.github.io/GraphBridge.EFCore/features/VECTOR_DATA.html) and the [documentation](https://joslat.github.io/GraphBridge.EFCore/) for GraphBridge itself. Feedback is welcome in [GitHub issues](https://github.com/joslat/GraphBridge.EFCore/issues). MIT licensed.
