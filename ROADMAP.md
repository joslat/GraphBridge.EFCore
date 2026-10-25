# Roadmap

GraphBridge is a **developer preview**. This page shows where it is heading and why. It is a direction, not a promise: priorities move with what people report and ask for. What has already shipped is in the [changelog](CHANGELOG.md), and what works today is in [compatibility](docs/reference/COMPATIBILITY.md) and [known limitations](docs/reference/KNOWN_LIMITATIONS.md).

*Last updated: October 2026, with 0.1.0-preview.5.*

## Now: the next previews

- **Fixes from feedback.** Reports from people trying the previews come first.
- **GraphRAG toolkit for .NET** (experimental; in the repository now, with packages in a later preview once verified with a real model; see the [GraphRAG docs](docs/graphrag/README.md)). Building blocks on GraphBridge for the whole lifecycle: documents, sections and chunks with exact byte positions; ontologies as C# classes, with three standard ones (generic document, novel, article); schema-guided extraction; vector, full-text and graph retrieval fused by identity; answers with verified citations, or an honest "insufficient evidence"; re-ingest, remove and health checks; agent tools, and adapters for Microsoft.Extensions.DataIngestion and Agent Framework. A layout preset uses the names of the document and chunk layer that Neo4j's Python GraphRAG library writes; interop between the two isn't tested yet.
- **Provider features GraphRAG needs** (shipped in 0.1.0-preview.5). Embeddings as EF properties, search index steps in migrations, and typed upserts for relationships.
- **EF Core 11 readiness.** EF Core 11 is due in November 2026. We'll build against it and list what changes. EF Core 10, the long-term-support release, stays the supported target, and the next preview's packages declare EF Core below 11.
- **Monthly dependency refresh.** EF Core 10 patches, Neo4j.Driver 6, Microsoft.Extensions.VectorData and the latest Neo4j calendar release.

## Next: likely, driven by demand

- **Neo4j Aura compatibility check.** Run the preflight, capability, CRUD and search checks against Aura and document the result.
- **Search inside Fluent Cypher.** A vector or text match followed by a graph pattern in one typed query.
- **Scoped search.** Search within part of the graph, such as a tenant or a set of documents.
- **LINQ gaps people report.** For example set operations, composed paging and Include edge cases.
- **GraphRAG beyond the first toolkit.** Text2Cypher, several labels per entity type (for full interoperability with Neo4j's Python GraphRAG library), evidence export, and an ASP.NET Core endpoint package.

## Exploring: no commitment

- Fluent Cypher writes.
- Query Lab on the VS Code Marketplace.
- More Neo4j versions and editions in the tested matrix.

## Not planned, by design

These are deliberate choices, not gaps:

- **Silent client-side evaluation.** A query GraphBridge can't translate is refused with a code, never quietly run in memory.
- **A second ORM or state tracker.** EF Core stays the only owner of the model and change tracking.
- **Creating schema at runtime.** Constraints and indexes come from explicit, reviewed steps, never from a query that happens to need them.
- **Hidden `DETACH DELETE`.** Deleting relationships along with a node is always explicit.
- **Graph algorithms inside the provider.** Use Neo4j Graph Data Science or native Cypher; GraphBridge stays a data-access provider.

## Before leaving preview

These are the proposed criteria for a first stable release:

- No breaking API changes across two consecutive previews.
- The documented subset is covered by live tests on the supported Neo4j versions.
- Several real users or projects are running it and reporting back.
- Neo4j Aura is validated.
- A clear EF Core 11 story.

## How to influence it

- **Propose an API.** Open an [API proposal or feature request](https://github.com/joslat/GraphBridge.EFCore/issues/new/choose). A clear proposal with a usage example and acceptance criteria is the fastest way to get something built. The VectorData adapter in 0.1.0-preview.3 started as an API proposal from Luis Quintanilla ([issue #1](https://github.com/joslat/GraphBridge.EFCore/issues/1)).
- **Report what fails.** A query shape GraphBridge refuses, with a small synthetic repro, moves LINQ gaps up the list.
- **Ask and discuss.** Questions and early ideas belong in [Discussions](https://github.com/joslat/GraphBridge.EFCore/discussions).
- **Get credit.** Contributions and accepted proposals are credited in the changelog and the release notes.
