# 26 · GraphBridge Doctor

**Goal:** check that your model and registered queries compile, and what the database schema actually provides.
**Track:** ★ core · **Mode:** offline + Neo4j · **Time:** ~10 min · **Builds on:** 03, 16, 17

## Concepts

- A **query contract** declares a query's id, meaning, columns, duplicate policy and budget, plus a factory that builds it.
- **Offline Doctor** compiles the model and every contract without a database. Use it in builds and code review.
- **Connected Doctor** adds read-only schema inspection of the selected database. It never runs registered queries or changes schema.

## Run it

```powershell
dotnet run --project workshop/GraphBridge.Workshop -- run 26
```

## Walkthrough

1. **The catalog.** Two workshop queries from lessons 16 and 17, registered as contracts:

<!-- region: Catalog -->
```csharp
public static GraphQueryCatalog Create()
{
    var budget = new GraphQueryBudget(maxRows: 100, deadline: TimeSpan.FromSeconds(10));
    return new([
        GraphQueryContract.Fluent(new GraphQueryDefinition("workshop.coactors.v1",
            "Distinct co-actor names ordered by name.", "Lessons/16-FluentPatterns#Coactors",
            [new("name", GraphResultKind.String)], GraphResultComparison.Ordered, GraphDuplicatePolicy.Eliminate, budget),
            db => Lesson16FluentPatterns.Coactors(db, "Keanu Reeves")),
        GraphQueryContract.Fluent(new GraphQueryDefinition("workshop.recent-work.v1",
            "Everyone, with any movie after 2010 they acted in; people without one keep a null title.", "Lessons/17-FluentOptionalAndCounts#Optional",
            [new("name", GraphResultKind.String), new("title", GraphResultKind.String, nullable: true)],
            GraphResultComparison.Multiset, GraphDuplicatePolicy.Eliminate, budget),
            db => Lesson17FluentOptionalAndCounts.RecentWork(db))
    ]);
}
```

2. **Offline.**

<!-- region: Offline -->
```csharp
var offline = GraphDoctor.InspectOffline(db, WorkshopContracts.Create());
```

   The report status is `Pass`, and the lesson lists its diagnostics. By default the JSON report contains no Cypher text or parameter values.

3. **Connected.**

<!-- region: Connected -->
```csharp
var connected = await GraphDoctor.InspectAsync(db, WorkshopContracts.Create(), "workshop.local.v1", cancellationToken: cancellationToken);
```

## What you should see

`Status: Pass` twice, each with its diagnostics table. Connected warnings, such as application-enforced requiredness on Community edition, are listed but do not fail the report. `workshop.local.v1` is a profile id: your name for the environment the evidence describes.

## Try it yourself

1. Print `offline.ToText()` in full. Which sections does it contain?
2. Register a contract whose factory throws. How does Doctor report it?

## Limits

- Doctor observes; it never repairs schema, runs registered results or certifies production readiness.

## Read more

[GraphBridge Doctor](../../../../docs/tools/DOCTOR.md) · [GraphBridge Verify](../../../../docs/tools/VERIFY.md)
