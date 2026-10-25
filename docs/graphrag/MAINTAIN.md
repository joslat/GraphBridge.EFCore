# Maintain

`IGraphRagMaintenance` keeps the graph healthy with explicit, bounded deletes. It refuses graphs the toolkit didn't write: nodes under its labels without a known run ID.

| Method | What it does |
|---|---|
| `ReingestChangedAsync(sources)` | Ingests again; unchanged documents are skipped |
| `RemoveSourceAsync(documentId)` | Removes one document, with its sections, chunks, links, mentions and quotes, and takes it off its facts |
| `ResetAsync(scope)` | Removes documents by run, by ID, or every document the toolkit wrote |
| `RemoveOrphansAsync()` | Removes sections and chunks whose document is gone, expired leases, and things no mention points to (with their facts) |
| `CheckAsync()` | The health report |

Every delete runs by key, in pages, never with `DETACH DELETE`. One document is removed per transaction.

## The health report

Its findings are stable codes; none means healthy.

| Finding | Meaning |
|---|---|
| `schema-missing`, `index-not-online` | A constraint or index of `GraphRagSchema` is missing, or not ONLINE |
| `missing-embedding`, `mixed-embedding-sets` | Chunks without a vector, or with one from another embedding set |
| `orphans`, `orphan-entities` | Chunks or sections without their document; things no mention points to |
| `stale-facts` | Facts listing a document that is gone |
| `readback-mismatch` | A chunk whose text is not as long as its span allows |
| `expired-leases` | Leases left by runs that stopped |

## Re-embedding

A vector index covers one label and property, so a new embedding set is written to a staging property with its own index. Nothing is replaced in place. Start from the plan, then follow the stages below:

```csharp
var plan = GraphRagReembedding.Plan(layout, currentSet, newSet, stagingProperty: "EmbeddingNext");
```

1. **Prepare:** put `plan.PrepareSteps` in your migration plan. `await plan.PreviewAsync(db)` shows how many chunks remain.
2. **Backfill:** `await plan.BackfillAsync(db, newGenerator)` embeds the remaining chunks in pages. Stop it and run it again whenever you like.
3. **Cut over:** deploy `AddGraphRag(newSet.Dimensions, o => o.Layout = plan.NewLayout)` with the new set, then run `await plan.CompleteAsync(db)`.
4. **Clean up:** apply `plan.CleanupSteps`, which drop the old index, then run `await plan.CleanupAsync(db)` to remove the old vectors.

Completing or cleaning up with the old model is refused. The health report flags mixed sets until the plan completes.

## Tracing

Subscribe to the `ActivitySource` named `GraphRagTracing.ActivitySourceName` (`GraphBridge.GraphRag`).
- **Ingestion:** `GraphRag.Ingest` covers each call, `GraphRag.Document` each document within it, and `GraphRag.Embed`, `GraphRag.Extract` and `GraphRag.Write` each stage.
- **Questions:** `GraphRag.Retrieve` and `GraphRag.Answer`.

Activities carry IDs, counts, outcomes and timings only, never document text, questions, quotes, prompts or source URIs. A failure sets the status to its error code. The provider's own [command tracing](../features/COMMAND_TRACING.md) covers the statements underneath.
