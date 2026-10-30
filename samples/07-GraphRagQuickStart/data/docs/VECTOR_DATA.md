# Vector search through Microsoft.Extensions.VectorData

The optional package `GraphBridge.EntityFrameworkCore.VectorData` exposes an existing Neo4j vector index as `IVectorSearchable<TRecord>` from `Microsoft.Extensions.VectorData`. Retrieval code written against that interface can then search GraphBridge data without knowing about Neo4j or GraphBridge, and the application can swap the search implementation without rewriting it. The adapter is a thin layer over [typed vector search](https://github.com/joslat/GraphBridge.EFCore/blob/main/docs/features/GRAPH_SEARCH.md): the same index check, limits, transactions and cancellation apply.

```xml
<PackageReference Include="GraphBridge.EntityFrameworkCore.VectorData" Version="<version>" />
```

The core `GraphBridge.EntityFrameworkCore` package does not reference VectorData; only this package does.

## Bind an index in application setup

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.VectorData;
using Neo4j.Driver;

public sealed record MovieHit(string Title, long Released);

services.AddDbContext<MoviesContext>(options => options.UseNeo4j(driver, "neo4j"));
services.AddScoped<IVectorSearchable<MovieHit>>(provider => provider.GetRequiredService<MoviesContext>().Database
    .AsNodeVectorSearchable("movie_taste", movie => new MovieHit(movie["title"].As<string>(), movie["released"].As<long>())));
```

`AsNodeVectorSearchable` binds a node vector index and `AsRelationshipVectorSearchable` a relationship vector index. Binding does no database work. A missing context or projector, or an index name that doesn't match `[A-Za-z_][A-Za-z0-9_]*`, throws right away. Whether the index exists is checked at each search; to check it before deploying, declare it for [Doctor](https://github.com/joslat/GraphBridge.EFCore/blob/main/docs/tools/DOCTOR.md#search-indexes).

The projector turns each native match into your record, once per match; return application keys in it when you need graph queries afterwards. A projector that throws fails the search. If it returns `null` for any match, the whole search fails before any result is returned.

## Search through the interface

```csharp
public sealed class MovieFinder(IVectorSearchable<MovieHit> search)
{
    public async Task<List<VectorSearchResult<MovieHit>>> FindAsync(ReadOnlyMemory<float> embedding, CancellationToken cancellationToken)
    {
        var matches = new List<VectorSearchResult<MovieHit>>();
        await foreach (var match in search.SearchAsync(embedding, top: 5, cancellationToken: cancellationToken))
            matches.Add(match);
        return matches;
    }
}
```

Nothing runs until you enumerate. Each enumeration reads the input at that moment and performs one new search; results are buffered, as in the typed search API, and never tracked by EF.

## What is supported

| | Behavior |
|---|---|
| Inputs | `ReadOnlyMemory<float>`, `float[]` and `Embedding<float>` from `Microsoft.Extensions.AI`. Coordinates are widened to `double` exactly and never normalized. Any other type throws `NotSupportedException` before database work: generate the embedding first. |
| Limits | `top` from 1 to 10,000; 1 to 4,096 finite coordinates. Invalid values throw before database work. |
| Options | `null` or default `VectorSearchOptions`. `Filter`, `VectorProperty`, `Skip`, `IncludeVectors` and `ScoreThreshold` each throw `NotSupportedException` naming the option. |
| Scores | Passed through unchanged, in the native order (highest first). |
| Index check | Missing, offline or wrong-kind indexes, dimension mismatches and zero vectors on cosine indexes fail as in [typed search](https://github.com/joslat/GraphBridge.EFCore/blob/main/docs/features/GRAPH_SEARCH.md#inputs-limits-and-scores). Nothing creates an index. |
| `GetService` | Returns the adapter for its own types when `serviceKey` is `null`; otherwise `null`. |

### Scores

Neo4j returns a normalized similarity between 0 and 1, not a raw cosine value or a distance:

| Index similarity | Score |
|---|---|
| `cosine` | (1 + cos θ) / 2, so an orthogonal match scores 0.5 |
| `euclidean` | 1 / (1 + d²) |

Neo4j 2026 vector indexes use binary quantization by default (`vector.quantization.type: BINARY`). On Neo4j 2026.09 the scores matched these formulas exactly at limits of 3 and 100. At a limit of 10,000 they were approximations, off by up to about 0.01, and two nearly tied matches came back in swapped order. Keep `top` moderate when exact scores or the order of near-ties matter.

Other vector stores may return raw cosine similarity or a distance, so do not compare scores across stores or across indexes.

### Filtering

VectorData defines `Filter` as a filter applied during the search. The top 10 matches of the whole index, filtered afterwards, are not the top 10 matches of the filtered set, so the adapter refuses `Filter` rather than imitating it. EF global query filters and tenant rules do not apply to this search either. When callers may only see part of the data, give them an index that holds only that part.

## Lifetime, transactions and concurrency

- The adapter borrows the context's database facade and owns nothing: it never disposes the context or the driver. Register it per scope, like the context, and **never as a singleton**.
- With `AddDbContextPool`, a context goes back to the pool at the end of its scope and can be leased to another scope. An adapter kept from the earlier scope detects this and throws `InvalidOperationException`; a disposed context throws `ObjectDisposedException`.
- An active context transaction is used and left open; the adapter never commits or disposes a transaction it did not start. Vector indexes do not see changes made earlier in the same transaction.
- One context runs one operation at a time: a second search on the same context while the first is still reading throws `InvalidOperationException`.
- Cancellation stops the search, including while buffered results are being returned.

## What it does not do

No `VectorStore` or collection API, no record writes or deletes, no index creation, no embedding generation, no paging, thresholds or hybrid search. `VectorSearchResult` has no provenance; `QueryVectorNodesAsync` keeps the observed server and index details. The proposed DataRetrieval pipeline in dotnet/extensions#7507 currently takes a `VectorStoreCollection`, so this adapter alone does not plug into it.

## Try it

[Sample 06](https://github.com/joslat/GraphBridge.EFCore/tree/main/samples/06-VectorSearch) creates a small index, registers the adapter in dependency injection and searches through a `MovieFinder` that knows only `IVectorSearchable<MovieHit>`. Workshop lesson [20 · Full-text and vector search](https://github.com/joslat/GraphBridge.EFCore/blob/main/workshop/GraphBridge.Workshop/Lessons/20-Search/README.md) ends with the same search through the interface and checks it against typed vector search.

The design was proposed by Luis Quintanilla in [issue #1](https://github.com/joslat/GraphBridge.EFCore/issues/1).
