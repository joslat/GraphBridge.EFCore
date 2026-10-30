# Agents and APIs

## Agent tools

`GraphRagTools.Create<TContext>(services, options)` returns `AIFunction`s for any chat client or agent that calls functions.

| Tool | Returns |
|---|---|
| `answer_from_documents(question)` | The checked answer and its citations, or a refusal and its reason |
| `search_documents(query, limit)` | Up to 20 passages with document, chunk and byte span: evidence to read, not answers |
| `find_things(name, kind)` | Up to 10 extracted things from the names index (with an ontology) |
| `related_facts(thingId)` | Up to 50 facts that involve a thing, with the documents that state them (with an ontology) |

Every result carries IDs and byte spans. A citation in an [adopted](INTEROP.md#adopt-a-graph-neo4j-graphrag-built) graph whose span is within its chunk's own text, not a source file, also says `"grounding": "Chunk"`. An optional `IGraphRagToolTrail` records each call: the tool, its arguments and the evidence it returned, never document text. `GraphRagToolTrail` keeps the records in memory.

## Microsoft Agent Framework

`GraphRagContextProvider`, in `GraphBridge.GraphRag.AgentFramework`, adds the tools to every invocation. Its instructions tell the agent to answer only through `answer_from_documents`, whose citations are checked. The agent's own wording isn't checked: show the tool's citations, which the trail records.

```csharp
var provider = GraphRagContextProvider.Create<AppDbContext>(services, new GraphRagToolOptions { Trail = trail });
var agent = new ChatClientAgent(chatClient, new ChatClientAgentOptions { AIContextProviders = [provider] });
```

## DataIngestion pipelines

`GraphBridge.GraphRag.DataIngestion` connects `Microsoft.Extensions.DataIngestion` pipelines, and through them its readers for formats such as PDF.
- **`GraphBridgeChunkWriter`** stores each document's chunks through the toolkit: leased, skipped when unchanged, embedded, extracted, one transaction per document.
- **The stored text is the reader's Markdown,** and every chunk keeps its exact span in it.
  - Chunks from other chunkers are placed in order: byte-exact after a header chunker's heading path, then with whitespace runs normalised.
  - A chunk that can't be placed fails its document with `position-not-recovered`, and nothing of that document is written.
- **`GraphRagSpanChunker`** chunks with the toolkit's own chunker and records exact spans, so nothing needs recovering.

```csharp
var writer = new GraphBridgeChunkWriter(services.GetRequiredService<IGraphRagIngestion>());
using var pipeline = new IngestionPipeline<string>(reader, new GraphRagSpanChunker(), writer);
await foreach (var result in pipeline.ProcessAsync(new DirectoryInfo("docs"), "*.md", SearchOption.AllDirectories)) { }
```

### Office documents

Word, PowerPoint and Excel files reach the toolkit through DataIngestion's `MarkItDownReader`, which runs Microsoft's [markitdown](https://github.com/microsoft/markitdown) tool to convert each file to Markdown:

```csharp
// dotnet add package Microsoft.Extensions.DataIngestion.MarkItDown --prerelease
// pip install "markitdown[docx,pptx,xlsx]" (an isolated environment; the tool's path goes below)
var reader = new MarkItDownReader(new FileInfo(@".venv\Scripts\markitdown.exe"));
var writer = new GraphBridgeChunkWriter(services.GetRequiredService<IGraphRagIngestion>(),
    new GraphBridgeChunkWriterOptions { TextOrigin = "markitdown 0.1.9" });
using var pipeline = new IngestionPipeline<string>(reader, new GraphRagSpanChunker(), writer);
await foreach (var result in pipeline.ProcessAsync(new DirectoryInfo("reports"), "*.docx")) { }
```

- **The stored text is markitdown's Markdown,** and citations are its bytes. It is derived text, so set `TextOrigin`: every citation from those documents then names the converter ([derived text](INGEST.md#derived-text-ocr-and-transcripts)).
- **What converts:** markitdown turns headings, paragraphs, lists and tables into Markdown. Images aren't read unless the reader is asked to extract them, and their text needs OCR elsewhere. The recipe's test covers Word files with headings and paragraphs; check PowerPoint and Excel files, and lists and tables, on files of your own.
- **Tested** with markitdown 0.1.9 and DataIngestion's reader 10.11.0-preview.1: Word files built by the test are converted, ingested, retrieved and cited, with the origin on every citation (an opt-in test, `tests/interop/markitdown`).

## Endpoints

[`GraphBridge.GraphRag.AspNetCore`](../../src/GraphBridge.GraphRag.AspNetCore/README.md) maps the toolkit's HTTP endpoints. They read your documents and call your model, so add your own authentication, authorization and rate limits to the group it returns:

```csharp
app.MapGraphRag<AppDbContext>("/graphrag")
    .RequireAuthorization()
    .RequireRateLimiting("graphrag");
```

- **`POST /graphrag/ask`** with `{"question": "..."}` streams server-sent events:
  - `retrieved`: what the evidence holds, and the trace ID when tracing is on;
  - `delta`: the next piece of the answer as the model writes it. It's provisional, because no citation has been checked yet;
  - then `verified`, with the answer and its checked citations, or `refused`, with the reason. `withdraws` says that provisional text was sent and must be removed;
  - `error` ends a failed stream with `retrieval-failed` or `model-failed`; the details are in your log.

  Closing the request stops the model call. A follow-up also sends `history`, the conversation so far ([follow-up questions](ANSWERS.md#follow-up-questions)).
- **`POST /graphrag/search`** with `{"query": "...", "limit": 5}` returns up to 20 passages with exact spans, and, when the document has them, its pages, its text origin and a chunk's grounding, as `search_documents` does for an agent.
- **`GET /graphrag/documents/{id}`** returns a document's metadata and its chunks' byte spans.

`ScopeFor` sets each request's [scope](RETRIEVE.md#scoped-search), decided by your application rather than the caller, for example from the signed-in user's tenant:

```csharp
app.MapGraphRag<AppDbContext>("/graphrag", o => o.ScopeFor = http => GraphRagScope.FromCypher(
        "MATCH (d:Document {Tenant: $tenant}) RETURN d.Id AS documentId",
        new Dictionary<string, object?> { ["tenant"] = http.User.FindFirst("tenant")?.Value }))
    .RequireAuthorization();
```

The scope covers `ask`, `search` and `documents/{id}`: a document outside it answers 404, and only its chunks inside the scope are listed. The `retrieved` event then says how the scope was kept.

The agent tools `find_things` and `related_facts` aren't scoped by `GraphRagToolOptions.Retrieval.Scope`: they read the things and facts of the whole graph. In an application with several tenants, don't give them to an agent, or keep each tenant in a database of its own. The same stream is available without HTTP: `IGraphRagAnswers.AnswerStreamingAsync` returns the evidence, provisional pieces of text, and then the same checked answer `AnswerAsync` gives. [Sample 09](../../samples/09-GraphRagArticles/README.md) serves these endpoints over NASA's Artemis II news releases, and its `agent` command runs an Agent Framework agent with a real model.

## Try it

- [Sample 09](../../samples/09-GraphRagArticles/README.md): `agent` asks an Agent Framework agent with the context provider, and `serve` hosts the endpoints and the explorer page.
- The [Office-document recipe](../../tests/interop/markitdown/README.md) runs as an opt-in test.
