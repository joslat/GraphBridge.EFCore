# GraphBridge package notices

Original GraphBridge code is licensed under MIT; see the included LICENSE. Third-party material retains its own terms. No license on a third-party dependency or Neo4j server is replaced by GraphBridge's license.

The package includes `DEPENDENCY-INVENTORY.json`: the repository's complete pinned NuGet reference inventory across runtime, tooling, generators, tests and benchmarks. This is **not a list of DLLs bundled in this package**. The runtime provider package embeds only its own runtime DLL; the generator embeds only its own analyzer DLL. The tooling library references its authoring dependencies separately. No server image, ignored research checkout, test binary or font is included.

Provider no-tracking Include fixup follows an EF InMemory metadata-accessor pattern. Conservative attribution retains the .NET Foundation and Contributors MIT text in `notices/efcore-10.0.12-MIT.txt`. Source: EF Core v10.0.12 / commit `001bfac21c1910cdf1110ca256c9ea0a06f374ca`. GraphBridge supplies its own graph binding/casts/helper and delegates readonly assignment to EF's provider helper; no verbatim copied block is asserted.

The official Neo4j.Driver 6.4.0 source license and notice at its nuspec repository commit `4ed413b01906c5d1682c62a3ee3c982cb28f99cd` are preserved as `notices/neo4j.driver-6.4.0-LICENSE.txt` and `notices/neo4j.driver-6.4.0-NOTICE.txt`. The driver is restored as a separate provider dependency, not bundled into the provider DLL/package.

Legacy build-package terms are preserved for inventory review: `notices/netstandard.library-2.0.3.txt` is MIT; `notices/microsoft.netcore.platforms-1.1.0.txt` is Microsoft .NET Library terms and is not labeled MIT. Their assemblies are not embedded in these GraphBridge packages. The inventory also identifies license/notice assets inside Roslyn and other restored dependencies. A future application bundle must account for the actual assemblies it redistributes; this package inventory does not certify arbitrary bundles.

Full provenance and distribution distinctions: https://github.com/joslat/GraphBridge.EFCore/blob/main/THIRD_PARTY_NOTICES.md
