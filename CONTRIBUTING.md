# Contributing

This public repository starts at 0.1.0-preview.2. Earlier development history, including the hosted runs and commits cited in older evidence, is kept in a private repository.

Start with [testing and validation](tests/README.md), [supported behavior](docs/reference/COMPATIBILITY.md), [known limits](docs/reference/KNOWN_LIMITATIONS.md) and the [architecture decisions](docs/architecture/ARCHITECTURE_DECISIONS.md). Follow [AGENTS.md](AGENTS.md) for repository engineering and resource rules.

`GraphBridge.sln` includes the provider, optional tooling/generator, samples, repository tests and executable benchmarks. Tests and benchmarks are development assets; they are not bundled in runtime NuGet packages. No ignored maintainer planning pack is required to build or run the supported test entry points.

Use exact locked dependencies. Add focused regression coverage for behavior changes, name the commands actually run, and distinguish offline checks from real database acceptance. Live and Enterprise suites require explicitly owned disposable fixtures. Never point test runners at production or another developer's database.

Workflows remain manual-only to control cost. A source change, local verification or push does not authorize dispatching hosted tests or changing workflow triggers. Avoid running every database suite just to change documentation.

Keep generated reports, credentials, installer packages, performance traces and raw run captures out of Git. Public test fixtures must contain synthetic data and documented provenance. Review new dependencies and update the [inventory](eng/dependencies/packages.json) and [notices](THIRD_PARTY_NOTICES.md) where relevant.
