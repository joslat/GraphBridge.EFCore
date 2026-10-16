# 05 · LINQ queries and visible Cypher

**Goal:** filter, order, page and count with LINQ, and read the Cypher each query becomes.
**Track:** ★ core · **Mode:** offline + Neo4j · **Time:** ~15 min · **Builds on:** 04

## Concepts

- Supported LINQ runs **on the server** as Cypher. `ToQueryString()` shows exactly what will run.
- Every value is a parameter, so the query text is the same whatever the values are.
- LINQ keeps **C# semantics**: comparisons with null behave as they do in C#. That is what the `coalesce(...)` and `IS NULL` parts of the Cypher are for.

## Run it

```powershell
dotnet run --project workshop/GraphBridge.Workshop -- run 05
```

Offline you see the Cypher; with a database the queries run against the seeded movie graph.

## Walkthrough

1. **Filter, order, select a column.**

<!-- region: Born -->
```csharp
var bornBefore = 1965;
var veterans = db.People
    .Where(p => p.Born < bornBefore)
    .OrderBy(p => p.Born)
    .Select(p => p.Name);
```

<!-- snapshot: 05-born-before -->
```cypher
MATCH (n:`Person`)
WHERE coalesce(n.`born` < $p0, false)
RETURN n.`name` AS `c2`
ORDER BY n.`born` IS NULL DESC, n.`born` ASC
```

   `ORDER BY n.born IS NULL DESC` puts nulls first, as LINQ to Objects does.

2. **Count, Any, First.**

<!-- region: Aggregates -->
```csharp
var nicknamed = db.People.Where(p => p.Nickname != null);
var bornLate = db.People.Where(p => p.Born > 1975);
var youngest = db.People.OrderByDescending(p => p.Born).Select(p => p.Name);
```

   The printed Cypher is the filter. `CountAsync`, `AnyAsync` and `FirstAsync` change its `RETURN` on the server into a count, an existence check or `LIMIT 1`. Nothing is loaded and counted in memory.

3. **Membership in a local list.**

<!-- region: Contains -->
```csharp
string[] names = ["Keanu Reeves", "Tom Hanks", "Nobody"];
var cast = db.People.Where(p => names.Contains(p.Name)).OrderBy(p => p.Name).Select(p => p.Name);
```

   The whole array becomes one list parameter, `$p0`, of at most 1,024 values.

## What you should see

Offline: three Cypher statements. Connected: the four oldest actors in birth order, a count of 2, `true`, `Emil Eifrem` and two matching names.

## Try it yourself

1. Add `.Skip(1).Take(2)` to the first query. Where do `SKIP` and `LIMIT` appear?
2. Change `p.Nickname != null` to `p.Nickname == "Larry"` and compare the Cypher.

## Limits

- No joins, `GroupBy`, `Distinct`, set operators, `Sum` or `Average` in LINQ (lesson 06).
- Aggregates run on ordinary node roots like `Person`, not on dependents like `Movie` (lesson 07).

## Read more

[Query inspection](../../../../docs/features/QUERY_INSPECTION.md) · [Contains queries](../../../../docs/features/LINQ_QUERIES.md) · [Null and type semantics](../../../../docs/features/NULLS_TYPES_AND_TIME.md)
