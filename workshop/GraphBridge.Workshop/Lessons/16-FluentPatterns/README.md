# 16 · Fluent Cypher I: patterns

**Goal:** write a graph pattern in C#: typed node and edge symbols, direction, and a C# predicate.
**Track:** ★ core · **Mode:** offline + Neo4j · **Time:** ~15 min · **Builds on:** 05, 10

## Concepts

- `db.Cypher()` is a typed builder for **native Cypher clauses**. It uses the same EF mappings for labels, relationship types and property names, and returns untracked rows: scalars, DTOs or whole entities.
- **Symbols** stand for pattern variables: `Graph.Node<Person>()`, `Graph.Edge<Credit>()`. Two `Person` symbols are two different variables.
- C# predicates such as `actor.Value.Name == actorName` are accepted only where they mean the same in Cypher as in C# (lesson 17 shows a refusal).
- Predicates come in two forms that compile to the same Cypher: C# lambdas (`() => actor.Value.Name == actorName`, the C# meaning) and explicit methods (`actor.Property(p => p.Name).Eq(actorName)`, the native meaning). See [choosing a level](../../../../docs/features/FLUENT_CYPHER.md#choosing-a-level).

## Run it

```powershell
dotnet run --project workshop/GraphBridge.Workshop -- run 16
```

## Walkthrough

1. **Who acted with Keanu Reeves?**

<!-- region: Coactors -->
```csharp
public static CypherResultQuery<string> Coactors(DbContext db, string actorName)
{
    var actor = Graph.Node<Person>();
    var movie = Graph.Node<Movie>();
    var coactor = Graph.Node<Person>();
    return db.Cypher()
        .Match(actor.Out<Credit>(movie).In<Credit>(coactor))
        .Where(() => actor.Value.Name == actorName && coactor.Value.Id != actor.Value.Id)
        .ReturnDistinct(() => coactor.Value.Name)
        .OrderBy(() => coactor.Value.Name)
        .Limit(25);
}
```

<!-- snapshot: 16-coactors -->
```cypher
MATCH (n0:`Person`)-[r2:`ACTED_IN`]->(n1:`Movie`)<-[r4:`ACTED_IN`]-(n3:`Person`)
WHERE n0.`name` = $p0 AND n3.`id` <> n0.`id`
RETURN DISTINCT n3.`name` AS c0
ORDER BY c0 ASC
LIMIT $p1
```

   Compare it with lesson 05: no `coalesce`, no hidden integrity columns. This is the pattern you would write by hand. `actor.Out<Credit>(movie)` creates an edge symbol you cannot refer to in C#; the compiler still gives it a Cypher variable (`r2`). Use `Graph.Edge<Credit>()` when you need the edge's properties.

2. **The explicit form.** Predicate methods state native semantics directly and compile to the identical Cypher:

<!-- region: Explicit -->
```csharp
public static CypherResultQuery<string> CoactorsExplicit(DbContext db, string actorName)
{
    var actor = Graph.Node<Person>();
    var movie = Graph.Node<Movie>();
    var coactor = Graph.Node<Person>();
    return db.Cypher()
        .Match(actor.Out(Graph.Edge<Credit>(), movie).In(Graph.Edge<Credit>(), coactor))
        .Where(actor.Property(p => p.Name).Eq(actorName)
            .And(coactor.Property(p => p.Id).Ne(actor.Property(p => p.Id))))
        .ReturnDistinct(() => coactor.Value.Name)
        .OrderBy(coactor.Property(p => p.Name))
        .Limit(25);
}
```

3. **Lists and text.** C# collection and string methods translate where they mean the same as Cypher:

<!-- region: ListsAndText -->
```csharp
/// <summary>Co-actors of any listed actor, except the listed actors and names starting with a prefix.</summary>
public static CypherResultQuery<string> CoactorsOfAny(DbContext db, string[] actorNames, string skipPrefix)
{
    var actor = Graph.Node<Person>();
    var movie = Graph.Node<Movie>();
    var coactor = Graph.Node<Person>();
    return db.Cypher()
        .Match(actor.Out<Credit>(movie).In<Credit>(coactor))
        .Where(() => actorNames.Contains(actor.Value.Name)
            && !actorNames.Contains(coactor.Value.Name)
            && !coactor.Value.Name.StartsWith(skipPrefix, StringComparison.Ordinal))
        .ReturnDistinct(() => coactor.Value.Name)
        .OrderBy(() => coactor.Value.Name)
        .Limit(25);
}
```

<!-- snapshot: 16-lists-and-text -->
```cypher
MATCH (n0:`Person`)-[r2:`ACTED_IN`]->(n1:`Movie`)<-[r4:`ACTED_IN`]-(n3:`Person`)
WHERE n0.`name` IN $p0 AND NOT (n3.`name` IN $p1) AND NOT (n3.`name` STARTS WITH $p2)
RETURN DISTINCT n3.`name` AS c0
ORDER BY c0 ASC
LIMIT $p3
```

   - `actorNames.Contains(actor.Value.Name)` becomes `IN` with one list parameter. The array is copied when `Where` runs.
   - `!` is accepted because a person's name is required: the operand can never be null, so C# negation and native `NOT` agree. On a nullable property it is refused with `GBFC104`.
   - `StartsWith` needs `StringComparison.Ordinal`. The plain overload compares using the current culture in C#, so it is refused with `GBFC106`; the lesson shows the message.

4. **Relationship uniqueness.** Within one MATCH, a physical relationship is used at most once:

<!-- region: SameMovieTwice -->
```csharp
/// <summary>One MATCH never uses the same physical relationship twice, so this finds people with two credits in one movie.</summary>
public static CypherResultQuery<string> TwoRolesInOneMovie(DbContext db)
{
    var person = Graph.Node<Person>();
    var movie = Graph.Node<Movie>();
    return db.Cypher()
        .Match(person.Out<Credit>(movie).In<Credit>(person))
        .ReturnDistinct(() => person.Value.Name)
        .OrderBy(() => person.Value.Name)
        .Limit(25);
}
```

   A person can only match themselves through **two different** credits in the same movie. That is exactly Hugo Weaving and Tom Hanks, who each play two roles in Cloud Atlas.

5. **Whole entities.** Return the symbol itself to get the mapped entity:

<!-- region: Entities -->
```csharp
/// <summary>Whole entities: the movies themselves, untracked, instead of scalar columns.</summary>
public static CypherResultQuery<Movie> MoviesOf(DbContext db, string actorName)
{
    var actor = Graph.Node<Person>();
    var movie = Graph.Node<Movie>();
    return db.Cypher()
        .Match(actor.Out<Credit>(movie))
        .Where(() => actor.Value.Name == actorName)
        .ReturnDistinct(() => movie.Value)
        .OrderBy(() => movie.Value.Released)
        .ThenBy(() => movie.Value.Title)
        .Limit(25);
}
```

<!-- snapshot: 16-entities -->
```cypher
MATCH (n0:`Person`)-[r2:`ACTED_IN`]->(n1:`Movie`)
WHERE n0.`name` = $p0
RETURN DISTINCT n1 {.`id`, .`released`, .`tagline`, .`title`, __gb_reference: COLLECT { MATCH (__gb_parent)-[:`DIRECTED`]->(n1) RETURN __gb_parent {.`id`, __gb_principal: (__gb_parent:`Person`)} LIMIT 2 }} AS c0
ORDER BY c0.`released` ASC, c0.`title` ASC
LIMIT $p1
```

   - The query returns a map of the movie's **mapped properties only**, never the whole node, and EF builds each `Movie` from it.
   - `DirectorId` isn't stored on the movie: lesson 08 maps it to the incoming `DIRECTED` relationship. The `COLLECT` subquery reads the director's key the way LINQ does: one valid director, or none for an optional reference.
   - The movies are **untracked**: nothing is attached to the context. Ordering by `movie.Value.Released` orders by the returned map, which also works after `ReturnDistinct`.

## What you should see

Carrie-Anne Moss, Hugo Weaving and Laurence Fishburne; Carrie-Anne Moss and Hugo Weaving for the list query; Hugo Weaving and Tom Hanks for two roles; the three Matrix movies and John Wick as `Movie` entities, with the Wachowskis as directors and none for John Wick; and an empty change tracker.

## Try it yourself

1. Split the pattern in `TwoRolesInOneMovie` into two `Match` calls. Why does every actor now match?
2. Replace `ReturnDistinct` with `Return` in `Coactors`. Why do names repeat?
3. In `CoactorsOfAny`, use `coactor.Value.Name.Contains("o")` instead of the `StartsWith` test. Which C# overload did you call, and why is it accepted without `StringComparison`?

## Limits

- Read-only; entities come back untracked. To change and save them, return keys and load tracked entities with EF ([guide](../../../../docs/features/FLUENT_CYPHER.md#from-fluent-results-to-tracked-entities)).
- A `LIMIT` between 1 and 10,000 is required to execute.

## Read more

[Fluent Cypher guide](../../../../docs/features/FLUENT_CYPHER.md) · [Predicates as C# lambdas](../../../../docs/features/FLUENT_CYPHER.md#predicates-as-c-lambdas) · [Returning whole entities](../../../../docs/features/FLUENT_CYPHER.md#returning-whole-entities)
