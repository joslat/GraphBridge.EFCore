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

## Provenance

Evidence carries the server version, the embedding set, the indexes that answered and the similarity function.
