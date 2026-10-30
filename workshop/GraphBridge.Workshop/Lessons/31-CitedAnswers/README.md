# 31 · From documents to cited answers

**Goal:** store a short document with two headings, with exact chunk positions, then answer a question with a citation checked against the stored bytes, and refuse one the document doesn't answer.
**Track:** full · **Mode:** offline + Neo4j 2026.09 or later · **Time:** ~15 min · **Builds on:** 20, 23

> The GraphRAG toolkit is **experimental** (`GBRAG001`): its API may change in any release.

## Concepts

- The toolkit stores documents as a typed graph through EF Core: documents, sections and chunks, linked child to parent. Every chunk's text is exactly its document's UTF-8 bytes between `StartByte` and `EndByte`.
- An answer's citations are located in the stored text before the answer is returned. If one isn't found, or none supports the answer, the result is a refusal with a reason.
- The toolkit never creates schema: its constraints and indexes are reviewed migration steps.

## Run it

```powershell
dotnet run --project workshop/GraphBridge.Workshop -- run 31
```

## Walkthrough

1. **Chunk a document, offline.** Chunks never cross a heading, and each one is exactly its bytes.

<!-- region: Chunk -->
```csharp
var bytes = Encoding.UTF8.GetBytes(Notes);
var plan = GraphRagChunkers.Create(new GraphRagChunkerOptions { MaxUtf8Bytes = 160 })
    .Chunk(new GraphRagLoadedDocument("text:notes", "text/markdown", "Notes", bytes));
```

2. **Check a quote the way answers are checked.** Whitespace runs, quotation marks, typographic apostrophes and dashes are normalised, the emphasis marks `_` and `*` are ignored, and up to two `...` elisions are allowed; nothing else is: no case folding, no fuzzy matching. The citation records the stored text, not the quote as written.

<!-- region: Locate -->
```csharp
var chunk = new GraphRagEvidenceItem("c", "d", "Tom Hanks and Hugo Weaving each play several roles.", 0, 51, 1, []);
var found = GraphRagCitationVerifier.Locate(chunk, "Hugo   Weaving each play...roles");
var invented = GraphRagCitationVerifier.Locate(chunk, "Hugo Weaving plays one role");
```

3. **The schema.** The lesson prints the steps it applies: a uniqueness constraint per key, the chunk vector index and a full-text index. In an application they go in a migration plan (lesson 23).

4. **Ingest.** The second call finds the document unchanged and skips it.

<!-- region: Ingest -->
```csharp
var ingestion = services.GetRequiredService<IGraphRagIngestion>();
var receipt = await ingestion.IngestAsync([GraphRagSources.Text("notes", Notes, "text/markdown")], cancellationToken);
var again = await ingestion.IngestAsync([GraphRagSources.Text("notes", Notes, "text/markdown")], cancellationToken);
```

5. **Answer and refuse.** An extractive chat client stands in for a model, so the run needs no key: it quotes the evidence line that shares the most words with the question. Embeddings come from a stand-in, `HashingEmbedder`, which hashes words, so here the vector route also ranks by shared words; a real embedding model ranks by meaning.

<!-- region: Answer -->
```csharp
var answers = services.GetRequiredService<IGraphRagAnswers>();
var answer = await answers.AnswerAsync("Which roles do Tom Hanks and Hugo Weaving play?", cancellationToken: cancellationToken);
var refusal = await answers.AnswerAsync("Who directed Jaws?", cancellationToken: cancellationToken);
```

The lesson checks that the citation's quote is exactly the stored bytes at its span, and that a question about another film is refused. It removes everything it wrote at the end: documents, chunks, runs, leases and its schema.

## Try it yourself

- Ask "When was The Matrix released?" and look at which line is cited.
- Change the quote in step 2 to a paraphrase and watch `Locate` return null.

## Limits

- Citations prove that the stored text says something, not that it is true. The [GraphRAG pages](../../../../docs/graphrag/ANSWERS.md) explain what is and isn't guaranteed.
- The lesson's labels (`WorkshopDocument`, `WorkshopChunk`, ...) are its own, so it never touches the movie graph.
