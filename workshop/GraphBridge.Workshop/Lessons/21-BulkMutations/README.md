# 21 · Bulk update and delete

**Goal:** change or delete many nodes on the server without loading them, and see what that means for tracked entities.
**Track:** full · **Mode:** needs Neo4j · **Time:** ~10 min · **Builds on:** 04, 12

## Concepts

- EF Core's `ExecuteUpdateAsync` and `ExecuteDeleteAsync` run on the server as one atomic, bounded Cypher call.
- They bypass the change tracker: entities you already loaded are **not** refreshed.
- Setters are limited to constants, parameters, copies of another property, and integer increments. Keys, endpoints and generated properties cannot be set.

## Run it

```powershell
dotnet run --project workshop/GraphBridge.Workshop -- run 21
```

The lesson creates three extra people and deletes them at the end.

## Walkthrough

1. **Update on the server.**

<!-- region: Update -->
```csharp
var updated = await db.People
    .Where(p => ids.Contains(p.Id))
    .ExecuteUpdateAsync(set => set
        .SetProperty(p => p.Nickname, "Background actor")
        .SetProperty(p => p.Born, 2000), cancellationToken);
```

   The person loaded before the update still says `Born = 1900` in memory; a fresh context sees 2000.

2. **A refused setter.** Setting the key throws `Neo4jQueryTranslationException` before any database work.

3. **Delete on the server.**

<!-- region: Delete -->
```csharp
var deleted = await db.People.Where(p => ids.Contains(p.Id)).ExecuteDeleteAsync(cancellationToken);
```

## What you should see

Three rows updated, a stale tracked copy, a refused key setter, and three rows deleted.

## Try it yourself

1. Try `ExecuteDeleteAsync` on `db.People.Where(p => p.Name == "Keanu Reeves")`. Why is it refused by the database? (Keanu still has relationships.)
2. Use `SetProperty(p => p.Nickname, p => p.Name)` to copy one property into another.

## Limits

- At most 10,000 selected rows per call; more aborts the whole call.
- Deletes are restrictive, and synchronous `ExecuteUpdate`/`ExecuteDelete` are not supported.

## Read more

[Bulk mutations](../../../../docs/features/BULK_MUTATIONS.md)
