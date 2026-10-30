# Maintain

`IGraphRagMaintenance` keeps the graph healthy with explicit, bounded deletes. It refuses graphs the toolkit didn't write: nodes under its labels without a known run ID.

| Method | What it does |
|---|---|
| `ReingestChangedAsync(sources)` | Ingests again; unchanged documents are skipped |
| `RemoveSourceAsync(documentId)` | Removes one document, with its sections, chunks, links, mentions and quotes, and takes it off its facts |
| `ResetAsync(scope)` | Removes documents by run, by ID, or every document the toolkit wrote |
| `RemoveOrphansAsync()` | Removes sections and chunks whose document is gone, expired leases, and things no mention points to (with their facts) |
| `CheckAsync()` | The health report |

Every delete runs by key, in pages, never with `DETACH DELETE`. Documents are removed in batches of up to 100, one transaction each: every document in a batch is removed whole, or the batch not at all.

```csharp
var maintenance = services.GetRequiredService<IGraphRagMaintenance>();
// A run's documents, for example after ingesting the wrong folder:
var report = await maintenance.ResetAsync(new GraphRagResetScope(RunIds: [receipt.RunId]));
// Or chosen documents, by their IDs:
report = await maintenance.ResetAsync(new GraphRagResetScope(DocumentIds: staleIds));
Console.WriteLine($"{report.Documents} documents, {report.Chunks} chunks, {report.Edges} links, {report.Entities} things removed");
```

`GraphRagRemovalReport` counts what was removed: documents, sections, chunks, links, leases and things. With neither runs nor IDs, `ResetAsync` removes every document the toolkit wrote. The schema stays.

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

## Merging duplicates

Extraction resolves things by exact name, so one character can arrive twice: the whole of *Alice* gave "Caterpillar" and "the Caterpillar". Merging is a judgement, so the toolkit suggests and you decide:

```csharp
var suggestions = await GraphRagResolution.SuggestAsync(db);
var plan = GraphRagResolution.Plan(suggestions.Where(s => s.Reason == "same-name"));
var preview = await plan.PreviewAsync(db);      // what would move, and blockers
var receipt = await plan.ApplyAsync(db);        // one run per merge
foreach (var run in receipt.RunIds.Reverse()) await GraphRagMergePlan.UndoAsync(db, run);   // last first
```

- **Suggestions** pair things of one class whose names are equal once case, extra spaces, a leading "the", "a" or "an" and trailing punctuation (`.,;:!?`) are ignored (`same-name`), or whose name is the other's alias (`alias`). Placeholders are never suggested, nor are names that only contain another, such as "Mouse" and "Dormouse". The most-mentioned thing is kept.
- **A merge** moves the mentions and quotations to the kept thing, with their bytes, and re-points the facts. A fact the kept thing already states gets the union of the documents; one that would join the kept thing to itself is dropped. In a class with aliases, the merged name becomes an alias.
- **Undo** puts back every row and property the merge changed. It is refused once the merged thing or one of its rows is written again, for example by re-ingesting a changed document; merge again instead. It is refused, too, while something the merge wrote was changed since: by a later merge into the same thing, or by a document ingested after it that states a fact the merge moved. So undo a plan's runs last first.
- **Don't merge while ingesting.** A merge reads what it moves before it writes, so a document written in between could lose its place on a fact.
- **A merge that fails** normally writes nothing of itself, but the merges before it in the plan are written. `ApplyAsync` then throws `GraphRagMergeException`: `Failed` is the merge that failed, `AppliedRunIds` the runs written, to undo last first or keep. In the rare case that the failed merge's save broke after its commit, its own run was written too: the message says so and that run is the last in `AppliedRunIds`. Its message names the element the database refused, when there is one.
- **Known limitation:** in the toolkit's whole-book test, a merge plan failed this way 2 times in about 43 runs, refused at the first merge's deletion of one quote it had just read, over the same graph as the runs that passed. The cause isn't known yet. Nothing of that merge is written, so the plan's remaining suggestions can be applied again; the test passed each time it was run again.

Read the suggestions before you apply them: they inherit the extraction's mistakes. On the whole of *Alice*, 26 of the 27 were one character or place, and one was not: the model gave Alice the alias "Mabel", so "Mabel" was suggested as Alice.

Extraction doesn't read aliases, so re-ingesting a document can bring a merged name back as a thing of its own, and suggestions show it again.

Try it: [sample 08](../../samples/08-GraphRagNovel/README.md)'s `merge`, `merge apply` and `merge undo`, after ingesting the whole book.

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
- **Questions:** `GraphRag.Retrieve` and `GraphRag.Answer`, `GraphRag.Rewrite` for a follow-up's rewrite, and `GraphRag.Text2Cypher` for a question about the graph.

Activities carry IDs, counts, outcomes and timings only, never document text, questions, quotes, prompts or source URIs. A failure sets the status to its error code. The provider's own [command tracing](../features/COMMAND_TRACING.md) covers the statements underneath.
