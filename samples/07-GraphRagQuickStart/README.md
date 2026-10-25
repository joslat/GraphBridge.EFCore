# 07 · GraphRAG quick start

Ingest GraphBridge's own documentation into Neo4j, then ask questions and get answers whose every quote is checked against the stored bytes, or an honest refusal. Uses the experimental [GraphRAG toolkit](../../src/GraphBridge.GraphRag/README.md).

It runs offline: a hashing embedder stands in for an embedding model, and an extractive chat client answers with the evidence line that shares the most words with the question. No model, no key. To use real models, register your own `IEmbeddingGenerator<string, Embedding<float>>` and `IChatClient` in `QuickStart.AddQuickStart`, and change the embedding set to your model's name and dimensions.

Point the four `GRAPHBRIDGE_NEO4J_*` variables at a disposable Neo4j 2026.09 or later database, then:

```text
dotnet run --project samples/07-GraphRagQuickStart -- setup
dotnet run --project samples/07-GraphRagQuickStart -- ingest
dotnet run --project samples/07-GraphRagQuickStart -- ask "How are vector indexes created in migrations?"
dotnet run --project samples/07-GraphRagQuickStart -- ask "What is the capital of Mongolia?"
dotnet run --project samples/07-GraphRagQuickStart -- cleanup
```

- `setup` previews the schema plan (constraints, a vector index and a full-text index), then applies it as a migration and waits until the indexes are ONLINE.
- `ingest` reads the `docs` folder's Markdown. Run it again and every unchanged file is skipped.
- `ask` prints the answer and each citation's document, byte span and quote. The second question isn't in the docs, so it is refused.
- `cleanup` removes what the sample ingested. The schema stays: dropping indexes is a reviewed migration of its own.
