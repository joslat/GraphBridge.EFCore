# Limits and performance

What the toolkit bounds, and what it measured on three corpora. GraphBridge GraphRAG is experimental: these are measurements on one machine, not guarantees.

## Bounds the toolkit enforces

Every bound has a default and a maximum. A bound reached by ingestion, retrieval, an answer or Text2Cypher is reported in the receipt or the evidence: retrieval and Text2Cypher name it in `LimitsReached`. The bounds marked *cut* below are applied without a report: they cut what a model, an agent or a client asked for to a size the toolkit accepts.

| Area | Option | Default | Range |
|---|---|---|---|
| Ingestion | `MaxDocuments` per run | 10,000 | 1 to 100,000 |
| | `MaxDocumentBytes` | 16 MiB | 1 KiB to 256 MiB |
| | `EmbeddingBatchSize` | 32 | 1 to 256 |
| | `Lanes`, embedding calls in parallel; documents are ingested one at a time | 2 | 1 to 8 |
| | `LeaseDuration` per document, renewed while it's worked on | 10 minutes | 1 second to 1 day |
| Chunking | `MaxUtf8Bytes` per passage | 2,048 | 64 to 16,384 |
| | `OverlapParagraphs` | 1 | 0 to 4 |
| Embeddings | dimensions | — | 1 to 4,096 |
| Extraction | `Lanes`, passages in parallel | 2 | 1 to 8 |
| | things, relationships and quotes per passage | 50, 100, 50 | 1 to 500, 0 to 1,000, 0 to 500 |
| | quarantined reply kept for diagnosis | 2,000 characters | 0 to 100,000 |
| Retries | `MaxRetries` | 4 | 0 to 10 |
| Retrieval | `CandidatesPerRoute` | 20 | 1 to 256 |
| | `MaxItems` | 8 | 1 to 128 |
| | `MaxUtf8Bytes` of evidence | 16,384 | 64 to 65,536 |
| | `NeighbourChunks` | 1 | 0 to 4 |
| | Cypher expansion rows | 256 | fixed |
| Follow-ups | earlier turns used: the newest, reported as `HistoryTurns` | 6 | fixed |
| | each earlier answer in the rewrite prompt, *cut* | 1,000 characters | fixed |
| | the rewritten question; a longer one is `unreadable` and the question is retrieved as asked | 2,000 characters | fixed |
| Text2Cypher | `MaxRows`, reported as `GraphQueryRows` | 50 | 1 to 1,000 |
| | items of a returned list, reported as `GraphQueryListItems` | 1,000 | fixed |
| | characters of a returned string, reported as `GraphQueryText` | 10,000 | fixed |
| | `Timeout` | 10 seconds | 1 second to 5 minutes |
| | `MaxQueryLength` | 4,000 characters | 100 to 100,000 |
| Scoped search | IDs in a scope, or documents a metadata or Cypher scope keeps | 10,000 | fixed |
| | `ScopeExactChunks`, scored exactly below this | 10,000 | 0 to 100,000 |
| | candidates an over-fetch asks for | at most 10,000 | fixed |
| Endpoints | `MaxQuestionLength` | 2,000 characters | 1 to 10,000 |
| | passages from `search`; another `limit` is a 400 | 5 | 1 to 20 |
| | `history` turns sent to `ask`; more is a 400 | 20 | fixed |
| | each history turn: its question, and its answer; longer is a 400 | `MaxQuestionLength`, and 4 times it | fixed |
| Agent tools | passages from `search_documents`, *cut* to the range | as asked | 1 to 20 |
| | things from `find_things`, *cut* | 10 | fixed |
| | facts from `related_facts`, *cut* | 50 | fixed |
| Re-embedding | backfill batch | 64 | 1 to 1,000 |
| Removal | documents in one transaction | 100, and at most 8 MiB of their text unless one is larger | fixed |
| | facts listing the documents of one transaction: a batch over it is halved and tried again, and one document over it is refused | 100,000 | fixed |
| Merge suggestions | things read per class | 50,000 | fixed |
| PDF | `MaxFileBytes` | 64 MiB | 1 KiB to 1 GiB |
| | `MaxPages`; a PDF with more is `document-too-large` | 5,000 | 1 to 100,000 |

### Where each option is set

| Options | Set them in |
|---|---|
| Layout, ontology | `modelBuilder.AddGraphRag(dimensions, o => { o.Layout = ...; o.Ontology = ...; })`, `GraphRagModelOptions` |
| Ingestion, chunking, extraction, retries, retrieval defaults | `services.AddGraphRag<TContext>(set, o => { o.Ingestion.Lanes = 4; o.Chunking.MaxUtf8Bytes = 4096; o.Extraction.Lanes = 4; o.Retry.MaxRetries = 6; o.Retrieval.MaxItems = 12; })` |
| One retrieval or answer | a `GraphRagRetrievalOptions` passed to `RetrieveAsync` or `AnswerAsync`: routes, scope, history and the retrieval bounds |
| Text2Cypher | `services.AddGraphRagText2Cypher<TContext>(o => { o.MaxRows = ...; o.Timeout = ...; o.MaxQueryLength = ...; })` |
| PDF | `services.AddGraphRagPdf(o => o.MaxFileBytes = ...)` |
| Endpoints | `app.MapGraphRag<TContext>("/graphrag", o => { o.MaxQuestionLength = ...; o.Retrieval = ...; o.ScopeFor = http => ...; })` |
| DataIngestion | `new GraphBridgeChunkWriter(ingestion, new GraphBridgeChunkWriterOptions { ChunkerName = ..., TextOrigin = ... })` |
| The extraction model | an `IChatClient` keyed `GraphRagServiceKeys.ExtractionChatClient`, or the container's unkeyed one |

Every option is validated when it's used; a value out of range throws `ArgumentOutOfRangeException` naming it. The bounds marked fixed aren't options.

## Measured on three corpora

Measured on 25 and 26 October 2026, on a Windows 11 workstation with 20 logical processors, against Neo4j 2026.09.0 Community in Docker. The database was kept deliberately small: a 512 MB heap and a 256 MB page cache, in a container capped at 1.5 GB. The generated corpus and the docs used the default ingestion settings (2 lanes, 2 KB passages); *Alice* ran in the real-model tests, with 4 KB passages and four extraction lanes. The harness for the other two is [`benchmarks/GraphBridge.GraphRag.Scale`](../../benchmarks/GraphBridge.GraphRag.Scale/Program.cs). It stops if the machine's free memory falls below 5 GB, and removes everything it wrote.

| | The whole of *Alice* | A generated corpus | GraphBridge's docs |
|---|---|---|---|
| Size | 151 KB, 41 passages of up to 4 KB | 4,000 documents, 17.1 MB, 20,000 passages | 93 files, 0.8 MB, 768 passages |
| Models | GLM-5.3-Flash and bge-m3 | none: hashed embeddings | bge-m3 |
| Ingestion | 41 extraction calls and 2 embedding calls, about 25 minutes, most of it the model's reasoning | 15.9 minutes: 4.2 documents, 21 passages a second | 77 s, 96 embedding calls (273,048 tokens) |
| Retrieval, median and 95th percentile | not measured | 170 ms and 492 ms (200 questions) | 348 ms and 603 ms, with the question embedded by the model (10 questions) |
| Scoped to 10 documents | not measured | 135 ms and 495 ms | 297 ms and 396 ms |
| Ingesting again, unchanged | 0 calls | 88 s | 1.6 s |
| Health report | not measured | 1.1 s, no findings | 0.4 s, no findings |
| Re-embedding backfill | not measured | 19,500 passages in 10.5 s | not run |
| Removal | not measured | 100 documents in 5.3 s; the other 3,900 in 42 s. Before batching: 68 s and 23 minutes | 83 documents in 12 s, before batching (not run again: it needs the model's embeddings) |
| Harness memory | | 181 MB at most | 139 MB at most |

What the numbers say:
- **Removal runs in batches** of up to 100 documents per transaction, about 11 ms a document on the generated corpus. It still deletes by key, in pages, so only the documents' own pieces are touched; one document at a time took about a third of a second.
- **Ingestion runs one transaction and one lease per document,** with the unchanged check and the write. It isn't tuned for bulk loads.
- **Retrieval stays under half a second at the 95th percentile with 20,000 passages,** on a page cache too small to hold them. With a real model, embedding the question adds the provider's latency.
- **The *Alice* numbers** are in [sample 08](../../samples/08-GraphRagNovel/README.md#the-whole-book-with-a-real-model): tokens, what was written, and what was pruned and why.

## Not claimed

- **Nothing over 50,000 passages** has been run, and no throughput or latency is promised: the numbers above are one machine and one configuration.
- **Concurrency:** each measurement ran alone. Several runs at once are safe, because each document is leased, but their speed wasn't measured.
- **Merges never failing:** a merge plan can, rarely, fail at one merge with nothing of that merge written; apply the rest again ([merging duplicates](MAINTAIN.md#merging-duplicates)).
- **A scope on every read:** a retrieval scope, and the endpoints' `ScopeFor`, cover `ask`, `search` and `documents/{id}`. The agent tools `find_things` and `related_facts` read the things and facts of the whole graph, and Text2Cypher reads the whole database. In an application with several tenants, don't give those tools to an agent, or keep each tenant in a database of its own ([agents and APIs](AGENTS.md#endpoints)).
- **Relevance at scale:** the generated corpus is made-up words, so it measures speed, not answer quality. Answer quality is measured on the [evaluation corpus](EVALUATE.md).
