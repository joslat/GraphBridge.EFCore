# 34 · Keeping the graph tidy

**Goal:** merge a character that arrived under two names, then undo it; re-ingest only what changed; remove a document and the things only it mentioned; and check the graph's health.
**Track:** full · **Mode:** Neo4j 2026.09 or later · **Time:** ~15 min · **Builds on:** 32

> The GraphRAG toolkit is **experimental** (`GBRAG001`): its API may change in any release.

## Concepts

- Extraction resolves things by exact name, ignoring case and spacing, so one character can arrive twice: "White Rabbit" and "the White Rabbit". **Merge suggestions** pair things of one class whose names are equal once case, spaces and a leading article are ignored, or whose name is the other's alias. Nothing is merged automatically.
- **A merge** moves the mentions and quotes to the kept thing with their bytes, re-points the facts, and makes the merged name an alias. Each merge is a **run** that records what it changed, so it can be undone exactly.
- **Re-ingesting** compares each document's content with what is stored: an unchanged one is skipped, a changed one is replaced in one transaction.
- **Removing** a document removes its passages and its links; the things it alone mentioned are removed by a separate, explicit call. Nothing uses `DETACH DELETE`.

## Run it

```powershell
dotnet run --project workshop/GraphBridge.Workshop -- run 34
```

The lesson needs a database. It ingests two short documents with replies written by hand in place of a model, so the run needs no key.

## Walkthrough

1. **Suggestions.** The lesson lists them, keeps the same-name ones, and previews what merging would move.

<!-- region: Suggest -->
```csharp
var suggestions = await GraphRagResolution.SuggestAsync(db, cancellationToken);
var plan = GraphRagResolution.Plan(suggestions.Where(s => s.Reason == "same-name"));
var preview = await plan.PreviewAsync(db, cancellationToken);
```

2. **Merge and undo.** After the merge one White Rabbit is left, with "the White Rabbit" as an alias; the undo puts every mention, fact, quote and property back.

<!-- region: Merge -->
```csharp
var merged = await plan.ApplyAsync(db, cancellationToken);
var afterMerge = await CharactersAsync();
var undone = await GraphRagMergePlan.UndoAsync(db, merged.RunIds.Single(), cancellationToken);
```

3. **Only what changed.** The first document is skipped; the second, with a sentence added, is replaced.

<!-- region: Changed -->
```csharp
var maintenance = services.GetRequiredService<IGraphRagMaintenance>();
var changed = await maintenance.ReingestChangedAsync(
    [GraphRagSources.Text("first", First), GraphRagSources.Text("later", Later + " Alice ran.")], cancellationToken);
```

4. **Remove and check.** Removing the first document leaves "White Rabbit" and the river mentioned nowhere, so the orphan clean-up removes them. The health check then compares the counts, the indexes and what was read back.

<!-- region: Remove -->
```csharp
var removed = await maintenance.RemoveSourceAsync(first, cancellationToken);
var orphans = await maintenance.RemoveOrphansAsync(cancellationToken);
var health = await maintenance.CheckAsync(cancellationToken);
```

The lesson removes everything it wrote at the end.

## Try it yourself

- Re-ingest the second document with a sentence added after the merge, then try the undo: it is refused, because the merged name was written again. Unchanged, the document would be skipped and the undo would succeed.
- Read `health.IndexStates` to see each index's state.

## Limits

- Suggestions inherit the extraction's mistakes, so a person decides: on the whole of *Alice*, one of 27 suggestions was wrong. The [maintenance guide](../../../../docs/graphrag/MAINTAIN.md#merging-duplicates) explains the rules and what to do when a merge fails.
- [Sample 08](../../../../samples/08-GraphRagNovel/README.md)'s `merge`, `merge apply` and `merge undo` do the same on the whole book.
