# Tutorial: a real model, end to end

This tutorial takes [sample 09](../../samples/09-GraphRagArticles/README.md) from an empty database to answers with checked citations, on a real model. It reads NASA's Artemis II news releases. You ask on the command line, through an agent, and from a streaming endpoint, then clean up. It takes about half an hour.

You rehearse every step first at zero cost, replaying the model calls recorded on 26 October 2026, then run it on your own model.

## What you need

- The repository, cloned, and the .NET SDK 10.0.401, which the repository's `global.json` pins exactly;
- Docker, for a disposable Neo4j;
- for the real run, a key for an OpenAI-compatible endpoint. The recorded run used GLM-5.3-Flash and `BAAI/bge-m3` on Bitdeer.

The commands are PowerShell; in a POSIX shell, set the variables with `export`.

## 1. Start a disposable database

GraphRAG needs Neo4j 2026.09 or later. This container keeps its data in memory, listens on this machine only, and is removed when it stops:

```powershell
$env:GRAPHBRIDGE_NEO4J_PASSWORD = "<a password of at least 8 characters>"
docker run --rm -d --name graphrag-tutorial -p 127.0.0.1:7687:7687 --memory 1536m `
  -e NEO4J_AUTH="neo4j/$env:GRAPHBRIDGE_NEO4J_PASSWORD" `
  -e NEO4J_server_memory_heap_max__size=512m -e NEO4J_server_memory_pagecache_size=256m `
  neo4j:2026.09.0-community
$env:GRAPHBRIDGE_NEO4J_URI = "bolt://127.0.0.1:7687"
$env:GRAPHBRIDGE_NEO4J_USERNAME = "neo4j"
$env:GRAPHBRIDGE_NEO4J_DATABASE = "neo4j"
```

Give it about half a minute to start.

## 2. Rehearse at zero cost

Replay mode answers every model call from the sample's recordings, [`data/article-recordings.json`](../../samples/09-GraphRagArticles/data/article-recordings.json). Only the recorded requests are there, so ask the recorded questions. A provider variable wins over replay, so clear it first; the sample warns if both are set.

```powershell
Remove-Item env:AI_INFERENCE_PROVIDER -ErrorAction SilentlyContinue
$env:GRAPHBRIDGE_AI_MODE = "replay"
dotnet run --project samples/09-GraphRagArticles -- setup
dotnet run --project samples/09-GraphRagArticles -- ingest nasa
dotnet run --project samples/09-GraphRagArticles -- report
dotnet run --project samples/09-GraphRagArticles -- ask "When and where did Artemis II splash down?"
dotnet run --project samples/09-GraphRagArticles -- ask "What is the capital of Mongolia?"
dotnet run --project samples/09-GraphRagArticles -- agent "How far from Earth did the Artemis II crew travel?"
```

- **`setup`** applies the toolkit's schema as a [migration](QUICK_START.md#3-apply-the-schema-as-a-migration): key constraints, a vector index and a full-text index. The toolkit never creates schema by itself.
- **`ingest nasa`** reads the opening paragraphs of the five releases. It splits them into passages, embeds them, and extracts people, organisations, places, events and quotations under the sample's [ontology](ONTOLOGIES.md). Each extracted item keeps the exact quote it came from.
- **`report`** prints the extracted graph through typed LINQ: the four astronauts and their agencies, NASA officials and their roles, and every quotation with its speaker.
- **`ask`** prints the answer and each citation's byte span and quote. Every quote has been [found in the stored text](ANSWERS.md#how-citations-are-checked). The second question isn't in the releases, so the answer is a refusal, with no text.
- **`agent`** asks an Agent Framework agent. It answers through the toolkit's `answer_from_documents` tool, and the sample prints each tool call with the spans it returned.

A question that wasn't recorded fails, and says so. That's what keeps a replayed test honest.

## 3. Stream from an endpoint

`serve` hosts the toolkit's endpoints on `http://localhost:5179`:

```powershell
dotnet run --project samples/09-GraphRagArticles -- serve
```

In a second terminal, with the same variables:

```powershell
'{"question": "Who are the four Artemis II astronauts?"}' |
  curl.exe -N -X POST http://localhost:5179/graphrag/ask -H "Content-Type: application/json" --data-binary '@-'
```

The answer arrives as server-sent events:
- first `retrieved`, what the evidence holds;
- then `delta`, the model's provisional text, before any citation is checked;
- finally `verified`, the same checked answer `ask` gives, or `refused`.

Or open `http://localhost:5179/explorer` in a browser: ask the same question, then open a citation to see its passage, the quoted bytes, and the things and facts extracted from it ([the explorer](../../samples/09-GraphRagArticles/README.md#the-explorer)).

The endpoints, and what to add before anyone else can reach them, are in [Agents and APIs](AGENTS.md). Stop the server with Ctrl+C.

## 4. Run it on your model

Start again, and name your provider instead of replay mode. With bge-m3, as below, `cleanup` is enough: the schema stays the same. With another embedding model, the schema is another plan, so stop the container with `docker stop graphrag-tutorial`, which removes it, then start a new one as in [step 1](#1-start-a-disposable-database) and run `setup` first.

```powershell
dotnet run --project samples/09-GraphRagArticles -- cleanup
Remove-Item env:GRAPHBRIDGE_AI_MODE
$env:AI_INFERENCE_PROVIDER = "bitdeer"
$env:BITDEER_API_KEY = "<your key>"
dotnet run --project samples/09-GraphRagArticles -- ingest nasa
dotnet run --project samples/09-GraphRagArticles -- ask "How far from Earth did the Artemis II crew travel, and whose record did they break?"
```

- **Other providers:** `openai` reads `OPENAI_API_KEY`. `openai-compatible` reads `OPENAI_COMPATIBLE_ENDPOINT`, `OPENAI_COMPATIBLE_MODEL` and `OPENAI_COMPATIBLE_EMBEDDING_MODEL`; set `AI_EMBEDDING_DIMENSIONS` for an embedding model the samples don't know. Azure OpenAI and Foundry serve that API at `/openai/v1`. `ollama` runs local models. The sample's [README](../../samples/09-GraphRagArticles/README.md) and [Use a real model](REAL_MODELS.md) have the details.
- **The cost:** the recorded ingestion made 5 extraction calls, with 8,988 input and 143,981 output tokens, most of them the model's reasoning. Each question is one embedding and one chat call. A reasoning model spends far more on output than on input. For your own documents, [preview the cost](REAL_MODELS.md#know-the-cost-first) before you ingest.
- **Your answers will differ** from the recorded ones: another model, or the same model on another day, words them differently and extracts differently. What doesn't change is the check: every citation is the releases' own bytes, or the answer is a refusal.

Any question works now. `fetch` downloads the whole releases, and `ingest` reads any folder of Markdown articles with an `articles.json` beside it.

## 5. Clean up

```powershell
dotnet run --project samples/09-GraphRagArticles -- cleanup
docker stop graphrag-tutorial
```

`cleanup` removes what the sample ingested; stopping the container removes the database.

## Make it yours

- **Your own documents and context:** the [quick start](QUICK_START.md) maps the model into your `DbContext` and registers the toolkit; [Ingest](INGEST.md) covers sources and chunking.
- **Your own ontology:** [Ontologies](ONTOLOGIES.md).
- **Tests that don't call a model:** record once and replay, as this tutorial did ([Use a real model](REAL_MODELS.md#record-once-replay-in-tests)); measure with labelled questions ([Test and evaluate](EVALUATE.md)).
- **A graph built by Neo4j's Python neo4j-graphrag:** [adopt it](INTEROP.md#adopt-a-graph-neo4j-graphrag-built).
