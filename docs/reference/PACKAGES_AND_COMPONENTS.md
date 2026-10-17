# Packages and components

GraphBridge has one runtime provider package and four optional packages. The diagnostic tools (Doctor, Verify and Query Lab) are components inside the optional `GraphBridge.Tooling` package, not separate packages. The packages are prereleases on nuget.org; `GraphBridge.EntityFrameworkCore.VectorData` is first published with 0.1.0-preview.3. [Getting started](../GETTING_STARTED.md#install-from-a-local-feed) shows how to try unreleased changes from a local feed.

| Package ID | Purpose | Dependency / installation boundary |
|---|---|---|
| `GraphBridge.EntityFrameworkCore` | Non-relational EF Core provider: LINQ, tracked async persistence, graph mapping, Fluent Cypher, native access and explicit schema/plan APIs | Application runtime; depends on EF Core and Neo4j.Driver. Does not depend on the other GraphBridge packages. |
| [`GraphBridge.EntityFrameworkCore.VectorData`](../features/VECTOR_DATA.md) | Optional `Microsoft.Extensions.VectorData` adapter: search existing vector indexes through `IVectorSearchable<TRecord>` | References the provider and `Microsoft.Extensions.VectorData.Abstractions` 10.11.0 or later. The provider itself never takes this dependency. |
| `GraphBridge.Tooling` | Optional model authoring, ontology/schema-first generation, database-first inspection/scaffolding, design-time helpers and diagnostic evidence | References the provider and RDF/YAML/Roslyn libraries. Install in a trusted application or development host when needed. No CLI executable is bundled. |
| [`GraphBridge.Generators`](../tools/MAPPING_GENERATOR.md) | Optional incremental mapping generator and build-time authoring diagnostics | Roslyn analyzer asset; not an application runtime assembly or runtime provider dependency. |
| [`GraphBridge.Templates`](../tools/TEMPLATE.md) | Optional `dotnet new` starter with a package-based application and contract tests | Template content; generated applications reference the provider separately. No server or runtime libraries are bundled in the template. |

Use the same GraphBridge version for every package, and pin the tested EF Core and driver versions. NuGet dependency ranges can allow newer versions than the [compatibility contract](COMPATIBILITY.md) covers; resolving one is not a support claim. Upgrade notes are in the [changelog](../../CHANGELOG.md).

## Components are not automatically separate packages

| Product component | Current implementation / location | User surface |
|---|---|---|
| **[GraphBridge Doctor](../tools/DOCTOR.md)** | `GraphBridge.Tooling.Diagnostics.GraphDoctor` in `GraphBridge.Tooling` | C# API; application-hosted console commands; JSON and plain-text evidence. Offline model/query compilation and explicit read-only schema/profile inspection. |
| **[GraphBridge Verify](../tools/VERIFY.md)** | `GraphBridge.Tooling.Diagnostics.GraphVerify` in `GraphBridge.Tooling` | V0 offline compilation and V1 explicit read-only inspection/EXPLAIN, JSON/text and required-contract exit codes. V2 compares registered cases against independent native references and fixed expectations on an explicitly owned disposable fixture; no separate package. |
| **[GraphBridge Query Lab](../tools/QUERY_LAB.md)** | `GraphBridge.Tooling.Diagnostics.GraphQueryLab` in `GraphBridge.Tooling` | Self-contained HTML/CSS/SVG reports of captured source, Cypher, typed patterns/scopes and optional plans/results. Opens in a browser without a server or JavaScript; rendering executes no queries. |
| Query Lab for VS Code | [`extensions/graphbridge-query-lab`](../../extensions/graphbridge-query-lab/README.md) | Optional extension that views saved Doctor, Verify and Query Lab evidence, and can capture it through an explicit application-host bridge. Built locally; not on the Marketplace. |
| [Source authoring CLI](../tools/GRAPHBRIDGE_TOOL.md) | `src/GraphBridge.Tool`, currently `IsPackable=false` | Run from source. It is not an installable global `dotnet tool`, nor a generic application assembly loader. |

Doctor answers: "Can this model and these queries compile, and what did metadata inspection actually establish about this selected database?" It does not repair schema, run migrations, execute registered query results, certify production readiness or own application state.

## Where the tools run

Doctor and Verify run inside a trusted host that already knows how to construct the application's context and register its queries: a diagnostic command in your application, a sample host in an IDE terminal, or a deployment check that explicitly selects the intended configuration and read-only credentials. Do not attach them to every application request or run connected inspection on ordinary startup. There is no standalone installable Doctor or Verify command.

The headless APIs and [Query Lab HTML reports](../tools/QUERY_LAB.md) work on their own. The VS Code extension presents the same evidence with filtering and navigation; it adds no UI dependency to either .NET package, and execution and credentials stay in your application host.
