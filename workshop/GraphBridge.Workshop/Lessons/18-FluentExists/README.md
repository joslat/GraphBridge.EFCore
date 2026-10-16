# 18 · Fluent Cypher III: existence

**Goal:** ask whether a pattern exists, or does not, without adding rows to the result.
**Track:** full · **Mode:** offline + Neo4j · **Time:** ~10 min · **Builds on:** 17

## Concepts

- `Graph.Exists(pattern)` becomes a correlated `EXISTS { MATCH ... }`; `.Not()` negates it.
- Outer symbols used inside EXISTS are the same variables (correlation). Symbols introduced inside stay local.
- Lambda and explicit predicates combine: repeated `Where` calls are joined with AND.

## Run it

```powershell
dotnet run --project workshop/GraphBridge.Workshop -- run 18
```

## Walkthrough

1. **Likes without acting.**

<!-- region: LikedNotActed -->
```csharp
/// <summary>People who like a movie they did not act in.</summary>
public static CypherResultQuery<string> LikedButNotActedIn(DbContext db)
{
    var person = Graph.Node<Person>();
    var movie = Graph.Node<Movie>();
    return db.Cypher()
        .Match(person.Out<Like>(movie))
        .Where(Graph.Exists(person.Out<Credit>(movie)).Not())
        .ReturnDistinct(() => person.Value.Name)
        .OrderBy(() => person.Value.Name)
        .Limit(25);
}
```

<!-- snapshot: 18-liked-not-acted -->
```cypher
MATCH (n0:`Person`)-[r2:`LIKES`]->(n1:`Movie`)
WHERE NOT EXISTS { MATCH (n0:`Person`)-[r3:`ACTED_IN`]->(n1:`Movie`) }
RETURN DISTINCT n0.`name` AS c0
ORDER BY c0 ASC
LIMIT $p0
```

2. **The same check inside a lambda.** `Graph.Is(...)` embeds an explicit predicate in a C# lambda, so EXISTS can be combined with `!`, `&&` and `||`:

<!-- region: LambdaExists -->
```csharp
public static CypherResultQuery<string> LikedButNotActedInLambda(DbContext db)
{
    var person = Graph.Node<Person>();
    var movie = Graph.Node<Movie>();
    return db.Cypher()
        .Match(person.Out<Like>(movie))
        .Where(() => !Graph.Is(Graph.Exists(person.Out<Credit>(movie))))
        .ReturnDistinct(() => person.Value.Name)
        .OrderBy(() => person.Value.Name)
        .Limit(25);
}
```

   It compiles to the identical Cypher. `!` is accepted because EXISTS is always true or false, never null. `Graph.Is` is only translated; calling it directly throws.

3. **Follow suggestions:** two hops away, excluding self and people already followed.

<!-- region: Suggestions -->
```csharp
/// <summary>Follow suggestions: people two FOLLOWS hops away whom the person does not already follow.</summary>
public static CypherResultQuery<string> FollowSuggestions(DbContext db, string name)
{
    var person = Graph.Node<Person>();
    var friend = Graph.Node<Person>();
    var candidate = Graph.Node<Person>();
    return db.Cypher()
        .Match(person.Out<Follow>(friend).Out<Follow>(candidate))
        .Where(() => person.Value.Name == name && candidate.Value.Id != person.Value.Id)
        .Where(Graph.Exists(person.Out<Follow>(candidate)).Not())
        .ReturnDistinct(() => candidate.Value.Name)
        .OrderBy(() => candidate.Value.Name)
        .Limit(25);
}
```

## What you should see

Emil Eifrem, Keanu Reeves, Laurence Fishburne and Tom Hanks; then Carrie-Anne Moss as Emil's suggestion.

## Try it yourself

1. Remove `.Not()` from `LikedButNotActedIn`. Who remains?
2. Suggest people for Lana Wachowski. Why is the list empty?

## Limits

- The inner predicate of `Graph.Exists(pattern, ...)` can be an explicit predicate or a lambda: `Graph.Exists(person.Out<Credit>(movie), () => movie.Value.Released > 2000)`. Symbols created inside EXISTS stay inside it.

## Read more

[Fluent Cypher guide](../../../../docs/features/FLUENT_CYPHER.md)
