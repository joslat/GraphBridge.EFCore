# Use a real model

The toolkit talks to models only through `Microsoft.Extensions.AI`: an `IEmbeddingGenerator<string, Embedding<float>>` for embeddings and an `IChatClient` for extraction and answers. Any provider with those clients works. This page covers what changes when a real model replaces the stand-ins: structured output, reasoning, rate limits, cost, and how to test without paying twice.

## Register the clients

```csharp
var set = new GraphRagEmbeddingSet("BAAI/bge-m3", 1024);
services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(embeddings)
    .AddSingleton<IChatClient>(chat)
    // Optional: a client of its own for extraction, here with less reasoning.
    .AddKeyedSingleton<IChatClient>(GraphRagServiceKeys.ExtractionChatClient,
        chat.AsBuilder().ConfigureOptions(o => o.Reasoning = new ReasoningOptions { Effort = ReasoningEffort.Low }).Build())
    .AddGraphRag<AppDbContext>(set);
```

The embedding set names the model and its dimensions, and `AddGraphRag(dimensions)` in your model must use the same dimensions. Change the model and the set changes, so stored chunks are embedded again; see [re-embedding](MAINTAIN.md).

## Structured output

Extraction asks for a reply under a JSON schema read from your model. The schema meets strict structured output's rules: every object is closed, every field is required, and the fields a reply may leave out are nullable.
- **Strict mode** is on by default (`GraphRagExtractionOptions.StrictSchema`). It sets the `strict` entry of `ChatOptions.AdditionalProperties`, which Microsoft.Extensions.AI's OpenAI client sends as `"strict": true`; a test checks the request that client makes. A client that doesn't know the entry ignores it.
- **An unreadable reply is asked for once more**, with what was wrong (`RetryUnreadableReplies`), and quarantined if the second reply is unreadable too. The receipt counts the replies repaired this way, and names each quarantined one: `empty-reply`, `invalid-json`, `truncated` (cut off at the output limit) or `schema-mismatch`.
- **Items are still checked** after the reply parses. An unknown kind, a relationship the ontology doesn't allow, or a quote that isn't in the passage is pruned, with its reason in the receipt.

## Reasoning models

A reasoning model thinks before it answers, and the receipt counts that: `OutputTokens` includes `ReasoningTokens`. To ask for less, set `ChatOptions.Reasoning` on the client, as above; Microsoft.Extensions.AI's OpenAI client sends it as `reasoning_effort`. Extraction and answers can use different clients, so you can keep full reasoning for answers and less for extraction, or the other way round.

## Rate limits and retries

`GraphRagOptions.Retry` sets how failed embedding and extraction calls are retried:
- **Which:** by default, an `HttpRequestException` with status 408, 429 or 5xx, a dropped connection, or a timeout.
- **How often and how long:** 4 retries, waiting 1 second and doubling, with up to 25% jitter, at most a minute.
- **Retry hints:** a client that puts a `TimeSpan` under `GraphRagRetryOptions.RetryAfterKey` in the exception's data, such as a 429's `Retry-After`, is waited for. A hint longer than the longest wait fails the call at once, with the code `rate-limited`.

Clients built on `System.ClientModel`, such as OpenAI's and Azure OpenAI's, retry on their own and throw their own exception type, which the default doesn't retry; set `IsTransient` to change that. The receipt's `Retries` counts retries, how many answered a rate limit, and the time spent waiting. Extraction runs `GraphRagExtractionOptions.Lanes` calls at a time (2 by default, up to 8) and embeddings `GraphRagIngestionOptions.Lanes` batches at a time.

A run that's stopped leaves each document whole or absent, and the next run completes it without duplicates. The calls already made for the document in progress are lost, unless a caching client sits under the toolkit.

## Know the cost first

`PreviewAsync` loads, checks and chunks the sources without a model call or a write, and counts what ingestion would do:

```csharp
var preview = await ingestion.PreviewAsync(GraphRagSources.Folder("docs"));
Console.WriteLine($"{preview.Chunks} chunks: {preview.EmbeddingCalls} embedding calls, {preview.ExtractionCalls} extraction calls, " +
    $"{preview.ExtractionInputTokens} input tokens ({(preview.TokensCounted ? "counted" : "estimated")}).");
```

Register an `IGraphRagTokenCounter` for your model's tokenizer and the tokens are counted; without one they're estimated at four bytes of text per token. The preview doesn't estimate output tokens, which depend on the model.

## Spend within a budget

Preview first, then decide. The repository's own real-model runs follow two rules you can copy:
- **A cap on calls.** The recording clients count real calls and refuse the one that would pass the cap (`GRAPHBRIDGE_AI_MAX_CALLS`, 40 by default), before it's sent. Recorded requests replay free and don't count.
- **A stop rule before the expensive part.** The whole-book test previews the cost, extracts chapter I alone, and stops if more than one passage in four comes back unreadable; only then does it extract the rest.

Your provider's own spending limit is the last line: a cap in code doesn't stop another process using the same key.

## Record once, replay in tests

The samples' tests record every real call, keyed by a hash of its request, and replay them offline at zero calls; a request that wasn't recorded fails the test. The code is in [`samples/GraphRagShared/ModelRecordings.cs`](../../samples/GraphRagShared/ModelRecordings.cs), and you can wrap your own clients the same way. A recording keeps the model's name and the date, never a key or an endpoint. Samples [07](../../samples/07-GraphRagQuickStart/README.md), [08](../../samples/08-GraphRagNovel/README.md) and [09](../../samples/09-GraphRagArticles/README.md) replay theirs with `GRAPHBRIDGE_AI_MODE=replay`, as the [tutorial](TUTORIAL.md) does.

- **Replays in any clone.** A request carries document and chunk IDs, in the agent tools' results and in the order of tied evidence. So the samples name their shipped files by their address in the repository ([document identity](INGEST.md#sources-and-loading)), and the IDs don't depend on where the repository is.
- **Recording again.** A request that changes is recorded again, and its old entry stays. Replay every suite that reads the recordings with `GRAPHBRIDGE_AI_USED_KEYS` naming a file, then `eng/scripts/Prune-ModelRecordings.py` removes the entries no test used.

## What has been run

| Provider | Chat model | Embedding model | What ran | When |
|---|---|---|---|---|
| Bitdeer, OpenAI-compatible | `zai-org/GLM-5.3-Flash` | `BAAI/bge-m3`, 1,024 dimensions | Samples 07 and 08; the whole of *Alice's Adventures in Wonderland*, 41 passages; the 22-case evaluation | 25 October 2026 |
| Bitdeer, OpenAI-compatible | `zai-org/GLM-5.3-Flash` | `BAAI/bge-m3` | Sample 09 over the NASA excerpts, with an Agent Framework agent; 8 answers of samples 07 to 09 and the evaluation recorded again when document IDs became portable | 26 October 2026 |
| Bitdeer, OpenAI-compatible | `zai-org/GLM-5.3-Flash` | `BAAI/bge-m3` | The Text2Cypher evaluation, 14 questions on samples 08 and 09; four follow-ups on the whole book | 26 October 2026 |
| Bitdeer, OpenAI-compatible | `zai-org/GLM-5.3-Flash` | `BAAI/bge-m3` | One whole-book follow-up recorded again with a sharper label; sample 09's explorer follow-up, 3 calls | 29 October 2026 |
| Bitdeer, OpenAI-compatible | `deepseek-ai/DeepSeek-V4.1-Flash` | `BAAI/bge-m3` | Sample 08's excerpt, as a smoke run | 25 October 2026 |

What they showed:
- **Structured output held.** GLM-5.3-Flash returned JSON for the schema on all 46 extraction calls (41 passages, the 4 of chapter I and the excerpt); none was quarantined or needed a second ask. DeepSeek-V4.1-Flash's reply matched the schema too, but it found 1 thing in the excerpt where GLM found 5, and named relationships to things it hadn't listed, which were pruned as `endpoint-not-found`. The checks held for both; how much a model extracts varies by model.
- **Reasoning is most of the cost.** On the whole book, GLM-5.3-Flash used 60,583 input and 733,516 output tokens for extraction, 652,802 of them reasoning: about 17,900 output tokens per 4 KB passage, and about a minute and a half per call. With four extraction lanes the book took about 25 minutes, 7 of its calls retried after transient failures. `reasoning_effort: low` cut a probe's reasoning from about 150 tokens to 1.
- **The preview matched.** For the whole book it predicted the 41 extraction calls exactly, the extraction input tokens within 5.9% and the embedding tokens within 5.0%, estimating four bytes per token.
- **No rate limit was met** with four lanes on Bitdeer.
- **Bitdeer refuses requests without a `User-Agent` header**, with HTTP 403; the samples' client sends one.
- **A 429 isn't always a rate limit.** OpenAI answers 429 when an account has no credits left; retrying that only waits. The receipt names the code `rate-limited` either way.

The samples choose a provider with environment variables: `AI_INFERENCE_PROVIDER=bitdeer` and `BITDEER_API_KEY` are enough for the runs above. See [sample 08](../../samples/08-GraphRagNovel/README.md).

## The samples' variables

Samples 07 to 09, the tutorial and the real-model tests read the same variables ([`SampleModels.cs`](../../samples/GraphRagShared/SampleModels.cs)). With none set, a sample runs offline with stand-ins.

| Variable | Meaning |
|---|---|
| `AI_INFERENCE_PROVIDER` | `bitdeer`, `openai`, `openai-compatible` or `ollama`. It wins over replay, so clear it when you want stand-ins or recordings. Azure OpenAI and Foundry serve an OpenAI-compatible API at `/openai/v1`: use `openai-compatible` |
| `GRAPHBRIDGE_AI_MODE=replay` | Without a provider: answer every call from the sample's recordings, at zero calls. A request that wasn't recorded fails |
| `AI_EMBEDDING_DIMENSIONS` | The embedding width, for a model the samples don't know. They know `bge-m3` (1,024) and OpenAI's `text-embedding-3-small` and `text-embedding-ada-002` (1,536) |
| `GRAPHBRIDGE_AI_REASONING_EFFORT` | `low`, `medium` or `high`: asked of a reasoning model on every chat call that doesn't set its own |
| `GRAPHBRIDGE_NEO4J_URI`, `_USERNAME`, `_PASSWORD`, `_DATABASE` | The database. Samples 07 to 09 refuse to start without them |

Each provider reads four variables of its own; the key is required except for the last two:

| Provider | Endpoint | Key | Chat model | Embedding model |
|---|---|---|---|---|
| `bitdeer` | `BITDEER_ENDPOINT`, default `https://api-inference.bitdeer.ai/v1` | `BITDEER_API_KEY` | `BITDEER_MODEL`, default `zai-org/GLM-5.3-Flash` | `BITDEER_EMBEDDING_MODEL`, default `BAAI/bge-m3` |
| `openai` | `OPENAI_BASE_URL`, default `https://api.openai.com/v1` | `OPENAI_API_KEY` | `OPENAI_MODEL`, default `gpt-4o-mini` | `OPENAI_EMBEDDING_MODEL`, default `text-embedding-3-small` |
| `openai-compatible` | `OPENAI_COMPATIBLE_ENDPOINT`, required | `OPENAI_COMPATIBLE_API_KEY`, optional | `OPENAI_COMPATIBLE_MODEL`, required | `OPENAI_COMPATIBLE_EMBEDDING_MODEL`, required |
| `ollama` | `OLLAMA_ENDPOINT`, default `http://127.0.0.1:11434/v1` | `OLLAMA_API_KEY`, optional | `OLLAMA_MODEL`, required | `OLLAMA_EMBEDDING_MODEL`, default `bge-m3` |

An endpoint must be `https`, or `http` to this machine, without user information or a query. Missing values are named in the error, never their values.

The real-model tests add three switches:
- `GRAPHBRIDGE_AI_RECORD=1`, with a provider: replay what's recorded and send the rest to the model, writing the replies into the repository's recordings;
- `GRAPHBRIDGE_AI_MAX_CALLS`: the most real calls one run may make, 40 by default; one more stops the run;
- `GRAPHBRIDGE_AI_FRESH=1`: ignore the recordings and record into a scratch file, so every request reaches the model and the repository is left alone.

`GRAPHBRIDGE_AI_USED_KEYS` names a file that lists every recording a run used, for pruning (see above).
