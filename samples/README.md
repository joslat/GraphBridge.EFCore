# Samples

Complete applications to read and copy from. **New to GraphBridge? Take the [workshop](../workshop/README.md) first:** one menu-driven application, one topic per lesson, about three hours for the core track.

## Choose an authoring workflow

All three samples use the same GraphBridge provider and finalized EF model. They are executable previews with live acceptance, not three different runtime ORMs.

| Start here | Authoring authority | Demonstration | Important boundary |
|---|---|---|---|
| [01 Blogging — code-first](01-Blogging-CodeFirst/README.md) | C# entities and EF configuration | Async CRUD, concurrency/rollback and native HAS_POST references with Include | Required references; no FK reassignment or general Include |
| [02 Organization — ontology/schema-first](02-Organization-OntologyFirst/README.md) | Turtle vocabulary plus explicit persistence policy | Generate C#/EF mappings, separate SHACL validation, parallel employment edges and v1→v2 migration | No OWL reasoning; generation does not deploy schema |
| [03 Movies — database-first](03-Movies-DatabaseFirst/README.md) | Bounded inspection plus human-reviewed key/mapping policy | Deterministic scaffolding, generated EF reads/writes, native payload preservation and backed-up identity backfill | Sampling cannot prove global integrity or infer durable keys |

## Tooling and query samples

| Sample | Demonstration |
|---|---|
| [04 Design-time commands](04-DesignTime/README.md) | Context discovery and graph migration inspection around the three workflows, with `dotnet ef`-style commands. It adds no new authoring authority |
| [05 Fluent Cypher gallery](05-FluentCypher/README.md) | Nine reusable graph queries over mapped nodes and edges: co-actors, shared-movie scores, optional matches, nested EXISTS, lists and text, aggregates, whole entities and bounded paths. The integration suite runs the same source against hand-written native Cypher, and Doctor, Verify and Query Lab read it |
| [06 Vector search through VectorData](06-VectorSearch/README.md) | A `MovieFinder` that knows only `IVectorSearchable<MovieHit>`; application setup binds a small Neo4j vector index with the optional VectorData adapter. Explicit setup and cleanup commands. |

## Running the samples

Prerequisites: SDK **10.0.401**, repository dependency restore, and Docker for live operations. Use only this repository's [owned disposable fixture](../eng/README.md), keeping at least 5 GiB host RAM available for other work. The scripts support Windows and Linux; use PowerShell 7 on Linux. Reuse an owned compatible fixture and serialize heavy sample/test runs. Never stop another developer's containers.

Start with Blogging's four-command setup/run/cleanup sequence. Organization also provides an offline `--preview`; Movies separates inspection/scaffolding from explicit schema, seed and backed-up backfill commands. Follow each README's mode-specific instructions: appsettings examples describe environment names and do not load credentials automatically. No sample implicitly upgrades existing data.

The baseline [verification inventory](../tests/verification/VERIFICATION_SUMMARY.md) links executed tests and cleanup evidence. Read the [compatibility contract](../docs/reference/COMPATIBILITY.md) and [known limitations](../docs/reference/KNOWN_LIMITATIONS.md) before adapting a sample to another application.

