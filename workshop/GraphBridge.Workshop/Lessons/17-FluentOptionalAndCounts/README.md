# 17 · Fluent Cypher II: optional matches and counts

**Goal:** keep rows without a match with OPTIONAL MATCH, and count what you mean: rows or distinct things.
**Track:** ★ core · **Mode:** offline + Neo4j · **Time:** ~15 min · **Builds on:** 16

## Concepts

- A predicate that belongs to an **OPTIONAL MATCH** keeps rows that find no match, with nulls. The same predicate after **WITH** filters those rows out.
- `count(*)` counts rows, which are physical matches; `count(DISTINCT movie)` counts things. Parallel relationships make them differ.
- A C# predicate that would mean something different in Cypher is refused with a code, never silently translated.

## Run it

```powershell
dotnet run --project workshop/GraphBridge.Workshop -- run 17
```

## Walkthrough

1. **Everyone, with any movie after 2010.**

<!-- region: Optional -->
```csharp
/// <summary>Everyone, with any movie after 2010 they acted in. The predicate belongs to the OPTIONAL MATCH.</summary>
public static CypherResultQuery<CastingRow> RecentWork(DbContext db)
{
    var person = Graph.Node<Person>();
    var movie = Graph.Node<Movie>();
    return db.Cypher()
        .Match(person)
        .OptionalMatch(person.Out<Credit>(movie), where: () => movie.Value.Released > 2010)
        .ReturnDistinct(() => new CastingRow(person.Value.Name, movie.Value.Title))
        .OrderBy(() => person.Value.Name)
        .Limit(100);
}
```

   The `where:` argument attaches the predicate to the OPTIONAL MATCH itself. All nine people are returned; those without a recent movie get a null title.

2. **The same predicate after WITH.**

<!-- region: Filtered -->
```csharp
/// <summary>The same predicate after WITH filters rows instead: people without a recent movie disappear.</summary>
public static CypherResultQuery<CastingRow> RecentWorkOnly(DbContext db)
{
    var person = Graph.Node<Person>();
    var movie = Graph.Node<Movie>();
    return db.Cypher()
        .Match(person)
        .OptionalMatch(person.Out<Credit>(movie))
        .With(person, movie)
        .Where(() => movie.Value.Released > 2010)
        .ReturnDistinct(() => new CastingRow(person.Value.Name, movie.Value.Title))
        .OrderBy(() => person.Value.Name)
        .Limit(100);
}
```

   Now only Hugo Weaving, Keanu Reeves and Tom Hanks remain. One clause boundary, a different question.

3. **Rows versus distinct movies.**

<!-- region: Counts -->
```csharp
/// <summary>Distinct movies versus physical ACTED_IN rows per actor.</summary>
public static CypherResultQuery<CreditCount> CreditCounts(DbContext db)
{
    var person = Graph.Node<Person>();
    var movie = Graph.Node<Movie>();
    var movies = Graph.CountDistinct(movie);
    var credits = Graph.CountRows();
    return db.Cypher()
        .Match(person.Out<Credit>(movie))
        .With(person, movies, credits)
        .Return(() => new CreditCount(person.Value.Name, movies.Value, credits.Value))
        .OrderBy(() => person.Value.Name)
        .Limit(100);
}
```

   Hugo Weaving: two distinct movies but three credits, because of his two Cloud Atlas roles.

4. **Other aggregates.** `Min`, `Max`, `Sum`, `Avg` and `Collect` are introduced by WITH like counts:

<!-- region: Aggregates -->
```csharp
/// <summary>First and last release year and the titles per person, including people without credits.</summary>
public static CypherResultQuery<Career> Careers(DbContext db)
{
    var person = Graph.Node<Person>();
    var movie = Graph.Node<Movie>();
    var first = Graph.Min(movie.Property(m => m.Released));
    var last = Graph.Max(movie.Property(m => m.Released));
    var titles = Graph.CollectDistinct(movie.Property(m => m.Title), max: 10);
    return db.Cypher()
        .Match(person)
        .OptionalMatch(person.Out<Credit>(movie))
        .With(person, first, last, titles)
        .Return(() => new Career(person.Value.Name, (int?)first.Value, (int?)last.Value, titles.Value))
        .OrderBy(() => person.Value.Name)
        .Limit(100);
}
```

<!-- snapshot: 17-aggregates -->
```cypher
MATCH (n0:`Person`)
OPTIONAL MATCH (n0:`Person`)-[r2:`ACTED_IN`]->(n1:`Movie`)
WITH n0, min(n1.`released`) AS v3, max(n1.`released`) AS v4, collect(DISTINCT n1.`title`)[..$p0] AS v5
RETURN n0.`name` AS c0, v3 AS c1, v4 AS c2, v5 AS c3
ORDER BY c0 ASC
LIMIT $p1
```

   People without credits keep one row: native `min` and `max` of an all-null group are null (hence `(int?)`), and `collect` skips nulls, so their list is empty. `collect` requires a maximum, applied on the server, and does not guarantee order.

5. **A refused predicate.**

<!-- region: Refused -->
```csharp
var person = Graph.Node<Person>();
var movie = Graph.Node<Movie>();
var afterOptional = db.Cypher().Match(person).OptionalMatch(person.Out<Credit>(movie)).With(person, movie);
lesson.ExpectFailure<NotSupportedException>("'!=' on a symbol that OPTIONAL MATCH may leave null is refused (GBFC110)",
    () => afterOptional.Where(() => movie.Value.Title != "The Matrix").Return(() => person.Value.Name).ToQueryString());
```

   After the OPTIONAL MATCH, `movie` may be null. In C#, a missing movie "is not The Matrix", so the row stays. In Cypher, `null <> 'The Matrix'` is unknown, so the row is dropped. GraphBridge refuses with `GBFC110` and names the explicit predicate for each meaning.

## What you should see

Nine people with null titles where appropriate; three people after the filter; `(Hugo Weaving, 2, 3)` and `(Tom Hanks, 1, 2)`; a career table where Keanu spans 1999 to 2014 with four titles and people without credits show nulls and no titles; one refusal.

## Try it yourself

1. Copy `RecentWorkOnly`, replace its `Where` with `.Where(movie.Property(m => m.Title).IsNull().Or(movie.Property(m => m.Title).Ne("The Matrix")))`, and call it from the lesson with `ToListAsync`. Who is kept?
2. In `RecentWork`, replace `ReturnDistinct` with `Return`. Who appears twice, and why?

## Limits

- Fluent Cypher is read-only, and every executed query needs `Limit(1..10000)`. `Collect` needs a max (1 to 10,000) and doesn't guarantee order. Predicates whose C# and Cypher meanings differ are refused (`GBFC110`); state the meaning with `.Ne`, `.IsNull` or `.IsNotNull`.

## Read more

[Fluent Cypher guide: optional matches and scope](../../../../docs/features/FLUENT_CYPHER.md#optional-matches-and-scope) · [Error codes](../../../../docs/features/FLUENT_CYPHER.md#predicates-as-c-lambdas)
