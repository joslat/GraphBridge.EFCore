# Retrieve

`IGraphRagRetriever.RetrieveAsync(question, options)` returns evidence: chunks with exact spans, labelled context, and a trace of where each item came from.

## Routes and fusion

- **Vector:** the question is embedded with the toolkit's generator and searched in the embedding set's vector index.
- **Full-text:** the question's words are searched in the chunk text index.
- **Fusion:** reciprocal-rank fusion (k = 60) keyed by chunk identity, never by text. Each item records its rank and native score in every route (`Routes`).

Presets follow neo4j-graphrag's names: `GraphRagRetrievers.Vector()`, `.Hybrid()`, `.VectorCypher(cypher)` and `.HybridCypher(cypher)`.

## The budget

| Option | Default | Range |
|---|---|---|
| `CandidatesPerRoute` | 20 | 1 to 256 |
| `MaxItems` | 8 | 1 to 128 |
| `MaxUtf8Bytes` of evidence text | 16,384 | 64 to 65,536 |
| `NeighbourChunks` before and after each item | 1 | 0 to 4 |

The budget takes items in rank order. A cut item keeps its exact span, and `OmittedItems` and `LimitsReached` report what didn't fit.

## Context and the Cypher escape hatch

Neighbouring chunks along `NEXT_CHUNK` come back as labelled context, never as citations. `WithCypherExpansion(cypher, parameters)` adds rows from a read-only query, bound to `$chunkIds`, as context too. It returns columns `chunkId` and `text`, runs in a read transaction, and is capped at 256 rows.

## Scoped search

`Scope` keeps retrieval inside part of the graph: a tenant's documents, one report, a chapter, last month's articles.

```csharp
var options = new GraphRagRetrievalOptions { Scope = GraphRagScope.Documents(reportId) };
var evidence = await retriever.RetrieveAsync("What did the crew test?", options);
```

| Scope | Keeps to |
|---|---|
| `GraphRagScope.Documents(ids)` | whole documents |
| `GraphRagScope.Sections(ids)` | the chunks that lie wholly within each section, nested sections included |
| `GraphRagScope.Matching(filter)` | documents by publisher, media type, source URI prefix and published date (`PublishedFrom`, `PublishedBefore`; ISO 8601 text compares as dates); a document must match every value you set |
| `GraphRagScope.FromCypher(cypher, parameters)` | the documents a read-only query returns in a `documentId` column, optionally with `startByte` and `endByte`: use it for your own properties, such as a tenant |

- **Nothing outside the scope becomes evidence or context.** Every route filters on the server, and each item and neighbour is checked against the scope again before it's used. A Cypher expansion gets `$scopeDocumentIds`; it's your query, so it keeps to the scope when it uses them.
- **How it searches:** a scope of up to `ScopeExactChunks` chunks (10,000 by default) is scored exactly, chunk by chunk. A larger one asks the vector index for more candidates, about twice the limit inside the scope and at most 10,000, and keeps those inside. Full-text search always does the latter. An over-fetch can miss a match ranked lower, and reports `VectorScopeOverFetch` or `FullTextScopeOverFetch` in `LimitsReached` when it may have.
- **The evidence says how:** `evidence.Scope` names the scope, how many documents and chunks it holds, and each route's strategy.
- **An empty scope finds nothing,** so an answer is refused without calling the model. A scope takes at most 10,000 IDs; a metadata or Cypher scope that matches more keeps the first by ID and reports `ScopeDocuments`.

Neo4j 2026.09 can filter inside a vector index, but only on properties declared when the index is created, which the toolkit's index doesn't declare. Exact scoring and over-fetching need no schema change.

## Provenance

Evidence carries the server version, the embedding set, the indexes that answered and the similarity function.

## Try it

- [Sample 07](../../samples/07-GraphRagQuickStart/README.md)'s `ask` retrieves by vector and full text, and prints each citation's span.
- The [scope tests](../../tests/GraphBridge.IntegrationTests/GraphRagScopeTests.cs) keep retrieval to documents, sections, metadata and Cypher scopes, live.
