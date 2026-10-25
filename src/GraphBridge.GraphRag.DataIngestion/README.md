# GraphBridge.GraphRag.DataIngestion

**Experimental.** Connects [Microsoft.Extensions.DataIngestion](https://www.nuget.org/packages/Microsoft.Extensions.DataIngestion) pipelines to the [GraphBridge GraphRAG toolkit](https://github.com/joslat/GraphBridge.EFCore/tree/main/src/GraphBridge.GraphRag). DataIngestion is itself a preview, and this adapter's API may change in any preview; using it reports `GBRAG001`.

- **`GraphBridgeChunkWriter`** is an `IngestionChunkWriter<string>` that stores each document's chunks through the toolkit:
  - leased, and skipped when unchanged;
  - embedded with the toolkit's generator, and extracted when your model maps an ontology;
  - one transaction per document.
- **Exact positions:** the stored text is the reader's Markdown, and every chunk keeps its exact span in it, so answers can cite it.
  - Chunks from other chunkers are placed in order: without a header chunker's heading-path prefix, byte-exact, then with whitespace runs normalised.
  - A chunk that can't be placed fails its document with `position-not-recovered`, and nothing of that document is written.
- **`GraphRagSpanChunker`** chunks with the toolkit's own structure-aware chunker and records each span in the chunk's metadata, so no recovery is needed.

```csharp
var writer = new GraphBridgeChunkWriter(services.GetRequiredService<IGraphRagIngestion>());
using var pipeline = new IngestionPipeline<string>(reader, new GraphRagSpanChunker(), writer);
await foreach (var result in pipeline.ProcessAsync(new DirectoryInfo("docs"), "*.md", SearchOption.AllDirectories))
    Console.WriteLine($"{result.DocumentId}: {(result.Succeeded ? "written" : result.Exception?.Message)}");
// writer.Receipts holds one ingestion receipt per document.
```

Requires Neo4j 2026.09 or later. Design: ADR 0052, section 8, in the repository's `docs/architecture/adr`.
