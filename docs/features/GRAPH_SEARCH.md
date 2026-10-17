# Typed vector and full-text search

Four `db.Database` methods query an existing Neo4j vector or full-text index and return your projected values with native scores and the observed index provenance. Use them for similarity and text search; results are untracked and never attached to EF.

```csharp
using Microsoft.EntityFrameworkCore;
using Neo4j.Driver;

var text = await db.Database.QueryFullTextNodesAsync("workshop_movie_text", "matrix",
    node => node["title"].As<string>(), limit: 5, cancellationToken: cancellationToken);

var similar = await db.Database.QueryVectorNodesAsync("workshop_movie_embedding", new[] { 0.1, 1.0, 0.9 },
    node => node["title"].As<string>(), limit: 2, cancellationToken: cancellationToken);

foreach (var match in similar.Results)
    Console.WriteLine($"{match.Value}: {match.Score}");
Console.WriteLine(similar.Provenance.SimilarityFunction);   // as configured on the index
```

| Method | Index | Input | Projector |
|---|---|---|---|
| `QueryVectorNodesAsync` | Node vector index | `IReadOnlyList<double>` vector | `Func<INode, T>` |
| `QueryVectorRelationshipsAsync` | Relationship vector index | `IReadOnlyList<double>` vector | `Func<IRelationship, T>` |
| `QueryFullTextNodesAsync` | Node full-text index | Lucene query string | `Func<INode, T>` |
| `QueryFullTextRelationshipsAsync` | Relationship full-text index | Lucene query string | `Func<IRelationship, T>` |

Each returns `GraphSearchResults<T>`: `Results`, a list of `GraphScoredResult<T>` (`Value`, `Score`, `ElementId`), and `Provenance`. Provenance is present even for an empty result: server version and edition, index name and provider, labels or types, properties, vector dimensions and similarity function or full-text analyzer and consistency, and the requested limit. `ElementId` is a diagnostic store identity, not an application key.

To search the same indexes through `Microsoft.Extensions.VectorData`'s `IVectorSearchable<TRecord>`, use the optional [VectorData adapter](VECTOR_DATA.md).

## Requirements

- **Create the index yourself and wait until it is ONLINE.** GraphBridge never creates schema. Before each search it reads the database's server components and index metadata in the same transaction; a missing index, the wrong kind or target, or a vector of the wrong dimensions fails. There is no fallback scan and no retry.
- **Server:** queries use Cypher 25 vector `SEARCH` and the full-text query procedures. The capability check admits known Neo4j 2026 releases for vector search, and 2026.09 or later for full-text; other servers get `NotSupportedException`. On those, write the search as [native Cypher](NATIVE_CYPHER.md). Tested profiles are in the [version and capability matrix](../reference/COMPATIBILITY.md#neo4j-servers-and-editions).
- **Index names** follow the identifier rule `[A-Za-z_][A-Za-z0-9_]*` and must match observed metadata.

## Inputs, limits and scores

| Item | Rule |
|---|---|
| Vector | 1 to 4,096 finite coordinates in float32 range, copied before I/O, matching the index's dimensions. Cosine needs a nonzero vector. No embedding generation or normalization. |
| Full-text query | Lucene syntax, including fields, phrases and operators; not a substring search. Nonblank, at most 16,384 characters; invalid syntax is a server error. The index's analyzer and eventual-consistency setting affect results. |
| `limit` | 1 to 10,000, default 10. Applied in the search itself, the returned rows and the client buffer. It bounds rows, not index work or property bytes. |
| Vector score | Native approximate similarity in [0, 1]. Neo4j 2026 vector indexes use binary quantization by default. Observed on 2026.09: scores at limits of 3 and 100 equal the exact similarity, but at a limit of 10,000 they are approximations (off by up to about 0.01), and near-ties can swap order. |
| Full-text score | Native relevance. |

Results are sorted by descending score; ties have no promised order. Scores from different indexes, queries or search kinds are not comparable, and no hybrid ranking or exact nearest-neighbor guarantee is implied. Query text and vectors are parameters.

## Transactions and visibility

Metadata checks and search run in one transaction, following the [native Cypher rules](NATIVE_CYPHER.md#transactions): an explicit EF transaction is borrowed and left to you; otherwise the call owns its transaction and commits after projection succeeds. A projector failure or cancellation rolls back an owned transaction.

Vector search does not see index changes made earlier in the same transaction. Full-text freshness depends on the index's consistency setting. EF query filters and tenant rules do not apply: index membership and filtering are part of your search design. The call is traced as `GraphBridge.Search`; query text, vectors and projected values are not logged.

## Try it

- Workshop lesson [20 · Full-text and vector search](../../workshop/GraphBridge.Workshop/Lessons/20-Search/README.md): creates both indexes, then searches them.

Evidence: [verification summary](../../tests/verification/VERIFICATION_SUMMARY.md). Design: [ADR-0033](../architecture/adr/ADR-0033.md). Neo4j reference: [SEARCH](https://neo4j.com/docs/cypher-manual/current/clauses/search/), [vector indexes](https://neo4j.com/docs/cypher-manual/current/indexes/search-indexes/vector-indexes/), [full-text indexes](https://neo4j.com/docs/cypher-manual/current/indexes/search-indexes/full-text-indexes/).
