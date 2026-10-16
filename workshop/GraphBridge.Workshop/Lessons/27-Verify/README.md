# 27 · GraphBridge Verify

**Goal:** turn registered query contracts into pass/fail evidence: compile offline, then plan against a real database.
**Track:** full · **Mode:** offline + Neo4j · **Time:** ~10 min · **Builds on:** 26

## Concepts

- **V0** compiles every registered contract offline. It fits a build or a pull-request check.
- **V1** adds read-only schema inspection and `EXPLAIN` for every contract: the database plans each query without running it.
- Each report has a status and an exit code (0 pass, 1 not passing), so it can gate a pipeline.

## Run it

```powershell
dotnet run --project workshop/GraphBridge.Workshop -- run 27
```

## Walkthrough

1. **V0.**

<!-- region: Offline -->
```csharp
var offline = GraphVerify.Offline(db, WorkshopContracts.Create());
```

2. **V1.**

<!-- region: Connected -->
```csharp
var connected = await GraphVerify.InspectAsync(db, WorkshopContracts.Create(), "workshop.local.v1", cancellationToken: cancellationToken);
```

   The profile id names the environment the evidence is about. V1 never runs registered results, `PROFILE` or DDL.

## What you should see

`Status: Pass` and exit code 0 twice.

## Try it yourself

1. Print `connected.ToText()`. Find the captured plan operators for `workshop.coactors.v1`.
2. In lesson 16's `Coactors`, change `.Limit(25)` to `.Limit(50)` and rerun this lesson. Does the statement fingerprint change? Why not? Change it back afterwards.

## Limits

- V2, which compares results with independent native references, needs an explicitly provisioned disposable fixture and is a maintainer tool.
- Plans are observations of one server and configuration, not performance guarantees.

## Read more

[GraphBridge Verify](../../../../docs/tools/VERIFY.md) · [GraphBridge Doctor](../../../../docs/tools/DOCTOR.md)
