# GraphRAG (experimental)

> **Experimental.** The GraphRAG packages are previews of previews: their API may change in any release, and using them reports `GBRAG001`, which you acknowledge once with `<NoWarn>$(NoWarn);GBRAG001</NoWarn>`.

> **Not on NuGet yet.** The packages ship with a later preview, once they have been verified with a real model. Until then, build them from source; the samples use project references.

GraphBridge's GraphRAG toolkit stores documents as a typed graph in Neo4j through EF Core, and answers questions from them with citations that are checked against the stored text. It is a set of building blocks, not a framework: every piece is an EF entity, a typed query or an interface you can replace.

## Two layers

- **The lexical layer** is always present: documents, sections and chunks, linked child to parent. Every chunk's text is exactly its document's UTF-8 bytes `[StartByte, EndByte)`, and every element carries the ingestion run that wrote it.
- **The domain layer** comes from an optional ontology: things such as people, places and organisations; the facts between them; and who said what. Every mention and quote keeps the exact bytes it came from.

## The lifecycle

| Stage | What the toolkit does | Page |
|---|---|---|
| Create | Map the lexical layer and an ontology into your EF model; declare the schema as migration steps | [Ontologies](ONTOLOGIES.md) |
| Store | Load, chunk with exact positions, embed, extract, write: one transaction per document, resumable | [Ingest](INGEST.md) |
| Retrieve | Vector and full-text search fused by rank, with a budget and a trace | [Retrieve](RETRIEVE.md) |
| Answer | A cited answer whose quotes are located in the stored text, or a refusal | [Answer with evidence](ANSWERS.md) |
| Serve | Tools for agents, the Agent Framework provider, DataIngestion pipelines | [Agents and APIs](AGENTS.md) |
| Maintain | Re-ingest what changed, remove, reset, re-embed, health, tracing | [Maintain](MAINTAIN.md) |
| Evaluate | Deterministic checks of citations, abstention and coverage | [Test and evaluate](EVALUATE.md) |

## What's verified

- **Offline:** the toolkit's tests and the samples, with stand-ins for the models: a hashing embedder, an extractive chat client, and extraction replies written by hand for the samples, not recorded from a model.
- **Live:** the integration suite on Neo4j 2026.09.0, which runs samples 07, 08 and 09 against a real database with the same stand-ins.
- **Not yet:** a real chat or embedding model, Neo4j releases after 2026.09.0, large corpora, and interop with neo4j-graphrag ([Interop](INTEROP.md)).

Start with the [quick start](QUICK_START.md). Interop with Neo4j's Python `neo4j-graphrag` library is on its [own page](INTEROP.md).

## Packages

| Package | Depends on |
|---|---|
| [`GraphBridge.GraphRag`](../../src/GraphBridge.GraphRag/README.md) | The provider and `Microsoft.Extensions.AI.Abstractions` |
| [`GraphBridge.GraphRag.DataIngestion`](../../src/GraphBridge.GraphRag.DataIngestion/README.md) | The core and the `Microsoft.Extensions.DataIngestion` preview |
| [`GraphBridge.GraphRag.AgentFramework`](../../src/GraphBridge.GraphRag.AgentFramework/README.md) | The core and `Microsoft.Agents.AI.Abstractions` |

All need Neo4j 2026.09 or later, and have been tested on 2026.09.0: retrieval uses typed search, which needs Cypher 25, and full-text search needs 2026.09.

## What isn't in the box

- **No model:** the toolkit talks to any `IEmbeddingGenerator` and `IChatClient`; the samples run offline with stand-ins.
- **No hidden prompts or ranking:** prompts are short and visible in the source, and ranking is reciprocal-rank fusion.
- **Not yet:**
  - several labels per entity type;
  - search inside Fluent Cypher, and scoped search;
  - Text2Cypher;
  - adopting graphs other tools built;
  - PDF and Office readers beyond DataIngestion's.

## What the provider doesn't do yet

Where GraphBridge lacked a feature, the toolkit uses a named workaround: native Cypher marked `// Workaround for GR-06` in the source, and a line here. The list is ordered by what closing each gap would remove.

1. **Shared identified relationship types.** An identified relationship type has one owner, so each ontology class gets its own mention type (`MENTIONED_IN_PERSON`), neo4j-graphrag's shared `FROM_CHUNK` can't be written, and a fact can't hold one relationship per assertion.
2. **Correlated subqueries and string functions in LINQ** (GBQ120). These maintenance steps use bounded native queries:
   - finding orphans, and things no mention points to;
   - counting stale facts;
   - taking a document off its facts.
3. **`Count` over identified relationships** (GBQ170). The samples read the rows instead.
4. **A second mapped embedding property.** Re-embedding writes the staging vectors with native Cypher until cut-over.
5. **Several labels per entity type.** neo4j-graphrag's `__Entity__` label can't be added (see [Interop](INTEROP.md)).
6. **Search inside Fluent Cypher, and scoped search.** Retrieval runs typed search, then expands with a bounded native query; the agent tool `related_facts` reads facts of every type in one native query.
