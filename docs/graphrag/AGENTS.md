# Agents and APIs

## Agent tools

`GraphRagTools.Create<TContext>(services, options)` returns `AIFunction`s for any chat client or agent that calls functions.

| Tool | Returns |
|---|---|
| `answer_from_documents(question)` | The checked answer and its citations, or a refusal and its reason |
| `search_documents(query, limit)` | Up to 20 passages with document, chunk and byte span: evidence to read, not answers |
| `find_things(name, kind)` | Up to 10 extracted things from the names index (with an ontology) |
| `related_facts(thingId)` | Up to 50 facts that involve a thing, with the documents that state them (with an ontology) |

Every result carries IDs and byte spans. An optional `IGraphRagToolTrail` records each call: the tool, its arguments and the evidence it returned, never document text. `GraphRagToolTrail` keeps the records in memory.

## Microsoft Agent Framework

`GraphRagContextProvider`, in `GraphBridge.GraphRag.AgentFramework`, adds the tools to every invocation. Its instructions tell the agent to answer questions about the documents only through `answer_from_documents`, so the agent cites only text that is stored, or says the documents don't answer.

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

## Endpoints

There is no endpoint package. [Sample 09](../../samples/09-GraphRagArticles/README.md) shows a small ASP.NET Core endpoint inside the application: `GET /ask` and `GET /report`.
