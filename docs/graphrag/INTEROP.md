# Interop with neo4j-graphrag

`GraphRagLayout.Neo4jGraphRag` writes the document and chunk layer with the names Neo4j's Python [`neo4j-graphrag`](https://github.com/neo4j/neo4j-graphrag-python) library uses, checked against its version 1.22.0:
- `(:Chunk)-[:FROM_DOCUMENT]->(:Document)` and `(:Chunk)-[:NEXT_CHUNK]->(:Chunk)`;
- chunk properties `index`, `text` and `embedding`, and `id`, which the library's reader uses.

## What's tested

An interop suite runs neo4j-graphrag 1.22.0 and GraphBridge on one Neo4j 2026.09 database, with the same vectors and no model ([how to run it](../../tests/interop/python/README.md)). The library's own pipeline builds its graphs, with one step replaced: its writer creates relationships with APOC, which the test database doesn't have, so the suite's writer uses a plain `MERGE` and fails loudly where the library's would fail silently:
- **GraphBridge adopts a neo4j-graphrag graph:** the dry run changes nothing neo4j-graphrag reads; after apply, answers cite the source files' bytes; neo4j-graphrag finds the same passages before apply, after it and after undo.
- **neo4j-graphrag on a GraphBridge graph,** with renamed labels on both sides: it counts the same documents, chunks and links. Its vector retriever finds the same top passages as GraphBridge, with the same scores. Its hybrid retriever runs on GraphBridge's vector and full-text indexes.
- **GraphBridge on a neo4j-graphrag graph:** its health report counts the same documents and chunks. Its reset refuses the graph and removes nothing, because no GraphBridge run wrote it.
- **neo4j-graphrag reads the things GraphBridge extracts,** stored in its entity shape: it counts the same entities and mentions, and its `VectorCypherRetriever` returns each passage with exactly the things GraphBridge extracted from it, through `FROM_CHUNK` and `name`.
- **Both stacks embed a text into the same vector,** so the comparisons above are like for like.

Retrieving and answering from a graph neo4j-graphrag built needs more than its names. Its chunks have no ID, byte positions or ingestion run, and GraphBridge's citations rely on all three. Adopt mode adds them.

## Adopt a graph neo4j-graphrag built

`GraphRagAdoption.Plan` takes over a lexical graph neo4j-graphrag wrote, so GraphBridge can retrieve and answer from it. The library keeps reading the graph as before.

```csharp
var layout = GraphRagLayout.Neo4jGraphRag with { VectorIndexName = "chunk_embeddings" };   // the index neo4j-graphrag created
var plan = GraphRagAdoption.Plan(layout, embeddingSet, new GraphRagAdoptionOptions
{
    SourceFiles = new Dictionary<string, string> { ["report.md"] = "docs/report.md" }      // by the document's path property
});
// plan.PrepareSteps go into your migration plan: key constraints and the full-text index.
var preview = await plan.PreviewAsync(db);     // the dry run: reads, writes nothing
var receipt = await plan.ApplyAsync(db);       // one run
await plan.UndoAsync(db, receipt.RunId);       // removes exactly what that run added
```

- **The dry run** counts the documents, chunks and links, says which documents their source files place, and lists what blocks adoption: chunks without a document, two documents with one path, a missing vector index or one of other dimensions, or nodes a GraphBridge run already owns.
- **Apply** adds keys and a run to documents; the document, byte span and embedding set to chunks; and the run to their relationships. It writes nothing else: the text, index, embedding, the library's labels and properties, and its indexes stay.
- **Grounding:** when a document's source file holds every chunk byte-exact, in order, the chunks get the file's byte spans and their citations are source-grounded, as for ingested documents. Otherwise they are *chunk-grounded*: a citation's span is inside the stored chunk's text, and the citation says so ([answers](ANSWERS.md#how-citations-are-checked)).
- **Indexes:** Neo4j allows one vector index per label and property, so `GraphRagLayout.VectorIndexName` names the one neo4j-graphrag created, and both stacks search it. `FullTextIndexName` does the same for a full-text index.
- **Undo** removes the properties and the run that adoption added. A document GraphBridge has since ingested again belongs to the later run, and stays.

## One graph, two stacks: entities

To let neo4j-graphrag's retrievers read what GraphBridge extracts, store the things in its entity shape as well as its lexical names:

```csharp
modelBuilder.AddGraphRag(1536, o =>
{
    o.Layout = GraphRagLayout.Neo4jGraphRag;
    o.Ontology = GraphRagOntologies.Generic.WithNeo4jGraphRagEntities();
});
```

`WithNeo4jGraphRagEntities()` keeps the ontology the model sees and changes how it is stored:
- every class's nodes also carry `__Entity__` and `__KGBuilder__`;
- every mention is a `FROM_CHUNK` relationship from the thing to its chunk;
- the name is stored as `name`.

A Python application then reads GraphBridge's graph as its own, for example with a `VectorCypherRetriever` that brings each passage's entities:

```python
retrieval_query = (
    "WITH node, score OPTIONAL MATCH (node)<-[:FROM_CHUNK]-(entity:__Entity__) "
    "RETURN node.text AS text, score, collect(DISTINCT entity.name) AS entities"
)
# The vector index GraphBridge created: embeddingSet.VectorIndexName on the C# side, which with the default
# layout is "graphrag_chunk_embedding_" followed by the embedding set's ID.
retriever = VectorCypherRetriever(driver, index_name, retrieval_query)
```

The interop suite runs this query (`interop.py entities`) against a graph GraphBridge extracted. GraphBridge keeps what it adds, such as each mention's byte span and quote, and each thing's aliases, as further properties neo4j-graphrag ignores.

## What neo4j-graphrag writes

- **Every node gets `__KGBuilder__`,** the documents and chunks included; extracted things also get `__Entity__`.
- **Chunks have no `id`:** the writer keeps an ID only while it writes, and removes it afterwards.
- **Documents** have `path` and `createdAt`, and any metadata.
- **It needs APOC:** relationships are written with `apoc.merge.relationship`. Without the APOC plugin, the writer writes the nodes, then reports a failure for the relationships, without raising an error. The Neo4j image ships APOC core; `NEO4J_PLUGINS='["apoc"]'` enables it.

To write the same names from GraphBridge, choose the layout when you map the model:

```csharp
modelBuilder.AddGraphRag(1536, o => o.Layout = GraphRagLayout.Neo4jGraphRag);
```

## Caveats

- **Sections:** neo4j-graphrag has none. They keep GraphBridge's names (`Section`, `SECTION_OF`, `IN_SECTION`), which that library ignores.
- **Extracted things go one way:** with [the entity shape](#one-graph-two-stacks-entities), neo4j-graphrag reads what GraphBridge extracts. GraphBridge doesn't read the entities neo4j-graphrag extracts: they have no stable key, spans or run, and adoption covers documents and chunks only.
- **neo4j-graphrag's entity resolver merges nodes,** which would remove GraphBridge's things and their keys behind its back. Use GraphBridge's [merge suggestions](MAINTAIN.md#merging-duplicates) on a graph GraphBridge writes.
- **Scores:** retrieval fuses ranks, not neo4j-graphrag's normalised scores, so the two libraries can order hybrid results differently.
- **Labels:** GraphBridge maps a label to one entity type. If your application already maps `Document` or `Chunk`, pick other labels in the layout.
