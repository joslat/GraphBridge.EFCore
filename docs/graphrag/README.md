# GraphRAG (experimental)

> **Experimental.** The GraphRAG packages are less settled than the rest of GraphBridge's previews: their API may change in any release, and using them reports `GBRAG001`, which you acknowledge once with `<NoWarn>$(NoWarn);GBRAG001</NoWarn>`.

> **On NuGet as previews.** The five packages are published from `0.2.0-preview.1`. Use them at the provider's exact version: the toolkit needs the provider built from the same source. The samples use project references.

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

- **Offline:** the toolkit's tests and the samples, with stand-ins for the models: a hashing embedder and an extractive chat client. Sample 08's extraction replays a real model's recorded reply; sample 09's offline replies for its fictional articles are written by hand, not recorded from a model.
- **With a real model:** samples 07 to 09, including excerpts of five NASA news releases and an Agent Framework agent, and the whole of *Alice's Adventures in Wonderland* (41 passages), on GLM-5.3-Flash with `BAAI/bge-m3` embeddings through Bitdeer's OpenAI-compatible endpoint, from 25 to 29 October 2026. No extraction reply was unreadable; the cost preview's input tokens were within 6% of the bill's; on 22 labelled questions every citation matched the stored bytes, 91% of answer-or-refuse decisions were right, and retrieval found 81% of the required evidence ([numbers](EVALUATE.md#numbers-with-a-real-model)). The recorded calls replay offline at zero calls. See [Use a real model](REAL_MODELS.md).
- **Live:** the integration suite on Neo4j 2026.09.0, which runs samples 07, 08 and 09 against a real database with the stand-ins; the separate real-model suite replays the recorded real-model runs. On 30 October it also passed on 2026.10.1, with every feature on this page.
- **Scale:** a generated corpus of 20,000 passages, on a deliberately small database: retrieval's 95th percentile under half a second, ingestion about 21 passages a second, and 3,900 documents removed in 42 seconds. See [Limits and performance](LIMITS.md), with every bound the toolkit enforces.
- **Every claim and its evidence:** the [claims table](../../tests/verification/GRAPHRAG_CLAIMS.md).
- **Interop:** an opt-in suite runs GraphBridge and neo4j-graphrag 1.22.0 on one database: each reads the other's graph, and GraphBridge adopts one neo4j-graphrag built ([Interop](INTEROP.md)).
- **Not yet:** a second model provider (a second model family on the same provider ran as a smoke test), corpora over 50,000 passages, and reading the things neo4j-graphrag extracts; it reads GraphBridge's ([entities](INTEROP.md#one-graph-two-stacks-entities)).

Start with the [quick start](QUICK_START.md), or follow the [tutorial](TUTORIAL.md) from an empty database to cited answers on a real model. Interop with Neo4j's Python `neo4j-graphrag` library is on its [own page](INTEROP.md).

## Packages

| Package | Depends on |
|---|---|
| [`GraphBridge.GraphRag`](../../src/GraphBridge.GraphRag/README.md) | The provider and `Microsoft.Extensions.AI.Abstractions` |
| [`GraphBridge.GraphRag.DataIngestion`](../../src/GraphBridge.GraphRag.DataIngestion/README.md) | The core and the `Microsoft.Extensions.DataIngestion` preview |
| [`GraphBridge.GraphRag.AgentFramework`](../../src/GraphBridge.GraphRag.AgentFramework/README.md) | The core and `Microsoft.Agents.AI.Abstractions` |
| [`GraphBridge.GraphRag.AspNetCore`](../../src/GraphBridge.GraphRag.AspNetCore/README.md) | The core and ASP.NET Core |
| [`GraphBridge.GraphRag.Pdf`](../../src/GraphBridge.GraphRag.Pdf/README.md) | The core and PdfPig |

All need Neo4j 2026.09 or later, and have been tested on 2026.09.0 and 2026.10.1: retrieval uses typed search, which needs Cypher 25, and full-text search needs 2026.09.

## Try it

[Sample 07](../../samples/07-GraphRagQuickStart/README.md) answers questions about GraphBridge's own docs; [08](../../samples/08-GraphRagNovel/README.md) extracts a novel; [09](../../samples/09-GraphRagArticles/README.md) adds articles, an agent, endpoints and an explorer page. They run without a model, and the [tutorial](TUTORIAL.md) runs sample 09 with one. To start a project of your own, see the [quick start](QUICK_START.md).

## What isn't in the box

- **No model:** the toolkit talks to any `IEmbeddingGenerator` and `IChatClient`; the samples run offline with stand-ins.
- **No hidden prompts or ranking:** prompts are short and visible in the source, and ranking is reciprocal-rank fusion.
- **No OCR or conversion engine:** scans, recordings and Office files are converted by tools you choose, and their text is ingested as [derived text](INGEST.md#derived-text-ocr-and-transcripts); Office files have a [recipe](AGENTS.md#office-documents) with Microsoft's markitdown.

## What the provider doesn't do yet

Where GraphBridge lacked a feature, the toolkit uses a named workaround: native Cypher marked `// Workaround for GR-06` in the source, and a line here. The list is ordered by what closing each gap would remove.

1. **One relationship per assertion of a fact.** A fact is one relationship between its two things, listing the documents that state it.
2. **Correlated subqueries and string functions in LINQ** (GBQ120). These maintenance steps use bounded native queries:
   - finding orphans, and things no mention points to;
   - counting stale facts;
   - taking a document off its facts.
3. **`Count` over identified relationships** (GBQ170). The samples read the rows instead, and merge suggestions count each thing's mentions in one bounded native query.
4. **A second mapped embedding property.** Re-embedding writes the staging vectors with native Cypher until cut-over.
5. **A filter inside a search.** The provider starts a Fluent Cypher query from a search ([ADR 0057](../architecture/adr/ADR-0057.md)), but the toolkit's retriever keeps its own typed search: scoped retrieval filters by the scope in native queries, and the agent tool `related_facts` reads facts of every type in one native query.
6. **Entities without a key.** Adopt mode reads and updates the nodes another tool wrote, which have no key EF can track, with native queries by element ID.

Closed in the provider while the toolkit was built: several labels per entity type ([ADR 0055](../architecture/adr/ADR-0055.md)), which the [entity shape](INTEROP.md#one-graph-two-stacks-entities) uses for neo4j-graphrag's `__Entity__` and `__KGBuilder__`; identified relationship types that several mappings share ([ADR 0056](../architecture/adr/ADR-0056.md)), which its `FROM_CHUNK` uses; and Fluent queries that start from a search.
