# Architecture overview

![GraphBridge architecture](../images/architecture.svg)

GraphBridge is a genuine, non-relational EF Core 10 provider backed by the official Neo4j .NET driver. It derives a small, validated graph binding from the finalized EF model, translates a defined LINQ subset and typed graph patterns into parameterized Cypher, shapes results through EF, and turns tracked changes into ordered node and relationship mutations in one transaction.

## Rules the design keeps

1. **One state owner.** EF Core tracks every entity. There is no second identity map or change tracker.
2. **One model authority.** The finalized EF `IModel` is the only runtime model. Ontology, database-first and generator tooling all end in ordinary EF configuration.
3. **Two lowering paths.** Queries become a typed Cypher plan; tracked entries become a graph mutation plan.
4. **Explicit storage kinds.** Every mapped type is a node, a native reference, an association or an identified relationship, chosen in the model. See [concepts](../CONCEPTS.md).
5. **One transport.** The official `Neo4j.Driver` owns connections, sessions and transactions. Your application owns the driver; contexts borrow it.
6. **No client-side evaluation.** A query shape that cannot be translated faithfully is refused with a code and an alternative.
7. **Explicit schema.** Runtime code never creates constraints or indexes. Schema is inspected, proposed and applied by an explicit step.
8. **Parameters always.** Values are always parameters; labels, types and property names come only from validated metadata.

## Components

| Component | Package | Role |
|---|---|---|
| Provider | `GraphBridge.EntityFrameworkCore` | `UseNeo4j`, mapping extensions, LINQ translation, Fluent Cypher, native Cypher, update pipeline, schema inspection, migration plans, search, bounded paths, tracing |
| Tooling | `GraphBridge.Tooling` | Doctor, Verify and the Query Lab report, built on the provider's compiled query snapshots |
| Generator | `GraphBridge.Generators` | Optional source generator for attributed mappings, with build-time diagnostics |
| Template | `GraphBridge.Templates` | A `dotnet new` project template |
| CLI | `src/GraphBridge.Tool` (source) | Ontology compilation, SHACL validation, graph inspection and database-first scaffolding |
| Editor | `extensions/graphbridge-query-lab` | VS Code viewer for Query Lab evidence |

Package details: [packages and components](../reference/PACKAGES_AND_COMPONENTS.md).

## Inside the provider

| Area | Source | What it does |
|---|---|---|
| Model and binding | `src/GraphBridge.EntityFrameworkCore/Metadata`, `Infrastructure` | Mapping extensions, model validation, the storage manifest |
| Queries | `Query` | EF query pipeline: translation to a typed Cypher plan, graph validation, EF shaping |
| Fluent Cypher | `FluentCypher` | Typed symbols, clause builder, lambda translator, compiler and renderer |
| Writes | `Update`, `Storage` | Classify tracked entries, order node and relationship commands, check concurrency tokens, execute in one transaction |
| Native Cypher, paths and search | `Cypher`, `Paths`, `Search`, `Transport`, `Extensions` | Native queries with projectors, bounded paths, typed search and the driver session and transaction plumbing |
| Schema and change | `Schema`, `Migrations` | Inspection against the model, reviewed proposals, migration plans and journal |
| Diagnostics | `Diagnostics` | Refusal codes, tracing, value-free events |

The exact folder names are in the source tree; the [EF service map](EF_SERVICE_MAP.md) lists every internal EF API the provider depends on, because those can change between EF releases.

## How a request flows

**A LINQ query.** EF preprocesses the expression tree and hands the shape to the provider. GraphBridge translates it into a typed plan and refuses anything it cannot express faithfully. The driver runs one parameterized statement. Results are validated against the graph contract (for example, exactly one parent link) and shaped and tracked by EF.

**`SaveChangesAsync`.** EF hands over the tracked entries. GraphBridge classifies each as a node or relationship change, orders them, adds concurrency checks and runs all commands in one Neo4j transaction. EF accepts the changes only when the transaction commits; otherwise it reports which entries failed. Node deletion is restrictive: a node that still has relationships is not deleted.

**A native relationship.** Adding to a skip navigation creates an EF join entity. GraphBridge lowers it to one directed relationship with its payload, using the endpoints, type and direction from the mapping. Reading it back validates that the pair has exactly one relationship.

## Boundaries

GraphBridge pins exact versions of .NET 10, EF Core 10 and Neo4j.Driver 6 and tests each feature against a real Neo4j database before it counts as supported. It does not promise relational EF compatibility, evaluate predicates on the client, reason over ontologies, use Neo4j element IDs as EF keys, retry writes automatically or delete with `DETACH DELETE` implicitly. See [compatibility](../reference/COMPATIBILITY.md) and [known limitations](../reference/KNOWN_LIMITATIONS.md).

Every significant decision has a record: [architecture decisions](ARCHITECTURE_DECISIONS.md).
