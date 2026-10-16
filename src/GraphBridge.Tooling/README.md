![GraphBridge](https://raw.githubusercontent.com/joslat/GraphBridge.EFCore/main/docs/images/graphbridge-logo-small.png)

# GraphBridge.Tooling

[![NuGet](https://img.shields.io/nuget/vpre/GraphBridge.Tooling?label=nuget&color=0b6ae6)](https://www.nuget.org/packages/GraphBridge.Tooling)
![.NET 10](https://img.shields.io/badge/.NET-10-512bd4)
[![License: MIT](https://img.shields.io/badge/license-MIT-0b6ae6)](https://github.com/joslat/GraphBridge.EFCore/blob/main/LICENSE)
![Developer preview](https://img.shields.io/badge/status-developer%20preview-e8a317)

Optional tools for [GraphBridge](https://www.nuget.org/packages/GraphBridge.EntityFrameworkCore), the Entity Framework Core provider for Neo4j. They work on your real EF model and registered queries, run inside your own application, and are never needed at runtime.

```shell
dotnet add package GraphBridge.Tooling --prerelease
```

## What's inside

| Tool | What it answers | Guide |
|---|---|---|
| **Doctor** | Does my model finalize, do my registered queries compile, and what does the database's schema actually provide? Redacted text or JSON reports. | [Doctor](https://joslat.github.io/GraphBridge.EFCore/tools/DOCTOR.html) |
| **Verify** | Pass/fail evidence with an exit code for build and deployment gates: compile offline, plan read-only, compare real results on a disposable fixture. | [Verify](https://joslat.github.io/GraphBridge.EFCore/tools/VERIFY.html) |
| **Query Lab** | C# source, generated Cypher, the typed graph pattern and captured evidence side by side, as self-contained HTML or in VS Code. | [Query Lab](https://joslat.github.io/GraphBridge.EFCore/tools/QUERY_LAB.html) |
| **Design-time commands** | `list`, `preview` and read-only `status` for your declared graph migration plans. | [Design time](https://joslat.github.io/GraphBridge.EFCore/tools/DESIGN_TIME.html) |
| **Ontology and scaffolding** | Compile an ontology and policy into an EF model, validate RDF with SHACL, inspect an existing graph and propose a reviewed model. | [Command line](https://joslat.github.io/GraphBridge.EFCore/tools/GRAPHBRIDGE_TOOL.html) |

## Doctor in a few lines

```csharp
using GraphBridge.Tooling.Diagnostics;

// Register the queries to check. The factory only builds a Fluent query; Doctor never runs it.
var budget = new GraphQueryBudget(maxRows: 100, deadline: TimeSpan.FromSeconds(10));
var catalog = new GraphQueryCatalog([
    GraphQueryContract.Fluent(new GraphQueryDefinition("workshop.coactors.v1",
        "Distinct co-actor names ordered by name.", "Lessons/16-FluentPatterns#Coactors",
        [new("name", GraphResultKind.String)], GraphResultComparison.Ordered, GraphDuplicatePolicy.Eliminate, budget),
        db => Lesson16FluentPatterns.Coactors(db, "Keanu Reeves"))
]);

// db is your existing DbContext configured with UseNeo4j. Offline: no connection is opened.
var report = GraphDoctor.InspectOffline(db, catalog);
Console.WriteLine(report.ToText());   // or report.ToJson()
return report.Status == GraphEvidenceStatus.Pass ? 0 : 1;
```

## Boundaries

- Nothing here deploys schema, seeds, repairs or cleans a database. Connected inspection reads metadata and `EXPLAIN` plans only; result comparisons run only on a disposable fixture you own.
- Reports exclude parameter values, and Doctor never keeps exception text.
- The components are libraries you host, not installed applications. The ontology and scaffolding command line runs from a repository checkout.

Learn more in the [documentation](https://joslat.github.io/GraphBridge.EFCore/), the [packages and components map](https://joslat.github.io/GraphBridge.EFCore/reference/PACKAGES_AND_COMPONENTS.html) and the [workshop](https://github.com/joslat/GraphBridge.EFCore/blob/main/workshop/README.md). Feedback is welcome in [GitHub issues](https://github.com/joslat/GraphBridge.EFCore/issues). MIT licensed; see the included THIRD_PARTY_NOTICES.md.
