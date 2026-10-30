# Packages and components

GraphBridge has one runtime provider package and four optional packages on NuGet, and five experimental GraphRAG packages, first published with 0.2.0-preview.1. The diagnostic tools (Doctor, Verify and Query Lab) are components inside the optional `GraphBridge.Tooling` package, not separate packages. The packages are prereleases on nuget.org; `GraphBridge.EntityFrameworkCore.VectorData` is first published with 0.1.0-preview.3. [Getting started](../GETTING_STARTED.md#install-from-a-local-feed) shows how to try unreleased changes from a local feed.

| Package ID | Purpose | Dependency / installation boundary |
|---|---|---|
| `GraphBridge.EntityFrameworkCore` | Non-relational EF Core provider: LINQ, tracked async persistence, graph mapping, Fluent Cypher, native access and explicit schema/plan APIs | Application runtime; depends on EF Core and Neo4j.Driver. Does not depend on the other GraphBridge packages. |
| [`GraphBridge.EntityFrameworkCore.VectorData`](../features/VECTOR_DATA.md) | Optional `Microsoft.Extensions.VectorData` adapter: search existing vector indexes through `IVectorSearchable<TRecord>` | References the provider and `Microsoft.Extensions.VectorData.Abstractions` 10.11.0 or later. The provider itself never takes this dependency. |
| `GraphBridge.Tooling` | Optional model authoring, ontology/schema-first generation, database-first inspection/scaffolding, design-time helpers and diagnostic evidence | References the provider and RDF/YAML/Roslyn libraries. Install in a trusted application or development host when needed. No CLI executable is bundled. |
| [`GraphBridge.Generators`](../tools/MAPPING_GENERATOR.md) | Optional incremental mapping generator and build-time authoring diagnostics | Roslyn analyzer asset; not an application runtime assembly or runtime provider dependency. |
| [`GraphBridge.Templates`](../tools/TEMPLATE.md) | Optional `dotnet new` starters: `graphbridge`, a package-based application with contract tests, and the experimental `graphbridge-graphrag`, which ships with the GraphRAG packages | Template content; generated applications reference the provider separately. No server or runtime libraries are bundled in the template. |
| [`GraphBridge.GraphRag`](../../src/GraphBridge.GraphRag/README.md) | **Experimental; on NuGet from 0.2.0-preview.1.** GraphRAG building blocks: documents and chunks with exact positions, resumable ingestion, retrieval, answers with checked citations, ontologies and extraction, maintenance, agent tools and re-embedding | References the provider and `Microsoft.Extensions.AI.Abstractions` 10.11.0 or later. Reports `GBRAG001`. First published with 0.1.0-preview.5. |
| [`GraphBridge.GraphRag.DataIngestion`](../../src/GraphBridge.GraphRag.DataIngestion/README.md) | **Experimental; on NuGet from 0.2.0-preview.1.** A `Microsoft.Extensions.DataIngestion` chunk writer that stores pipeline chunks with exact positions, and a span-aware chunker | References `GraphBridge.GraphRag` and the `Microsoft.Extensions.DataIngestion` 10.11.0 preview. |
| [`GraphBridge.GraphRag.AgentFramework`](../../src/GraphBridge.GraphRag.AgentFramework/README.md) | **Experimental; on NuGet from 0.2.0-preview.1.** A Microsoft Agent Framework context provider whose answers carry checked citations | References `GraphBridge.GraphRag` and `Microsoft.Agents.AI.Abstractions` 1.26.0 or later. |
| [`GraphBridge.GraphRag.AspNetCore`](../../src/GraphBridge.GraphRag.AspNetCore/README.md) | **Experimental; on NuGet from 0.2.0-preview.1.** ASP.NET Core endpoints: ask with a streamed answer whose citations are checked, search passages, read a document's metadata | References `GraphBridge.GraphRag` and the ASP.NET Core shared framework; no other package. Secure the endpoints with your own authentication and rate limits. |
| [`GraphBridge.GraphRag.Pdf`](../../src/GraphBridge.GraphRag.Pdf/README.md) | **Experimental; on NuGet from 0.2.0-preview.1.** A PDF loader: a PDF's text layer, page by page, with a page map so citations name their pages | References `GraphBridge.GraphRag` and PdfPig (Apache-2.0); no PDF dependency in the core. |

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
