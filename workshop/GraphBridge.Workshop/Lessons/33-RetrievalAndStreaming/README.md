# 33 · Retrieval, scopes and streamed answers

**Goal:** retrieve passages by meaning and by words, keep retrieval to chosen documents, and stream an answer whose text counts only once its citations are checked.
**Track:** full · **Mode:** offline + Neo4j 2026.09 or later · **Time:** ~15 min · **Builds on:** 31

> The GraphRAG toolkit is **experimental** (`GBRAG001`): its API may change in any release.

## Concepts

- **Two routes.** The vector route ranks passages by the meaning of the question, the full-text route by its words. Their lists are fused by rank, and each passage keeps its rank in each route, so you can see why it was chosen.
- **Every list is bounded:** candidates per route, the passages kept, and the bytes they take. A bound that cut something is reported, never hit silently.
- **A scope** keeps retrieval to chosen documents, sections, documents matching metadata, or a Cypher query's documents. The trace says how each route kept to it: the vector route scores a small scope exactly, and fetches more and cuts for a large one; the full-text route always fetches more and cuts.
- **A streamed answer** sends the model's text as it arrives, then a completed update. The pieces are unverified: the completed update replaces them with the checked answer, or withdraws them with a refusal.

## Run it

```powershell
dotnet run --project workshop/GraphBridge.Workshop -- run 33
```

Offline, the lesson shows the retrieval options. With a database, it ingests two short documents and answers with the extractive stand-in from lesson 31, so the run needs no key. Embeddings come from a stand-in, `HashingEmbedder`, which hashes words, so here the vector route also ranks by shared words; a real embedding model ranks by meaning.

## Walkthrough

1. **The options.** Routes, candidates per route and the evidence budget, with their defaults; `MaxItems` is lowered here.

<!-- region: Options -->
```csharp
var options = new GraphRagRetrievalOptions { MaxItems = 4 };
```

2. **Retrieve.** Hugo Weaving is in both documents: each passage comes back with its rank in each route.

<!-- region: Retrieve -->
```csharp
var retriever = services.GetRequiredService<IGraphRagRetriever>();
var evidence = await retriever.RetrieveAsync("Which films does Hugo Weaving play in?", options, cancellationToken);
```

3. **Scope.** The same question kept to *Cloud Atlas*. The lesson prints how each route kept to the scope.

<!-- region: Scope -->
```csharp
var scoped = await retriever.RetrieveAsync("Which films does Hugo Weaving play in?",
    new GraphRagRetrievalOptions { MaxItems = 4, Scope = GraphRagScope.Documents(atlas) }, cancellationToken);
```

4. **Stream.** The first update reports the retrieval, the pieces follow, and the last one completes the answer with its checked citation. A question about another film completes as a refusal.

<!-- region: Stream -->
```csharp
var answers = services.GetRequiredService<IGraphRagAnswers>();
var updates = new List<GraphRagAnswerUpdate>();
await foreach (var update in answers.AnswerStreamingAsync("Which roles do Tom Hanks and Hugo Weaving play?", cancellationToken: cancellationToken))
    updates.Add(update);
```

The lesson removes everything it wrote at the end.

## Try it yourself

- Set `Routes = GraphRagRoutes.FullText` and compare the ranks.
- Scope with `GraphRagScope.Matching(new GraphRagDocumentFilter { SourceUriPrefix = "text:m" })`.

## Limits

- In a web application, `MapGraphRag` from `GraphBridge.GraphRag.AspNetCore` sends these updates as server-sent events; [sample 09](../../../../samples/09-GraphRagArticles/README.md)'s explorer shows them. Show the pieces as unverified.
- The [retrieval guide](../../../../docs/graphrag/RETRIEVE.md) covers neighbouring passages, Cypher expansion and every bound.
