# Samples

Five complete applications to read and copy from, in [`samples/`](../samples/README.md). Each one runs against a disposable Neo4j database and is covered by the repository's tests. New to GraphBridge? Take the [workshop](WORKSHOP.md) first.

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
| [05 Fluent Cypher gallery](../samples/05-FluentCypher/README.md) | Eight reusable graph queries: co-actors, shared-movie scores, optional matches, nested EXISTS, lists and text, aggregates and bounded paths. The same source feeds the integration tests, [Doctor](tools/DOCTOR.md), [Verify](tools/VERIFY.md) and [Query Lab](tools/QUERY_LAB.md) |

## Running them

You need the .NET SDK pinned in `global.json`, PowerShell 7 and Docker for the database steps. Each README lists its exact commands; most start from the repository's [disposable test database](../eng/README.md). Settings come from environment variables, never from files in the repository, and no sample upgrades existing data implicitly.
