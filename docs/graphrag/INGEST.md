# Ingest

`IGraphRagIngestion.IngestAsync` takes sources and writes each document in one transaction. A run killed midway leaves each document complete or absent, and a rerun skips what is already written.

## Sources and loading

- **`GraphRagSources.File`, `.Folder` and `.Text`:** Markdown and plain text, in deterministic order. `.Folder` reads `*.md` by default; pass `"*.txt"` as its `searchPattern` for plain text. PDF comes with the [PDF package](#pdf), and other formats through the [DataIngestion adapter](AGENTS.md#dataingestion-pipelines).
- **A document's ID hashes its source URI.** For a file, that is its full path, unless you name it: `File(path, uri)`, or `Folder(path, baseUri)`, which adds each file's path inside the folder to the base. Name the files when their identity must survive a move or another machine. Otherwise a moved folder is ingested as new documents, and the old ones stay until you remove them:

  ```csharp
  var sources = GraphRagSources.Folder("docs", new Uri("https://docs.example.com/"));   // docs/guides/setup.md is https://docs.example.com/guides/setup.md
  ```
- **Text is loaded as UTF-8 and refused when invalid.** A leading byte-order mark is removed. Positions count the bytes that remain.
- **`GraphRagSourceMetadata`** sets a title, publication date, publisher and URL from the source itself, and where [derived text](#derived-text-ocr-and-transcripts) came from.

## PDF

`GraphBridge.GraphRag.Pdf` (experimental) loads `.pdf` files with [PdfPig](https://github.com/UglyToad/PdfPig):

```csharp
services.AddGraphRag<AppDbContext>(embeddingSet).AddGraphRagPdf();
var receipt = await ingestion.IngestAsync(GraphRagSources.Folder("reports", "*.pdf"));
```

- **The document is the PDF's text layer,** page by page in reading order, one paragraph per text block. Citations are located in that text, byte for byte, and name the page of their first and last byte (`Page`, `EndPage`).
- **Refused, with a code:** `pdf-no-text` for a PDF without a text layer, as in a scan, which needs [OCR first](#derived-text-ocr-and-transcripts); `pdf-unreadable` for a file PdfPig can't open or can't read a page of, or an encrypted one; `document-too-large` for a file over `MaxFileBytes` (64 MiB by default), more than `MaxPages` pages (5,000) or text over `MaxDocumentBytes`. A refused PDF fails its own document only; the rest of the run goes on.
- **Not given:** where on the page a quote sits. A citation proves the quote is in the PDF's extracted text, on its page.

Try it: [sample 07](../../samples/07-GraphRagQuickStart/README.md) reads a folder's PDFs with its Markdown: `ingest folder`, then `ask`, whose citations name their pages.

## Derived text: OCR and transcripts

The toolkit reads text; it has no OCR or speech engine. For a scan, a photo or a recording, make the text with a tool you choose, then ingest that text and say where it came from:

```csharp
// The text an OCR engine read from a scan, saved next to it.
var source = GraphRagSources.File("scans/1987-annual-report.txt", new Uri("https://archive.example.com/1987-annual-report.pdf")) with
{
    Metadata = new GraphRagSourceMetadata(Title: "Annual report 1987", TextOrigin: "ocr: Tesseract 5.3, eng")
};
```

- **Stored and shown:** the document keeps `TextOrigin`, and every evidence item and citation from it carries it (`textOrigin` in JSON, in the endpoints and the agent tools), so an answer can say its quote comes from OCR text.
- **What a citation proves:** the quote is exactly the bytes of the derived text, as for any document. Whether the derived text matches the scan or the recording is the OCR or transcription engine's accuracy, which the toolkit can't check.
- **Recipes:**
  - a scanned PDF: run an OCR tool that adds a text layer, such as `ocrmypdf`, and load the result with the [PDF package](#pdf); set `TextOrigin` on the source;
  - images or scans: an OCR engine's plain-text output, loaded with `GraphRagSources.File` or `.Text`;
  - audio or video: a transcript, with `TextOrigin` naming the model, such as `transcript: Whisper large-v3`;
  - Word, PowerPoint or Excel files: Microsoft's markitdown through DataIngestion, with `GraphBridgeChunkWriterOptions.TextOrigin` ([Office documents](AGENTS.md#office-documents)).

  Name the engine and its version, so a reader can judge the text, and name the original by URI, as above, so the document keeps its identity.
- **Unchanged documents keep their metadata:** a document is re-ingested only when its text or the settings change, so set `TextOrigin` on the first ingestion, or change the text.

## Chunking and positions

The core chunker:
- starts a section at each Markdown heading outside fenced code;
- packs paragraphs whole up to the byte budget (`MaxUtf8Bytes`, 2,048 by default) and an optional token budget;
- repeats one paragraph of overlap by default.

Every byte is in at least one chunk, and each chunk's text is exactly its span.

### Your own loader, chunker or token budget

- **A token budget:** register an `IGraphRagTokenCounter` (tokenizers are model-specific, so the application brings one) and set `Chunking.MaxTokens`; the byte budget still applies.
- **A loader** for another format implements `IGraphRagDocumentLoader`: `CanLoad(source)` and `LoadAsync(source)`, which returns the text as UTF-8. Ingestion uses the first registered loader that can load a source, and the text loader comes first, so give yours a media type the text loader doesn't take, as the PDF loader does. To refuse a document, throw `GraphRagLoadException` with a code; it becomes the document's outcome.
- **A chunker** implements `IGraphRagChunker`; register it before `AddGraphRag`, which only adds the core chunker when none is registered. Its chunks must be exact byte spans of the loaded text.
- **Chunks made elsewhere:** `GraphRagChunkedDocument.Create(source, text, spans, chunkerFingerprint)` takes text you've already split, as UTF-16 ranges, and `IGraphRagIngestion.IngestChunkedAsync` stores them with the same runs, leases and receipts. `GraphRagChunkers.Create(options)` gives you the core chunker for that. The [DataIngestion adapter](AGENTS.md) is built this way.

```csharp
var source = GraphRagSources.Text("notes/standup.md", text, "text/markdown");
var spans = MySplitter.Split(text);   // [(Start, End)] in reading order
var document = GraphRagChunkedDocument.Create(source, text, spans, chunkerFingerprint: "my-splitter-v1", title: "Stand-up");
var receipt = await services.GetRequiredService<IGraphRagIngestion>().IngestChunkedAsync([document]);
```

## Embedding sets

A `GraphRagEmbeddingSet` names the embedding model, its dimensions, similarity and normalisation. Its ID names the vector index. Vectors are checked before they are written: the right length, finite values, never all zeros. Embedding calls run in parallel lanes (2 by default) in batches (32), and transient failures are retried, 4 times by default ([retries](REAL_MODELS.md#rate-limits-and-retries)).

## Runs, leases and receipts

- **Each call is a run** with an ID and a settings fingerprint: the chunker, the embedding set and, with an ontology, the extraction schema.
- **A document is unchanged** when its content hash and that fingerprint match its last write, and is then skipped.
- **A lease per document** stops two runs writing the same document. A lease is renewed inside the write transaction, so a run that lost it can't commit.
- **The receipt** lists every document's outcome, chunks written, embedding calls and tokens. A failed document has a stable code, and nothing of it is written:

| Code | The document |
|---|---|
| `invalid-utf8` | isn't valid UTF-8 text |
| `document-too-large` | is larger than `MaxDocumentBytes` |
| `read-failed` | couldn't be read: the file is gone or access was denied |
| `unsupported-media-type` | has a type no loader reads, such as a PDF without `AddGraphRagPdf` |
| `pdf-no-text`, `pdf-unreadable` | is a PDF without a text layer, or one PdfPig can't open ([PDF](#pdf)) |
| `position-not-recovered` | came through DataIngestion with a chunk that couldn't be placed in its text ([agents and APIs](AGENTS.md)) |
| `lease-held` | is being written by another run, whose lease hasn't expired |
| `lease-lost` | lost its lease while being worked on, for example after a pause longer than `LeaseDuration` |
| `embedding-failed`, `embedding-count`, `embedding-dimensions`, `invalid-embedding` | got no embeddings after the retries, or a wrong number of them, or one of the wrong width, or one with a non-finite value or all zeros |
| `extraction-failed` | got no usable extraction reply after the retries |
| `write-failed` | failed in the database; its transaction rolled back |
| `rate-limited` | met a rate limit on every retry ([use a real model](REAL_MODELS.md)) |

## Extraction

When the model maps an [ontology](ONTOLOGIES.md), each chunk is extracted through an `IChatClient`: the one keyed `GraphRagServiceKeys.ExtractionChatClient`, or the container's unkeyed one.
- **Checks:** anything the ontology doesn't allow, or the chunk doesn't contain, is pruned. The receipt records each item with its reason:

| Reason | The item |
|---|---|
| `unknown-kind` | is a thing of a class the ontology doesn't have |
| `missing-name` | is a thing without a name |
| `quote-not-found` | is a mention or a quote whose words aren't in the chunk |
| `ungrounded` | is a thing none of whose mentions is in the chunk |
| `pattern-not-allowed` | is a fact the ontology doesn't allow between those classes |
| `endpoint-not-found` | is a fact or a quote whose thing or speaker wasn't extracted from the chunk |
| `self-relationship` | is a fact from a thing to itself |
| `unknown-type` | is a quote of a type the ontology doesn't have |
| `limit` | is over the per-chunk limit for things, facts or quotes |
| `refused: …` | was refused by your `IGraphRagExtractionVerifier`, with its reason |
- **Quarantine:** an unreadable reply is quarantined and never written.
- **Verifier:** an optional `IGraphRagExtractionVerifier` sees every candidate and can refuse it.
- **Resolution:** things resolve by exact normalised name, and placeholders never merge. [Merge suggestions](MAINTAIN.md#merging-duplicates) find what one name split in two.
- **Failure:** a failed extraction writes nothing of the document, so the next run retries it.

The extraction receipt (`receipt.Extraction`) holds names and reply excerpts from your documents; treat it like the documents.
