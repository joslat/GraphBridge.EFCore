# Interop with neo4j-graphrag

`GraphRagLayout.Neo4jGraphRag` writes the document and chunk layer with the names Neo4j's Python [`neo4j-graphrag`](https://github.com/neo4j/neo4j-graphrag-python) library uses, checked against the source of its version 1.22.0:
- `(:Chunk)-[:FROM_DOCUMENT]->(:Document)` and `(:Chunk)-[:NEXT_CHUNK]->(:Chunk)`;
- chunk properties `id`, `index`, `text` and `embedding`.

> **Not tested across the two stacks yet.** No test has run neo4j-graphrag on a graph GraphBridge built, or GraphBridge on a graph neo4j-graphrag built. The names match; whether each library can use the other's graph is unverified. Reading a graph that neo4j-graphrag built is unlikely to work today: its chunks don't carry the byte positions (`StartByte`, `EndByte`) and the ingestion run that GraphBridge's citations rely on.

```csharp
modelBuilder.AddGraphRag(1536, o => o.Layout = GraphRagLayout.Neo4jGraphRag);
```

## Caveats

- **Sections:** neo4j-graphrag has none. They keep GraphBridge's names (`Section`, `SECTION_OF`, `IN_SECTION`), which that library ignores.
- **Extracted things:** neo4j-graphrag gives every extracted node a shared `__Entity__` label, and links it to its chunk with one `FROM_CHUNK` type. GraphBridge maps one label per entity type, and an identified relationship type to one owner. So its things have their class label only, and their mentions use one type per class (`MENTIONED_IN_PERSON`). Both are on the [gap list](README.md#what-the-provider-doesnt-do-yet).
- **Scores:** retrieval fuses ranks, not neo4j-graphrag's normalised scores, so the two libraries can order hybrid results differently.
- **Labels:** GraphBridge maps a label to one entity type. If your application already maps `Document` or `Chunk`, pick other labels in the layout.
