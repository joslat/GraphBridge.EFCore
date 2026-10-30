# 13 · Transactions

**Goal:** group saves and native queries in one explicit transaction, and see what rollback does and does not undo.
**Track:** full · **Mode:** needs Neo4j · **Time:** ~10 min · **Builds on:** 04

## Concepts

- `BeginTransactionAsync` opens one Neo4j transaction that EF saves **and** native queries (`QueryCypherAsync`, lesson 15) share.
- Rollback undoes database work. It does **not** undo EF's in-memory state: entries that were saved stay accepted.
- Without an explicit transaction, every `SaveChangesAsync` is its own atomic transaction.

## Run it

```powershell
dotnet run --project workshop/GraphBridge.Workshop -- run 13
```

## Walkthrough

1. **Save, read your own write, roll back.**

<!-- region: Rollback -->
```csharp
await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
draft = new Person { Id = id, Name = "Draft Person" };
db.People.Add(draft);
await db.SaveChangesAsync(cancellationToken);
// A native query borrows the same transaction, so it sees the uncommitted node.
var inside = await db.Database.QueryCypherAsync("MATCH (p:Person {id: $id}) RETURN count(p) AS value",
    new Dictionary<string, object?> { ["id"] = id.ToString("D") }, r => r["value"].As<long>(), cancellationToken: cancellationToken);
await transaction.RollbackAsync(cancellationToken);
```

   Inside the transaction the native query sees the new node. After rollback, a fresh context does not. The original context still believes the person exists: its entry is `Unchanged`, because EF accepted the save before the rollback.

2. **Two saves, one commit.**

<!-- region: Commit -->
```csharp
await using (var db = lesson.CreateContext())
{
    await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
    var person = new Person { Id = id, Name = "Committed Person" };
    db.People.Add(person);
    await db.SaveChangesAsync(cancellationToken);
    person.Nickname = "Two saves";
    await db.SaveChangesAsync(cancellationToken);
    await transaction.CommitAsync(cancellationToken);
}
```

## What you should see

Four passing checkpoints: the transaction saw its own write, EF still holds the accepted entry, the node is gone after rollback, and both committed saves are visible.

## Try it yourself

1. Throw an exception between the two saves in step 2 instead of committing. What does a fresh context see?
2. Read the uncertain-commit guidance in [recovering a failed graph operation](../../../../docs/features/TRANSACTIONS_AND_CONCURRENCY.md#recovering-a-failed-graph-operation). How would you make a payment-like operation safe to retry?

## Limits

- No savepoints, nested or ambient transactions, and no automatic retries.
- A lost commit acknowledgement does not prove rollback; check durable state before retrying.

## Read more

[Transactions and concurrency](../../../../docs/features/TRANSACTIONS_AND_CONCURRENCY.md) · [Native Cypher DTOs](../../../../docs/features/NATIVE_CYPHER.md)
