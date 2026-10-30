# GraphBridge.GraphRag.Pdf

**Experimental.** A PDF loader for the [GraphBridge GraphRAG toolkit](https://github.com/joslat/GraphBridge.EFCore/tree/main/src/GraphBridge.GraphRag), built on [PdfPig](https://github.com/UglyToad/PdfPig) (Apache-2.0). The API may change in any preview; using it reports `GBRAG001`.

```csharp
services.AddGraphRag<AppDbContext>(embeddingSet)
    .AddGraphRagPdf();   // .pdf files now load: GraphRagSources.Folder("reports", "*.pdf")
```

- **What is stored:** the PDF's text layer, page by page in reading order, one paragraph per text block. The document's bytes are that text, so citations are located in it, byte for byte, as for any document.
- **Pages:** the document keeps a page map, and each citation names the page of its first and last byte (`Page`, `EndPage`).
- **Refusals,** each a stable code in the ingestion receipt:
  - `pdf-no-text`: no page has a text layer, as in a scan; it needs OCR first;
  - `pdf-unreadable`: the file can't be opened, a page can't be read, or it is encrypted;
  - `document-too-large`: the file is over `MaxFileBytes` (64 MiB by default), has more than `MaxPages` pages (5,000), or its text is over the ingestion's `MaxDocumentBytes`.

A citation proves the quote is in the PDF's extracted text, on the page it names. It doesn't give the quote's position on the page. A PDF ingested without `AddGraphRagPdf` fails with `unsupported-media-type`.

Requires Neo4j 2026.09 or later. Guide: [PDF](https://joslat.github.io/GraphBridge.EFCore/graphrag/INGEST.html#pdf). Design: ADR 0052, section 14, in the repository's `docs/architecture/adr`.

GraphBridge's code is MIT licensed; PdfPig keeps its Apache-2.0 license.
