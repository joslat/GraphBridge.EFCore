# 04 · Create, read, update, delete

**Goal:** save, query, change and delete a node with ordinary EF Core code and `SaveChangesAsync`.
**Track:** ★ core · **Mode:** needs Neo4j · **Time:** ~10 min · **Builds on:** 03

## Concepts

- EF Core is the only owner of entity state: the change tracker decides what to insert, update and delete.
- `SaveChangesAsync` writes all pending changes in one transaction and returns the number of **EF entries** saved.
- `Person.Version` is a provider-maintained row version: 1 on insert, +1 on every update.

## Run it

```powershell
dotnet run --project workshop/GraphBridge.Workshop -- run 04
```

The lesson creates its own `Ada Lovelace` node and deletes it at the end; the seeded movie graph is not touched.

## Walkthrough

1. **Create.**

<!-- region: Create -->
```csharp
var ada = new Person { Id = id, Name = "Ada Lovelace", Born = 1815 };
await using (var db = lesson.CreateContext())
{
    db.People.Add(ada);
    var saved = await db.SaveChangesAsync(cancellationToken);   // one EF entry → one CREATE
    lesson.Expect("one entry saved", 1, saved);
}
```

2. **Read** in a fresh context, tracked and untracked:

<!-- region: Read -->
```csharp
var query = db.People.Where(p => p.Id == id);
var tracked = await query.SingleAsync(cancellationToken);
var detached = await db.People.AsNoTracking().Where(p => p.Id == id).SingleAsync(cancellationToken);
```

   The key lookup becomes a parameterized `MATCH`:

<!-- snapshot: 04-read-by-key -->
```cypher
MATCH (n:`Person`)
WHERE coalesce(n.`id` = $p0, (n.`id` IS NULL AND $p0 IS NULL))
RETURN n.`id` AS `c0`, n.`born` AS `c1`, n.`name` AS `c2`, n.`nickname` AS `c3`, n.`version` AS `c4`
```

3. **Update.** Only the changed property is written, and the version moves to 2. To prove it, the lesson first sets a property this model does not map (`source`) with native Cypher; it survives the EF update.

<!-- region: Update -->
```csharp
var person = await db.People.SingleAsync(p => p.Id == id, cancellationToken);
person.Nickname = "Enchantress of Numbers";
await db.SaveChangesAsync(cancellationToken);              // writes only the changed property
```

4. **Delete**, then confirm in a fresh context that Ada is gone.

<!-- region: Delete -->
```csharp
var person = await db.People.SingleAsync(p => p.Id == id, cancellationToken);
db.People.Remove(person);
await db.SaveChangesAsync(cancellationToken);
```

## What you should see

Ten passing checkpoints, including version 1 after insert, version 2 after update, and the unmapped `source` property surviving the update.

## Try it yourself

1. Comment out step 4 (the delete) and run the lesson. In Neo4j Browser, `MATCH (p:Person {name: 'Ada Lovelace'}) RETURN p` shows `nickname` and `source`. Now also set `Born = null` in step 3 and run again: Neo4j removes a property set to null. Restore step 4 afterwards; `reset` removes leftover Adas.
2. Load the same person in two contexts, change both and save both. What happens to the second save? (Lesson 12.)

## Limits

- Deleting a node that still has relationships is refused. There is no silent `DETACH DELETE`.
- Synchronous `SaveChanges()` is not supported (lesson 01).

## Read more

[Mapping nodes: saving](../../../../docs/features/MAPPING_NODES.md#save-changes) · [Known limitations](../../../../docs/reference/KNOWN_LIMITATIONS.md)
