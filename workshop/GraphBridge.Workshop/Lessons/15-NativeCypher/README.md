# 15 · Native Cypher with a projector

**Goal:** run reviewed, parameterized Cypher through the same context, transaction and driver, with an explicit row cap.
**Track:** full · **Mode:** needs Neo4j · **Time:** ~10 min · **Builds on:** 07, 13

## Concepts

- `db.Database.QueryCypherAsync(cypher, parameters, projector, maxRows)` is the escape hatch for anything the typed surfaces do not express.
- It uses the context's driver and borrows an active EF transaction (lesson 13); otherwise it runs in its own transaction.
- Results are untracked and buffered up to `maxRows`. You write the projector with the driver's record API.

## Run it

```powershell
dotnet run --project workshop/GraphBridge.Workshop -- run 15
```

## Walkthrough

1. **The statement.** Reviews per movie, with an average. LINQ cannot express it: it aggregates over dependents and uses `OPTIONAL MATCH` and `avg()`.

<!-- region: Statement -->
```csharp
public const string ReviewStats = """
    MATCH (m:Movie)
    WHERE m.released >= $from
    OPTIONAL MATCH (m)-[:HAS_REVIEW]->(r:Review)
    RETURN m.title AS title, count(r) AS reviews, avg(r.rating) AS average
    ORDER BY reviews DESC, title
    """;
```

2. **Run it.** Values are passed as parameters, never concatenated into the text:

<!-- region: Run -->
```csharp
var rows = await db.Database.QueryCypherAsync(ReviewStats,
    new Dictionary<string, object?> { ["from"] = 1999L },
    record => new MovieStats(record["title"].As<string>(), record["reviews"].As<long>(),
        record["average"] is null ? null : record["average"].As<double>()),
    maxRows: 50, cancellationToken: cancellationToken);
```

## What you should see

Five movies; The Matrix first with two reviews averaging 4.5; John Wick with a null average; an empty change tracker.

## Try it yourself

1. Change `$from` to `2010`. How many rows remain?
2. Return the reviews themselves with `collect(r.text) AS texts` and read them with `record["texts"].As<List<string>>()`.

## Limits

- GraphBridge does not inspect native Cypher; it only enforces the row cap. Keeping values in parameters, and the Cypher's correctness and server cost, are yours.
- Native writes do not refresh entities already tracked by EF; use a fresh context afterwards.

## Read more

[Native Cypher](../../../../docs/features/NATIVE_CYPHER.md) · [Query inspection](../../../../docs/features/QUERY_INSPECTION.md)
