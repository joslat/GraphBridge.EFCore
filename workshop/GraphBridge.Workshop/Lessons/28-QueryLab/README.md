# 28 · Query Lab

**Goal:** produce a Query Lab report that shows each query's C#, its Cypher and its typed graph pattern side by side.
**Track:** ★ core · **Mode:** offline · **Time:** ~10 min · **Builds on:** 26

## Concepts

- **Query Lab** renders captured evidence: the C# source of each contract, its generated Cypher, the typed pattern and clause scopes, and any captured plans.
- The HTML report is self-contained: no server, no JavaScript dependencies, nothing runs queries.
- The same evidence can be exported as JSON for the **VS Code Query Lab** viewer.

## Run it

```powershell
dotnet run --project workshop/GraphBridge.Workshop -- run 28
```

The lesson writes `query-lab.html` and `query-lab.json` to `%TEMP%/GraphBridgeWorkshop` (or `$TMPDIR/GraphBridgeWorkshop`) and prints both paths.

## Walkthrough

1. **The HTML report.**

<!-- region: Report -->
```csharp
var verification = GraphVerify.Offline(db, WorkshopContracts.Create(), includeStatements: true);
var html = GraphQueryLab.RenderHtml(verification, WorkshopContracts.Sources());
var htmlPath = Path.Combine(folder, "query-lab.html");
await File.WriteAllTextAsync(htmlPath, html, cancellationToken);
```

   Open the file in a browser. Each contract shows its C# excerpt (the exact source region from lessons 16 and 17), the Cypher, and a diagram of the **query pattern**: variables and directions, not database rows.

2. **The VS Code envelope.**

<!-- region: Json -->
```csharp
var envelope = JsonSerializer.Serialize(new
{
    kind = "graphbridge-query-lab",
    formatVersion = 1,
    report = JsonSerializer.Deserialize<JsonElement>(verification.ToJson()),
    sources = WorkshopContracts.Sources().Select(s => new { contractId = s.ContractId, text = s.Text })
}, new JsonSerializerOptions { WriteIndented = true });
var jsonPath = Path.Combine(folder, "query-lab.json");
await File.WriteAllTextAsync(jsonPath, envelope, cancellationToken);
```

   Build and install the extension from `extensions/graphbridge-query-lab` (see its README), then run **GraphBridge Query Lab: Open Saved Evidence** and pick `query-lab.json`.

## What you should see

Four passing checkpoints and two file paths.

## Try it yourself

1. In the HTML report, find where the OPTIONAL MATCH predicate of `workshop.recent-work.v1` is attached.
2. Change `Coactors` in lesson 16, rerun this lesson and refresh the report.

## Limits

- Statements are included deliberately here; parameter values never are. The extension is built locally and is not published to the Marketplace.

## Read more

[Query Lab](../../../../docs/tools/QUERY_LAB.md) · [VS Code extension](../../../../extensions/graphbridge-query-lab/README.md)
