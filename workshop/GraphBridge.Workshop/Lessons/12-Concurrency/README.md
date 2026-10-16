# 12 · Optimistic concurrency

**Goal:** detect a lost update with a provider-maintained row version, and recover by reading again.
**Track:** full · **Mode:** needs Neo4j · **Time:** ~10 min · **Builds on:** 04

## Concepts

- `IsRowVersion()` on a `long` property makes the provider maintain a version: it checks the loaded value on every update and increments it.
- If someone else saved first, your save matches nothing and throws `DbUpdateConcurrencyException`. Nothing is overwritten.
- Recovery is an application decision: read the current state in a fresh context, decide, save again.

## Run it

```powershell
dotnet run --project workshop/GraphBridge.Workshop -- run 12
```

The lesson changes Emil Eifrem's nickname and restores it at the end.

## Walkthrough

1. Two contexts load the same person, with the same version.
2. **The first save wins.**

<!-- region: First -->
```csharp
mine.Nickname = "Neo4j co-founder";
await first.SaveChangesAsync(cancellationToken);      // MATCH ... WHERE version = loaded → SET version = loaded + 1
```

3. **The second save is refused**, because its loaded version is stale:

<!-- region: Second -->
```csharp
theirs.Nickname = "Graph person";
await lesson.ExpectFailureAsync<DbUpdateConcurrencyException>("the stale save is refused",
    () => second.SaveChangesAsync(cancellationToken));
```

4. **Recover** in a fresh context:

<!-- region: Recover -->
```csharp
await using (var retry = lesson.CreateContext())
{
    var current = await retry.People.SingleAsync(p => p.Id == SeedData.Emil, cancellationToken);
    current.Nickname = "Emil";                          // the workshop's seeded value
    await retry.SaveChangesAsync(cancellationToken);
}
```

## What you should see

The version moves by one per successful save, the stale save fails with `DbUpdateConcurrencyException`, and the first writer's value survives until the recovery.

## Try it yourself

1. Catch the exception and inspect `error.Entries`. Which entity is reported?
2. Use `IsConcurrencyToken()` on `Nickname` instead of a row version. What changes?

## Limits

- No automatic merge or retry.
- Rollback does not rewind entities EF has already accepted; use a fresh context (lesson 13).

## Read more

[Mapping nodes: saving](../../../../docs/features/MAPPING_NODES.md#save-changes) · [Transactions and concurrency](../../../../docs/features/TRANSACTIONS_AND_CONCURRENCY.md)
