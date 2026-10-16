# Nulls and converted scalar values

GraphBridge translates supported scalar predicates to server-side Cypher and compensates for the differences between CLR and Cypher null logic, so LINQ keeps C# semantics. It does not evaluate unsupported predicates over client-loaded rows. Read this page when you compare nullable values, Guids, enums or timestamps, or when you configure a value converter.

```csharp
// C#: a person without a nickname is "not Larry".
var names = db.People.Where(p => p.Nickname != "Larry").OrderBy(p => p.Name).Select(p => p.Name);
```

```cypher
MATCH (n:`Person`)
WHERE (NOT coalesce(n.`nickname` = $p0, (n.`nickname` IS NULL AND $p0 IS NULL)))
RETURN n.`name` AS `c2`
ORDER BY n.`name` IS NULL DESC, n.`name` ASC
```

In plain Cypher, `null <> 'Larry'` is unknown and the row would be dropped. The `coalesce(...)` keeps people without a nickname. [Fluent Cypher](FLUENT_CYPHER.md) keeps native semantics instead.

| Predicate | Contract |
|---|---|
| Nullable equality | Two null values compare equal; exactly one null compares unequal. |
| Nullable inequality | Negates the compensated equality result. |
| Nullable `<`, `<=`, `>`, `>=` | Returns false when either operand is null. Negating that complete predicate returns true. |
| `&&`, `||`, `!` over supported Boolean predicates | Compose the compensated predicates. Nullable Boolean values can be compared with true, false or null. |
| Missing graph property | Materializes as null for an optional property. A required missing/null property fails explicitly. |
| Explicit property assignment to null | Neo4j removes the property; querying it has the same optional-scalar semantics as an omitted property. |
| Nullable scalar parameters | Preserve null, convert non-null values using the finalized EF model, and remain parameters in Cypher. |

Null always means an absent graph property. Converters with `ConvertsNulls=true` are rejected during model validation; custom null-to-sentinel conversion is not supported.

## Supported scalar types

| CLR type (and its nullable form) | Stored in Neo4j as | Notes |
|---|---|---|
| `string` | String | Ordering and comparison use Neo4j's string collation. |
| `bool` | Boolean | |
| `int`, `long` | Integer | `int` reads use checked narrowing; out-of-range values fail. |
| `double` | Float | |
| `Guid` | Lowercase canonical `D` string | See [Guid and enum values](#guid-and-enum-values). |
| Enum | Integer (checked signed 64-bit) | See [Guid and enum values](#guid-and-enum-values). |
| `DateTimeOffset` | Zoned datetime | Payload only; see [native DateTimeOffset payloads](#native-datetimeoffset-payloads). |

Any other type, such as `decimal`, `float`, `DateTime`, `DateOnly`, `TimeOnly` or `TimeSpan`, needs an explicit `HasConversion` to one of these store types; see [explicit temporal representations](#explicit-temporal-representations). Collections, owned types and complex properties are not mapped. Key properties have extra rules; see [application keys](MAPPING_NODES.md#application-keys).

## Guid and enum values

Guid values use canonical `D` strings in the graph. A literal `new Guid("...")` in a supported comparison is parsed into a parameter, with an exact constructor whitelist. Captured Guid expressions use EF's existing parameter extraction. Invalid literals fail before transport without echoing the literal in the provider diagnostic. Arbitrary constructors, per-row Guid creation and arbitrary client methods are not supported.

Enums use checked signed Int64 storage. Nullable enum nulls bypass conversion; undefined enum values remain ordinary enum values if representable. Store values outside a narrow underlying type, negative values for unsigned types, and unsigned values above Int64.MaxValue fail instead of wrapping. Integer materialization similarly checks Int64-to-Int32 overflow and rejects incompatible store types rather than parsing strings or truncating fractions.

## Native DateTimeOffset payloads

`DateTimeOffset` and `DateTimeOffset?` payload properties use Neo4j native zoned datetime storage by default, on nodes and identified relationships. The driver preserves the instant, numeric offset and all 100-nanosecond ticks. Null assignment removes an optional property. Values outside .NET's date/offset range, offsets containing seconds and finer-than-tick nanoseconds fail materialization rather than being truncated.

Ordinary LINQ `==`, `!=`, `<`, `<=`, `>` and `>=`, property comparisons and ordering use **UTC instant semantics**, including the null rules above. Generated Cypher normalizes each comparison/order operand to UTC; projection and storage retain the original offset. Equal instants with different offsets therefore compare equal. Normalization may prevent direct index-backed filtering or ordering; there is no performance guarantee for these comparisons.

Fluent Cypher retains **native Cypher semantics**: different offsets or zone identities can distinguish equal instants. Its projections use the same checked decoder. Choose the query surface deliberately when comparing timestamps.

The default EF value comparer detects offset-only assignments so they can be saved. EF still owns tracking; a configured application comparer can change its mutation-detection policy. Reading a named-zone value retains its effective numeric offset, not its IANA zone identifier. An unchanged tracked read does not rewrite it; assigning a new value writes a numeric-offset datetime.

Native temporal keys, foreign keys and concurrency tokens are rejected. Use an application key and an integer version, or a deliberately configured non-temporal representation. Temporal membership (`Contains`), `object.Equals`, date parts, arithmetic and other temporal CLR types remain unsupported. DTO projection, bounded bulk assignment, rollback and integer-version concurrency use the same payload binding.

## Explicit temporal representations

Explicit converters take precedence. Other temporal types need an explicit representation, for example:

| Model value | Example representation | Normalization |
|---|---|---|
| `DateTime?` | `DateTimeToTicksConverter` | Preserves ticks and CLR equality; materializes with Unspecified Kind. |
| `DateTimeOffset?` | UTC ticks in an Int64 | Preserves instant equality; materializes with zero offset. Original offset is not retained. |
| `DateOnly?` | DayNumber in an Int32/Neo4j integer | Preserves the date. |
| `TimeOnly?` | `TimeOnlyToTicksConverter` | Preserves subsecond ticks. |
| `TimeSpan?` | `TimeSpanToTicksConverter` | Preserves duration, including negative values. |

These choices are explicit model policy, not universal time-zone rules. Select a representation that preserves the information your application needs. For example, `DateTimeToTicksConverter` stores equal ticks with different `Kind` values identically, as CLR `DateTime` equality does, and UTC ticks make equal instants with different offsets compare equal, as CLR `DateTimeOffset` equality does. Make sure your representation keeps the CLR equality semantics you rely on.

Arbitrary converters do not prove ordering or range preservation. GraphBridge rejects range comparisons and ordering over properties with explicit converters, and comparisons across incompatible converter instances. Equality correctness also depends on the chosen conversion: a lossy converter can collapse distinct model values. Nullable member access, date-part extraction, arithmetic, lossy numeric casts and arbitrary type coercions are not supported.

## Try it

- Workshop lesson [14 · Nulls, types and time](../../workshop/GraphBridge.Workshop/Lessons/14-NullsTypesTime/README.md): the same question in LINQ and Fluent Cypher, timestamps with offsets, and stored Guids.

Evidence: [scalar conversion boundary](../../tests/verification/VERSION_CAPABILITY_MATRIX.md#scalar-conversion-boundary) and the [named upstream inventory](../../tests/verification/EF_UPSTREAM_INVENTORY.md).
