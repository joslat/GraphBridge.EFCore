# LINQ queries

Query mapped entities with ordinary EF Core LINQ. GraphBridge translates a documented subset into one parameterized Cypher statement that runs on the server, and refuses everything else before any database work. Use LINQ for entities you track and save; use [Fluent Cypher](FLUENT_CYPHER.md) for graph patterns and [native Cypher](NATIVE_CYPHER.md) for anything else.

```csharp
var bornBefore = 1965;
var veterans = db.People
    .Where(p => p.Born < bornBefore)
    .OrderBy(p => p.Born)
    .Select(p => p.Name);

Console.WriteLine(veterans.ToQueryString());   // offline: placeholders, never values
var names = await veterans.ToListAsync(cancellationToken);
```

```cypher
MATCH (n:`Person`)
WHERE coalesce(n.`born` < $p0, false)
RETURN n.`name` AS `c2`
ORDER BY n.`born` IS NULL DESC, n.`born` ASC
```

Every value travels as a parameter, so the statement text is the same whatever the values are, and EF reuses its compiled query. Labels and property names come from the finalized EF model (`Born` is stored as `born` here). See [query inspection](QUERY_INSPECTION.md) for reading and planning the generated Cypher.

## What translates

| LINQ | Supported form | Cypher |
|---|---|---|
| `Where` | `==`, `!=`, `<`, `<=`, `>`, `>=` between a mapped scalar property and a captured variable or constant, or between two mapped properties; `&&`, `\|\|`, `!`; a `bool` property; `object.Equals(p.X, value)` and `p.X.Equals(value)` on `Guid`, `string`, `int`, `long`, `bool` and enum properties, which translate as `==`; [`Contains` on a local collection](#contains-over-a-local-collection) | `WHERE`, with [C# null semantics](#null-comparisons) |
| `OrderBy`, `ThenBy` and their descending forms | A direct mapped scalar property without an explicit value converter | `ORDER BY`, nulls first when ascending |
| `Skip`, `Take` | Integer values | `SKIP $p`, `LIMIT $p` |
| `Select` | One mapped scalar property, or a [DTO](#dto-projections) | `RETURN` of only the selected properties |
| `FirstAsync`, `FirstOrDefaultAsync` | With or without a predicate | `LIMIT 1` |
| `SingleAsync`, `SingleOrDefaultAsync` | With or without a predicate | `LIMIT 2`, so a second row is detected |
| `CountAsync`, `LongCountAsync`, `AnyAsync` | Ordinary node roots | Server aggregate; no rows are loaded |
| `MinAsync`, `MaxAsync` | Direct `int` or `long` properties on ordinary node roots | [Integer aggregates](#integer-aggregates) |
| `ContainsAsync` | After a direct scalar `Select` | Null-safe equality and a server existence check |
| `Include` | One direct native-reference navigation, in either direction | See [references and Include](REFERENCES.md) |
| `FindAsync` | Primary key | EF's identity map first, then a keyed query |

Tracking, `AsNoTracking` and `AsNoTrackingWithIdentityResolution` keep their EF meaning; EF is the only state owner. `EF.CompileAsyncQuery` works and reuses one translation across contexts and parameter values.

Association and identified-relationship `DbSet`s support the same filters, ordering, paging and scalar projections. Their rows, and rows of native-reference dependents, are checked for endpoint identity and cardinality before EF shapes them. Aggregates over those roots are refused (GBQ170) because an aggregate would hide those per-row checks; aggregate the related node set instead, or use Fluent or native Cypher.

String methods such as `StartsWith`, computed values such as `p.Born + 1` and any other method call over an entity member are not translated in LINQ (GBQ120). Fluent Cypher has explicit string operators.

### Compose in order

Write a query as filter, then order, then page, then project or aggregate. Cypher would need a subquery to apply an operator after paging, so these compositions are refused with GBQ110:

- `Where`, `OrderBy`, `Contains` or an aggregate after `Skip` or `Take`, for example `db.People.Take(5).Where(...)`.
- `Skip` after `Take`, or a second `Take`.
- `Where` or `OrderBy` after a scalar `Select`.

`FirstAsync` and `SingleAsync` without a predicate after `Take` are fine: they tighten the existing limit.

## Null comparisons

LINQ keeps **C# semantics**. A missing graph property reads as null, and comparisons treat it the way C# treats null:

```csharp
// C# meaning: people without a nickname are "not Larry" and are kept.
var notLarry = db.People.Where(p => p.Nickname != "Larry").OrderBy(p => p.Name).Select(p => p.Name);
```

```cypher
MATCH (n:`Person`)
WHERE (NOT coalesce(n.`nickname` = $p0, (n.`nickname` IS NULL AND $p0 IS NULL)))
RETURN n.`name` AS `c2`
ORDER BY n.`name` IS NULL DESC, n.`name` ASC
```

- `==` and `!=`: two nulls are equal; exactly one null is unequal.
- `<`, `<=`, `>`, `>=`: false when either side is null, so `!` of that comparison is true.
- Ordering: nulls come first ascending and last descending, as in LINQ to Objects. String ordering follows Neo4j, not .NET culture rules.

Fluent Cypher's explicit methods keep native Cypher semantics instead, where `null <> 'Larry'` drops the row. [Nulls and converted values](NULLS_TYPES_AND_TIME.md) has the full truth table and the Guid, enum and `DateTimeOffset` rules; LINQ compares `DateTimeOffset` values as UTC instants.

## Async only

Execute queries with the async operators: `ToListAsync`, `FirstAsync`, `CountAsync`, `AnyAsync` and so on. Synchronous enumeration, such as `ToList()` or `foreach` over a query, throws `NotSupportedException` before any database work. `ToQueryString()` does not execute and can be called synchronously.

`AsAsyncEnumerable()` streams rows; dispose an unfinished enumerator (`await foreach` does) to release its session. Await each query before starting another on the same context. The [async-only decision](../architecture/adr/ADR-0037.md) records the reasons.

## Contains over a local collection

Membership in a captured collection becomes **one list parameter**, with null handling that also holds under negation:

```csharp
string[] names = ["Keanu Reeves", "Tom Hanks", "Nobody"];
var cast = db.People.Where(p => names.Contains(p.Name)).OrderBy(p => p.Name).Select(p => p.Name);
```

```cypher
MATCH (n:`Person`)
WHERE (coalesce(n.`name` IN $p0, false) OR (n.`name` IS NULL AND any(__gb_contains_value IN $p0 WHERE __gb_contains_value IS NULL)))
RETURN n.`name` AS `c2`
ORDER BY n.`name` IS NULL DESC, n.`name` ASC
```

- **Collections:** one-dimensional arrays and `List<T>`, captured or inline constants, with **at most 1,024 elements** including duplicates and nulls. The values are copied and converted when the query runs; changing them, or the collection's length, reuses the same compiled query.
- **Item types:** a directly mapped `string`, `int`, `long`, `bool`, `Guid` or enum property, nullable or not, without an explicit value converter. Built-in Guid and enum storage conversion applies to each value.
- **Semantics:** an empty collection matches nothing; duplicates do not duplicate rows; a null element matches a missing or null property; `!names.Contains(p.Name)` returns exactly the complement.
- **Refused at translation:** other item types, floating-point or converted properties and inline arrays over 1,024 values (GBQ130), and the comparer overload `Enumerable.Contains(values, item, comparer)`.
- **Refused when the query runs, before anything is sent (`InvalidOperationException`):** a null collection, a captured collection over 1,024 values, and other collection types such as `HashSet<T>`, lazy enumerables or `IQueryable`. GraphBridge never enumerates an arbitrary sequence to find out whether it is finite.

The cap bounds the parameter list, not server scan cost. For larger sets, write a deliberately bounded native query; GraphBridge does not split one query into hidden batches, which could change ordering and paging.

With C# 14, `array.Contains(x)` can bind to the span-based `MemoryExtensions.Contains`. EF Core 10 rewrites the ordinary form, so it translates as above; a comparer overload is refused, or can fail earlier inside EF. See Microsoft's [overload resolution change](https://learn.microsoft.com/en-us/dotnet/core/compatibility/core-libraries/10.0/csharp-overload-resolution).

A direct scalar projection also supports server-side `ContainsAsync`:

```csharp
bool exists = await db.People
    .Where(p => p.Born > 1960)
    .Select(p => p.Id)
    .ContainsAsync(id, cancellationToken);
```

Entity membership, `ContainsAsync` over a DTO, after paging or with `Include`, and correlated subqueries are refused.

## DTO projections

`Select` can construct records, anonymous objects and objects with member assignments from mapped scalar properties. Only the distinct columns you use are returned, and nothing is tracked:

```csharp
public sealed record PersonCard(string Name, int? Born);

var people = await db.People
    .Where(p => p.Born >= 1965)
    .OrderBy(p => p.Born).ThenBy(p => p.Name)
    .Select(p => new PersonCard(p.Name, p.Born))
    .ToListAsync(cancellationToken);
```

```cypher
MATCH (n:`Person`)
WHERE coalesce(n.`born` >= $p0, false)
RETURN n.`born` AS `c1`, n.`name` AS `c2`
ORDER BY n.`born` IS NULL DESC, n.`born` ASC, n.`name` IS NULL DESC, n.`name` ASC
```

The DTO is constructed by EF's compiled shaper after the stored values have been read and converted; no predicate or row-dependent code runs on the client.

- **Allowed leaves:** mapped properties, `EF.Property` (including shadow properties), supported scalar constants, `null` and captured variables. Nested constructions and repeated fields are fine. Nullable widening and boxing are allowed.
- **Refused (GBQ150):** arithmetic, numeric casts and method calls over row values, collection initializers and nested member mutation.
- **Entities inside a DTO:** selecting the entity itself, alone or inside a DTO, materializes it through EF with the query's tracking mode. A DTO of scalars never creates tracked entities.
- **Dependents and relationships:** a native-reference dependent, such as a `Movie` with an optional director, still checks its parent link before shaping each row, so its Cypher is longer. Relationship roots still read their hidden endpoint keys.
- **Empty or constant-only projections** return one literal row per match, so filtering, paging and counts stay correct without reading an arbitrary property.
- **Captured-only constructors:** when a DTO depends only on captured values, EF can evaluate it once as a parameter, and every row can receive the same instance. Do not rely on a fresh object per row in that case.

Apply filters and ordering before the projection: `Where` or `OrderBy` after a DTO `Select` is refused (GBQ110). A projection combined with `Include` is refused (GBQ150), and a one-field DTO is not a scalar for `ContainsAsync`. A DTO is not a navigation, join or collection-loading facility; use Fluent Cypher for graph-shaped results.

## Integer aggregates

`MinAsync` and `MaxAsync` over a direct `int`, `long`, `int?` or `long?` property run on the server for ordinary node roots:

```csharp
var earliest = await db.People.MinAsync(p => p.Born, cancellationToken);          // Born is int?
var latest = await db.People.Select(p => p.Born).MaxAsync(cancellationToken);
var latestNicknamed = await db.People.Where(p => p.Nickname != null).MaxAsync(p => p.Born, cancellationToken);
```

Both the selector form and `Select(...).MaxAsync()` are supported. Filters must come before the aggregate; ordering is dropped because it cannot change an extremum. A required property can be widened, as in `MinAsync(p => (int?)p.Age)`. With no matching rows, a non-nullable result throws `InvalidOperationException` (`Sequence contains no elements.`), and a nullable result returns null.

Neo4j aggregates skip nulls and compare mixed types by their own rules, so a plausible minimum does not prove that every input is a valid integer. The same statement therefore counts inputs that break the model's type, requiredness or `int` range. For a required `int` property stored as `age`:

```cypher
MATCH (n:`Person`)
RETURN min(n.`age`) AS aggregate, count(CASE WHEN n.`age` IS :: INTEGER NOT NULL AND n.`age` >= -2147483648 AND n.`age` <= 2147483647 THEN null ELSE 1 END) AS __gb_aggregate_invalid
```

A nonzero count throws `InvalidOperationException`; values are never coerced from strings or floats. The check covers rows that match the query's filter, not the whole database.

Refused before transport:

- `Sum`, `Average`, and `Min`/`Max` over strings, Guids, enums, floating-point values, converted properties, DTOs or entities, or a nullable property unwrapped to a non-nullable result (GBQ105).
- Aggregates after `Skip` or `Take` (GBQ110), and with `Include`.
- Aggregates over association, identified-relationship or native-reference roots (GBQ170).

For grouping, sums and averages, use Fluent Cypher's `With(...)` with `Graph.Count`, `Sum`, `Avg`, `Min` and `Max`, or [native Cypher](NATIVE_CYPHER.md).

## What is refused

A shape outside the subset throws `Neo4jQueryTranslationException` (namespace `GraphBridge.EntityFrameworkCore.Diagnostics`, derived from `InvalidOperationException`) while the query is compiled. Nothing reaches Neo4j, and nothing runs on the client: GraphBridge never loads rows to filter them in C#.

```csharp
try
{
    await db.Movies.GroupBy(m => m.Released).Select(g => g.Key).ToListAsync(cancellationToken);
}
catch (Neo4jQueryTranslationException error)
{
    Console.WriteLine(error.Message);
    // GraphBridge cannot translate GroupBy [GBQ101]. Group in Fluent Cypher with With(...) and
    // Graph.Count, Min, Max, Sum or Avg, or group a bounded list in memory.
    var code = error.Code;   // "GBQ101", also in error.Data["GraphBridge.QueryDiagnostic"]
}
```

The message names the shape in LINQ terms, a stable code and a supported alternative. The common ones are `GroupBy` (GBQ101), `Distinct` (GBQ102), joins and `SelectMany` (GBQ103), set operators (GBQ104), `Sum` and `Average` (GBQ105), operators after paging (GBQ110) and computation over entity members (GBQ120). [Known limitations](../reference/KNOWN_LIMITATIONS.md#refusal-codes) lists every code with its alternative.

## Try it

- Workshop lesson [05 · LINQ queries and visible Cypher](../../workshop/GraphBridge.Workshop/Lessons/05-LinqQueries/README.md): filters, ordering, counts and `Contains`.
- Workshop lesson [06 · The failure lab](../../workshop/GraphBridge.Workshop/Lessons/06-FailureLab/README.md): refused shapes and their codes.
- Workshop lesson [07 · Projections and aggregates](../../workshop/GraphBridge.Workshop/Lessons/07-Projections/README.md): DTOs and integer `Min`/`Max`.
- Workshop lesson [14 · Nulls, types and time](../../workshop/GraphBridge.Workshop/Lessons/14-NullsTypesTime/README.md): LINQ and Fluent null semantics side by side.
- Workshop lesson [22 · One question, three query surfaces](../../workshop/GraphBridge.Workshop/Lessons/22-ThreeSurfaces/README.md): the same question in LINQ, Fluent and native Cypher.
- Sample [01 Blogging — code-first](../../samples/01-Blogging-CodeFirst/README.md).

Evidence: [EF conformance matrix](../../tests/verification/EF_CONFORMANCE_MATRIX.md) and [upstream EF test inventory](../../tests/verification/EF_UPSTREAM_INVENTORY.md).
