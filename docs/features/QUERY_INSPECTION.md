# Inspect Cypher and plans

`ToQueryString()` shows the Cypher a LINQ query becomes, without running it. `ExplainCypherAsync` asks Neo4j for the real plan of a statement, without executing it. Use them to review generated Cypher in code review and to investigate performance.

```csharp
var name = "Keanu Reeves";
var query = db.People.Where(p => p.Name == name).OrderBy(p => p.Name).Take(5);
Console.WriteLine(query.ToQueryString());   // offline; parameter placeholders only
var people = await query.ToListAsync(cancellationToken);
```

```cypher
MATCH (n:`Person`)
WHERE coalesce(n.`name` = $p0, (n.`name` IS NULL AND $p0 IS NULL))
RETURN n.`id` AS `c0`, n.`born` AS `c1`, n.`name` AS `c2`, n.`nickname` AS `c3`, n.`version` AS `c4`
ORDER BY n.`name` IS NULL DESC, n.`name` ASC
LIMIT $p1
```

## What `ToQueryString()` shows

GraphBridge implements EF Core's own `ToQueryString()`; there is no separate GraphBridge method. It translates the query without opening a connection and returns the statement text with `$p0`-style placeholders. Captured and literal values never appear in it, so the text is the same whatever the values are, and EF reuses the compiled query when they change. The `coalesce(...)` and `IS NULL` parts reproduce C# null semantics; see [LINQ queries](LINQ_QUERIES.md#null-comparisons).

- The text describes this query shape. Adding an operator can change the translation, so inspect the query you actually run.
- A refused shape throws `Neo4jQueryTranslationException` from `ToQueryString()` just as it would on execution, which makes it a cheap offline check in tests.
- Placeholders are not values. To try a statement in a Cypher editor, supply synthetic parameter values separately; never rebuild a query by interpolating application values into the text.
- Fluent Cypher queries have their own `ToQueryString()`, `Inspect()` and `Prepare()`; see [Fluent Cypher](FLUENT_CYPHER.md#supported-clauses-and-values). Native Cypher is already the text you wrote.
- EF Core's command log (category `Microsoft.EntityFrameworkCore.Database.Command`) is a separate channel that can include generated Cypher; treat it as sensitive. [Command tracing](COMMAND_TRACING.md) never carries statement text or values.

## Real server plan helper

```csharp
var explanation = await db.Database.ExplainCypherAsync(
    "MATCH (p:Person) WHERE p.born > $born RETURN p.name",
    new Dictionary<string, object?> { ["born"] = 1960 },
    cancellationToken: cancellationToken);
Console.WriteLine(explanation.StatementFingerprint);
Console.WriteLine(explanation.Plan.OperatorType);
foreach (var child in explanation.Plan.Children)
    Console.WriteLine(child.OperatorType);
```

The helper prefixes the statement with `EXPLAIN` and returns a `GraphQueryExplanation`: the driver's real `IPlan` with child operators, arguments and identifiers, plus `StatementFingerprint`, `ResultAvailableAfter` and `ResultConsumedAfter`. It verifies that the server returned no rows and no update or profile evidence.

To plan a LINQ query, pass its generated text with explicit **store** values for its placeholders:

```csharp
var statement = db.People.Where(p => p.Id == id).ToQueryString();
var plan = await db.Database.ExplainCypherAsync(statement,
    new Dictionary<string, object?> { ["p0"] = id.ToString("D") }, cancellationToken: cancellationToken);
```

The helper does not extract LINQ closure values or apply EF converters for you. Here `p0` and the canonical Guid string match this query's shape; read the placeholders of your own query rather than assuming those names or conversions.

| Rule | Behavior |
|---|---|
| Accepted statements | Must start with `MATCH`, `OPTIONAL`, `WITH`, `UNWIND`, `RETURN`, `CALL`, `CREATE` or `MERGE`. Leading comments and `PROFILE`, `EXPLAIN` or `CYPHER` preambles throw `ArgumentException` before transport. |
| Transactions | Without an EF transaction, the helper uses its own read transaction and always rolls it back. An existing EF transaction is borrowed unchanged. |
| `maxPlanNodes` | Default 1,024 (1 to 100,000). A larger plan throws `InvalidOperationException` with no partial result. The cap is checked after the driver has read the summary; it does not bound planning work or argument size. |
| `StatementFingerprint` | SHA-256 of the exact statement text, excluding parameter values. Whitespace or literal changes alter it; it is not a server plan-cache key. |
| Privacy | Nothing is logged. Plan arguments can contain literals from your statement, so choose what you display or keep. |
| Cancellation | Checked between driver operations; an in-flight call is not abandoned. No automatic retry. |

## EXPLAIN and PROFILE

**`EXPLAIN` plans a statement without executing it. `PROFILE` executes it, including writes.** A `PROFILE` can create, update or delete data and can do substantial work even for reads, so it is not a dry run. GraphBridge never selects `PROFILE`; run it yourself, deliberately, against a disposable database. See Neo4j's [execution modes](https://neo4j.com/docs/cypher-manual/current/planning-and-tuning/query-tuning/).

## Cypher in VS Code

The [Neo4j for VS Code extension](https://marketplace.visualstudio.com/items?itemName=neo4j-extensions.neo4j-for-vscode) highlights `.cypher` files and Cypher embedded in .NET strings marked with `/*cypher*/`:

```csharp
var diagnosticQuery = "/*cypher*/ MATCH (n:Person {id: $id}) RETURN n";
```

The marker changes highlighting only, not parameterization or safety. Its C# grammar expects the marker immediately after the opening `"""` of a raw string, where valid multi-line C# cannot put it; use the single-line form above, or save generated text to a `.cypher` file and set synthetic values in the extension's parameter pane.

## Try it

- Workshop lesson [01 · Your first context](../../workshop/GraphBridge.Workshop/Lessons/01-FirstContext/README.md): the first `ToQueryString()`.
- Workshop lesson [05 · LINQ queries and visible Cypher](../../workshop/GraphBridge.Workshop/Lessons/05-LinqQueries/README.md).
- Workshop lesson [15 · Native Cypher with a projector](../../workshop/GraphBridge.Workshop/Lessons/15-NativeCypher/README.md).
- Workshop lesson [22 · One question, three query surfaces](../../workshop/GraphBridge.Workshop/Lessons/22-ThreeSurfaces/README.md): LINQ and Fluent Cypher text side by side.
- [Query Lab](../tools/QUERY_LAB.md) renders captured Cypher and EXPLAIN operators as an HTML report.

Evidence: [capability and evidence map](../../tests/verification/CAPABILITY_EVIDENCE.md). Design: [ADR-0023](../architecture/adr/ADR-0023.md).
