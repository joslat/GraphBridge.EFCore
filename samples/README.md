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
| [07 GraphRAG quick start](07-GraphRagQuickStart/README.md) | The experimental GraphRAG toolkit on GraphBridge's own docs: schema as a reviewed migration, resumable ingestion, and answers whose quotes are checked against the stored bytes, or a refusal. Runs offline with a hashing embedder and an extractive chat client. |
| [08 GraphRAG novel](08-GraphRagNovel/README.md) | *Alice's Adventures in Wonderland* with the novel ontology: extraction that prunes with reasons, placeholders that never merge, and characters, kin and who says what through typed LINQ. Offline on the opening of chapter I with recorded replies; the whole book with your model. |
| [09 GraphRAG articles](09-GraphRagArticles/README.md) | Articles with the article ontology, offline over fictional ones and with a real model over excerpts of NASA's Artemis II releases: authors, topics, jobs and quotations, facts listing every article that states them, the source's own metadata; `chat` with follow-ups, `graph` (Text2Cypher), an Agent Framework `agent`, and `serve`, the endpoints with the explorer page. |

## Running the samples

Prerequisites: SDK **10.0.401**, repository dependency restore, and Docker for live operations. Run the live parts against a disposable Neo4j: the repository's [fixture script](../eng/README.md) starts one in Docker. The scripts support Windows and Linux; use PowerShell 7 on Linux. Run heavy sample and test runs one at a time.

Start with Blogging's four-command setup/run/cleanup sequence. Organization also provides an offline `--preview`; Movies separates inspection/scaffolding from explicit schema, seed and backed-up backfill commands. Follow each README's mode-specific instructions: appsettings examples describe environment names and do not load credentials automatically. No sample implicitly upgrades existing data.

The baseline [verification inventory](../tests/verification/VERIFICATION_SUMMARY.md) links executed tests and cleanup evidence. Read the [compatibility contract](../docs/reference/COMPATIBILITY.md) and [known limitations](../docs/reference/KNOWN_LIMITATIONS.md) before adapting a sample to another application.

