# Command tracing

GraphBridge reports every database operation as .NET `Activity` spans from an `ActivitySource` named `GraphBridge.EntityFrameworkCore`. Spans carry kinds, counts, fingerprints and outcomes, never statement text or values. Use them to see what your application sends to Neo4j, how long it takes and where it fails.

Tracing is opt-in: the application subscribes to the source. With OpenTelemetry, add `.AddSource(GraphBridgeDiagnostics.ActivitySourceName)` to your `TracerProviderBuilder`. Without it, a plain listener works:

```csharp
using System.Collections.Concurrent;
using System.Diagnostics;
using GraphBridge.EntityFrameworkCore.Diagnostics;

var activities = new ConcurrentQueue<Activity>();
using var listener = new ActivityListener
{
    ShouldListenTo = source => source.Name == GraphBridgeDiagnostics.ActivitySourceName,
    Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
    ActivityStopped = activities.Enqueue
};
ActivitySource.AddActivityListener(listener);
```

A LINQ query then produces `GraphBridge.Query → GraphBridge.Statement → GraphBridge.Command.Run`, and `SaveChangesAsync` produces `GraphBridge.SaveChanges` with its schema check and mutation statements and the transaction spans. The provider installs no exporter or collector; your application owns collection, sampling and export. The name constants are in `GraphBridge.EntityFrameworkCore.Diagnostics.GraphBridgeDiagnostics`. This follows the [.NET library instrumentation model](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/distributed-tracing-instrumentation-walkthroughs).

## Spans

Every span inherits the caller's trace context. The ambient activity is restored between streaming callbacks and after awaited operations, so unrelated application work is not parented to an idle cursor.

| Operation name | Kind | Duration |
|---|---|---|
| `GraphBridge.Query` | Internal | A LINQ query from first enumeration to exhaustion, early disposal or failure cleanup; includes consumer pauses and materialization. |
| `GraphBridge.SaveChanges` | Internal | The provider's persistence call, including planning, owned commit and cleanup. A borrowed transaction is committed later by its caller. |
| `GraphBridge.Bulk` | Internal | One `ExecuteUpdateAsync` or `ExecuteDeleteAsync` call. |
| `GraphBridge.NativeQuery` | Internal | One `QueryCypherAsync` call through result processing and cleanup. Fluent Cypher queries run through it. |
| `GraphBridge.Search`, `GraphBridge.Path` | Internal | One search or `QueryPathsAsync` call, including its metadata checks. |
| `GraphBridge.Explain` | Internal | One `ExplainCypherAsync` call. |
| `GraphBridge.SchemaInspect` | Internal | Runtime schema inspection through cleanup. |
| `GraphBridge.Migration` | Internal | One migration transport operation; initialization, validation and application can create separate spans. |
| `GraphBridge.CanConnect` | Internal | A connectivity probe. A handled driver failure that returns false still marks the span as an error. |
| `GraphBridge.Statement` | Internal | One statement from submission through cursor exhaustion, consumption, early disposal or failure. Consumer pauses count. |
| `GraphBridge.Command.Run` | Client | The awaited driver submission only. |
| `GraphBridge.Transaction.Begin`, `.Commit`, `.Rollback` | Client | The awaited driver call, including implicit rollback during disposal. |

These are wall-clock durations, not server CPU time. There is no span or event per returned row. Dispose an unfinished async enumerator to finish its query span and release its session. Translation errors before enumeration, EF interceptors and driver calls your own code makes are outside this source.

Sampled statement spans carry the events `GraphBridge.Command.Starting`, `GraphBridge.Command.Submitted`, then exactly one of `GraphBridge.Command.Completed` or `GraphBridge.Command.Failed`; a submission failure omits `Submitted`. Events have no payload. All-data sampling records tags and events; propagation-only sampling keeps context with no GraphBridge tags or events.

## Tags

| Tag | Values |
|---|---|
| `db.system.name` | `neo4j` |
| `graphbridge.command.kind` | `linq`, `schema_preflight`, `mutation`, `native`, `explain`, `migration`, `schema`, `connectivity` |
| `graphbridge.command.stage` | `run` |
| `graphbridge.parameter.count` | Number of top-level parameters on a statement span. |
| `graphbridge.parameter.<category>.count` | Counts per category present: `null`, `boolean`, `string`, `integer`, `floating`, `map`, `collection`, `other`. Values are never read beyond their type. |
| `graphbridge.statement.fingerprint` | SHA-256 of provider-generated statement text, for kinds `linq`, `mutation`, `schema_preflight`, `schema` and `connectivity`. Never for `native`, `explain` or `migration` statements, which include Fluent Cypher, path and search queries. |
| `graphbridge.commit.outcome` | `acknowledged` when the driver commit returns, `unconfirmed` when it throws. Neither authorizes a replay. |
| `error.type` | `canceled`, `transient`, `driver_error`, `concurrency`, `operation_error`: fixed categories, never exception names or messages. |

A failure sets `ActivityStatusCode.Error` without a description or exception event; success stays `Unset`. The first failure category on a span is kept when cleanup also fails. Tracing does not change exceptions, cleanup or retry behavior: a `transient` category is not a retry policy. See [transactions and concurrency](TRANSACTIONS_AND_CONCURRENCY.md) before retrying anything.

## Privacy

GraphBridge spans contain no statement text, parameter names or values, database name, connection URI, exception message or stack, credentials, migration identity or entity identifiers, even with EF sensitive-data logging enabled. Parameter counts and fingerprints still reveal query structure; fingerprints are for correlation, not anonymization. Collectors, other listeners and baggage you add can carry their own data.

EF Core's command log (category `Microsoft.EntityFrameworkCore.Database.Command`) is a separate channel that can include generated Cypher; treat it as sensitive. `ToQueryString()` and EXPLAIN plans are explicit [inspection APIs](QUERY_INSPECTION.md), not telemetry.

Span names and tags are a preview contract; changes are recorded in the [changelog](../../CHANGELOG.md).

## Try it

- Workshop lesson [24 · Deployment preflight and command tracing](../../workshop/GraphBridge.Workshop/Lessons/24-PreflightAndTracing/README.md): listens to the source and shows that a searched name appears in no tag.

Evidence: [capability and evidence map](../../tests/verification/CAPABILITY_EVIDENCE.md). Design: [ADR-0028](../architecture/adr/ADR-0028.md).
