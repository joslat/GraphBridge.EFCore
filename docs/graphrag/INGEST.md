# Ingest

`IGraphRagIngestion.IngestAsync` takes sources and writes each document in one transaction. A run killed midway leaves each document complete or absent, and a rerun skips what is already written.

## Sources and loading

- **`GraphRagSources.File`, `.Folder` and `.Text`:** Markdown and plain text, in deterministic order. Other formats come through the [DataIngestion adapter](AGENTS.md#dataingestion-pipelines).
- **Text is loaded as UTF-8 and refused when invalid.** A leading byte-order mark is removed. Positions count the bytes that remain.
- **`GraphRagSourceMetadata`** sets a title, publication date, publisher and URL from the source itself.

## Chunking and positions

The core chunker:
- starts a section at each Markdown heading outside fenced code;
- packs paragraphs whole up to the byte budget (`MaxUtf8Bytes`, 2,048 by default) and an optional token budget;
- repeats one paragraph of overlap by default.

Every byte is in at least one chunk, and each chunk's text is exactly its span.

## Embedding sets

A `GraphRagEmbeddingSet` names the embedding model, its dimensions, similarity and normalisation. Its ID names the vector index. Vectors are checked before they are written: the right length, finite values, never all zeros. Embedding calls run in parallel lanes (2 by default) in batches (32), and transient failures are retried at most twice.

## Runs, leases and receipts

- **Each call is a run** with an ID and a settings fingerprint: the chunker, the embedding set and, with an ontology, the extraction schema.
- **A document is unchanged** when its content hash and that fingerprint match its last write, and is then skipped.
- **A lease per document** stops two runs writing the same document. A lease is renewed inside the write transaction, so a run that lost it can't commit.
- **The receipt** lists every document's outcome, chunks written, embedding calls and tokens. A failed document has a stable code, for example:
  - `invalid-utf8`, `document-too-large`, `lease-held`;
  - `embedding-failed`, `extraction-failed`, `write-failed`.

## Extraction

When the model maps an [ontology](ONTOLOGIES.md), each chunk is extracted through an `IChatClient`: the one keyed `GraphRagServiceKeys.ExtractionChatClient`, or the container's unkeyed one.
- **Checks:** unknown kinds, facts the ontology doesn't allow, missing names, quotes not found in the chunk and anything over the per-chunk limits are pruned. The receipt records each item's reason.
- **Quarantine:** an unreadable reply is quarantined and never written.
- **Verifier:** an optional `IGraphRagExtractionVerifier` sees every candidate and can refuse it.
- **Resolution:** things resolve by exact normalised name, and placeholders never merge.
- **Failure:** a failed extraction writes nothing of the document, so the next run retries it.

The extraction receipt (`receipt.Extraction`) holds names and reply excerpts from your documents; treat it like the documents.
