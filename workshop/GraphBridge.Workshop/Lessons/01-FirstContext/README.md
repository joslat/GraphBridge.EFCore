# 01 · Your first context

**Goal:** configure an EF Core context for Neo4j with a driver your application owns, and see your first query.
**Track:** ★ core · **Mode:** offline · **Time:** ~10 min · **Builds on:** 00

## Concepts

- GraphBridge is an **EF Core provider**. You write an ordinary `DbContext`; EF owns the model, identity and change tracking.
- Your application owns the Neo4j `IDriver` and lends it to EF with `UseNeo4j`. The official driver does all network work.
- Every query can be compiled to Cypher without a database. Values are always parameters.

## Run it

```powershell
dotnet run --project workshop/GraphBridge.Workshop -- run 01
```

No database is needed.

## Walkthrough

1. **Lend the driver to EF.** The whole provider configuration is one call in `MoviesContext`:

<!-- region: Domain/MoviesContext.cs#Configure -->
```csharp
protected override void OnConfiguring(DbContextOptionsBuilder options)
    => options.UseNeo4j(driver, database);
```

   Create the driver once with `GraphDatabase.Driver(uri, auth)`, keep it for the application's lifetime and dispose it at shutdown. Contexts are short-lived and never close it.

2. **EF builds the model.** The lesson prints the provider name and the six mapped entity types: `Person`, `Movie`, `Review`, `Credit`, `Like` and `Follow`. Lesson 02 shows how they are mapped.

3. **Your first query.**

<!-- region: FirstQuery -->
```csharp
var name = "Keanu Reeves";
var query = db.People.Where(p => p.Name == name).OrderBy(p => p.Name).Take(5);
var cypher = query.ToQueryString();   // parameter placeholders only, never the value
```

   The generated Cypher uses `$p0`, never the name itself:

<!-- snapshot: 01-first-query -->
```cypher
MATCH (n:`Person`)
WHERE coalesce(n.`name` = $p0, (n.`name` IS NULL AND $p0 IS NULL))
RETURN n.`id` AS `c0`, n.`born` AS `c1`, n.`name` AS `c2`, n.`nickname` AS `c3`, n.`version` AS `c4`
ORDER BY n.`name` IS NULL DESC, n.`name` ASC
LIMIT $p1
```

   The `coalesce(...)` and `IS NULL` parts reproduce **C# null semantics** in Cypher: in C#, `null == null` is true. Lesson 16 shows the Fluent Cypher surface, which keeps native Cypher semantics instead.

4. **Async only.**

<!-- region: AsyncOnly -->
```csharp
lesson.ExpectFailure<NotSupportedException>("synchronous enumeration is rejected", () => query.ToList());
db.People.Add(new Person { Id = Guid.NewGuid(), Name = "Not saved" });
lesson.ExpectFailure<NotSupportedException>("synchronous SaveChanges is rejected", () => db.SaveChanges());
```

   Both fail immediately, before any database work. Use `ToListAsync`, `SingleAsync`, `SaveChangesAsync` and `await using`.

## What you should see

Six passing checkpoints, the Cypher above and the two rejection messages.

## Try it yourself

1. Change the query to `db.Movies.Where(m => m.Released > 2000)` and print its Cypher. Where did `Released` become `released`?
2. Remove `Take(5)`. What disappears from the Cypher?

## Limits

- Synchronous persistence and synchronous query enumeration are not supported. `ToQueryString()` is offline and may be called synchronously.

## Read more

[Package quickstart](../../../../docs/GETTING_STARTED.md) · [Query inspection](../../../../docs/features/QUERY_INSPECTION.md) · [Known limitations](../../../../docs/reference/KNOWN_LIMITATIONS.md)
