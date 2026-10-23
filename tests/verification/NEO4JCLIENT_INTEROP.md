# Neo4jClient interoperability

Evidence for [Coming from Neo4jClient](../../docs/COMING_FROM_NEO4JCLIENT.md): what Neo4jClient 5.1.20 stores, whether GraphBridge reads it unchanged, whether Neo4jClient reads what GraphBridge writes, and whether both libraries can share one process.

**Checked 2026-10-23** on the owned disposable Neo4j 2026.09.0 Community fixture, with Neo4jClient **5.1.20** from nuget.org (its dependency is `Neo4j.Driver.Signed` 5.27.0; source read at commit [`a18f7a7`](https://github.com/DotNet4Neo4j/Neo4jClient/tree/a18f7a74d1b424251895796479c13ab94c5e7cac), the last commit, from 19 December 2024) and GraphBridge built from this repository (Neo4j.Driver 6.3.0, EF Core 10.0.13).

## Method

Three throwaway console projects outside `GraphBridge.sln`, so the solution gains no Neo4jClient dependency:

1. **Neo4jClient writer and reader.** Two `BoltGraphClient` instances, one with default settings and one with `useDriverDateTypes: true`, each write one node with `Create("(p:InteropPerson $p)").WithParam("p", person)`, and later read GraphBridge's nodes with `Return(p => p.As<InteropPerson>())`. The class has a `Guid` key, strings, an `int`, an enum, a UTC `DateTime` with sub-second ticks, a `DateTimeOffset` at +02:00 with sub-second ticks, and a null string.
2. **GraphBridge reader and writer.** Two contexts map the same class to the same label: one with the converters the guide shows for default Neo4jClient settings, one with GraphBridge's defaults plus an enum-to-string converter, for `useDriverDateTypes: true`. Each reads the matching Neo4jClient node with a LINQ query and saves one node of its own. The server's `valueType()` of every stored property is recorded.
3. **One process.** A project referencing both Neo4jClient 5.1.20 and GraphBridge passes GraphBridge's `IDriver` to Neo4jClient's `BoltGraphClient(IDriver)` constructor, then writes and reads one node.

A uniqueness constraint on `InteropPerson.Id` was created first; the nodes and the constraint were removed afterwards.

## Results

**What Neo4jClient stores** (server `valueType()`):

| Property | Default settings | `useDriverDateTypes: true` |
|---|---|---|
| `Guid` | `STRING`, lowercase "D" form | same |
| enum | `STRING`, the member name (`SciFi`) | same |
| `DateTime` (UTC) | `STRING`, `1990-05-17T08:30:15.1234567Z` | `ZONED DATETIME` |
| `DateTimeOffset` | `STRING`, `2026-10-23T14:05:06.7654321+02:00` | `ZONED DATETIME`, offset kept |
| `int` | `INTEGER` | same |
| null string | property absent | same |
| Property names | the C# names | same |

**Reading and writing across libraries:**

| Check | Result |
|---|---|
| GraphBridge, with the guide's converters, reads the node Neo4jClient wrote with default settings | Every value equal, including `DateTime` kind and ticks and `DateTimeOffset` offset and ticks |
| Neo4jClient (default settings) reads the node GraphBridge wrote with those converters | Every value equal; GraphBridge stored the same `STRING` forms |
| GraphBridge, with its defaults and an enum-to-string converter, reads the node Neo4jClient wrote with `useDriverDateTypes: true` | Every mapped value equal; `DateTimeOffset` read natively. The `DateTime` property was left out of this model, because GraphBridge has no mapping for a `DateTime` stored as a zoned datetime |
| Neo4jClient (`useDriverDateTypes: true`) reads the node GraphBridge wrote that way | Every value equal, except the `DateTime` that model doesn't write |
| One process, shared driver | Compiles, but fails at run time: only one `Neo4j.Driver.dll` can load (6.3.11.0 was loaded), and Neo4jClient throws `TypeLoadException`: "Method 'GetExecutableQueryBookmarkManager' in type 'Neo4jClient.DriverWrapper' … does not have an implementation" |

## Source findings

From Neo4jClient's source at `a18f7a7`:

- Parameters are converted by [`Neo4jDriverExtensions`](https://github.com/DotNet4Neo4j/Neo4jClient/blob/a18f7a74d1b424251895796479c13ab94c5e7cac/Neo4jClient/Neo4jDriverExtensions.cs): `DateTime` and `DateTimeOffset` with the format `yyyy'-'MM'-'dd'T'HH':'mm':'ss.FFFFFFFK` unless `UseDriverDateTypes` is set, `Guid` as its string, and null properties omitted unless `serializeNullValues` is set.
- Enums are written by name by the client's [default JSON converters](https://github.com/DotNet4Neo4j/Neo4jClient/blob/a18f7a74d1b424251895796479c13ab94c5e7cac/Neo4jClient/BoltGraphClient.cs#L38-L46), and property names come from a default Newtonsoft [`DefaultContractResolver`](https://github.com/DotNet4Neo4j/Neo4jClient/blob/a18f7a74d1b424251895796479c13ab94c5e7cac/Neo4jClient/BoltGraphClient.cs#L48).
- Over Bolt, `WithIdentifier` reaches only the `OperationCompleted` event, and `MaxExecutionTime` is applied only as the HTTP client's `max-execution-time` header ([BoltGraphClient](https://github.com/DotNet4Neo4j/Neo4jClient/blob/a18f7a74d1b424251895796479c13ab94c5e7cac/Neo4jClient/BoltGraphClient.cs#L198-L241), [ResponseBuilder](https://github.com/DotNet4Neo4j/Neo4jClient/blob/a18f7a74d1b424251895796479c13ab94c5e7cac/Neo4jClient/Execution/ResponseBuilder.cs#L91)). `WithQueryStats` reads the summary's counters.

## Not covered

Local and unspecified `DateTime` kinds, `TimeSpan`, `decimal`, collections, nested objects, `[JsonProperty]` renames and custom Neo4jClient converters were not part of this run. The guide makes no claim about them.
