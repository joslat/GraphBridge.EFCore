# 20 · Full-text and vector search

**Goal:** search movies by text and by vector similarity through existing indexes, with scores and provenance, then run the same vector search through `Microsoft.Extensions.VectorData`.
**Track:** full · **Mode:** needs Neo4j 2026.09 or later · **Time:** ~20 min · **Builds on:** 15

## Concepts

- `QueryFullTextNodesAsync` and `QueryVectorNodesAsync` query an **existing, ONLINE** index and return typed results with native scores.
- Every result set carries **provenance**: which index answered, its kind, its similarity function and the server version.
- Indexes are schema, and GraphBridge never creates schema. The workshop host creates them explicitly, just as an application's deployment step would.
- The optional `GraphBridge.EntityFrameworkCore.VectorData` package exposes the same vector index as `IVectorSearchable<T>` from `Microsoft.Extensions.VectorData`. Retrieval code written against that interface searches the graph without knowing about Neo4j or GraphBridge. Luis Quintanilla proposed this adapter in [issue #1](https://github.com/joslat/GraphBridge.EFCore/issues/1).

## Run it

```powershell
dotnet run --project workshop/GraphBridge.Workshop -- run 20
```

The lesson needs Neo4j 2026.x with Cypher 25 (full-text search needs 2026.09 or later). On an older server it explains why and skips the searches.

## Walkthrough

1. **Create the indexes.**

<!-- region: Indexes -->
```csharp
await WorkshopData.ExecuteAsync(connection, """
    CREATE FULLTEXT INDEX workshop_movie_text IF NOT EXISTS FOR (n:Movie) ON EACH [n.title, n.tagline]
    OPTIONS {indexConfig: {`fulltext.eventually_consistent`: false}}
    """, cancellationToken);
await WorkshopData.ExecuteAsync(connection, """
    CREATE VECTOR INDEX workshop_movie_embedding IF NOT EXISTS FOR (n:Movie) ON (n.embedding)
    OPTIONS {indexConfig: {`vector.dimensions`: 3, `vector.similarity_function`: 'cosine'}}
    """, cancellationToken);
```

   The lesson then waits for both indexes to come online and stores toy three-dimensional embeddings (action, philosophy, drama) on each movie with native Cypher. EF neither maps nor touches that property.

2. **Full-text search.**

<!-- region: Text -->
```csharp
var text = await db.Database.QueryFullTextNodesAsync(TextIndex, "matrix",
    node => node["title"].As<string>(), limit: 5, cancellationToken: cancellationToken);
```

3. **Vector search.**

<!-- region: Vector -->
```csharp
var similar = await db.Database.QueryVectorNodesAsync(VectorIndex, new[] { 0.1, 1.0, 0.9 },
    node => node["title"].As<string>(), limit: 2, cancellationToken: cancellationToken);
```

   The query vector stands for "thoughtful drama"; Cloud Atlas is the closest by cosine similarity.

4. **The same search through `Microsoft.Extensions.VectorData`.**

<!-- region: VectorData -->
```csharp
IVectorSearchable<string> search = db.Database.AsNodeVectorSearchable(VectorIndex, node => node["title"].As<string>());
var matches = new List<VectorSearchResult<string>>();
await foreach (var match in search.SearchAsync(new[] { 0.1f, 1.0f, 0.9f }, top: 2, cancellationToken: cancellationToken))
    matches.Add(match);
```

   `AsNodeVectorSearchable` binds the index once; the projector turns each matched node into your record. Everything after the first line only knows `IVectorSearchable<string>`, so in an application it would live in a class that receives the interface through dependency injection, as in [sample 06](../../../../samples/06-VectorSearch/README.md). The lesson checks that the movies, their order and their scores match step 3. In your own application, add the `GraphBridge.EntityFrameworkCore.VectorData` package; the workshop references it as a project.

At the end the lesson removes the embeddings, so the movie graph is back to its seeded state. The indexes stay.

## What you should see

Three Matrix movies with scores, then Cloud Atlas at the top of the vector results, and the same two movies with the same scores through `IVectorSearchable`.

## Try it yourself

1. Search for `"real world"` with quotes. Which movie matches the phrase?
2. Change the query vector to `[1.0, 0.1, 0.1]`. Which movie wins now?
3. Pass `new VectorSearchOptions<string> { Skip = 1 }` to `SearchAsync`. Why does the adapter refuse instead of skipping a result?

## Limits

- Searches read existing indexes only; results are untracked.
- Vector results do not see writes made earlier in the same transaction.
- Full-text queries use Lucene syntax; invalid syntax is a server error.
- The VectorData adapter refuses `Filter`, `Skip`, `ScoreThreshold` and the other search options instead of imitating them.

## Read more

[Graph search](../../../../docs/features/GRAPH_SEARCH.md) · [VectorData search](../../../../docs/features/VECTOR_DATA.md)
