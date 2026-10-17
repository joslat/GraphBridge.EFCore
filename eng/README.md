# eng: engineering

`eng/` holds the repository's engineering toolbox, following the .NET repository convention (dotnet/efcore and dotnet/runtime use the same name): everything needed to test against a database, package, benchmark and qualify a release. Nothing here is part of a GraphBridge package or needed by an application.

| Path | What it is for |
|---|---|
| `docker-compose.yml`, `env.example`, `scripts/Start-Neo4jFixture.ps1`, `scripts/Stop-Neo4jFixture.ps1` | The disposable Neo4j test database (below) |
| `scripts/Run-OfflineTests.ps1`, `scripts/Run-IntegrationTests.ps1`, `scripts/Invoke-GuardedDotNet.ps1`, `scripts/Assert-FixtureResources.ps1` | Test runners with the shared-machine memory and ownership guards; see [tests/README](../tests/README.md) |
| `scripts/Test-FixtureResources.ps1`, `scripts/Test-ProcessEnvironment.ps1`, `scripts/Test-BloggingQuickstartPolicy.ps1`, `scripts/Test-BenchmarkPolicy.ps1` | Self-tests of those guards; they need no database |
| `GraphBridge.Package.props`, `PACKAGE_NOTICES.md`, `dependencies/` | Shared package metadata, the notice file and the dependency inventory and license texts packed into every package |
| `scripts/Export-DependencyInventory.ps1`, `scripts/Assert-PackageMetadata.ps1`, `scripts/Test-*Package.ps1`, `consumers/` | Regenerate or check the inventory, and prove each package from an isolated consumer (one consumer is also the template's `Program.cs`) |
| `release/` | Public snapshots and the release procedure; see [release/README](release/README.md) |
| `enterprise/` | The opt-in Neo4j Enterprise permission suite; see [enterprise/README](enterprise/README.md) |
| `scripts/Run-Benchmarks.ps1`, `scripts/Benchmark.Common.ps1`, `profiling/` | Benchmarks and allocation profiling; see [profiling/README](profiling/README.md). `Compare-BulkRuns.py` and `Compare-LiveQueryRuns.py` audit retained benchmark runs; `Measure-GeneratorBuild.ps1` times generator builds |
| `scripts/Run-BloggingSample.ps1`, `scripts/Run-BloggingQuickstart.ps1` | Sample 01 and the package quickstart, end to end |
| `scripts/Run-CompatibilityProbe.ps1` | Read-compatibility tests against Neo4j 5.26 LTS in an owned fixture |
| `query-lab/verify_lab.py` | Headless browser check of generated Query Lab HTML and PDF reports |
| `docs-site/`, `scripts/Build-DocsSite.ps1` | The documentation site built from `docs/` (MkDocs, pinned versions); builds locally, publishing is manual |

## Disposable test database

The public fixture supports the Blogging sample. Repository tests, benchmark source and acceptance runners are tracked alongside the provider.
See [build and test instructions](../tests/README.md). Public CI remains manual-only;
its build job does not claim live database acceptance.

The fixture uses pinned Neo4j 2026.09.0 Community, loopback Bolt 17687 and HTTP 17474,
a unique random Compose project, and ephemeral data/log mounts. No existing database
is selected. Stopping the fixture discards its graph.

From the repository root, with Docker, the pinned .NET SDK and PowerShell 7 installed:

```powershell
pwsh -File eng/scripts/Run-BloggingQuickstart.ps1
```

This restores/builds and executes Blogging, then removes its owned fixture. It refuses
a preexisting fixture. It monitors the memory reserve and times out its own child process.
For a fixture you already own, use `Run-BloggingSample.ps1` instead; its schema preparation
and cleanup boundaries are documented in the [sample guide](../samples/01-Blogging-CodeFirst/README.md).

Startup requires at least 7 GiB available host RAM: a 5 GiB shared-machine reserve,
a 1.5 GiB fixture limit and 0.5 GiB workload headroom. Using an existing fixture requires
5.5 GiB available. Unknown memory or container ownership fails closed. These snapshots
are not a machine-wide reservation; serialize heavy workloads and keep monitoring memory.
Never stop another developer's resources or shut down shared Docker/WSL.

The explicitly opted-in dedicated GitHub-hosted profile uses a 2 GiB reserve. This requires
both GitHub Actions and a fresh hosted runner; local/self-hosted execution retains 5 GiB.
Only the manual Live Neo4j acceptance workflow starts a database.

`Start-Neo4jFixture.ps1` generates credentials and a safety marker in ignored
`eng/.fixture.env`, waits for health, and writes the fixture marker. Never print or commit
that file. The sample runner restores prior process environment settings after execution.
`Stop-Neo4jFixture.ps1` removes only the selected owned Compose project and its generated
credential file. If startup fails, use that stop script before starting another fixture.
