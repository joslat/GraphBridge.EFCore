# Transactions and concurrency

Every `SaveChangesAsync` call is one atomic Neo4j transaction. An explicit transaction groups several saves with LINQ, Fluent Cypher and native queries, and row versions or concurrency tokens stop lost updates. GraphBridge never retries on its own; this page also shows how to recover when a commit's outcome is unknown.

```csharp
await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
var draft = new Person { Id = id, Name = "Draft Person" };
db.People.Add(draft);
await db.SaveChangesAsync(cancellationToken);
// A native query borrows the same transaction, so it sees the uncommitted node.
var inside = await db.Database.QueryCypherAsync("MATCH (p:Person {id: $id}) RETURN count(p) AS value",
    new Dictionary<string, object?> { ["id"] = id.ToString("D") }, r => r["value"].As<long>(), cancellationToken: cancellationToken);
await transaction.RollbackAsync(cancellationToken);
```

Inside the transaction the query counts one node. After the rollback a fresh context finds none, but this context's entry for `draft` is still `Unchanged`: EF accepted the save before the rollback.

## SaveChangesAsync is atomic

Without an explicit transaction, each `SaveChangesAsync` that has changes opens one transaction, writes every pending entry (nodes, references, associations and identified relationships) and commits. Either all of them are stored or none are. New endpoints are created before their relationships, and relationship deletions run before node deletions. Deletion is restrictive: a node that still has relationships is not deleted, and there is no implicit `DETACH DELETE`.

| Failure | Exception | EF state afterwards |
|---|---|---|
| Neo4j rejects a command, for example a duplicate key | `DbUpdateException` with the affected entries | Entries stay pending; nothing was stored |
| A row version or token no longer matches, or the element is gone | `DbUpdateConcurrencyException` | Entries stay pending; nothing was stored |
| A key uniqueness constraint is missing | `InvalidOperationException` before any mutation | Entries stay pending; see [schema and migrations](SCHEMA_AND_MIGRATIONS.md) |
| The commit fails or its response is lost | `DbUpdateException` whose message says the commit outcome may be unknown; non-Neo4j errors can propagate directly | Outcome unknown; see [recovering a failed graph operation](#recovering-a-failed-graph-operation) |

Persistence is async only. `SaveChanges()` with pending changes throws `NotSupportedException`; an unchanged context can still return 0 through EF's own shortcut. See the [async-only decision](../architecture/adr/ADR-0037.md).

## Explicit transactions

`db.Database.BeginTransactionAsync()` opens one Neo4j write transaction on the context's database. Everything the context runs before you commit or roll back uses it, so later reads see earlier uncommitted writes:

| Operation | Inside an explicit transaction |
|---|---|
| `SaveChangesAsync` | Writes in the transaction; nothing is durable until `CommitAsync` |
| LINQ queries | Run in the transaction and see its writes |
| Fluent Cypher `ToListAsync` and `QueryCypherAsync` | Borrow the transaction; you decide commit or rollback, also when a projector fails |
| `ExecuteUpdateAsync`, `ExecuteDeleteAsync` | Run in the transaction; a failure aborts the whole transaction |
| Vector and full-text search, `QueryPathsAsync` | Borrow the transaction. Vector index updates made in the same transaction are not visible to search |
| `InspectGraphSchemaAsync`, `ExplainCypherAsync` | Borrow the transaction unchanged |
| Migration APIs | Refused with `NotSupportedException`; they need their own step transactions |

Two saves, one commit:

```csharp
await using (var db = new MoviesContext(driver, "neo4j"))
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

Rules:

- Use `await using` for the transaction and the context. Disposing an uncommitted transaction rolls it back. Synchronous `BeginTransaction`, `Commit`, `Rollback` and `Dispose` of an active transaction throw `NotSupportedException`.
- One transaction per context at a time. Nested transactions throw `InvalidOperationException`; savepoints and ambient or distributed transactions (`TransactionScope`) are not supported.
- A failed save inside an explicit transaction aborts the entire transaction, because there are no savepoints. Earlier successful saves in it are lost with it. Rebuild the whole operation in a fresh context; retrying only the last save can lose work.
- Native writes through `QueryCypherAsync` do not refresh entities the context already tracks.

### What rollback does not undo

Rollback restores the database. It does not rewind EF's change tracker: entries saved earlier in the transaction stay accepted (`Unchanged`), with their new row versions. After a rollback, discard the context and start again with a fresh one, or control acceptance yourself with EF's `SaveChangesAsync(acceptAllChangesOnSuccess: false)`. GraphBridge adds no second tracker. `ReloadAsync` and `GetDatabaseValuesAsync` are not available as a recovery path; they are refused before any database work.

## Optimistic concurrency

Mark an unconverted, non-nullable `long` with `IsRowVersion()` and GraphBridge maintains it: a new element starts at 1, and every update or delete checks the loaded value and, on update, increments it.

```csharp
person.Property(p => p.Version).HasGraphPropertyName("version").IsRowVersion();
```

Two contexts load the same person. The first save wins and moves the version; the second is based on a stale version and is refused:

```csharp
mine.Nickname = "Neo4j co-founder";
await first.SaveChangesAsync(cancellationToken);      // MATCH ... WHERE version = loaded → SET version = loaded + 1

theirs.Nickname = "Graph person";
await second.SaveChangesAsync(cancellationToken);     // throws DbUpdateConcurrencyException; nothing is overwritten
```

Recovery is your decision: read the current state in a fresh context, decide, save again.

```csharp
await using (var retry = new MoviesContext(driver, "neo4j"))
{
    var current = await retry.People.SingleAsync(p => p.Id == id, cancellationToken);
    current.Nickname = "Graph person";            // or merge, or ask the user
    await retry.SaveChangesAsync(cancellationToken);
}
```

| Option | Configure | Behavior |
|---|---|---|
| Row version | `IsRowVersion()` on an unconverted non-nullable `long` | Provider-generated: starts at 1, increments by one per update. Other types or converters are refused with `NotSupportedException` |
| Concurrency token | `IsConcurrencyToken()` on a mapped property | You change the value; the save compares the original value, including null |

Both work on nodes, associations and identified relationships; optional reference changes share the dependent node's check. Each update or delete first locks the element, then compares its values in the same transaction, so a concurrent write cannot slip in between. A changed value or a missing element raises `DbUpdateConcurrencyException`, its `Entries` name the conflicting entities, and the whole save rolls back. There is no automatic merge or retry. [Bulk mutations](BULK_MUTATIONS.md) recheck their candidates the same way and abort with a concurrency failure when one changed.

## Recovering a failed graph operation

GraphBridge does not automatically retry `SaveChangesAsync()` or an explicit transaction. A failed commit can have succeeded on the server. Keep the operation identity, dispose the failed context, and reconcile through a fresh context before deciding what to do next. The [recovery design](../architecture/adr/ADR-0026.md) describes the fault boundaries.

### Interpret the failure boundary

| Boundary | Provider behavior | Application decision |
|---|---|---|
| Token cancelled before work | Throws cancellation before starting the save | The save performed no work; preserve the operation ID for any later attempt. |
| Command rejected or cancellation observed between commands | Attempts rollback and resource cleanup; ordinary save entries remain pending | Dispose the context. A rollback attempt alone does not prove successful cleanup; retain and inspect the original failure. |
| Cancellation while a driver command is in flight | Awaits that command, then observes cancellation before continuing/committing | Cancellation is cooperative, not an immediate server interrupt. Do not abandon the task and reuse/dispose its context concurrently. |
| COMMIT request or response fails | Clears the transaction and surfaces the error; does not replay or assume rollback | Outcome is uncertain. Pending EF entries are not evidence of rollback. Reconcile durable operation state. |
| COMMIT acknowledged, cleanup fails | Does not replay; a Neo4j cleanup exception reports that commit succeeded | Verify through a fresh context. Cleanup failure does not undo committed effects. |
| Later save fails inside an explicit transaction | Aborts the entire transaction; earlier successful saves may already have accepted EF state | Rebuild the whole application operation in a fresh context. Retrying only the last save can lose earlier work. |

Neo4j errors during a save are wrapped in `DbUpdateException`; concurrency conflicts use `DbUpdateConcurrencyException`. Cancellation and non-Neo4j exceptions can propagate directly. Do not implement recovery by parsing exception message strings or by treating all failures as retryable. Driver 6.3 has no cancellation-token overload for transaction commands or commit; GraphBridge deliberately awaits them rather than returning while a write can still be running.

### Store an operation receipt with the effects

An application can make deliberate retries safe within a graph transaction:

1. Assign a stable operation ID **before the first attempt**. Reuse it across all retries of that operation.
2. Define a versioned, deterministic fingerprint of the complete intended request: operation kind, tenant, target keys and values. The same ID must never identify a different request.
3. Protect the receipt's application key with a reviewed uniqueness constraint. Create the receipt and all graph effects in the same atomic save or explicit transaction. Every participating writer must follow this convention.
4. After an uncertain outcome, dispose the failed context. Read the receipt from a fresh context against an appropriate authoritative database view.
5. A matching receipt confirms the corresponding atomic effects committed. A different fingerprint is a conflict requiring resolution. **An absent receipt is inconclusive:** the original transaction may still be in flight, or the read may be stale.
6. If the application deliberately retries, use the same operation ID and fingerprint. A competing or already committed receipt must reject the entire new transaction, including mutations made before receipt creation. Reconcile afterward; an unrelated constraint failure is still an error.

The [executable recovery cases](../../tests/GraphBridge.IntegrationTests/ProviderCommitRecoveryTests.cs) model a counter increment, a separately created effect and an application-owned receipt. Their fingerprint is a fixed test value; a real application must bind its complete request as described above. Each retry uses fresh EF state and checks a real counter/version change, rather than relying on an idempotent property assignment. The provider does not add an operation-receipt entity or second state tracker automatically.

Receipts must remain available throughout the supported retry window. Deleting a receipt, changing its identity or bypassing its constraint removes the duplicate-prevention guarantee. An operation whose receipt and effects live in different transactions does not satisfy this contract. A durable receipt also does not atomically cover email, payments or other remote side effects; those need a separate delivery protocol.

### Transient failures and cancellation

A real deadlock abort establishes that the aborted transaction did not commit. A fresh retry must reconstruct the complete operation, including earlier saves in that transaction, retain its stable identity and apply an explicit bounded retry policy. A transient exception during commit can still have an uncertain outcome; exception classification alone is insufficient.

The [command-boundary tests](../../tests/GraphBridge.IntegrationTests/ProviderCommitRecoveryTests.Commands.cs) exercise a real server lock wait during cancellation and a two-transaction deadlock. The cancellation test releases only its owned blocking transaction, awaits the original save, and verifies rollback of earlier receipt/effect writes. The deadlock test distinguishes the loser from the winner, checks their durable receipts, and rebuilds the loser's whole operation.

### Reproduce the focused checks

From a built checkout, the parser tests need no database:

```powershell
dotnet test tests/GraphBridge.UnitTests --no-restore -m:1 --filter FullyQualifiedName~BoltBoundaryReaderTests
```

The live cases use the repository's disposable fixture and guarded runner:

```powershell
pwsh -File eng/scripts/Run-IntegrationTests.ps1 -NoBuild -Filter 'FullyQualifiedName~ProviderCommitRecoveryTests'
```

The harness accepts only the marker-verified Community fixture on loopback port 17687 and creates no additional container or database. A bounded TCP proxy forwards real driver traffic, cuts the verified COMMIT boundary, and never logs raw protocol payloads. The after-commit test requires the complete server SUCCESS response before dropping its acknowledgement; timing alone is not accepted as proof. All graph observations and cleanup use the original fixture driver and run-scoped labels and keys.

## Command timeouts

Neo4j times out transactions, not single statements. GraphBridge sets a timeout on every transaction it begins: a LINQ query's auto-commit run, the transaction of `SaveChangesAsync`, a bulk mutation, native Cypher, and a transaction you begin yourself ([ADR 0050](../architecture/adr/ADR-0050.md)).

```csharp
// The default for every context, in DI or OnConfiguring:
options.UseNeo4j(driver, "neo4j", neo4j => neo4j.CommandTimeout(TimeSpan.FromSeconds(30)));

// One context instance, until it is reset (for example returned to a pool):
db.Database.SetGraphCommandTimeout(TimeSpan.FromMinutes(5));

// One Fluent Cypher query:
var rows = await query.WithTimeout(TimeSpan.FromSeconds(2)).ToListAsync(cancellationToken);
```

- **Values** are positive and at most 24 hours; `null` uses the server's default, and the server's own maximum still applies.
- **Inside an explicit transaction** the timeout in effect when the transaction began covers all of it. A Fluent `WithTimeout` there throws `InvalidOperationException` instead of being ignored; set `SetGraphCommandTimeout` before `BeginTransactionAsync`.
- **A LINQ query** uses the context timeout; there is no per-query LINQ operator yet.
- **When it fires,** the driver's `Neo4jException` propagates unchanged with code `Neo.ClientError.Transaction.TransactionTimedOutClientConfiguration` (inside `SaveChangesAsync`, wrapped in `DbUpdateException`), and the failing [trace span](COMMAND_TRACING.md) gets `error.type = timeout`. The transaction is rolled back; nothing is retried.

## Limits

- No automatic retry, savepoints, nested, ambient or distributed transactions, and no exactly-once guarantee after a lost commit acknowledgement.
- Rollback does not rewind EF state; `ReloadAsync` and `GetDatabaseValuesAsync` are not available.
- Neo4j transactions are read committed: GraphBridge locks the elements it updates or deletes, not the ranges you queried.
- The recovery tests cover one disposable server. Cluster leader changes, routing and bookmark visibility, operational receipt retention and external side effects need deployment-specific validation.

## Try it

- Workshop lessons [12 · Optimistic concurrency](../../workshop/GraphBridge.Workshop/Lessons/12-Concurrency/README.md) and [13 · Transactions](../../workshop/GraphBridge.Workshop/Lessons/13-Transactions/README.md).
- Sample [01 Blogging](../../samples/01-Blogging-CodeFirst/README.md): concurrency conflicts and explicit rollback.

Evidence: [EF conformance matrix](../../tests/verification/EF_CONFORMANCE_MATRIX.md), [capability evidence](../../tests/verification/CAPABILITY_EVIDENCE.md). Decisions: [ADR-0013](../architecture/adr/ADR-0013.md), [ADR-0026](../architecture/adr/ADR-0026.md).
