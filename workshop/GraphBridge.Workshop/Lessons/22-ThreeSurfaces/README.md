# 22 · Capstone: one question, three query surfaces

**Goal:** answer "who acted in Cloud Atlas, in how many roles?" with LINQ, Fluent Cypher and native Cypher, and compare.
**Track:** ★ core · **Mode:** offline + Neo4j · **Time:** ~20 min · **Builds on:** 05, 16, 17

## Concepts

| Surface | Use it for | Semantics | Results |
|---|---|---|---|
| EF Core LINQ | Entity work: load, change, save | C# semantics; identity and integrity checks | Tracked entities |
| Fluent Cypher | Graph patterns, scopes, counts, paths | Native Cypher; C# lambdas only where they agree | Untracked scalars and DTOs |
| Native Cypher | Anything beyond the typed surfaces | Exactly what you write | Untracked, via your projector |

## Run it

```powershell
dotnet run --project workshop/GraphBridge.Workshop -- run 22
```

## Walkthrough

1. **LINQ.** `GroupBy` is not translated (lesson 06), so the lesson loads the bounded credit list, looks up the names with `Contains`, and counts in memory:

<!-- region: Linq -->
```csharp
var credits = await db.Credits.Where(c => c.MovieId == movieId).ToListAsync(cancellationToken);
Guid[] personIds = [.. credits.Select(c => c.PersonId).Distinct()];
var names = await db.People.Where(p => personIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Name, cancellationToken);
var linq = credits.GroupBy(c => c.PersonId)
    .Select(g => new RoleCount(names[g.Key], g.Count()))
    .OrderBy(r => r.Name, StringComparer.Ordinal).ToList();
```

   The Cypher includes the identified-relationship key check and C# null handling.

2. **Fluent Cypher.**

<!-- region: Fluent -->
```csharp
public static CypherResultQuery<RoleCount> FluentRoles(DbContext db, string title)
{
    var person = Graph.Node<Person>();
    var movie = Graph.Node<Movie>();
    var roles = Graph.CountRows();
    return db.Cypher()
        .Match(person.Out<Credit>(movie))
        .Where(() => movie.Value.Title == title)
        .With(person, roles)
        .Return(() => new RoleCount(person.Value.Name, roles.Value))
        .OrderBy(() => person.Value.Name)
        .Limit(100);
}
```

<!-- snapshot: 22-fluent -->
```cypher
MATCH (n0:`Person`)-[r2:`ACTED_IN`]->(n1:`Movie`)
WHERE n1.`title` = $p0
WITH n0, count(*) AS v3
RETURN n0.`name` AS c0, v3 AS c1
ORDER BY c0 ASC
LIMIT $p1
```

3. **Native Cypher.** The statement:

<!-- region: NativeText -->
```csharp
public const string NativeRoles = """
    MATCH (p:Person)-[r:ACTED_IN]->(:Movie {title: $title})
    RETURN p.name AS name, count(r) AS roles
    ORDER BY name
    """;
```

   runs through `db.Database.QueryCypherAsync` with parameters, an explicit projector and a row cap:

<!-- region: Native -->
```csharp
var native = await db.Database.QueryCypherAsync(NativeRoles,
    new Dictionary<string, object?> { ["title"] = Title },
    record => new RoleCount(record["name"].As<string>(), record["roles"].As<long>()),
    maxRows: 100, cancellationToken: cancellationToken);
```

4. **Compare.** The lesson prints all three tables. Each returns `Hugo Weaving 2` and `Tom Hanks 2`. Only LINQ tracked anything: four credits and two people.

## What you should see

Three identical tables (LINQ, Fluent, native), and a change tracker holding six entries from the LINQ path.

## Try it yourself

1. Change the question to "how many distinct movies did each actor make?" in each surface. Which one makes that easiest?
2. Which surface would you use to rename a role? (Hint: only one of them writes through EF's change tracking.)

## Limits

- GraphBridge does not inspect native Cypher beyond its row limit; keep values in parameters and review it like any SQL you write by hand.

## Read more

[Fluent Cypher guide](../../../../docs/features/FLUENT_CYPHER.md) · [Native Cypher DTOs](../../../../docs/features/NATIVE_CYPHER.md) · [Query inspection](../../../../docs/features/QUERY_INSPECTION.md)
