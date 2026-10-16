# `dotnet new graphbridge` template

The `GraphBridge.Templates` package adds a `dotnet new graphbridge` template that creates a small solution referencing GraphBridge from **packages**, not from a source checkout: a console application and offline query contract tests. Use it to start a fresh project against a locally packed preview and to confirm that the packages restore, build and translate queries. GraphBridge is not yet published to nuget.org, so you pack the provider and the template into a local feed first.

```powershell
# From the repository root: pack the provider and the template into one local feed.
$feed = Join-Path ([IO.Path]::GetTempPath()) 'graphbridge-feed'
dotnet pack src/GraphBridge.EntityFrameworkCore/GraphBridge.EntityFrameworkCore.csproj -o $feed
dotnet pack templates/GraphBridge.Templates.csproj -o $feed

# Install the template and create a solution.
dotnet new install (Join-Path $feed 'GraphBridge.Templates.0.1.0-preview.2.nupkg')
dotnet new graphbridge --name MyGraph --provider-version 0.1.0-preview.2
```

This creates a `MyGraph` folder. Both packages take the source version `0.1.0-preview.2` unless you pack with `-p:PackageVersion=…`; `--provider-version` must name a provider version that exists in your feed.

## Options

| Option | Required | Meaning |
|---|---|---|
| `--provider-version` | Yes | Exact `GraphBridge.EntityFrameworkCore` version available in your local or private feed. It is pinned with `[version]`. |
| `--name`, `--output` | No | Standard `dotnet new` options. `--name` replaces `GraphBridgeStarter` in file names, namespaces and content. |

The template has no post-actions: it does not restore, build, start Docker or add migrations.

## What it generates

| Path | Content |
|---|---|
| `MyGraph.slnx` | Solution with the two projects. |
| `App/MyGraph.csproj` | Console application pinning `GraphBridge.EntityFrameworkCore` to your version, `Microsoft.EntityFrameworkCore` to `[10.0.12]` and `Neo4j.Driver` to `[6.3.0]`. |
| `App/Program.cs` | A guarded CRUD demo: create, parameterized read, update with row version, a Fluent Cypher read, an explicitly rolled-back transaction, delete and an absence check. It also defines `SmokeContext` and `SmokeItem`. |
| `Tests/MyGraph.Tests.csproj`, `Tests/QueryContractTests.cs` | Two offline xUnit tests: a LINQ query translates to parameterized Cypher, and an unsupported query throws `Neo4jQueryTranslationException`. A proxy driver fails any transport call. |
| `Directory.Build.props` | `net10.0`, nullable, implicit usings, warnings as errors, lock files enabled. |
| `Directory.Packages.props`, `NuGet.Config` | Central package management off; nuget.org as the only source until you add your feed. |
| `global.json`, `LICENSE`, `README.md` | The pinned .NET SDK 10.0.401, the MIT license and run instructions. |

`App/Program.cs` is the same consumer as [`eng/consumers/ProviderLiveConsumer.cs`](../../eng/consumers/ProviderLiveConsumer.cs), which the [package quickstart](../GETTING_STARTED.md) walks through.

## Build and run

From the new folder, add your local feed, then restore, build and test:

```powershell
cd MyGraph
dotnet nuget add source $feed --name GraphBridgePreview --configfile NuGet.Config
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

## Limits

- Not published to a public template feed; install it from a locally packed `.nupkg`. Remove it with `dotnet new uninstall GraphBridge.Templates`.
- The template contains no binaries and declares no package dependencies; the generated projects reference packages that must be in your feed or on nuget.org.
- Only the provider package is referenced. Add `GraphBridge.Tooling` or `GraphBridge.Generators` yourself if you need them.

## Try it

- The [package quickstart](../GETTING_STARTED.md) builds the same package-only consumer step by step.
- Workshop lesson [01 · Your first context](../../workshop/GraphBridge.Workshop/Lessons/01-FirstContext/README.md) explains the context, mapping and first query the starter uses.
- [templates/README.md](../../templates/README.md) describes the template source.
