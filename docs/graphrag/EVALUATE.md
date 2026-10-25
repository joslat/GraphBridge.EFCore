# Test and evaluate

## Testing without a model

Every model call goes through `IEmbeddingGenerator` and `IChatClient`, so tests can use stand-ins:
- **Samples:** they run offline with a hashing embedder, an extractive chat client and extraction replies written by hand for each sample, not recorded from a model.
- **Integration suite:** the repository's suite uses the same stand-ins, so no test calls a real model.

## The evaluation harness

`tests/GraphBridge.GraphRag.Evaluation` is a test-only library, not a package. Reference it from your test project to score your own corpus. It builds on [AgentEval](https://github.com/AgentEvalHQ/AgentEval), a test-only dependency that the runtime packages never take. Its metrics are deterministic and need no model:

| Metric | Passes when |
|---|---|
| `graphrag_citation_validity` | Every citation's quote equals the evidence text at its byte span |
| `graphrag_abstention` | An unanswerable question is refused, and an answerable one is answered |
| `graphrag_retrieval_coverage` | The retrieved evidence contains each required phrase |

```csharp
var cases = GraphRagEvaluator.LoadCases("cases.json");   // id, question, answerable, requiredEvidence
var report = await GraphRagEvaluator.RunAsync(answers, cases);
Assert.True(report.Passed, string.Join("; ", report.Failures.Select(f => $"{f.Case}: {f.Metric.Explanation}")));
```

Each metric is an AgentEval `IRAGMetric`, so it also runs in AgentEval's own pipelines.

The harness also has offline stand-ins:
- `GraphRagHashingEmbedder` and `GraphRagExtractiveChatClient`;
- `GraphRagScriptedChatClient`;
- `GraphRagInMemoryRetriever`, which ranks chunks of in-memory texts by shared words, with exact positions.

The repository's own corpus is the public-domain opening of *Alice's Adventures in Wonderland*, with five labelled questions, three of them answerable. The suites run it offline and against Neo4j. The checks are deterministic: they show that citations are real and refusals happen, not how good the answers read.
