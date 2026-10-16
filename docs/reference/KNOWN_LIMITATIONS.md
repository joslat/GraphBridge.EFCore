# Known limitations

GraphBridge supports a tested subset of EF Core and Cypher. Anything outside it is refused with an exception before any database work: a query is never moved to the client and evaluated over a whole graph. This page lists the common limits, the supported alternative for each, and the refusal codes. [Compatibility](COMPATIBILITY.md) lists what is supported.

## Common limitations

| Operation or symptom | Current limit | Supported alternative |
|---|---|---|
| `SaveChanges()`, `ToList()` and other synchronous enumeration | Persistence and query enumeration are async only; pending synchronous writes throw `NotSupportedException`. EF can return 0 without provider work when nothing changed | `SaveChangesAsync`, `ToListAsync`, `await using`. `ToQueryString()` is offline and can be called synchronously. See the [async-only decision](../architecture/adr/ADR-0037.md) |
| Entity or custom-comparer `Contains`, joins, grouping, `Distinct`, set operators, `Sum`/`Average`, non-integer or relationship `Min`/`Max` | Not translated. [Integer `Min`/`Max`](../features/LINQ_QUERIES.md#integer-aggregates) on ordinary nodes and [bounded scalar `Contains`](../features/LINQ_QUERIES.md#contains-over-a-local-collection) are supported | [Fluent Cypher](../features/FLUENT_CYPHER.md) or reviewed [native Cypher](../features/NATIVE_CYPHER.md) |
| Row-dependent client methods or arithmetic in `Select`; composition after a DTO projection | [DTO construction](../features/LINQ_QUERIES.md#dto-projections) from mapped scalars, parameters and nested entities is supported; arbitrary computed expressions are refused | Filter and order before the projection, or use native Cypher with an explicit projector |
| `Take(...).Where(...)`, filtering or ordering after paging, repeated `Take` | Refused when the server order cannot be preserved | Filter and order before paging when that keeps your meaning; otherwise native Cypher |
| Nested, filtered, multiple or string `Include` | One direct native-reference `Include` is supported ([references](../features/REFERENCES.md)); association and identified-relationship `Include` are not | Query the relationship or node roots explicitly, or a second query by key |
| Required foreign-key reassignment, moving a relationship to other endpoints | Required references and tracked relationship endpoints keep their identity | Optional references support parent changes. Otherwise replace the relationship with a new key or use a reviewed migration; changing an endpoint key does not move a relationship |
| Missing key constraints or duplicate graph identities | Saves require reviewed uniqueness constraints; malformed identities are refused | Inspect and deploy a reviewed schema change ([schema and migrations](../features/SCHEMA_AND_MIGRATIONS.md)). Queries and saves never repair schema or data |
| `ReloadAsync`, `GetDatabaseValuesAsync` | Their projection is refused | Read in a fresh context. Rollback does not rewind values EF already accepted ([transactions](../features/TRANSACTIONS_AND_CONCURRENCY.md#what-rollback-does-not-undo)) |
| Retry after a lost commit acknowledgement | A connection error does not prove rollback; there is no automatic retry or exactly-once guarantee | Check durable state with application IDs in a fresh context first ([recovery](../features/TRANSACTIONS_AND_CONCURRENCY.md#recovering-a-failed-graph-operation)) |
| Owned, complex or inherited entities; arbitrary converter operations | Unsupported mappings are refused; a conversion does not prove operator equivalence | Explicit graph entities and scalars with reversible conversions; check the specific comparison or ordering ([nulls, types and time](../features/NULLS_TYPES_AND_TIME.md)) |
| `ExecuteUpdateAsync`, `ExecuteDeleteAsync` | At most 10,000 selected rows, scalar setters only; keys, endpoints, `Include` and projected roots are refused; tracked entities are not updated | [Bulk mutations](../features/BULK_MUTATIONS.md); a failure aborts the whole current transaction |

## Refusal codes

A refused LINQ query throws `Neo4jQueryTranslationException` before any database work. Its message names the shape in LINQ terms, a stable code and a supported alternative, for example `GraphBridge cannot translate GroupBy [GBQ101]. Group in Fluent Cypher with With(...) ...`. The code is also in `Exception.Data["GraphBridge.QueryDiagnostic"]`, and Doctor reports it as the `subcode` of `GBD-QUERY-001`. Match on the code, not the message text. Fluent Cypher refusals use `GBFC` codes ([Fluent Cypher](../features/FLUENT_CYPHER.md#predicates-as-c-lambdas)).

| Code | Refused | Supported alternative |
|---|---|---|
| GBQ100 | Other unsupported shapes, such as unmapped roots or properties | The documented subset, or a reviewed native query |
| GBQ101 | `GroupBy` | Fluent Cypher `With(...)` with `Graph.Count`, `Min`, `Max`, `Sum` or `Avg`, or grouping a bounded list in memory |
| GBQ102 | `Distinct` | Fluent `ReturnDistinct`, or de-duplicating a bounded projected list |
| GBQ103 | `Join`, `GroupJoin`, `LeftJoin`, `RightJoin`, `SelectMany` | Match the relationship in Fluent Cypher, or query each side and join a bounded result |
| GBQ104 | `Union`, `Concat`, `Intersect`, `Except` | Separate queries combined in memory, or a reviewed native query |
| GBQ105 | `Sum`, `Average`, and `Min`/`Max` beyond direct int/long properties | Fluent `Graph.Sum`, `Avg`, `Min`, `Max`, or [integer aggregates](../features/LINQ_QUERIES.md#integer-aggregates) |
| GBQ106 | `Last`, `Reverse`, `ElementAt`, `SkipWhile`, `TakeWhile` | Opposite ordering with `First`, or `Skip(n)` with `First` |
| GBQ107 | `OfType`, `Cast` | The mapped type's own `DbSet` |
| GBQ108 | `All`, `DefaultIfEmpty` | `Any` with the opposite condition, or Fluent OPTIONAL MATCH |
| GBQ110 | Operators after `Skip`/`Take`, repeated `Take`, subqueries | Apply operators before paging, or split the query |
| GBQ120 | Methods or computation over entity members, unsupported predicate operators and converter comparisons | Compare mapped scalars with variables or constants |
| GBQ130 | Unsupported `Contains` shapes | `Contains` over a captured array or `List<T>` of supported scalars (at most 1,024) |
| GBQ140 | Nested, filtered, multiple or string `Include` | One direct native-reference `Include`, or a second query by key |
| GBQ150 | Projections beyond mapped scalars and DTOs | [DTO projections](../features/LINQ_QUERIES.md#dto-projections) |
| GBQ160 | Unsupported `ExecuteUpdateAsync`/`ExecuteDeleteAsync` shapes | Direct scalar setters on a mapped entity root ([bulk mutations](../features/BULK_MUTATIONS.md)) |
| GBQ170 | Aggregates over association, identified-relationship or native-reference roots | Aggregate the related node set, or a reviewed native query |

## Reporting a limitation

Model mapping errors come from [model validation](../../src/GraphBridge.EntityFrameworkCore/Infrastructure/Neo4jModelValidator.cs); query refusals from the [unsupported-method visitor](../../src/GraphBridge.EntityFrameworkCore/Query/Neo4jQueryableMethodTranslatingExpressionVisitor.Unsupported.cs). When you report one, include a small synthetic model, the LINQ shape, the refusal code, exact versions and the exception type. Remove credentials and application values. The [behavior matrix](../../tests/verification/EF_CONFORMANCE_MATRIX.md) maps each supported behavior to its tests.

## Try it

Workshop lesson [06 · The failure lab](../../workshop/GraphBridge.Workshop/Lessons/06-FailureLab/README.md) triggers the common refusals and shows each code and alternative.
