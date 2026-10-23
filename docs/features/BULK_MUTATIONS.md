# Bounded bulk mutations

EF Core's `ExecuteUpdateAsync` and `ExecuteDeleteAsync` change or delete the rows a query selects, on the server, in one atomic call of at most 10,000 rows. Use them to change many entities without loading them, when you do not need the change tracker's per-entity behavior.

```csharp
var changed = await db.People
    .Where(p => ids.Contains(p.Id))
    .ExecuteUpdateAsync(set => set
        .SetProperty(p => p.Nickname, "Background actor")
        .SetProperty(p => p.Born, 2000), cancellationToken);

var removed = await db.People
    .Where(p => ids.Contains(p.Id))
    .ExecuteDeleteAsync(cancellationToken);
```

Each call runs immediately and returns the number of affected rows. It works on node roots, native-reference dependents, association edges and identified edges, using the same [LINQ filters, ordering and paging](LINQ_QUERIES.md).

## The change tracker is bypassed

Bulk calls skip tracked entity updates, `SaveChanges` interception and EF's original-value concurrency checks. Entities you already loaded keep their old values, including their row version; read them again in a fresh context.

- A provider-generated `long` row version (`IsRowVersion()`) advances once per updated row, so a later save of a stale tracked copy fails as usual.
- Application concurrency tokens are not checked for you. Put the expected token in the `Where` and, where needed, set the new token explicitly. A stale predicate matches zero rows without throwing.

## Setters

| Setter | Example | Rule |
|---|---|---|
| Constant or captured value | `SetProperty(p => p.Nickname, "updated")` | Converted with the property's EF mapping. Setting a nullable property to null removes it; a required property rejects null. |
| Copy of another property | `SetProperty(p => p.Nickname, p => p.Name)` | Same model CLR type. |
| Integer increment | `SetProperty(p => p.Age, p => p.Age + 1)` | A property plus or minus a value, where source and target are `int` or `long` of the same type, without converters. Arithmetic is checked; overflow aborts the call. |

All setters read the same original row, so copying a property that another setter also changes uses its old value. One call accepts 1 to 64 distinct setters. Unmapped graph properties are preserved.

Refused before transport with `Neo4jQueryTranslationException`, usually GBQ160: setting a key, a relationship endpoint, a foreign key or a generated property; duplicate setters; computed expressions other than the integer increment; `Include`, aggregates or projections in the source query; synchronous `ExecuteUpdate`/`ExecuteDelete`; and entity types with store-generated properties other than the key or a `long` row version. Relationship roots keep their endpoint key and identity checks, and native-reference updates cannot move a dependent to another parent.

## Atomicity and limits

- **10,000 selected rows per call.** One extra row is read as a witness; if it exists, the whole call aborts, and nothing is partially committed. Use your own filter and paging for larger work; GraphBridge does not split a call into several commits. The cap bounds rows, not row size, server sorting or time.
- **Locked and rechecked.** The call checks the required uniqueness constraints, selects candidates, locks them, rechecks every mapped value and then submits one parameterized `UNWIND` batch. A candidate that changed or disappeared in between aborts the call with a concurrency failure; decide deliberately whether to run it again. Physical Neo4j IDs locate candidates only within that transaction and never become EF keys. No predicate or range lock is taken, so this is not serializable isolation against other writers.
- **Transactions.** Without an explicit transaction, the call commits on its own. Inside `BeginTransactionAsync`, success leaves commit or rollback to you, and a failure aborts that whole transaction, because Neo4j has no savepoints. There is no automatic retry, and deadlocks surface as failures.
- **Restrictive deletes.** Deletion uses plain `DELETE`, never an implicit `DETACH DELETE`. A node with remaining relationships fails, and the call rolls back. Deleting a native-reference dependent also removes its modeled parent link, as a tracked delete would. Deleting relationships keeps their endpoint nodes.
- **Cancellation.** Checked before transport, between driver operations and while reading the bounded selection. An in-flight command is not abandoned. Cancellation observed before commit rolls back; it cannot undo an acknowledged commit. A lost commit acknowledgement leaves the outcome unknown: see [transactions and concurrency](TRANSACTIONS_AND_CONCURRENCY.md).

A non-empty call on plain nodes submits four statements (schema check, selection, lock and recheck, mutation) plus transaction exchanges; relationship and reference roots add a separate identity read. Bulk calls are traced as `GraphBridge.Bulk` with [command tracing](COMMAND_TRACING.md).

## Upserts

`ExecuteUpsertAsync` creates a node for each entity whose key no node has yet, and updates the node that has it. Like `ExecuteUpdateAsync`, it runs immediately, as one statement in one transaction, and bypasses the change tracker.

```csharp
var result = await db.People.ExecuteUpsertAsync(people, cancellationToken: cancellationToken);
Console.WriteLine($"{result.Created} created, {result.Matched} already existed");

// Copy only some properties onto nodes that already exist.
await db.People.ExecuteUpsertAsync(people, p => new { p.Name, p.Email });

// Create missing nodes and leave existing ones unchanged.
await db.People.ExecuteUpsertAsync(people, p => new { });
```

- **Keys.** Each entity carries its EF key, set by your code. A uniqueness constraint must cover the key; the call checks it before writing anything, because without it concurrent writers could create the same node twice. A key left at its default value is refused.
- **What is written.** A new node gets every stored mapped property, converted as `SaveChanges` converts it. An existing node gets the properties the selector names, or every stored non-key property when you pass none; a null removes that property. The key itself is never updated.
- **Limits.** At most 10,000 entities per call, and no two with the same key; either problem is refused before anything is sent. Larger inputs need several calls, each atomic.
- **Result.** `GraphUpsertResult` has `Created`, `Matched` and the server's `GraphWriteCounters`, read from the statement's summary without another round trip.
- **Transactions and retries.** Without an explicit transaction, the call commits on its own. Inside `BeginTransactionAsync` it joins your transaction, and a failure aborts the whole transaction, as for the other bulk calls. Repeating the same upsert writes the same values, so it is safe to run again after a failure or an unknown commit outcome; only `Created` and `Matched` change.
- **Not yet supported:** relationships (association and identified edges), native-reference dependents, concurrency tokens and shadow properties are refused. Tracked instances are not refreshed.

Upserts are traced as `GraphBridge.Upsert`. Design: [ADR-0051](../architecture/adr/ADR-0051.md).

## Try it

- Workshop lesson [21 · Bulk update and delete](../../workshop/GraphBridge.Workshop/Lessons/21-BulkMutations/README.md): update and delete on the server, a stale tracked copy and a refused key setter.

Evidence: [bounded bulk increment](../../tests/verification/EF_CONFORMANCE_MATRIX.md#bounded-bulk-increment); tests for [protocol rules](../../tests/GraphBridge.UnitTests/BulkMutationTests.cs), [nodes](../../tests/GraphBridge.IntegrationTests/ProviderUpdateTests.Bulk.cs), [concurrency and the cap](../../tests/GraphBridge.IntegrationTests/ProviderUpdateTests.BulkConcurrency.cs), [associations](../../tests/GraphBridge.IntegrationTests/ProviderAssociationUpdateTests.Bulk.cs), [identified edges](../../tests/GraphBridge.IntegrationTests/ProviderIdentifiedEdgeUpdateTests.Bulk.cs), [required](../../tests/GraphBridge.IntegrationTests/ProviderNodeReferenceUpdateTests.Bulk.cs) and [optional references](../../tests/GraphBridge.IntegrationTests/ProviderOptionalReferenceTests.Bulk.cs); upserts: [protocol rules](../../tests/GraphBridge.UnitTests/UpsertTests.cs) and [live](../../tests/GraphBridge.IntegrationTests/ProviderUpsertTests.cs). Design: [ADR-0038](../architecture/adr/ADR-0038.md), [ADR-0051](../architecture/adr/ADR-0051.md).
