# 24 · Deployment preflight and command tracing

**Goal:** gate a deployment on the schema the model needs, and observe every database command without leaking data.
**Track:** full · **Mode:** needs Neo4j · **Time:** ~10 min · **Builds on:** 03, 13

## Concepts

- `InspectGraphSchemaAsync` is a read-only, metadata-only check. A release pipeline can fail when the status is `Fail`.
- GraphBridge reports every operation through a .NET `ActivitySource` named `GraphBridge.EntityFrameworkCore`, which OpenTelemetry can collect.
- Activities carry kinds, counts, fingerprints and outcomes. They never carry statement text, parameter values, database names or exception messages.

## Run it

```powershell
dotnet run --project workshop/GraphBridge.Workshop -- run 24
```

## Walkthrough

1. **The preflight gate.**

<!-- region: Preflight -->
```csharp
var inspection = await db.Database.InspectGraphSchemaAsync(cancellationToken: cancellationToken);
var deploymentExitCode = inspection.Comparison.Status == GraphSchemaStatus.Fail ? 1 : 0;   // warnings do not block
```

2. **Listen to activities.**

<!-- region: Listener -->
```csharp
var activities = new ConcurrentQueue<Activity>();
using var listener = new ActivityListener
{
    ShouldListenTo = source => source.Name == GraphBridgeDiagnostics.ActivitySourceName,
    Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
    ActivityStopped = activities.Enqueue
};
ActivitySource.AddActivityListener(listener);
```

   The lesson then runs a LINQ query for `Keanu Reeves` and two saves. The table lists each activity: `GraphBridge.Query`, `GraphBridge.SaveChanges`, transaction and statement activities, with their command kind and a statement fingerprint. The name searched for appears in no tag.

## What you should see

The gate passes, and an activity table in which no value contains "Keanu Reeves".

## Try it yourself

1. Add the OpenTelemetry console exporter to a copy of this program and subscribe to `GraphBridge.EntityFrameworkCore`.
2. Group the activities by `graphbridge.statement.fingerprint`. Which statements ran more than once?

## Limits

- Fingerprints identify provider-generated statements, not your native Cypher text.
- The EF command log (category `Microsoft.EntityFrameworkCore.Database.Command`) is a separate channel; treat it as sensitive.

## Read more

[Command tracing](../../../../docs/features/COMMAND_TRACING.md) · [Schema preflight](../../../../docs/features/SCHEMA_AND_MIGRATIONS.md)
