# 07 · Projections and aggregates

**Goal:** return DTOs instead of tracked entities, and compute integer aggregates on the server.
**Track:** full · **Mode:** offline + Neo4j · **Time:** ~10 min · **Builds on:** 05

## Concepts

- `Select` into a record or anonymous type returns only what you ask for, and nothing is tracked.
- A **dependent** entity such as `Movie` (it has an optional director, lesson 09) still proves its parent link before any row is shaped, even into a DTO.
- `MinAsync` and `MaxAsync` over integer properties run on the server, on ordinary node roots such as `Person`.

## Run it

```powershell
dotnet run --project workshop/GraphBridge.Workshop -- run 07
```

## Walkthrough

1. **People into a DTO.** Only `born` and `name` are returned:

<!-- region: People -->
```csharp
var people = db.People
    .Where(p => p.Born >= 1965)
    .OrderBy(p => p.Born).ThenBy(p => p.Name)
    .Select(p => new PersonCard(p.Name, p.Born));
```

2. **Movies into a DTO.** The same kind of query on a dependent:

<!-- region: Movies -->
```csharp
var movies = db.Movies
    .Where(m => m.Released >= 2003)
    .OrderBy(m => m.Released).ThenBy(m => m.Title)
    .Select(m => new MovieCard(m.Title, m.Released, m.Tagline));
```

   The Cypher grows a `CALL (n) { OPTIONAL MATCH (parent)-[:DIRECTED]->(n) ... }` block. It reads at most two candidate directors, and the query fails with an error if a link is duplicated or points at the wrong label. GraphBridge validates graph integrity on read instead of trusting it.

3. **Integer aggregates.**

<!-- region: MinMax -->
```csharp
var earliest = await db.People.MinAsync(p => p.Born, cancellationToken);
var latest = await db.People.MaxAsync(p => p.Born, cancellationToken);
```

## What you should see

Five people born in 1965 or later, four movies from 2003 on, an empty change tracker, and birth years 1956 and 1978.

## Try it yourself

1. Change the people DTO to an anonymous type `new { p.Name, Decade = p.Born / 10 }`. What happens, and why? (Computed expressions are not translated.)
2. Try `db.Movies.MinAsync(m => m.Released)` and read what the provider says. Compare it with the limit below.

## Limits

- `Sum`, `Average`, `GroupBy`, non-integer `Min`/`Max`, and aggregates over dependents are not translated. Use Fluent Cypher (lesson 17) or native Cypher (lesson 15).

## Read more

[DTO projections](../../../../docs/features/LINQ_QUERIES.md#dto-projections) · [Integer aggregates](../../../../docs/features/LINQ_QUERIES.md#integer-aggregates)
