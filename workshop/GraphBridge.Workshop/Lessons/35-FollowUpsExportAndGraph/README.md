# 35 · Follow-ups, answer export and graph questions

**Goal:** answer a follow-up that only makes sense with its conversation, export an answer as evidence a check can trust, and ask the graph a question through a model-written query that may only read.
**Track:** full · **Mode:** offline + Neo4j 2026.09 or later · **Time:** ~15 min · **Builds on:** 33

> The GraphRAG toolkit is **experimental** (`GBRAG001`): its API may change in any release.

## Concepts

- **A follow-up** such as "When was it released?" names its subject only through the conversation. With `History`, the toolkit first asks the model to rewrite it into a question that stands alone, retrieves and answers that, and shows both in the evidence. The application keeps the conversation; the toolkit stores none of it.
- **An export** is one JSON document per answer: the question, the answer or refusal, its citations, the evidence the model saw and the receipt. `Check` confirms, without the database, that every citation is the bytes of the evidence it names.
- **Text2Cypher** (experimental, off until registered) asks a model for one query. It runs only if the server's plan says it reads; its answer is the rows and the query, with no citations.

## Run it

```powershell
dotnet run --project workshop/GraphBridge.Workshop -- run 35
```

Offline, the lesson shows the export format refusing another. With a database, a small stand-in plays the model: it rewrites the follow-up, writes the graph queries, and answers with the extractive client from lesson 31, so the run needs no key.

## Walkthrough

1. **A follow-up.** The second question is rewritten to "When was The Matrix released?", then answered from the passage that gives the year.

<!-- region: FollowUp -->
```csharp
var answers = services.GetRequiredService<IGraphRagAnswers>();
var first = await answers.AnswerAsync("Who does Keanu Reeves play?", cancellationToken: cancellationToken);
var followUp = await answers.AnswerAsync("When was it released?",
    new GraphRagRetrievalOptions { History = [new GraphRagTurn("Who does Keanu Reeves play?", first.Text)] }, cancellationToken);
```

2. **Export and check.** The export reads back and checks clean; a forged copy, with a citation's quote edited, doesn't.

<!-- region: Export -->
```csharp
var json = GraphRagAnswerExport.ToJson(followUp);
var read = GraphRagAnswerExport.FromJson(json);
var problems = GraphRagAnswerExport.Check(read);
var forged = read with { Citations = [read.Citations[0] with { Quote = "The Matrix was released in 2001." }] };
```

3. **Ask the graph.** The read returns the documents' titles. The "model" then writes a `DETACH DELETE`: the server plans it with `EXPLAIN`, the plan says it writes, and it is refused before it runs.

<!-- region: Graph -->
```csharp
var graph = services.GetRequiredService<IGraphRagGraphQuestions>();
var titles = await graph.AskAsync("Which documents are stored?", cancellationToken);
var write = await graph.AskAsync("Remove every document.", cancellationToken);
```

The lesson removes everything it wrote at the end.

## Try it yourself

- Send the follow-up without `History`: the question is asked as written and `Evidence.Rewrite` is null. With these two documents the stand-in still finds "released"; ingest a second film with a release year to see the question become ambiguous.
- Have the stand-in reply `{"insufficient": "..."}` to a graph question and read the refusal's reason.

## Limits

- `Check` catches a quote or span edited on its own, not a passage and its quote edited together: an export isn't signed. It holds the passages' text, so treat it like the documents.
- A graph answer has no citations: read the query, since the rows are what it returned whether or not it asks what the question asks. The [answers guide](../../../../docs/graphrag/ANSWERS.md) covers follow-ups, exports and Text2Cypher's gates.

Next: the [quick start](../../../../docs/graphrag/QUICK_START.md) to use the toolkit in your own project, and the [tutorial](../../../../docs/graphrag/TUTORIAL.md) with a real model.
