# 08 · GraphRAG novel

Extract the characters, places, groups and events of *Alice's Adventures in Wonderland* into a typed graph, with who says what. The sample uses the experimental [GraphRAG toolkit](../../src/GraphBridge.GraphRag/README.md) with its standard novel ontology.

Every mention and every spoken line keeps the exact bytes it came from. The extraction is checked before anything is written: an unknown kind, a relationship the ontology doesn't allow, or a quote that isn't in the passage is pruned, and the receipt says why. Placeholder names such as "the man" never merge into one character.

**Offline by default.** The sample ships the opening of chapter I with extraction replies written by hand for that passage (not recorded from a model), a hashing embedder and an extractive chat client, so it needs no model and no key. To extract the whole book, point it at an OpenAI-compatible endpoint. That path hasn't been run with a real model yet, so expect rough edges. The client is about a hundred lines in [`samples/GraphRagShared`](../GraphRagShared/SampleModels.cs), so the sample needs no provider package. Set these variables:

| Variable | Example |
|---|---|
| `GRAPHBRIDGE_AI_ENDPOINT` | `https://api.openai.com/v1`, or Ollama's `http://localhost:11434/v1` |
| `GRAPHBRIDGE_AI_KEY` | Sent as a bearer token, when set |
| `GRAPHBRIDGE_AI_CHAT_MODEL` | `gpt-4.1-mini` |
| `GRAPHBRIDGE_AI_EMBEDDING_MODEL`, `GRAPHBRIDGE_AI_EMBEDDING_DIMENSIONS` | `text-embedding-3-small`, `1536` |

Point the four `GRAPHBRIDGE_NEO4J_*` variables at a disposable Neo4j 2026.09 or later database, then:

```text
dotnet run --project samples/08-GraphRagNovel -- setup
dotnet run --project samples/08-GraphRagNovel -- ingest
dotnet run --project samples/08-GraphRagNovel -- characters
dotnet run --project samples/08-GraphRagNovel -- ask "What did the Rabbit take out of its waistcoat-pocket?"
dotnet run --project samples/08-GraphRagNovel -- cleanup
```

- **`setup`** previews the schema plan, then applies it as a migration. The plan has a constraint on each class and mention type, the passage vector and full-text indexes, and a full-text index on names.
- **`ingest`** reads the shipped excerpt. The commands below extract the whole book with your model:

  ```text
  dotnet run --project samples/08-GraphRagNovel -- fetch
  dotnet run --project samples/08-GraphRagNovel -- ingest book
  ```

  `fetch` downloads the book once from [Project Gutenberg](https://www.gutenberg.org/ebooks/11) (eBook #11, public domain in the USA), next to the build output. It keeps only the text between Project Gutenberg's start and end markers. Run `ingest` again and an unchanged book is skipped.
- **`characters`** prints, through typed LINQ over `Character`, `KinOf`, `Says` and `Place`:
  - each character with their aliases and how often they are mentioned;
  - who is kin to whom;
  - every spoken line with its speaker.
- **`ask`** answers from the passages, with every quote checked against the stored bytes, or refuses.
- **`cleanup`** removes the book and the things no longer mentioned anywhere. The schema stays; dropping it is a reviewed migration of its own.

The graph uses labels of its own (`Book`, `Chapter`, `Passage`, and the novel's `Character`, `Place`, `Group` and `Event`), so it can share a database with samples 07 and 09.
