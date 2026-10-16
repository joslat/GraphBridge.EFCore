# 03 · Schema before data

**Goal:** inspect the database schema, review the provider's proposals and apply them explicitly.
**Track:** ★ core · **Mode:** needs Neo4j · **Time:** ~10 min · **Builds on:** 02

## Concepts

- GraphBridge **never creates schema on its own**: not on startup, not during queries, not during `SaveChangesAsync`.
- `SaveChangesAsync` requires a uniqueness constraint for every application key. Without them, writes are refused.
- Inspection reads metadata only and returns reviewable proposals. Applying them is a deliberate application step.

## Run it

```powershell
dotnet run --project workshop/GraphBridge.Workshop -- run 03
```

## Walkthrough

1. **Inspect.**

<!-- region: Inspect -->
```csharp
var inspection = await db.Database.InspectGraphSchemaAsync(cancellationToken: cancellationToken);
var issues = inspection.Comparison.Issues
    .GroupBy(i => (i.Status, i.Code))
    .Select(g => new { g.Key.Status, g.Key.Code, Count = g.Count() });
var proposals = inspection.Comparison.Proposals.Select(p => new { p.Name, p.Kind, p.LabelOrType, p.IsRequired });
```

   On an empty database the status is `Fail`: each node key and the `ACTED_IN` relationship key lacks its uniqueness constraint. Each issue comes with a proposal such as:

<!-- snapshot: 03-first-proposal-shape -->
```cypher
CREATE CONSTRAINT `<name>` FOR (n:`Review`) REQUIRE (n.`id`) IS UNIQUE
```

2. **Apply deliberately.** The workshop host runs each required proposal through the driver and prints how many it applied. In your own application, this belongs in a reviewed deployment step or migration plan (lesson 23), not in application startup.

3. **Inspect again.** No required proposals remain. The status becomes `Pass` or `Warn`. The issues are grouped by status and code: `Fail` issues block writes, `Warn` issues do not. On Community edition most warnings say that requiredness and property types are enforced by the application, because those constraints need Enterprise edition.

## What you should see

The first run shows status `Fail`, then `Warn` or `Pass` after the constraints are applied. On later runs the constraints already exist, so nothing is applied.

## Try it yourself

1. Open Neo4j Browser and run `SHOW CONSTRAINTS`. Match each constraint to a manifest key from lesson 02.
2. In the grouped issue table, which codes are `Warn`? Which of them would an Enterprise server let you enforce with constraints?

## Limits

- A uniqueness constraint does not make a property required, and inspection never scans data for duplicates or missing values.
- Enterprise property-existence and type constraints are proposed only where the server supports them.

## Read more

[Schema preflight](../../../../docs/features/SCHEMA_AND_MIGRATIONS.md) · [Compatibility: version and edition detection](../../../../docs/reference/COMPATIBILITY.md#version-and-edition-capability-detection)
