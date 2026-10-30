# 07 · GraphRAG quick start

Ingest GraphBridge's own documentation into Neo4j, then ask questions and get answers whose every quote is checked against the stored bytes, or an honest refusal. Uses the experimental [GraphRAG toolkit](../../src/GraphBridge.GraphRag/README.md).

**Three ways to run it:**
- **Offline, by default.** A hashing embedder stands in for an embedding model, and an extractive chat client answers with the evidence line that shares the most words with the question. No model, no key.
- **Replaying a real model.** With `GRAPHBRIDGE_AI_MODE=replay`, every call is answered from [`data/quickstart-recordings.json`](data/quickstart-recordings.json), at zero calls. `ingest` then reads `data/docs`, the copy of three documentation pages the calls were recorded on. The recorded questions are "How does a migration create vector and full-text indexes?" and "What is the capital of Mongolia?". Anything else fails, and says so.
- **A real model,** named with `AI_INFERENCE_PROVIDER` as in [sample 08](../08-GraphRagNovel/README.md): `AI_INFERENCE_PROVIDER=bitdeer` with `BITDEER_API_KEY` runs GLM-5.3-Flash and `BAAI/bge-m3`. To use another client, register your own `IEmbeddingGenerator<string, Embedding<float>>` and `IChatClient` in `QuickStart.AddQuickStart`.

A named provider wins: with `AI_INFERENCE_PROVIDER` set, `GRAPHBRIDGE_AI_MODE=replay` is ignored, the calls go to the model, and the sample says so on its first line. The schema names the embedding model, so a database set up offline can't then run with a model, or the reverse: give each mode its own empty database.

Point the four `GRAPHBRIDGE_NEO4J_*` variables (URI, database, username, password) at an empty, disposable Neo4j 2026.09 or later database; the [tutorial](../../docs/graphrag/TUTORIAL.md#1-start-a-disposable-database) starts one in Docker with one command. Give each GraphRAG sample an empty database of its own: its schema is the database's first migration. Every model variable is in [use a real model](../../docs/graphrag/REAL_MODELS.md#the-samples-variables). Then:

```text
dotnet run --project samples/07-GraphRagQuickStart -- setup
dotnet run --project samples/07-GraphRagQuickStart -- ingest
dotnet run --project samples/07-GraphRagQuickStart -- ask "How does a migration create vector and full-text indexes?"
dotnet run --project samples/07-GraphRagQuickStart -- ask "What is the capital of Mongolia?"
dotnet run --project samples/07-GraphRagQuickStart -- cleanup
```

- `setup` previews the schema plan (constraints, a vector index and a full-text index), then applies it as a migration and waits until the indexes are ONLINE. On a database that already has other migrations, it applies nothing, says why and exits 2.
- `ingest` reads the `docs` folder's Markdown, or a folder you name, with its Markdown and PDF files. A PDF is read through its text layer, page by page, so its citations name their pages; a scan without one is refused with `pdf-no-text` ([PDF](../../docs/graphrag/INGEST.md#pdf)). Run it again and every unchanged file is skipped.
- `ask` prints the answer and each citation's document, byte span and quote. The docs don't answer the second question, so a model refuses it, as the replay shows. Offline, the stand-in isn't a model: it quotes the line that shares the most words with the question, and the [tutorial](../../docs/graphrag/TUTORIAL.md) asks this same question, so it answers with that line. The quote is still checked against the stored bytes.
- `cleanup` removes every document stored under the toolkit's default labels, which the sample uses: on the sample's own database, that is what it ingested. The schema stays: dropping indexes is a reviewed migration of its own.

The [quick start](../../docs/graphrag/QUICK_START.md) walks through the same steps in your own project.
