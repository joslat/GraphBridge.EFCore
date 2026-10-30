# Samples

Nine complete applications to read and copy from, in [`samples/`](../samples/README.md). Each one runs against a disposable Neo4j database and is covered by the repository's tests. New to GraphBridge? Take the [workshop](WORKSHOP.md) first.

## Choose by how you author the model

All three use the same provider and end in an ordinary EF Core model; they differ only in where the model comes from.

| Sample | You start from | It shows | Keep in mind |
|---|---|---|---|
| [01 Blogging](../samples/01-Blogging-CodeFirst/README.md) | C# entities and EF configuration | Async CRUD, optimistic concurrency and rollback, native `HAS_POST` references with `Include` | Required references only; no foreign-key reassignment |
| [02 Organization](../samples/02-Organization-OntologyFirst/README.md) | A Turtle vocabulary plus a YAML persistence policy | Generated C# and EF mappings, separate SHACL validation, parallel employment relationships, a v1 → v2 model migration | No OWL reasoning; generation never deploys schema |
| [03 Movies](../samples/03-Movies-DatabaseFirst/README.md) | An existing graph | Bounded inspection, a reviewed key policy, deterministic scaffolding, preserved native payloads and a backed-up identity backfill | Sampling cannot prove global integrity or infer durable keys |

## Tooling and query samples

| Sample | It shows |
|---|---|
| [04 Design-time commands](../samples/04-DesignTime/README.md) | Context discovery and graph migration commands (`list`, `preview`, `status`) around the authoring workflows; see [design-time commands](tools/DESIGN_TIME.md) |
| [05 Fluent Cypher gallery](../samples/05-FluentCypher/README.md) | Nine reusable graph queries: co-actors, shared-movie scores, optional matches, nested EXISTS, lists and text, aggregates and bounded paths. The same source feeds the integration tests, [Doctor](tools/DOCTOR.md), [Verify](tools/VERIFY.md) and [Query Lab](tools/QUERY_LAB.md) |
| [06 Vector search through VectorData](../samples/06-VectorSearch/README.md) | A search class that knows only `IVectorSearchable<T>`, bound to a Neo4j vector index by the optional VectorData adapter |

## GraphRAG samples (experimental)

They use the experimental GraphRAG toolkit through project references, so they run the repository's current source. They run without a model, with stand-ins for it, but always against a Neo4j 2026.09 or later database: here "offline" means no model, not no database. Give each one an empty database of its own: its schema is the database's first migration. Samples 07 to 09 have also run with a real model, GLM-5.3-Flash, and replay its recorded calls offline with `GRAPHBRIDGE_AI_MODE=replay`. Sample 09's offline extraction replies for its fictional articles are written by hand.

| Sample | It shows |
|---|---|
| [07 GraphRAG quick start](../samples/07-GraphRagQuickStart/README.md) | GraphBridge's own docs ingested with exact chunk positions, and answers whose quotes are checked against the stored bytes, or a refusal |
| [08 GraphRAG novel](../samples/08-GraphRagNovel/README.md) | *Alice's Adventures in Wonderland* with the novel ontology: characters, kin, places and who says what, read through typed LINQ |
| [09 GraphRAG articles](../samples/09-GraphRagArticles/README.md) | The article ontology on fictional articles and on NASA's Artemis II news releases: authors, topics, jobs and quotations, the source's own metadata, an Agent Framework agent, and the toolkit's streaming endpoints |

## Running them

You need the .NET SDK pinned in `global.json`, PowerShell 7 and Docker for the database steps. Each README lists its exact commands; most start from the repository's [disposable test database](../eng/README.md). Settings come from environment variables, never from files in the repository, and no sample upgrades existing data implicitly.
