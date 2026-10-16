# Bounded directed paths

`db.Database.QueryPathsAsync` follows relationships from one exact start node for a bounded number of hops and returns typed, untracked paths. Direction, maximum depth and result limit are mandatory, and `HasMorePaths` tells you when the limit cut the result. Use it for neighborhood and reachability questions where you need an exact anchor and an honest "there is more".

```csharp
using GraphBridge.EntityFrameworkCore.Paths;
using Microsoft.EntityFrameworkCore;
using Neo4j.Driver;

var pattern = new GraphPathPattern("Person", ["FOLLOWS"]);
var result = await db.Database.QueryPathsAsync(pattern,
    new Dictionary<string, object?> { ["id"] = personId.ToString("D") },   // stored names and values
    GraphTraversalDirection.Outgoing, maxDepth: 3, resultLimit: 25,
    projectNode: node => node["name"].As<string>(), projectRelationship: edge => edge.Type,
    cancellationToken: cancellationToken);

foreach (var path in result.Paths)
    Console.WriteLine(string.Join(" -> ", path.Nodes));
Console.WriteLine($"Start found: {result.StartFound}; more paths: {result.HasMorePaths}");
```

The result is a `GraphPathResults<string, string>`: `Paths`, `StartFound` and `HasMorePaths`. Each `GraphPath` has ordered `Nodes`, `Relationships` and `StepDirections`.

## Pattern and anchor

`GraphPathPattern(startLabel, relationshipTypes)` names a native start label and an allowlist of 1 to 16 relationship types. These are native graph names, validated as identifiers (`[A-Za-z_][A-Za-z0-9_]*`); they do not use or change EF mappings.

The anchor dictionary holds 1 to 16 **stored** property names and values. Values must be non-null `string`, `bool`, `int`, `long` or finite `double` in store representation, so convert a Guid key with `ToString("D")` yourself. Anchor values become parameters. EF value converters, global query filters and change tracking do not apply. Anchor on a selective, indexed application key; nothing creates an index for you.

| Anchor matches | Result |
|---|---|
| No node | `StartFound = false`, no paths, no exception |
| Exactly one node | `StartFound = true`; an isolated node has no paths of one hop or more |
| Two or more nodes | Throws `InvalidOperationException` before any projector runs; no arbitrary node is picked |

## Traversal and results

| Parameter | Rule |
|---|---|
| `direction` | `Outgoing` follows stored edge direction, `Incoming` the reverse, `Both` either at each hop. `StepDirections` records each edge's orientation along the returned path; a self-loop reports `Incoming` only when you asked for incoming. |
| `maxDepth` | 0 to 64. |
| `minDepth` | Defaults to 1. Pass `minDepth: 0` to include the start-only path; `maxDepth: 0, minDepth: 0` returns exactly that path when the anchor exists. |
| `resultLimit` | 1 to 10,000. One extra path is read to set `HasMorePaths`; it is never projected or returned. Exactly `resultLimit` paths with none beyond reports `false`. |
| `projectNode`, `projectRelationship` | Your projections from the driver's `INode` and `IRelationship`. A node that repeats is projected once per occurrence, without EF identity resolution. |

- Nodes may repeat on a path, but a physical relationship is used at most once per path, as in native Cypher 5. Cycles therefore end on their own; parallel edges remain distinct paths; self-loops are kept.
- Path order is unspecified. This is not a shortest-path or weighted-path API, and a capped result is an observed subset, not the best routes.
- Every buffered path, including the extra one, is checked for depth, structure, relationship types, edge uniqueness and orientation before your projectors run. Results stay usable after the driver resources are released.

## Cost, transactions and errors

Depth and path count are bounded; total expansion work, property bytes, projector allocations and elapsed time are not. Large fan-out and unindexed anchors can still be expensive, so keep depth and limit proportionate and use a server transaction timeout. Up to `resultLimit + 1` paths are buffered before projection.

Transactions, cancellation and projector failures follow [native Cypher](NATIVE_CYPHER.md#transactions): an explicit EF transaction is borrowed and left to you, otherwise the call owns its transaction. There is no automatic retry, implicit schema change or client-side traversal. The call is traced as `GraphBridge.Path` with value-free [command tracing](COMMAND_TRACING.md).

For a path inside a larger typed pattern, use Fluent Cypher's [`Repeat`](FLUENT_CYPHER.md#bounded-paths). For traversal forms outside both APIs, write a reviewed [native query](NATIVE_CYPHER.md#graph-values-and-paths) that returns `IPath`.

## Try it

- Workshop lesson [19 · Bounded paths](../../workshop/GraphBridge.Workshop/Lessons/19-BoundedPaths/README.md): Fluent `Repeat`, `QueryPathsAsync`, a cut result and a missing anchor.
- Sample [05 Fluent Cypher gallery](../../samples/05-FluentCypher/README.md): bounded paths in Fluent Cypher.

Evidence: [verification summary](../../tests/verification/VERIFICATION_SUMMARY.md) and [capability and evidence map](../../tests/verification/CAPABILITY_EVIDENCE.md). Design: [ADR-0034](../architecture/adr/ADR-0034.md).
