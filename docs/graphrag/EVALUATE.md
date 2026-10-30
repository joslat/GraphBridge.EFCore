# Test and evaluate

## Testing without a model

Every model call goes through `IEmbeddingGenerator` and `IChatClient`, so tests can use stand-ins:
- **Samples:** they run offline with a hashing embedder and an extractive chat client. Samples 07 to 09 also replay a real model's recorded calls; sample 09's offline replies for its fictional articles are written by hand, not recorded from a model.
- **Integration suite:** the repository's suite uses the same stand-ins, so no test calls a real model.
- **Recorded real-model calls:** `tests/GraphBridge.GraphRag.RealModelTests` runs samples 07 to 09 and the whole book with a real model when `GRAPHBRIDGE_AI_RECORD=1` and `AI_INFERENCE_PROVIDER` names one, such as `bitdeer` with `BITDEER_API_KEY`, and records each call, keyed by a hash of its request. Without them, it replays the recordings at zero calls, and a request with no recording fails the test.

## The evaluation harness

`tests/GraphBridge.GraphRag.Evaluation` is a test-only library, not a package. Reference it from your test project to score your own corpus. It builds on [AgentEval](https://github.com/AgentEvalHQ/AgentEval), a test-only dependency that the runtime packages never take. Its metrics are deterministic and need no model:

| Metric | Passes when |
|---|---|
| `graphrag_citation_validity` | Every citation's quote equals the evidence text at its byte span |
| `graphrag_abstention` | An unanswerable question is refused, and an answerable one is answered |
| `graphrag_retrieval_coverage` | The retrieved evidence contains each required phrase |

```csharp
var cases = GraphRagEvaluator.LoadCases("cases.json");   // id, question, answerable, requiredEvidence; history for a follow-up
var report = await GraphRagEvaluator.RunAsync(answers, cases);
Assert.True(report.Passed, string.Join("; ", report.Failures.Select(f => $"{f.Case}: {f.Metric.Explanation}")));
```

Each metric is an AgentEval `IRAGMetric`, so it also runs in AgentEval's own pipelines.

The harness also has offline stand-ins:
- `GraphRagHashingEmbedder` and `GraphRagExtractiveChatClient`;
- `GraphRagScriptedChatClient`;
- `GraphRagInMemoryRetriever`, which ranks chunks of in-memory texts by shared words, with exact positions.

The repository has two corpora from the public-domain *Alice's Adventures in Wonderland*:
- **The opening of chapter I**, shipped with sample 08, with five labelled questions, three of them answerable. The suites run it offline and against Neo4j.
- **The whole book**, which sample 08's `fetch` downloads and the repository never holds, with 22 labelled questions (`corpus/alice-book-cases.json`): 16 answered by one passage, 2 that need passages from different chapters, and 4 the book doesn't answer, such as the name of Alice's sister. Every evidence phrase is checked against the book. Four follow-ups (`corpus/alice-book-follow-ups.json`) carry the conversation before them in `history`: three answerable, one not, each scored with and without it ([follow-up questions](ANSWERS.md#follow-up-questions)).

The checks are deterministic: they show that citations are real and refusals happen, not how good the answers read.

## Numbers with a real model

The whole book, ingested and asked through the toolkit with `zai-org/GLM-5.3-Flash` and `BAAI/bge-m3` on Bitdeer, on 25 October 2026, with the default retrieval (hybrid, up to 8 items). Four answers were recorded again on 26 October, after the book's document was named by the address it is fetched from; the scores didn't change:

| Metric | Score | What it counts |
|---|---|---|
| Citation validity | 100% | All 40 citations of the 18 answers are the stored bytes at their spans |
| Abstention | 91% (20 of 22) | One answerable question refused, one unanswerable one answered |
| Retrieval coverage | 81% | The share of required phrases in the retrieved evidence, over the 18 answerable questions |

The two misses:
- **"What colour were the White Rabbit's eyes?"** was refused: retrieval didn't bring back the passage with "a White Rabbit with pink eyes", so the model declined rather than guess.
- **"What is the White Rabbit's first name?"** was answered from the brass plate that reads "W. RABBIT". The book never gives his first name, so the label stands: the answer is a miss. The model should have refused.

The labels were written by the implementing agent and reviewed by the project's owner on 25 October 2026. The run is recorded, so `GraphBridge.GraphRag.RealModelTests` replays it at zero calls wherever the book has been fetched.
