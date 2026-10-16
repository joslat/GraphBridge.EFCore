# Native Cypher

`db.Database.QueryCypherAsync` runs Cypher you write, with parameters and a row projector you write, through the context's driver, database and transaction. Use it for anything [LINQ](LINQ_QUERIES.md) and [Fluent Cypher](FLUENT_CYPHER.md) do not express, such as aggregates over dependents, `avg()` or procedure calls.

```csharp
using Microsoft.EntityFrameworkCore;
using Neo4j.Driver;

public sealed record MovieStats(string Title, long Reviews, double? AverageRating);

const string ReviewStats = """
    MATCH (m:Movie)
    WHERE m.released >= $from
    OPTIONAL MATCH (m)-[:HAS_REVIEW]->(r:Review)
    RETURN m.title AS title, count(r) AS reviews, avg(r.rating) AS average
    ORDER BY reviews DESC, title
    """;

var rows = await db.Database.QueryCypherAsync(ReviewStats,
    new Dictionary<string, object?> { ["from"] = 1999L },
    record => new MovieStats(record["title"].As<string>(), record["reviews"].As<long>(),
        record["average"] is null ? null : record["average"].As<double>()),
    maxRows: 50, cancellationToken: cancellationToken);
```

`rows` is an `IReadOnlyList<MovieStats>`: one buffered, untracked object per returned record. A movie without reviews has a null average.

## Parameters

```csharp
Task<IReadOnlyList<T>> QueryCypherAsync<T>(this DatabaseFacade database,
    string cypher, IReadOnlyDictionary<string, object?> parameters, Func<IRecord, T> project,
    int maxRows = 10_000, CancellationToken cancellationToken = default)
```

| Parameter | Meaning |
|---|---|
| `cypher` | The statement. GraphBridge does not parse, validate or rewrite it; you own its labels, property names and cost. |
| `parameters` | Values for `$name` placeholders. The dictionary is copied before execution; do not mutate nested values while the call runs. |
| `project` | Runs once per record with the driver's `IRecord` API, for example `record["name"].As<string>()`. It can return DTOs, scalars or driver values such as `INode`, `IRelationship` and `IPath`. |
| `maxRows` | Buffer cap, 10,000 by default. One more row throws `InvalidOperationException`; no partial result is returned. |
| `cancellationToken` | Checked between awaited driver operations and between rows. An in-flight driver call is not abandoned. |

**Always pass values as parameters**, never by concatenating them into the text. Parameters use the **stored representation**: EF value converters are not applied. Use stored property names (`born`, not `Born`), and pass a Guid key as `id.ToString("D")`, the canonical string GraphBridge stores. `ToQueryString()` on an equivalent LINQ query shows the stored names.

## Transactions

The helper shares the context's transaction handling with `SaveChangesAsync`:

- **No explicit transaction:** the helper opens its own transaction and commits after every row has been projected. A failure in the query, the projector, the row cap or cancellation rolls it back.
- **Inside `BeginTransactionAsync`:** the helper borrows that transaction and never commits, rolls back or disposes it. It sees writes that EF has saved but not yet committed:

```csharp
await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
db.People.Add(new Person { Id = id, Name = "Draft Person" });
await db.SaveChangesAsync(cancellationToken);
// Borrows the same transaction, so it sees the uncommitted node.
var inside = await db.Database.QueryCypherAsync("MATCH (p:Person {id: $id}) RETURN count(p) AS value",
    new Dictionary<string, object?> { ["id"] = id.ToString("D") }, r => r["value"].As<long>(), cancellationToken: cancellationToken);
await transaction.RollbackAsync(cancellationToken);
```

A failure inside a borrowed transaction leaves the decision to you; native writes made before the failure stay pending in it. A lost commit acknowledgement does not prove rollback, and nothing is retried automatically: see [transactions and concurrency](TRANSACTIONS_AND_CONCURRENCY.md).

## Rules and limits

- **Untracked.** Results are never attached to EF, even when the projector creates entity types. Native writes do not refresh entities already tracked; read them again in a fresh context.
- **Buffered and bounded.** `maxRows` bounds the rows buffered on the client, not server work or row size. Put a `LIMIT` in the statement as well.
- **One operation at a time.** Await the call before using the context again. Querying the same context from inside a projector throws `InvalidOperationException`.
- **Not logged.** The helper logs neither statement text nor parameter values. [Command tracing](COMMAND_TRACING.md) records a `GraphBridge.NativeQuery` span with kind `native` and no statement fingerprint.
- **Not inspected.** GraphBridge enforces only the row cap. Review native Cypher as you would hand-written SQL.

## Graph values and paths

A projector can read nodes, relationships and paths with the driver's types:

```csharp
var routes = await db.Database.QueryCypherAsync(
    "MATCH path = (:Person {name: $name})-[:FOLLOWS*1..3]->(:Person) RETURN path LIMIT 25",
    new Dictionary<string, object?> { ["name"] = "Keanu Reeves" },
    record => record["path"].As<IPath>().Nodes.Select(n => n["name"].As<string>()).ToList(),
    maxRows: 25, cancellationToken: cancellationToken);
```

For traversal with an exact anchor, mandatory bounds and a `HasMorePaths` flag, use the typed [`QueryPathsAsync`](BOUNDED_PATHS.md) instead. For vector and full-text indexes, use the typed [search APIs](GRAPH_SEARCH.md). Both run on this same native pipeline and follow the transaction rules above. To see a statement's server plan without running it, use [`ExplainCypherAsync`](QUERY_INSPECTION.md#real-server-plan-helper).

## Choosing LINQ, Fluent Cypher or native Cypher

| Surface | Use it for | Semantics | Results |
|---|---|---|---|
| [EF Core LINQ](LINQ_QUERIES.md) | Entities you load, change and save | C# semantics, with identity and integrity checks | Tracked entities or DTOs |
| [Fluent Cypher](FLUENT_CYPHER.md) | Graph patterns, optional matches, counts, aggregates and paths | Native Cypher; C# lambdas only where they agree | Untracked scalars, DTOs and paths |
| Native Cypher | Anything beyond the typed surfaces | Exactly the text you write | Untracked, through your projector |

Start at the highest level that can express the question; [choosing a level](FLUENT_CYPHER.md#choosing-a-level) explains the trade-offs. Fluent Cypher compiles to parameterized text and runs through this helper, so it shares the same transaction and cancellation behavior, with its required `Limit` as the row cap.

## Try it

- Workshop lesson [13 · Transactions](../../workshop/GraphBridge.Workshop/Lessons/13-Transactions/README.md): a native query inside an EF transaction.
- Workshop lesson [15 · Native Cypher with a projector](../../workshop/GraphBridge.Workshop/Lessons/15-NativeCypher/README.md).
- Workshop lesson [22 · One question, three query surfaces](../../workshop/GraphBridge.Workshop/Lessons/22-ThreeSurfaces/README.md).
- Sample [03 Movies — database-first](../../samples/03-Movies-DatabaseFirst/README.md): native DTO reads of unmapped relationship properties, inside a rolled-back transaction.

Evidence: [capability and evidence map](../../tests/verification/CAPABILITY_EVIDENCE.md). Design: [ADR-0016](../architecture/adr/ADR-0016.md).
