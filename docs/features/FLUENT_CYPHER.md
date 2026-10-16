# Fluent Cypher

`db.Cypher()` describes native, read-only graph clauses using typed symbols and finalized EF mappings. It complements the provider's ordinary [LINQ](LINQ_QUERIES.md) and [native Cypher](NATIVE_CYPHER.md) APIs. It returns untracked scalar DTOs or bounded native paths. C# lambda predicates follow [ADR 0047](../architecture/adr/ADR-0047.md) and aggregates [ADR 0048](../architecture/adr/ADR-0048.md). This is an experimental surface: it does not promise full Cypher coverage or production readiness.

## Choosing a level

GraphBridge offers four levels for reading data. Start at the highest level that can express the question, and step down only when you need to. Every refusal names its alternative, which is usually the next level down.

| Level | You write | The meaning you get | Use it for |
|---|---|---|---|
| 1 · EF Core LINQ | `db.People.Where(p => p.Name == name)` | EF Core's C# semantics, including null comparisons | Entities you track and save, and the [supported LINQ subset](LINQ_QUERIES.md) |
| 2 · Fluent Cypher with C# lambdas | `.Where(() => actor.Value.Name == name)` | The C# meaning; a lambda is refused when native Cypher would return different rows | Graph patterns, optional matches, counts, aggregates and paths |
| 3 · Fluent Cypher with explicit methods | `.Where(actor.Property(p => p.Nickname).Ne("Bob"))` | Native Cypher semantics, stated in the code | What lambdas refuse, such as `!=` or `NOT` over a nullable member and temporal comparisons; `Graph.Exists` |
| 4 · Native Cypher | `db.Database.QueryCypherAsync(text, parameters, projector)` | Exactly the text you write and review | Anything the typed levels do not cover; see [native Cypher](NATIVE_CYPHER.md) |

Levels 2 and 3 are one builder. Where both apply they compile to identical Cypher, and they mix: repeated `Where` calls AND together, and `Graph.Is(predicate)` places an explicit predicate, such as EXISTS, inside a lambda. Levels 2 to 4 return untracked rows; [return keys and load entities with LINQ](#from-fluent-results-to-tracked-entities) when you need tracking. The [workshop](../../workshop/README.md) walks through the levels in lessons 15 to 22, ending with one question answered at levels 1, 2 and 4.

## A graph pattern in C\#

```csharp
using Microsoft.EntityFrameworkCore;
using Graph = GraphBridge.EntityFrameworkCore.FluentCypher.Cypher;

var actor = Graph.Node<Person>();
var movie = Graph.Node<Movie>();
var coactor = Graph.Node<Person>();

var query = db.Cypher()
    .Match(actor.Out<Credit>(movie).In<Credit>(coactor))
    .Where(() => actor.Value.Name == actorName && coactor.Value.Id != actor.Value.Id)
    .ReturnDistinct(() => coactor.Value.Name)
    .OrderBy(() => coactor.Value.Name)
    .Limit(100);

Console.WriteLine(query.ToQueryString()); // Parameter placeholders, no values.
var names = await query.ToListAsync(cancellationToken);
```

The same query written with explicit native predicate methods compiles to identical Cypher:

```csharp
var explicitForm = db.Cypher()
    .Match(actor.Out(Graph.Edge<Credit>(), movie).In(Graph.Edge<Credit>(), coactor))
    .Where(actor.Property(p => p.Name).Eq(actorName)
        .And(coactor.Property(p => p.Id).Ne(actor.Property(p => p.Id))))
    .ReturnDistinct(() => coactor.Value.Name)
    .OrderBy(coactor.Property(p => p.Name))
    .Limit(100);
```

The two Person symbols are distinct variables. `Out<Credit>(movie)` creates an anonymous edge symbol; use `Graph.Edge<Credit>()` when the edge's own properties are needed. The Credit entity must be mapped to a relationship with Person as source and Movie as target. Incoming traversal reverses the traversal direction, not the mapping. There is one MATCH here; splitting it into two MATCH calls can change relationship reuse and row counts.

See the [compiled sample](../../samples/05-FluentCypher/README.md) for mappings, co-actors and shared-movie scores. The `GraphBridge.EntityFrameworkCore.FluentCypher` namespace ships in the provider package; there is no additional runtime dependency.

## Supported clauses and values

Mapped `DateTimeOffset` payloads use native zoned datetime parameters and checked projections. Comparisons and ordering preserve Cypher's offset/zone-sensitive semantics, whereas ordinary GraphBridge LINQ compares UTC instants. See the [temporal contract](NULLS_TYPES_AND_TIME.md) before translating an instant comparison between these query surfaces.

| Surface | Meaning |
|---|---|
| `Graph.Node<T>()`, `Graph.Edge<T>()` | Opaque mapped symbols; CLR type and EF metadata define storage |
| `node.Out(edge, target)`, `.In(edge, source)` | Explicit directions; chain steps for fixed patterns |
| `node.Out<TEdge>(target)`, `.In<TEdge>(source)` | Same directions through a new anonymous edge symbol |
| `Match(patterns)`, `OptionalMatch(patterns)` | Preserve native clause boundaries and physical row multiplicity |
| `OptionalMatch(pattern, where)` | OPTIONAL MATCH with a predicate visibly owned by that clause; same as `OptionalMatch(pattern).Where(where)` |
| `Where(predicate)` | Attach to the immediately preceding MATCH/OPTIONAL MATCH/WITH; repeated calls AND together |
| `Where(() => a.Value.Name == name && ...)` | C# predicate, accepted only where its native result equals its C# meaning; see [predicates as C# lambdas](#predicates-as-c-lambdas) |
| `Property(p => p.Name).Eq(value)` | Mapped scalar comparison; values parameterized using EF converters |
| `Ne/Gt/Ge/Lt/Le`, `And/Or/Not`, `IsNull/IsNotNull` | Native Cypher comparisons and three-valued Boolean logic |
| `Property(p => p.Id).In(values)` | Native `IN` over a parameter list copied when called (at most 1,024 values) |
| `Property(p => p.Name).StartsWith(v)`, `.EndsWith(v)`, `.Contains(v)` | Native, case-sensitive `STARTS WITH`, `ENDS WITH`, `CONTAINS`; `v` is a value or another string expression; refused on converted properties |
| `With(alias, value)`, `WithDistinct(...)` | Explicitly replace scope; forward aliases or introduce counts and other aggregates |
| `Graph.CountRows()`, `Count(alias/expression)`, `CountDistinct(...)` | Native aggregation; introduce the result in WITH, then use `.Value`/`.Expression` |
| `Graph.Min/Max(expr)`, `Sum(expr)`, `Avg(expr)`, `Collect(expr, max)`, `CollectDistinct(expr, max)` | Native aggregates in WITH ([ADR 0048](../architecture/adr/ADR-0048.md)): `min`/`max`/`avg` are null for an all-null group, `sum` is 0, `collect` skips nulls and is sliced on the server to `max` values; results read like the aggregated property |
| `Graph.Exists(pattern, predicate).Not()` | Correlated existence/exclusion; new inner symbols stay local |
| `Graph.Is(predicate)` | Embeds a native predicate, such as `Graph.Exists(...)`, inside a `Where` lambda, for example `() => a.Value.Born > 1960 \|\| Graph.Is(Graph.Exists(...))`, or returns it as a Boolean column: `Return(() => new { a.Value.Name, HasCredits = Graph.Is(exists) })`. A predicate that can be null must be returned as `(bool?)Graph.Is(...)` (GBFC407) |
| `Return(() => new Dto(node.Value.Name))`, `ReturnDistinct(...)` | Scalar, constructor or member-initializer DTO projection; no mapped entity construction |
| `OrderBy/ThenBy` and descending variants | Native ordering over returned scalar expressions: `alias.Property(...)`, `value.Expression` or a lambda such as `() => alias.Value.Name`. The order is native even in lambda form: ascending puts nulls last (LINQ puts them first), and converted keys such as `Guid` sort by their stored form |
| `Skip(n)`, `Limit(n)` | Explicit final paging; LIMIT 1–10,000 required for execution |
| `ToQueryString()`, `ToCommand()` | Offline compilation; the latter deliberately exposes sensitive parameter values |
| `Inspect()` | Offline compilation plus an immutable typed graph/clause/scope snapshot from the same compiler pass |
| `Prepare()` | Compile once into a context-bound command/reader handle; requires an explicit LIMIT, performs no I/O |
| `ToListAsync(token)` | Bounded buffering through the existing native execution/transaction pipeline |

Symbol `Value` is only valid inside Return, lambda Where and lambda ordering expressions. These expressions are translated, never executed; evaluating `Value` in ordinary C# throws. DTO constructors/assignments execute on returned rows; arbitrary method calls, arithmetic, nested client evaluation and entity materialization are rejected.

For evidence that must describe the exact command later executed, use `var prepared = query.Prepare()`, inspect `prepared.Command`, then await `prepared.ToListAsync(token)`. This uses the original compiled reader and parameter snapshot even if captured query-building variables later change. It retains its original context, uses existing native transaction ownership and returns untracked results. It is not a thread-safe or cross-context cache. The caller owns context lifetime and must not mutate objects held in parameter values; `Command.Parameters` is explicitly sensitive.

`query.Inspect().Semantics` records actual compiler aliases, pattern occurrences, directions, scalar relationship endpoint roles, clause order, WHERE attachment, WITH scope/aggregates and RETURN column aliases. One-based source spans refer to the generated command, not to C# source locations. `Prepare(captureSemantics: true)` retains the same snapshot with an executable handle. Normal compilation/execution does not capture snapshots. Snapshot strings include mapped names and expressions but no parameter values; the surrounding command still exposes its parameters explicitly.

A diagram describes a query pattern, not observed database nodes. Bounded paths include named path, direction and depths; their individual physical endpoints are not invented from outer aliases. Nested EXISTS currently returns `NestedExistsNotRepresented` with no diagram clauses while retaining the compiled query. Snapshots over 256 clauses or 4,096 pattern elements similarly return `BudgetExceeded`; the query itself is not truncated or rejected. Consumers must show these omissions explicitly.

## Predicates as C# lambdas

`Where(() => ...)` and `OptionalMatch(pattern, where: () => ...)` accept a C# Boolean expression over `alias.Value.Member` and WITH values such as `count.Value`. The lambda is translated when `Where` is called and never runs. One rule applies ([ADR 0047](../architecture/adr/ADR-0047.md)): **emit native Cypher only where its WHERE result equals the C# meaning; otherwise reject before transport.** No compensating clauses are added.

| C# | Cypher | Accepted when |
|---|---|---|
| `a == v`, `a < v`, `a <= v`, `a > v`, `a >= v` (non-null `v`) | `a op $p` | Always |
| `a == null`, `a != null`, or a captured `v` that is null | `a IS NULL`, `a IS NOT NULL` | Always |
| `a != v` | `a <> $p` | `a` can never be null |
| `a == b` (two symbol members) | `a = b` | At least one side can never be null |
| `a != b` (two symbol members) | `a <> b` | Both sides can never be null |
| `a` (a `bool` member) | `a = $p` (true) | Always |
| `a < v` and other orderings over an enum member | `a op $p` | Always: GraphBridge stores enums as their numeric value, which preserves order |
| `values.Contains(a)` | `a IN $p` | `values` is a captured array, `List<T>` or `HashSet<T>` with default equality, of at most 1,024 values; a null element needs `a` never null (otherwise write `\|\| a == null`) |
| `a.StartsWith(v, StringComparison.Ordinal)`, `a.EndsWith(v, StringComparison.Ordinal)`, `a.Contains(v)`, and the `char` overloads | `a STARTS WITH $p`, `a ENDS WITH $p`, `a CONTAINS $p` | `a` is an unconverted string member and `v` is not null |
| `!p` | `NOT p` | `p` can never be null: null tests, EXISTS, and comparisons, `IN` and string operators over members that can never be null |
| `Graph.Is(predicate)` | The predicate, with its native semantics | Always; under `!`, the predicate must also never be null |
| `&&`, `\|\|` | `AND`, `OR` | Both sides are accepted |

A member **can never be null** when its EF property is not nullable and its symbol was bound by MATCH, or by OPTIONAL MATCH and is used inside that same clause's predicate. A symbol first bound by OPTIONAL MATCH can be null in later clauses, including after WITH. Counts and sums introduced by WITH are never null; `min`, `max` and `avg` can be.

C#'s `StartsWith(string)` and `EndsWith(string)` compare using the current culture, so they are rejected; pass `StringComparison.Ordinal`. `string.Contains(string)` is already ordinal. As in EF LINQ, a string method on a null member is not a match: it never keeps a row on its own, though `|| other` can. `!` is accepted only where the operand can never be null, because C# turns false into true while native `NOT` keeps null as null.

Why `!=` needs this rule: for `person.Value.Nickname != "Bob"`, C# keeps people without a nickname, and native `<>` drops them. That shape is rejected with `GBFC110`. State the meaning explicitly instead:

```csharp
// Native meaning: people without a nickname are dropped.
.Where(person.Property(p => p.Nickname).Ne("Bob"))
// C# meaning: people without a nickname are kept.
.Where(person.Property(p => p.Nickname).IsNull().Or(person.Property(p => p.Nickname).Ne("Bob")))
```

Compared values and `Contains` collections are read from constants, closure fields and static fields **when `Where` is called**, and become parameters, as with `Eq(value)`. Collections are copied; other collection types, such as a LINQ query or a custom collection, are rejected because reading them could run application code. Changing a captured variable afterwards does not change the query. Property getters and method calls on captured objects are never run; assign the value to a local variable first. Values are converted to the member's own type before EF converters apply, so enum, Guid and converted-property equality use the mapped store representation.

Lambda and explicit forms combine: repeated `Where` calls AND together, so `.Where(() => ...).Where(Graph.Exists(...))` works. The generated predicate is flat, `a AND b AND c`, with parentheses only where AND and OR mix and around the operand of `NOT`.

`Graph.Is(...)` reads a captured predicate, or builds an inline one by running only Fluent Cypher builder methods over captured symbols and values. Any other call inside it is rejected without being run.

Rejected shapes throw `NotSupportedException` with a code in the message and in `Exception.Data["GraphBridge.FluentDiagnostic"]`:

| Code | Rejected shape | Use instead |
|---|---|---|
| GBFC101 | Method calls other than the listed ones, arithmetic or other computation over a symbol value; a non-predicate expression | Explicit predicate methods or a reviewed native query |
| GBFC102 | A compared value, collection or `Graph.Is` argument that would require running a getter or method; a null collection or string argument | Assign the value to a local variable first |
| GBFC103 | A comparison with no symbol member, or a node/edge/path symbol compared directly | Compare `alias.Value.Member` or a WITH value |
| GBFC104 | `!` over a predicate that can be null, such as a comparison on a nullable member | `.Not()` on an explicit predicate, or explicit null tests |
| GBFC105 | A conversion or operator with no exact native equivalent, such as comparing an `int` property with `1960.5` or an enum with an out-of-range number; `==` on a reference type other than `string` (reference equality in C#); NaN in `Contains` values; a value outside the stored range | A value of the property's own type |
| GBFC106 | A culture-sensitive or case-insensitive string method, such as `StartsWith("A")` | `StartsWith("A", StringComparison.Ordinal)` or the explicit `.StartsWith(...)` |
| GBFC107 | `Contains` over an unsupported collection, with a custom comparer, or over more than 1,024 values | Copy the values into an array or `List<T>`, or query in batches |
| GBFC110 | `!=` or `==` where null makes the C# and native results differ; a null `Contains` element with a member that can be null | `.Ne(...)`, `.IsNull()` and `.IsNotNull()` combined explicitly, or `\|\| a == null` |
| GBFC111 | `<`, `<=`, `>`, `>=` over a property with a value converter (enums excepted); string operators over converted properties; a converted property compared with a WITH value | An unconverted representation or a reviewed native query |
| GBFC112 | `DateTimeOffset` and other temporal comparisons or `Contains` over temporal members | Explicit predicate methods, which keep native temporal semantics |
| GBFC150 | An ordering lambda that is not `alias.Value.Member` or a WITH value | `OrderBy(alias.Property(...))` |

Every other Fluent refusal is coded the same way, by family. The exception type stays `NotSupportedException`, and builder misuse such as a missing `Limit` stays `InvalidOperationException` or `ArgumentException`:

| Code | Refused | Use instead |
|---|---|---|
| GBFC113 | Comparing two different converted properties | A reviewed native query |
| GBFC201–GBFC204 | Bounded or undirected patterns without a named path, mixed relationship mappings, a path bound twice | `Match(path, pattern.Repeat(min, max))` with one homogeneous relationship |
| GBFC205–GBFC208 | A non-node endpoint, rebinding a symbol after WITH dropped it, reusing an edge or relationship list with other endpoints | A new symbol |
| GBFC209–GBFC213 | Unmapped edge or node types, direction or endpoint types that contradict the mapping, EF query filters, unmapped or derived property slots | The mapped relationship and scalar properties, or EF LINQ for filtered entities |
| GBFC214 | Invalid `Repeat`/path construction | One explicitly bounded `Repeat` per named path |
| GBFC301–GBFC304 | Not starting with MATCH, WITH introducing anything but forwarded symbols and aggregates, a symbol outside the current scope | Start with `Match`; forward symbols through `With` |
| GBFC401–GBFC404 | Empty RETURN, unsupported DTO shapes, computed or entity returns | Scalar `Value` members, WITH values and DTOs |
| GBFC405 | ORDER BY over a value that is not returned | Return it, then order by it |
| GBFC406 | `Min`/`Max`/`Sum`/`Avg` over a converted property, or an aggregate over anything but a property expression | An unconverted property or a reviewed native query |
| GBFC407 | A returned `Graph.Is(...)` predicate that can be null, read as `bool` | `(bool?)Graph.Is(...)` |

## Optional matches and scope

```csharp
var credit = Graph.Edge<Credit>();
var query = db.Cypher().Match(actor)
    .OptionalMatch(actor.Out(credit, movie))
    .Where(movie.Property(m => m.Year).Gt(2020))
    .Return(() => new { actor.Value.Name, movie.Value.Title,
                       Year = (int?)movie.Value.Year })
    .Limit(100);
```

This retains an actor when no qualifying movie exists. Putting `.With(actor, movie)` before that Where filters the resulting rows instead. A missing optional match may produce null for even a required EF property. Use nullable value projections explicitly; a non-nullable value receiving null throws. Reference properties may also be null at runtime. This API does not synthesize C# nullable alias types or perform EF entity-integrity validation.

`Eq(null)` has native unknown semantics; it does not become IS NULL. Use `IsNull()` to test absence. `Not()` preserves unknown. Native ascending ordering places nulls last. Converters determine store representation; comparisons/order use that representation, with no promise of CLR ordering equivalence. Comparing separate converted EF properties is rejected rather than assuming compatible conversions.

WITH removes unlisted symbols. A removed symbol cannot be rebound silently; create a new symbol when you intend a new binding. Repeated node/edge CLR types need separate aliases. Global EF query filters cause compilation to reject: they are never silently omitted or partially applied.

Relationship endpoint foreign keys follow `startNode(edge)` and `endNode(edge)` using their mapped principal property names and FK converters. Both are null when the optional relationship is absent, even if its source node exists. Keeping the relationship in `With(credit)` is sufficient to return its endpoint keys; keeping the node aliases is unnecessary.

### Aggregates

```csharp
var first = Graph.Min(movie.Property(m => m.Year));
var titles = Graph.Collect(movie.Property(m => m.Title), max: 20);
var query = db.Cypher().Match(actor)
    .OptionalMatch(actor.Out<Credit>(movie))
    .With(actor, first, titles)
    .Return(() => new { actor.Value.Name, First = (int?)first.Value, Titles = titles.Value })
    .OrderBy(() => actor.Value.Name)
    .Limit(100);
```

WITH groups by the forwarded symbols (`actor`) and introduces the aggregates. An actor without credits keeps one row with `First = null` and an empty `Titles` list: native `min`, `max` and `avg` of an all-null group are null, `sum` is 0, and `collect` skips nulls. `collect` requires `max` and slices the list on the server; element order is not guaranteed. Results are read like the aggregated property, so an `int` property returns `int` and a collected `Guid` key returns `Guid`.

An aggregate can also be returned directly, without WITH: `Return(() => new { actor.Value.Name, Movies = count.Value })` renders `RETURN n0.name AS c0, count(n1) AS c1`, and native Cypher groups by the non-aggregate columns. Introduce it with WITH when you need to filter on it; a `Where` before RETURN cannot see an aggregate (GBFC303, which lists the symbols in scope).

`Exists(pattern, predicate)` introduces lexical local symbols. Reuse the same predicate in several Boolean expressions or nest it inside another EXISTS. Locals cannot be returned or forwarded by the outer query. An explicit outer `Match` can introduce a fresh outer binding using the same API symbol; it does not export a subquery result. Outer symbols dropped by WITH still cannot be rebound.

## From Fluent results to tracked entities

Fluent results are untracked scalars. To work with tracked EF entities, return their keys and load them with ordinary EF LINQ through the bounded [scalar `Contains`](LINQ_QUERIES.md#contains-over-a-local-collection):

```csharp
var ids = await db.Cypher()
    .Match(actor.Out<Credit>(movie).In<Credit>(coactor))
    .Where(() => actor.Value.Name == name && coactor.Value.Id != actor.Value.Id)
    .ReturnDistinct(() => coactor.Value.Id)
    .Limit(100)
    .ToListAsync(cancellationToken);
var keys = ids.ToArray();
var coactors = await db.People.Where(p => keys.Contains(p.Id)).ToListAsync(cancellationToken);
```

EF remains the only state owner: the second query tracks the entities with EF's own semantics. The two queries are separate reads, so another writer can change data between them. `Contains` accepts at most 1,024 values.

## Bounded paths

```csharp
using GraphBridge.EntityFrameworkCore.Paths;

var start = Graph.Node<Person>();
var end = Graph.Node<Person>();
var follows = Graph.Edge<Friendship>();
var path = Graph.Path();
var paths = await db.Cypher()
    .Match(path, start.Out(follows, end).Repeat(minDepth: 1, maxDepth: 3))
    .Where(start.Property(p => p.Id).Eq(personId))
    .Return(() => path.Value)
    .Limit(25)
    .ToListAsync(cancellationToken);

foreach (var result in paths)
    Console.WriteLine($"{result.Relationships.Count} hops; {result.Nodes.Count} node occurrences");
```

Paths return `GraphPath<INode, IRelationship>`: ordered native driver values, read-only topology collections and `StepDirections`. The named pattern uses EF labels and relationship metadata; property values inside native nodes/edges remain in store representation. There is no EF tracking, property conversion for whole path elements, or identity resolution.

- Repeat requires one relationship step, homogeneous source/target node mappings and `0 <= minDepth <= maxDepth <= 64`. `In` traverses incoming edges. `Undirected` permits either direction, only within named bounded paths.
- Nodes can repeat; a physical relationship cannot repeat within a path. Parallel edges remain distinct choices. Self-loop orientation is Incoming for an incoming request, otherwise Outgoing. Paths are validated for structure, depth, relationship type, unique physical edges and endpoint labels before their DTO is constructed.
- A repeated edge variable is a list, so its scalar properties/count forwarding are rejected. Inspect the returned path instead. Heterogeneous/multi-type repetition, shortest/weighted paths and quantified pattern grammars remain unsupported.
- Zero hops returns one node and no relationships when the endpoints match. An absent normal MATCH returns no rows; an absent OPTIONAL MATCH returns a null path. Duplicate matching starts remain native rows; no anchor uniqueness guard is implied.
- Ordering is unspecified unless supported scalar ordering is supplied. LIMIT returns a bounded observed subset; it gives neither a truncation witness nor a shortest-path guarantee. For explicit native anchor uniqueness and `HasMorePaths`, use the separate [QueryPathsAsync contract](BOUNDED_PATHS.md).
- Depth/row bounds do not cap expansion work, bytes or elapsed time. Use a selective indexed anchor and a server transaction timeout appropriate to the workload. These are read-only queries, not a graph algorithm framework.

## Execution and exclusions

Existing EF transactions remain caller-owned. Otherwise the native pipeline owns and closes a transaction. Cancellation, cursor draining, failed projectors, uncertain commits and diagnostics follow native query execution ([native Cypher](NATIVE_CYPHER.md#transactions)). Values stay out of ordinary query inspection and default diagnostics; explicit command parameters are sensitive.

There is no implicit retry, write DSL, arbitrary raw clause, IQueryable composition, synchronous execution, automatic schema change, AutoInclude, tracked graph return or full Cypher-language promise. Use ordinary EF LINQ for supported EF entity semantics and reviewed native Cypher for language forms beyond this surface. [ADR 0041](../architecture/adr/ADR-0041.md) and [ADR 0047](../architecture/adr/ADR-0047.md) record the decisions.

## Try it

- Workshop lesson [16 · Fluent Cypher I: patterns](../../workshop/GraphBridge.Workshop/Lessons/16-FluentPatterns/README.md).
- Workshop lesson [17 · Fluent Cypher II: optional matches and counts](../../workshop/GraphBridge.Workshop/Lessons/17-FluentOptionalAndCounts/README.md).
- Workshop lesson [18 · Fluent Cypher III: existence](../../workshop/GraphBridge.Workshop/Lessons/18-FluentExists/README.md).
- Workshop lesson [19 · Bounded paths](../../workshop/GraphBridge.Workshop/Lessons/19-BoundedPaths/README.md).
- Workshop lesson [22 · Capstone: one question, three query surfaces](../../workshop/GraphBridge.Workshop/Lessons/22-ThreeSurfaces/README.md).
- Sample [05 Fluent Cypher gallery](../../samples/05-FluentCypher/README.md): eight compiled queries over a movie graph.

Evidence: [Fluent Cypher acceptance](../../tests/verification/FLUENT_CYPHER_ACCEPTANCE.md) and [semantic matrix](../../tests/verification/FLUENT_SEMANTIC_MATRIX.md).
