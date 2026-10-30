# 08 · GraphRAG novel

Extract the characters, places, groups and events of *Alice's Adventures in Wonderland* into a typed graph, with who says what. The sample uses the experimental [GraphRAG toolkit](../../src/GraphBridge.GraphRag/README.md) with its standard novel ontology.

Every mention and every spoken line keeps the exact bytes it came from. The extraction is checked before anything is written: an unknown kind, a relationship the ontology doesn't allow, or a quote that isn't in the passage is pruned, and the receipt says why. Placeholder names such as "the man" never merge into one character.

**Three ways to run it:**
- **Offline, by default.** A hashing embedder and an extractive chat client stand in for the models, so it needs no model and no key. Extraction replays the reply that GLM-5.3-Flash gave for the shipped opening of chapter I, recorded on 25 October 2026. Any other passage extracts nothing offline.
- **Replaying a real model.** With `GRAPHBRIDGE_AI_MODE=replay`, every call is answered from [`data/novel-recordings.json`](data/novel-recordings.json), at zero calls: the excerpt's embeddings and extraction, and the answers to the two questions the tests ask ("What did the Rabbit take out of its waistcoat-pocket?" and "What is the capital of Mongolia?"). Anything that wasn't recorded fails, and says so. The recordings are keyed by a hash of each request and keep the model's name and the date, but no key and no endpoint.
- **A real model.** Name the provider with `AI_INFERENCE_PROVIDER`, the same variables AgentEval and AgentMemory read. Bitdeer needs only its key:

  ```text
  AI_INFERENCE_PROVIDER=bitdeer
  BITDEER_API_KEY=<your key>
  ```

  The model defaults to `zai-org/GLM-5.3-Flash` and the embeddings to `BAAI/bge-m3` (1,024 dimensions), at `https://api-inference.bitdeer.ai/v1`; `BITDEER_MODEL`, `BITDEER_EMBEDDING_MODEL` and `BITDEER_ENDPOINT` change them. `openai`, `openai-compatible` and `ollama` work the same way, with `AI_EMBEDDING_DIMENSIONS` for an embedding model the sample doesn't know. A named provider with a variable missing stops with the variable's name. Without `AI_INFERENCE_PROVIDER` the sample stays offline, even when a key is set. The client is about two hundred lines in [`samples/GraphRagShared`](../GraphRagShared/SampleModels.cs), so the sample needs no provider package.

  A reasoning model thinks before it answers, and you pay for that. On the excerpt, GLM-5.3-Flash took 820 input and 6,732 output tokens to extract one passage, 5,883 of them reasoning.

A named provider wins: with `AI_INFERENCE_PROVIDER` set, `GRAPHBRIDGE_AI_MODE=replay` is ignored, the calls go to the model, and the sample says so on its first line. The schema names the embedding model, so a database set up offline can't then run with a model, or the reverse: give each mode its own empty database.

Point the four `GRAPHBRIDGE_NEO4J_*` variables at an empty, disposable Neo4j 2026.09 or later database. Give each GraphRAG sample an empty database of its own: its schema is the database's first migration. Then:

```text
dotnet run --project samples/08-GraphRagNovel -- setup
dotnet run --project samples/08-GraphRagNovel -- ingest
dotnet run --project samples/08-GraphRagNovel -- characters
dotnet run --project samples/08-GraphRagNovel -- ask "What did the Rabbit take out of its waistcoat-pocket?"
dotnet run --project samples/08-GraphRagNovel -- merge
dotnet run --project samples/08-GraphRagNovel -- cleanup
```

- **`setup`** previews the schema plan, then applies it as a migration. The plan has a constraint on each class and mention type, the passage vector and full-text indexes, and a full-text index on names. On a database that already has other migrations, it applies nothing, says why and exits 2.
- **`ingest`** reads the shipped excerpt, and `ingest file.txt` any plain-text file of yours. The commands below extract the whole book with your model:

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
- **`merge`** lists [merge suggestions](../../docs/graphrag/MAINTAIN.md#merging-duplicates): characters or places that arrived under two names, such as "Caterpillar" and "the Caterpillar". It only reads, and previews what merging the same-name pairs would move. `merge apply` merges those pairs, never the alias ones, which inherit the model's mistakes, and prints one run per merge, last first, the order to undo them in; `merge undo run` reverses one, and refuses while a later merge into the same thing stands. If a merge fails, it says which and why, and prints the undo commands for the merges written before it. The shipped excerpt is too short for duplicates: run it after `ingest book`.
- **`cleanup`** removes the book and the things no longer mentioned anywhere. The schema stays; dropping it is a reviewed migration of its own.

The graph uses labels of its own (`Book`, `Chapter`, `Passage`, and the novel's `Character`, `Place`, `Group` and `Event`), so its nodes never mix with those of samples 07 and 09, and its `cleanup` removes only the book's.

## The whole book with a real model

Run on 25 October 2026 with `zai-org/GLM-5.3-Flash` and `BAAI/bge-m3` on Bitdeer, four extraction calls at a time. The calls are recorded in [`data/novel-recordings.json`](data/novel-recordings.json), so `GRAPHBRIDGE_AI_MODE=replay` with `ingest book` gives the same graph at zero calls once you've fetched the book.

| | |
|---|---|
| Passages | 41, of 4 KB at most |
| Calls | 2 embedding calls (42,668 tokens) and 41 extraction calls; 7 calls retried after transient failures |
| Extraction tokens | 60,583 input; 733,516 output, of which 652,802 reasoning |
| Written | 150 things, 545 mentions, 130 facts and 844 quotes; 92 characters, 25 places, 9 kin facts |
| Unreadable replies | None quarantined, none needing a second ask |
| Cost preview | 41 extraction calls, as made; input tokens within 5.9% |

What the checks pruned, with the reason the receipt gives:

| Item | Reason | Count |
|---|---|---|
| Relationship | `pattern-not-allowed`: a type between kinds the ontology doesn't allow | 66 |
| Relationship | `endpoint-not-found`: an end that isn't a thing kept from the passage | 16 |
| Quote | `limit`: over 50 quotes in one passage | 13 |
| Mention | `quote-not-found`: a phrase not in the passage | 9 |
| Thing | `ungrounded`: none of its phrases in the passage | 8 |
| Quote | `quote-not-found` | 5 |
| Quote | `endpoint-not-found`: a speaker that isn't a kept character | 1 |

All twelve main characters are there: Alice, the White Rabbit, the Caterpillar, the Duchess, the Cheshire Cat, the Hatter, the March Hare, the Dormouse, the Queen and King of Hearts, the Mock Turtle and the Gryphon. Alice speaks 323 of the 844 quotes, the King 48 and the Mock Turtle 40.

Names resolve by exact match, so some characters appear twice: "the Hatter" and "Hatter", "the Gryphon" and "Gryphon", "White Rabbit" and "the White Rabbit", "the Queen" and "the Queen of Hearts". [Merge suggestions](../../docs/graphrag/MAINTAIN.md#merging-duplicates) list them for review: 27 on this graph, of which 26 are one character or place. The other is "Mabel" as Alice, because the model gave Alice that alias. Nothing is merged automatically.

A first run, before the instructions asked for each quote to be one unbroken stretch of the passage, lost 290 of its 752 quotes: the model joined speech that narration interrupts, as in “I can’t help it,” said Alice very meekly: “I’m growing.”, into one quote that isn't in the text, and the check refused it.
