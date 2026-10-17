# 06 Vector search through VectorData

A small console application that searches a Neo4j vector index through `Microsoft.Extensions.VectorData`'s `IVectorSearchable<MovieHit>`, using the optional `GraphBridge.EntityFrameworkCore.VectorData` adapter.

| File | Knows about |
|---|---|
| [`MovieFinder.cs`](MovieFinder.cs) | Only `IVectorSearchable<MovieHit>`. Another vector store could replace GraphBridge without changing it. |
| [`MovieSearchSetup.cs`](MovieSearchSetup.cs) | Application setup: the GraphBridge adapter, the index name, the stored properties and the scoped registration. |
| [`Program.cs`](Program.cs) | Commands: create the index and data, search, clean up. |

Each movie stores a toy four-number "taste" embedding: science fiction, romance, comedy and crime. A real application would get embeddings from an embedding generator, such as an `IEmbeddingGenerator<string, Embedding<float>>`.

## Run it

Use a disposable Neo4j 2026 database only; the commands create and drop the `vector_sample_movie_taste` index and `VectorSampleMovie` nodes. Set `GRAPHBRIDGE_NEO4J_URI`, `GRAPHBRIDGE_NEO4J_DATABASE`, `GRAPHBRIDGE_NEO4J_USERNAME` and `GRAPHBRIDGE_NEO4J_PASSWORD` to the [owned disposable fixture](../../eng/README.md), then:

```powershell
dotnet run --project samples/06-VectorSearch -- setup
dotnet run --project samples/06-VectorSearch -- search            # pure science fiction: 1,0,0,0
dotnet run --project samples/06-VectorSearch -- search 0.1,0.9,0,0
dotnet run --project samples/06-VectorSearch -- cleanup
```

The first search prints:

```text
0.989  Interstellar (2014)
0.972  Arrival (2016)
0.799  Her (2013)
```

Scores are Neo4j's normalized cosine similarity, (1 + cos θ) / 2, so an unrelated movie scores about 0.5, not 0. See the [VectorData guide](../../docs/features/VECTOR_DATA.md) for supported inputs, refused options, lifetime and transactions.

## Verification

`ProviderVectorDataTests.Sample_consumer_finds_movies_through_IVectorSearchable_only` runs `MovieFinder` and the registration from `MovieSearchSetup` against the disposable fixture. It checks that the results and scores equal GraphBridge's direct vector search.
