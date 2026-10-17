# Build and test GraphBridge

Prerequisites: the SDK pinned in `global.json`, Python 3 for repository checks, and PowerShell 7 for guarded runners. Node 24+ and npm are needed only for the optional VS Code extension tests. Docker is needed only for live database acceptance.

From the repository root:

```powershell
dotnet restore GraphBridge.sln --locked-mode --disable-parallel
dotnet build GraphBridge.sln --no-restore -m:1 -p:UseSharedCompilation=false -nodeReuse:false
pwsh eng/scripts/Run-OfflineTests.ps1 -NoBuild
```

The offline runner selects provider, tooling, generator and benchmark-validation suites. Do not use an unfiltered `dotnet test GraphBridge.sln` as an offline entry point: the solution also includes database suites. Build/test workloads are serialized; the runner checks the shared-machine memory reserve.

| Source | What it verifies |
|---|---|
| `tests/GraphBridge.UnitTests` | Provider translation, shaping, state/persistence command construction and error paths |
| `tests/GraphBridge.ToolingTests` | Authoring/scaffolding, schema diagnostics, Doctor, Verify and Query Lab contracts |
| `tests/GraphBridge.Generators.Tests` | Mapping generation and diagnostics, including a compiled consumer |
| `benchmarks/GraphBridge.Benchmarks.Tests` | Benchmark correctness, metrics, cleanup and protocol accounting; no DB |
| `tests/GraphBridge.IntegrationTests` | Actual provider behavior against disposable Neo4j |
| `tests/GraphBridge.SpecificationTests` | Selected inherited EF specification cases; not the entire upstream suite |
| `tests/GraphBridge.Tooling.IntegrationTests` | Actual metadata/schema, Doctor/Verify and semantic-reference behavior |
| `tests/GraphBridge.EnterpriseIntegrationTests` | Explicitly opted-in Enterprise permission profile |
| `tests/GraphBridge.Workshop.Tests` | Every workshop lesson offline, README excerpts and Cypher snapshots, the safety guard and, with the fixture, every lesson live |
| [Query Lab tests](GraphBridge.QueryLab.Tests/README.md) | Evidence parsing/rendering, actual .NET host lifecycle, browser and editor interaction |

## Live tests

Use the [owned Community fixture](../eng/README.md); startup checks ownership and RAM. Keep fixture creation and all test runs serial.

The Tooling suite's live Query Lab editor-host test runs Node.js 24 with the extension's packages: run `npm ci --prefix extensions/graphbridge-query-lab` once first. To run every live suite on a fresh GitHub-hosted machine instead, start the manual **Live Neo4j acceptance** workflow (`.github/workflows/live.yml`).

```powershell
pwsh eng/scripts/Start-Neo4jFixture.ps1
try {
    pwsh eng/scripts/Run-IntegrationTests.ps1 -NoBuild -Suite Integration
    pwsh eng/scripts/Run-IntegrationTests.ps1 -NoBuild -Suite Specification
    pwsh eng/scripts/Run-IntegrationTests.ps1 -NoBuild -Suite Tooling
    pwsh eng/scripts/Run-IntegrationTests.ps1 -NoBuild -Suite Workshop
} finally {
    pwsh eng/scripts/Stop-Neo4jFixture.ps1
}
```

Enterprise acceptance has its own [fixture, license and permission prerequisites](../eng/enterprise/README.md). No Enterprise server starts from ordinary offline tests. The source is public; generated credentials remain ignored.

## Other validation

- [Benchmark methodology and runner](../benchmarks/GraphBridge.Benchmarks/README.md): measurements are opt-in; a build is not a benchmark run.
- Package consumers: `eng/scripts/Test-ProviderPackage.ps1`, `Test-ToolingPackage.ps1`, `Test-GeneratorPackage.ps1`, `Test-CombinedPackages.ps1` and `Test-TemplatePackage.ps1`; add `-Live` only where supported and after fixture setup.
- [Releasing a version](../eng/release/README.md#releasing-a-version): the build, offline tests and live tests that must pass on GitHub-hosted machines before publishing.
- [Conformance matrix](verification/EF_CONFORMANCE_MATRIX.md), [upstream inventory](verification/EF_UPSTREAM_INVENTORY.md), [Fluent semantics](verification/FLUENT_SEMANTIC_MATRIX.md).
- `python eng/release/Check-PublicDocuments.py` checks public Markdown paths. Dependency inventory: `pwsh eng/scripts/Export-DependencyInventory.ps1 -Check` after locked restore.

Historical counts in [verification evidence](verification/VERIFICATION_SUMMARY.md) retain their source/run scope; they do not become fresh results when files move. Raw receipts, PDFs, screenshots and benchmark archives are kept separately from source. Public CI remains manual-only and its build job does not claim live-test acceptance.
