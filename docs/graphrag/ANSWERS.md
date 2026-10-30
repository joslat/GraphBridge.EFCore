# Answer with evidence: GraphRAG you can trust

`IGraphRagAnswers.AnswerAsync(question)` asks the chat client for an answer and the exact supporting quote for each claim. The answer then has one of two dispositions:
- **`Answered`:** text with citations, each of which has been located in the stored text;
- **`InsufficientEvidence`:** a refusal with a reason, and no answer text.

## How citations are checked

Each quote must be found in the evidence item it names. Only these differences are forgiven:
- runs of whitespace;
- straight and typographic quotation marks and apostrophes;
- hyphens and dashes;
- the emphasis marks `_` and `*`, as in Markdown and Project Gutenberg's `_italics_`;
- at most two `...` elisions, in order.

Nothing else is forgiven: no fuzzy matching, no case folding, no match across chunks. A citation records the stored text and its exact byte span, emphasis marks included, not the model's copy. If any citation fails, or none supports the answer, the result is a refusal.

A citation from a PDF also names the pages of its first and last byte, `Page` and `EndPage` ([PDF](INGEST.md#pdf)). A citation from derived text, such as OCR or a transcript, names where the text came from in `TextOrigin` ([derived text](INGEST.md#derived-text-ocr-and-transcripts)).

Each citation also says what its span is a span of, in `Grounding`:
- **`Source`:** the source document's bytes, as for every chunk GraphBridge ingested.
- **`Chunk`:** the stored chunk's own text. This happens only for a chunk [adopted](INTEROP.md#adopt-a-graph-neo4j-graphrag-built) from a graph another tool built, when its source file wasn't given or didn't contain it. The quote is proven to be in the passage, but not where the passage sits in its source.

## When the answer refuses

| Reason | Meaning |
|---|---|
| `no-evidence` | Retrieval found nothing |
| `model-declined` | The model said the evidence doesn't answer the question |
| `unreadable-response` | The reply wasn't the JSON asked for |
| `no-citations` | The model answered without citing anything |
| `citation-not-found` | A quote isn't in the evidence it names |

Context passages, such as neighbours and Cypher expansions, help the model read. They are never cited.

## Receipts

Every answer carries a `Receipt`:
- its chat calls;
- input and output tokens, when the client reports them;
- the time spent retrieving and answering.

## Follow-up questions

A follow-up such as "Which organization does he work for?" can't be retrieved on its own. Pass the conversation so far, oldest first, and the answerer first asks the chat client to rewrite the question so that it stands alone:

```csharp
var first = await answers.AnswerAsync("Who commands Artemis II?");
var history = new[] { new GraphRagTurn("Who commands Artemis II?", first.Text) };
var answer = await answers.AnswerAsync("Which organization does he work for?", new GraphRagRetrievalOptions { History = history });
// answer.Evidence.Question: "Which organization does Reid Wiseman work for?"
// answer.Evidence.Rewrite: the question asked, the standalone one, the turns used, and the outcome
```

- **One more call:** the rewrite is a chat call of its own, counted on the receipt, and traced as `GraphRag.Rewrite` with the turns used and the outcome, never the text.
- **Outcomes:** `rewritten`; `unchanged`, when the question already stood alone; `unreadable`, when the reply wasn't the JSON asked for, and the question is retrieved as asked.
- **Bounds:** the newest six turns are used, and `LimitsReached` says so when there were more; each earlier answer is cut to 1,000 characters.
- **Only for answers:** `IGraphRagRetriever` takes one standalone question and refuses history.
- **Evaluated:** on the whole of *Alice* with GLM-5.3-Flash, recorded on 26 October 2026 and one again on 29 October, four follow-ups such as "What was it doing up there?" after a turn about the Caterpillar. With their history, all three answerable ones retrieved their passage, and the unanswerable one was refused. Asked bare, one of the three found its passage.

The toolkit keeps no conversation: you pass the history each time, and the [endpoints](AGENTS.md) take it from the client.

Try it: [sample 09](../../samples/09-GraphRagArticles/README.md)'s `chat` command, or its explorer page, which sends the conversation with each question.

## Exporting an answer

`GraphRagAnswerExport.ToJson(answer)` writes one answer, or one refusal with its reason and the evidence it refused on, as one JSON document, for an audit trail or a bug report. `FromJson` reads it back, and `Check` confirms without the database that every citation is the bytes of the evidence it names:

```csharp
await File.WriteAllTextAsync("answer.json", GraphRagAnswerExport.ToJson(answer));
var read = GraphRagAnswerExport.FromJson(await File.ReadAllTextAsync("answer.json"));
var problems = GraphRagAnswerExport.Check(read);   // empty when every citation holds
```

The document has a format, the toolkit's version, the time, and the answer with everything the model saw:

```json
{
  "format": "graphbridge-graphrag-answer/1",
  "exportedUtc": "2026-10-26T12:00:00.0000000+00:00",
  "toolkit": "0.2.0-preview.1+…",
  "answer": {
    "disposition": "Answered",
    "text": "She wondered what use a book without pictures was.",
    "citations": [{ "documentId": "…", "chunkId": "…", "startByte": 1079, "endByte": 1104, "quote": "What is the use of a book", "grounding": "Source" }],
    "reason": null,
    "evidence": { "question": "…", "items": [ … ], "context": [ … ], "utf8Bytes": 512, "omittedItems": 3, "limitsReached": [ … ], "provenance": { … } },
    "receipt": { "chatCalls": 1, "inputTokens": 1234, "outputTokens": 56, "retrieval": "00:00:00.1200000", "answer": "00:00:01.5000000" }
  }
}
```

Names are camel case and enums are written as names. The evidence items hold the passages' text, so keep an export as you keep the documents.

Try it: [sample 09](../../samples/09-GraphRagArticles/README.md)'s `ask "question" --export answer.json`.

## Questions about the graph (experimental)

Some questions are about the graph, not a passage: how many releases name a person, who works where. Text2Cypher lets a model write a read-only query for them. It is off by default:

```csharp
services.AddGraphRag<AppDbContext>(embeddingSet).AddGraphRagText2Cypher<AppDbContext>();
var graph = provider.GetRequiredService<IGraphRagGraphQuestions>();
var result = await graph.AskAsync("Who works for the Canadian Space Agency?");
// result.Cypher, result.Columns, result.Rows; or result.Reason when refused
```

- **The answer is rows,** with the query that returned them. It is not a text answer and has no citations, and no model turns it into prose.
- **The model sees** the question and a schema written from your model: labels, properties, facts and their ends.
- **Gates,** each a refusal reason:
  - `model-declined`, `unreadable-response`: the model said the graph can't answer, or its reply wasn't a query;
  - `not-allowed`: the query, with its comments and string literals set aside, has more than one statement, starts with something other than a reading clause, or has `LOAD CSV`, a procedure call, a function outside Neo4j's own namespaces (such as APOC's), `USE` or `IN TRANSACTIONS`, or is too long. The server's plan is checked as well, so a procedure call or a file read is refused however it is written;
  - `invalid-query`, `not-read-only`: the server's `EXPLAIN` fails, or says the query writes;
  - `query-failed`: the query runs in a read transaction with a timeout (10 seconds by default), so the server refuses a write, and a failure is reported.
- **Bounds:** at most `MaxRows` rows (50 by default); a list keeps 1,000 items and a string 10,000 characters. What a bound cuts is reported in `LimitsReached` (`GraphQueryRows`, `GraphQueryListItems`, `GraphQueryText`). A node or relationship comes back as its properties, and an embedding never comes back.
- **The whole database:** Text2Cypher doesn't take a [scope](RETRIEVE.md#scoped-search), so it can read every document in the database. Offer it to single-tenant or trusted callers only, and connect it as a Neo4j user that may not run procedures or load files, where your edition allows that.
- **Evaluated:** with GLM-5.3-Flash, recorded on 26 October 2026, 12 of 14 questions on samples 08 and 09 were answered correctly: every value the graph holds for the question, read through LINQ, was among the rows' cells. Extra rows didn't count against an answer. The model declined "How many news releases are there?", and for one question it wrote `t.name` where the property is `Name`, so the rows held nulls. Check the query before you trust its rows.

Try it: [sample 09](../../samples/09-GraphRagArticles/README.md)'s `graph "question"`; its recordings replay the queries for the NASA excerpts at zero calls.

## What isn't guaranteed

- **The truth of the sources.** A citation proves that the stored text says something, not that it is right.
- **Completeness.** Retrieval returns the best-ranked evidence within the budget. Something the budget left out can't be cited.
- **That a graph answer's query asks what the question asks.** The rows are what the query returned. Read the query: a model can count the wrong thing correctly.
- **That the answer follows from its quotes.** The quotes are real; whether they support the answer's wording is for an evaluation, or for a reader, to judge.
- **Exact scores at large limits.** Neo4j's vector indexes are approximate, so near-ties can reorder; never rely on the order of near-equal scores.

Extracted facts and mentions follow the same rule: every mention and quote is located in its chunk before it is stored, and a fact is kept only when both of its ends are grounded in the same chunk.
