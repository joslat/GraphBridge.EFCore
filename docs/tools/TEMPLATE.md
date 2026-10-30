# `dotnet new graphbridge` template

The `GraphBridge.Templates` package adds a `dotnet new graphbridge` template that creates a small solution referencing GraphBridge from **packages**, not from a source checkout: a console application and offline query contract tests. Use it to start a fresh project and to confirm that the packages restore, build and translate queries. Install it from nuget.org:

```powershell
dotnet new install GraphBridge.Templates::0.2.0-preview.1
dotnet new graphbridge --name MyGraph --provider-version 0.2.0-preview.1
```

To try changes that are not released yet, pack the provider and the template into a local feed instead:

```powershell
# From the repository root: pack the provider and the template into one local feed.
$feed = Join-Path ([IO.Path]::GetTempPath()) 'graphbridge-feed'
dotnet pack src/GraphBridge.EntityFrameworkCore/GraphBridge.EntityFrameworkCore.csproj -o $feed
dotnet pack templates/GraphBridge.Templates.csproj -o $feed

# Install the template and create a solution.
dotnet new install (Join-Path $feed 'GraphBridge.Templates.0.2.0-preview.1.nupkg')
dotnet new graphbridge --name MyGraph --provider-version 0.2.0-preview.1
```

Either way, this creates a `MyGraph` folder. `--provider-version` must name a provider version that exists on nuget.org or in your feed; locally packed packages take the source version `0.2.0-preview.1` unless you pack with `-p:PackageVersion=…`.

## Options

| Option | Required | Meaning |
|---|---|---|
| `--provider-version` | Yes | Exact `GraphBridge.EntityFrameworkCore` version available on nuget.org or in your feed. It is pinned with `[version]`. |
| `--name`, `--output` | No | Standard `dotnet new` options. `--name` replaces `GraphBridgeStarter` in file names, namespaces and content. |

The template has no post-actions: it does not restore, build, start Docker or add migrations.

## What it generates

| Path | Content |
|---|---|
| `MyGraph.slnx` | Solution with the two projects. |
| `App/MyGraph.csproj` | Console application pinning `GraphBridge.EntityFrameworkCore` to your version, `Microsoft.EntityFrameworkCore` to `[10.0.13]` and `Neo4j.Driver` to `[6.4.0]`. |
| `App/Program.cs` | A guarded CRUD demo: create, parameterized read, update with row version, a Fluent Cypher read, an explicitly rolled-back transaction, delete and an absence check. It also defines `SmokeContext` and `SmokeItem`. |
| `Tests/MyGraph.Tests.csproj`, `Tests/QueryContractTests.cs` | Two offline xUnit tests: a LINQ query translates to parameterized Cypher, and an unsupported query throws `Neo4jQueryTranslationException`. A proxy driver fails any transport call. |
| `Directory.Build.props` | `net10.0`, nullable, implicit usings, warnings as errors, lock files enabled. |
| `Directory.Packages.props`, `NuGet.Config` | Central package management off; nuget.org as the only source until you add your feed. |
| `global.json`, `LICENSE`, `README.md` | The pinned .NET SDK 10.0.401, the MIT license and run instructions. |

`App/Program.cs` is the same consumer as [`eng/consumers/ProviderLiveConsumer.cs`](../../eng/consumers/ProviderLiveConsumer.cs), which the [package quickstart](../GETTING_STARTED.md) walks through.

## Build and run

From the new folder, restore, build and test. If you packed into a local feed, first add it with `dotnet nuget add source $feed --name GraphBridgePreview --configfile NuGet.Config`.

```powershell
cd MyGraph
dotnet restore MyGraph.slnx --configfile NuGet.Config
dotnet build MyGraph.slnx --no-restore
dotnet test Tests/MyGraph.Tests.csproj --no-build
```

The tests are offline and need no database. Running the application needs a database:

```powershell
dotnet run --project App/MyGraph.csproj --no-build
```

The demo writes, so it refuses to run anywhere except a disposable fixture. It requires `GRAPHBRIDGE_INTEGRATION=1`, `GRAPHBRIDGE_NEO4J_URI=bolt://127.0.0.1:17687`, `GRAPHBRIDGE_NEO4J_DATABASE=neo4j`, `GRAPHBRIDGE_NEO4J_USERNAME`, `GRAPHBRIDGE_NEO4J_PASSWORD` and `GRAPHBRIDGE_FIXTURE_ID`, matching a `GraphBridgeFixture` marker node, on the pinned Neo4j 2026.09.0 Community server. The repository's [disposable fixture](../../eng/README.md) provides all of these. Without them, the application throws before connecting. It creates the uniqueness constraint `graphbridge_package_smoke_id` if missing and deletes only the node it created; the constraint stays until the fixture is removed. Never put credentials in source or configuration files.

To point the application at your own database, replace the guards and the demo with your own setup, ownership and schema policy.

## The GraphRAG starter (experimental)

The templates package adds `dotnet new graphbridge-graphrag`, a starter for the experimental [GraphRAG toolkit](../graphrag/README.md). It comes in the same templates package as `dotnet new graphbridge`; pass the version of the GraphRAG packages, which is the provider's:

```powershell
dotnet new install GraphBridge.Templates::0.2.0-preview.1
dotnet new graphbridge-graphrag --name MyRag --provider-version 0.2.0-preview.1
```

To try unreleased changes, pack the provider, `GraphBridge.GraphRag` and `templates/GraphBridge.Templates.csproj` into a local feed with one version, as the [quick start](../graphrag/QUICK_START.md#0-get-the-packages-and-a-database) shows, and install the template from its `.nupkg` there. The starter's `NuGet.Config` lists only nuget.org, so add the feed to it before you restore: `dotnet nuget add source $feed --name graphbridge-local --configfile NuGet.Config`.

- **`App/`:** a console application with `setup`, `ingest`, `ask` and `cleanup`, as in [sample 07](../../samples/07-GraphRagQuickStart/README.md). It runs offline with model stand-ins (`App/StandIns.cs`); register a real `IChatClient` and `IEmbeddingGenerator` in `Starter.AddStarter` to use a model. `App/docs` holds one page to ingest.
- **`Tests/`:** three offline tests: the schema plan, the stand-in embedder, and the citation check.
- **Pins:** GraphBridge.GraphRag and the provider at the version you chose, EF Core `[10.0.13]`, Microsoft.Extensions.AI.Abstractions `[10.11.0]` and Neo4j.Driver `[6.4.0]`. `Directory.Build.props` acknowledges the experimental diagnostic `GBRAG001`.

The application needs a Neo4j 2026.09 or later database you can write to. `setup` applies the default GraphRAG schema as its own migration.

The repository's `eng/scripts/Test-TemplatePackage.ps1` checks both templates the same way: install into an isolated hive, create, restore twice (the second locked), confirm the exact package versions, build, and run the offline tests. With `-Live`, it also runs the GraphRAG starter's four commands against the fixture and checks the answer's citation. That run needs a fixture where sample 07's tests haven't created the default GraphRAG schema, and the script refuses otherwise.

## Limits

- Published on nuget.org as `GraphBridge.Templates`. Remove it with `dotnet new uninstall GraphBridge.Templates`.
- The template contains no binaries and declares no package dependencies; the generated projects reference packages that must be in your feed or on nuget.org.
- The `graphbridge` starter references only the provider package. Add `GraphBridge.Tooling` or `GraphBridge.Generators` yourself if you need them.

## Try it

- The [package quickstart](../GETTING_STARTED.md) builds the same package-only consumer step by step.
- Workshop lesson [01 · Your first context](../../workshop/GraphBridge.Workshop/Lessons/01-FirstContext/README.md) explains the context, mapping and first query the starter uses.
- [templates/README.md](../../templates/README.md) describes the template source.
