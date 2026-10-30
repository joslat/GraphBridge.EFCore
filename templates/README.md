![GraphBridge](https://raw.githubusercontent.com/joslat/GraphBridge.EFCore/main/docs/images/graphbridge-logo-small.png)

# GraphBridge.Templates

[![NuGet](https://img.shields.io/nuget/vpre/GraphBridge.Templates?label=nuget&color=0b6ae6)](https://www.nuget.org/packages/GraphBridge.Templates)
![dotnet new](https://img.shields.io/badge/dotnet%20new-graphbridge-512bd4)
[![License: MIT](https://img.shields.io/badge/license-MIT-0b6ae6)](https://github.com/joslat/GraphBridge.EFCore/blob/main/LICENSE)
![Developer preview](https://img.shields.io/badge/status-developer%20preview-e8a317)

Start a new [GraphBridge](https://www.nuget.org/packages/GraphBridge.EntityFrameworkCore) project in one command: a console application on Entity Framework Core and Neo4j, plus offline query tests that prove the packages restore, build and translate queries.

```shell
dotnet new install GraphBridge.Templates::<version on this page>
dotnet new graphbridge --name MyGraph --provider-version <GraphBridge.EntityFrameworkCore version>
cd MyGraph
dotnet test Tests/MyGraph.Tests.csproj
```

The tests run offline and need no database.

## What you get

| Path | Content |
|---|---|
| `MyGraph.slnx` | The solution |
| `App/` | A console application pinned to the provider version you chose, EF Core `[10.0.13]` and Neo4j.Driver `[6.4.0]`, with a guarded demo: create, query, update with row version, a Fluent Cypher read, a rolled-back transaction and delete |
| `Tests/` | Two offline xUnit tests: a LINQ query translates to parameterized Cypher, and an unsupported query is refused |
| `Directory.Build.props`, `global.json`, `NuGet.Config` | `net10.0`, warnings as errors, lock files, the pinned SDK and nuget.org as the package source |

The template has no post-actions: it does not restore, build, start Docker or add migrations. The demo application writes, so it runs only against a disposable database it can identify; point it at your own database by replacing its guards with your own setup.

## The GraphRAG starter (experimental)

```shell
dotnet new graphbridge-graphrag --name MyRag --provider-version <GraphBridge.GraphRag version>
cd MyRag
dotnet test Tests/MyRag.Tests.csproj
```

A console application on the experimental [GraphRAG toolkit](https://www.nuget.org/packages/GraphBridge.GraphRag): `setup` applies the schema as your own migration, `ingest` reads a folder of Markdown, `ask` answers with citations checked against the stored bytes, and `cleanup` removes what was ingested. It runs offline with model stand-ins, so it needs no key; register a real `IChatClient` and `IEmbeddingGenerator` in one place to use a model. Its three offline tests check the schema plan, the stand-in embedder and the citation check. The application needs a Neo4j 2026.09 or later database you can write to.

See the [template guide](https://joslat.github.io/GraphBridge.EFCore/tools/TEMPLATE.html), [Getting started](https://joslat.github.io/GraphBridge.EFCore/GETTING_STARTED.html) and the [documentation](https://joslat.github.io/GraphBridge.EFCore/). Remove the template with `dotnet new uninstall GraphBridge.Templates`. MIT licensed.
