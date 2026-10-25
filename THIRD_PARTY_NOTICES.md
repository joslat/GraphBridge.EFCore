# Third-party notices and attribution inventory

This inventory distinguishes referenced packages, bundled assets and source-influenced implementation. Original GraphBridge code, documentation and original sample assets are covered by the root [MIT license](LICENSE); third-party materials retain their respective terms. Distribution boundaries are recorded in release readiness (maintainer-local record).

## Locked package inventory

[packages.json](eng/dependencies/packages.json) records **88 package/version pairs from 21 tracked lock files**: consuming projects, locked NuGet content hashes, license expressions/files/URLs, publisher metadata and SHA-256 hashes of bundled notice assets. Scope includes runtime, tooling, generator, samples, restored tests, benchmark source and test-host projects. Development dependencies are not automatically bundled in runtime packages. A missing license file does not imply an unlicensed package; many declare an SPDX expression.

Reproduce after locked restore with PowerShell 7:

```powershell
pwsh -File eng/scripts/Export-DependencyInventory.ps1
pwsh -File eng/scripts/Export-DependencyInventory.ps1 -Check
```

Generation reads restored metadata without fetching packages, accepting terms or copying assemblies. `-Check` fails on inventory drift and runs in security CI. Publisher strings are metadata, not independent ownership verification.

| Artifact | Actual included assets at reviewed checkpoint | Dependency distinction |
|---|---|---|
| Provider `.nupkg` | GraphBridge provider DLL, README, MIT license, inventory and preserved notices | EF Core 10.0.13 and Neo4j.Driver 6.4.0 are references restored separately; no dependency DLL embedded |
| Generator `.nupkg` | GraphBridge analyzer DLL, README, MIT license, inventory and preserved notices | Roslyn is private build tooling, not a bundled DLL or published dependency |
| Optional tooling library | GraphBridge tooling DLL, README, MIT license, inventory and preserved notices | Provider and five tooling dependencies are references; no dependency DLL embedded. Compiled CLI/application bundles remain outside this distribution. |
| Starter template | Original starter source, SDK pin, license and attribution payloads | Package references restored by generated applications; no dependency DLL embedded |
| Tests/research | Test source and package references; ignored upstream checkout | Not provider package content; ignored research stays out of releases |

The provider's net10.0 closure has 12 package/version entries, including EF's analyzer build asset. All except Neo4j.Driver declare MIT; Neo4j.Driver declares Apache-2.0. Application bundles may have different runtime closures. The inventory preserves Roslyn/Microsoft notice paths and hashes, including RTF files, without claiming their assemblies ship in GraphBridge's analyzer package.

## Source-influenced provider code

`src/GraphBridge.EntityFrameworkCore/Query/GraphIncludeBinding.cs` follows EF InMemory's metadata-accessor pattern for no-tracking navigation fixup. Conservative attribution retains the [.NET Foundation and Contributors MIT notice](eng/dependencies/licenses/efcore-10.0.12-MIT.txt). Reference: EF Core v10.0.12, commit `001bfac21c1910cdf1110ca256c9ea0a06f374ca`, [GenerateFixup and navigation helpers](https://github.com/dotnet/efcore/blob/001bfac21c1910cdf1110ca256c9ea0a06f374ca/src/EFCore.InMemory/Query/Internal/InMemoryShapedQueryCompilingExpressionVisitor.ShaperExpressionProcessingExpressionVisitor.cs).

GraphBridge implements its own casts, local helper and graph binding; readonly assignment delegates to EF's provider helper. Review did not establish a verbatim copied block. This notice acknowledges influence without inventing copying. EF_SERVICE_MAP.md (maintainer-local record) records other API research. The MongoDB provider was studied without copying/building its implementation; no MongoDB source redistribution is asserted.

## Preserved texts

| Source | Exact provenance | Text |
|---|---|---|
| EF Core | v10.0.12 checkout, commit above | [MIT](eng/dependencies/licenses/efcore-10.0.12-MIT.txt) |
| Neo4j.Driver | Official repository commit `4ed413b01906c5d1682c62a3ee3c982cb28f99cd`, identified in 6.4.0 nuspec | [LICENSE](eng/dependencies/licenses/neo4j.driver-6.4.0-LICENSE.txt), [NOTICE](eng/dependencies/licenses/neo4j.driver-6.4.0-NOTICE.txt) |
| NETStandard.Library 2.0.3 | Restored package `LICENSE.TXT` | [MIT](eng/dependencies/licenses/netstandard.library-2.0.3.txt) |
| Microsoft.NETCore.Platforms 1.1.0 | Restored package `dotnet_library_license.txt` | [Microsoft .NET Library terms](eng/dependencies/licenses/microsoft.netcore.platforms-1.1.0.txt) |

The two legacy build packages previously had unresolved forwarding URLs; their actual texts are now preserved. **Microsoft.NETCore.Platforms 1.1.0 is not labeled MIT.** Neither is a declared dependency or embedded assembly in the verified analyzer package. Preserving terms is not a new acceptance or recommendation to redistribute those build artifacts.

`xunit.abstractions` 2.0.3 has a legacy upstream license URL and no license asset in its package. Exact release-specific text remains a review item for distributing test binaries; it is not part of either reviewed GraphBridge package. Future bundles must preserve applicable notices for their actual contents. This inventory records those inputs and unresolved distribution choices, rather than certifying arbitrary bundles.

The Movies fixture is original fictional material, not imported tutorial records; retain its [provenance](samples/03-Movies-DatabaseFirst/PROVENANCE.md). Generated samples use checked-in project-authored inputs. Neo4j server images are not embedded in GraphBridge packages; disposable Enterprise evaluation authorization does not grant server redistribution rights or a GraphBridge license.
