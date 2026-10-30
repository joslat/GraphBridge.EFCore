# Compatibility and support

This is GraphBridge's support contract: the exact stack it is built and tested on, the Neo4j servers it has run against, what each feature supports, and what may change between previews. Anything not listed here is unsupported or unverified.

## Developer preview

GraphBridge is a **developer preview** (`0.2.0-preview.1`; see the [changelog](../../CHANGELOG.md)). That means:

- It is not production-supported. Use it to evaluate, learn and give feedback.
- Public APIs, generated source, supported model shapes and persisted mapping contracts may change between previews. Pin the exact preview.
- The packages are prereleases on nuget.org: [GraphBridge.EntityFrameworkCore](https://www.nuget.org/packages/GraphBridge.EntityFrameworkCore) and optional packages (see [packages and components](PACKAGES_AND_COMPONENTS.md)). [Getting started](../GETTING_STARTED.md#install-from-a-local-feed) shows how to try unreleased changes from a local feed.
- "Supported" below means implemented and tested within the stated boundary, on the stack below. It is not a promise for other versions, editions or deployments.

Report problems through the repository's issue templates with exact versions, a small synthetic model, the LINQ or Fluent query and the sanitized generated Cypher. Never attach credentials or private data.

## Exact stack

| Component | Version | Notes |
|---|---|---|
| .NET SDK | **10.0.401** | Pinned in [global.json](../../global.json) with roll-forward disabled |
| Target framework | `net10.0` | Tested on runtime 10.0.12; other runtimes are unverified |
| Entity Framework Core | **10.0.13** | Exact central pin; signatures checked against EF source tags `v10.0.12` and `v10.0.13` |
| Neo4j.Driver | **6.4.0** | The package reports assembly version 6.4.12.0; the informational version identifies 6.4.0. Its minimum is 6.4.0 for two security fixes in that release |
| Optional tooling libraries | dotNetRdf 3.5.2, YamlDotNet 18.1.0, Roslyn 5.9.0, AngleSharp 1.5.0 | Used by `GraphBridge.Tooling` and the generator only; never by the provider at run time |
| [GraphRAG toolkit](../graphrag/README.md) (experimental, `GBRAG001`) | Microsoft.Extensions.AI.Abstractions 10.11.0; its adapters: Microsoft.Extensions.DataIngestion 10.11.0-preview.1.26513.7, Microsoft.Agents.AI.Abstractions 1.26.0, PdfPig 0.1.16 | Needs Neo4j 2026.09 or later; tested on 2026.09.0 and 2026.10.1. Each adapter package brings only its own dependency; the provider takes none of them |

The provider package declares `Microsoft.EntityFrameworkCore` as `[10.0.13, 11.0.0)` and `Neo4j.Driver` as `[6.4.0, 7.0.0)`. GraphBridge uses EF Core's internal provider APIs, which change between major versions, so the ranges make NuGet flag EF Core 11 or driver 7 at restore instead of resolving them silently. EF Core 10 is a long-term support release; EF Core 11 is unverified. `0.1.0-preview.4` and earlier declared minimum versions only, with no upper bound.

Within the ranges, NuGet may resolve newer patches than the ones tested; that is not a compatibility claim. Pin EF Core 10.0.13 and Neo4j.Driver 6.4.0 in your application, and use matching GraphBridge versions for the provider, `GraphBridge.Tooling` and `GraphBridge.Generators`.

## Neo4j servers and editions

| Server | Status | Scope |
|---|---|---|
| Neo4j **2026.09.0 Community**, single server, database `neo4j` | Verified | All provider, tooling, sample and workshop live suites. Statements use the server's default Cypher dialect; actual plans report Cypher 25 |
| Neo4j **2026.10.1 Community** | Live probe | Every live suite passes on it (30 October 2026, the release candidate): the provider's integration suite with the GraphRAG toolkit's live tests 381/381, specification 81/81, tooling 25/25, the workshop 9/9, the GraphRAG samples' replayed real-model runs 23/23 and the interop suite 6/6. The test fixture stays on 2026.09.0, so later changes aren't rechecked on 2026.10.1 until it runs again |
| Neo4j **2026.09.0 Enterprise**, dedicated database | Verified for one runtime profile | Seven restricted-user facts with [reviewed grants](../../eng/enterprise/README.md): reader and writer CRUD, the schema-visibility requirement and refused administration. No Enterprise schema deployment, Graph Type deployment or tenant isolation claim |
| Neo4j **5.26.18 Community** | Read-only probe | Doctor and schema inspection, three Verify plans, one bounded LINQ projection and one Fluent/native comparison; plans report Cypher 5. No CRUD claim. Vector and full-text search are refused without fallback |
| Other 5.x, 2025.x and 2026.x releases | Untested | Capability detection recognizes them; nothing else is claimed |
| Clusters, multiple databases | Untested | Routing, bookmarks, leader changes and failover are not tested |

Features that need Cypher 25 select it explicitly: vector and full-text search, and Graph Type inspection. See [graph search](../features/GRAPH_SEARCH.md) for its own capability gate.

## Version and edition capability detection

Schema inspection, Doctor and the deployment preflight read the server's version and edition from `dbms.components()` and derive which schema features exist:

```csharp
var capabilities = (await db.Database.InspectGraphSchemaAsync()).Capabilities;
Console.WriteLine($"{capabilities.ServerVersion} {capabilities.Edition}: " +
    $"relationship keys {capabilities.RelationshipUniqueness}, existence {capabilities.PropertyExistence}");
```

| Capability | Available on |
|---|---|
| Node uniqueness constraints, range indexes | Every recognized server |
| Vector and full-text index steps in migrations | 2025 and 2026 releases; `Unknown` on 5.x |
| Relationship uniqueness constraints | 5.7 and later |
| Property existence constraints | Enterprise |
| Property type constraints | Enterprise, 5.9 and later |
| Node and relationship key constraints | Enterprise, 5.7 and later |
| Current Graph Type observation | Enterprise 2026.06 and later, advertising Cypher 25 |

Recognized versions are 5.x and the calendar releases 2025.01 to 2026.12; recognized editions are `community` and `enterprise`. For anything else every capability is `Unknown`, and unknown is treated as unsupported. Each value is `Supported`, `Unsupported` or `Unknown`, derived from documented availability, not a certification of that server.

When the version or edition is not recognized, Doctor reports **`GBD-CAP-001`** (server capability could not be established) and the sample [schema preflight](../features/SCHEMA_AND_MIGRATIONS.md#read-only-schema-drift-gate) exits with code 3. Check the server version and edition you are connected to, and that the account can call `dbms.components()`; then use a server from the table above. Connecting successfully does not certify every server capability.

## Feature support

| Surface | Supported | Boundary |
|---|---|---|
| [Mapping](../features/MAPPING_NODES.md) and tracking | EF tracking, no tracking and no tracking with identity resolution | The finalized EF model and state manager stay authoritative |
| [LINQ queries](../features/LINQ_QUERIES.md) | Async node and relationship roots, scalar comparisons, Boolean and null predicates, direct-property ordering, paging, [scalar and DTO projections](../features/LINQ_QUERIES.md#dto-projections), First/Single variants, Count/LongCount/Any, [integer Min/Max](../features/LINQ_QUERIES.md#integer-aggregates), [bounded scalar Contains](../features/LINQ_QUERIES.md#contains-over-a-local-collection) | Other shapes are refused before execution with a [`GBQ` code](KNOWN_LIMITATIONS.md#refusal-codes); no client-side evaluation |
| [Fluent Cypher](../features/FLUENT_CYPHER.md) | Typed MATCH and OPTIONAL MATCH patterns, anonymous edges, C# lambda predicates and ordering, explicit native predicates, WITH, counts, aggregates (`Min`, `Max`, `Sum`, `Avg`, bounded `Collect`), EXISTS, scalar DTOs, [whole entities](../features/FLUENT_CYPHER.md#returning-whole-entities) and bounded entity lists, bounded named paths, `TagWith` and `WithTimeout` | Read-only native semantics and untracked results. A lambda that native Cypher would evaluate differently is refused with a `GBFC` code. No write DSL, EF query filters, `IQueryable` composition or full Cypher promise |
| [Native Cypher](../features/NATIVE_CYPHER.md) | `QueryCypherAsync` with an explicit projector and row cap; `ExecuteCypherAsync` returning write counters | Untracked; does not refresh tracked entities |
| Persistence | Atomic `SaveChangesAsync`, modified-property updates, null removal, restrictive deletes, [optimistic concurrency](../features/TRANSACTIONS_AND_CONCURRENCY.md#optimistic-concurrency) | Async only; reviewed key uniqueness constraints required ([decision](../architecture/adr/ADR-0037.md)) |
| [Bulk mutations](../features/BULK_MUTATIONS.md) | Async `ExecuteUpdateAsync`/`ExecuteDeleteAsync` over supported node and relationship roots, in atomic lock-and-recheck batches; [`ExecuteUpsertAsync`](../features/BULK_MUTATIONS.md#upserts) for nodes keyed by a constrained EF key | 10,000-row cap, restricted setters, the EF tracker is not updated; upserts refuse relationships, native-reference dependents and concurrency tokens |
| [Transactions](../features/TRANSACTIONS_AND_CONCURRENCY.md) | Async explicit commit and rollback shared by saves, LINQ, Fluent and native queries; [command timeouts](../features/TRANSACTIONS_AND_CONCURRENCY.md#command-timeouts) | No savepoints, automatic retries, ambient or nested transactions, or guaranteed outcome after a lost commit acknowledgement |
| [References](../features/REFERENCES.md) | Required and optional native references; one direct Include in either direction; optional parent changes | No required foreign-key reassignment |
| [Associations](../features/ASSOCIATIONS.md) | Explicit or dictionary EF join entities stored as one relationship per endpoint pair, with payload | Duplicate physical pairs are refused; no association Include |
| [Identified relationships](../features/IDENTIFIED_RELATIONSHIPS.md) | Application relationship keys, parallel relationships, payload, CRUD and fix-up | Endpoints are immutable; no composite relationship keys or Include |
| [Scalars and time](../features/NULLS_TYPES_AND_TIME.md) | Nullable primitives, Guid, enum, native `DateTimeOffset`, explicit reversible converters | No temporal keys or membership, date arithmetic, other native temporal types, decimal or arrays |
| [Search](../features/GRAPH_SEARCH.md) | Explicit node and relationship vector and full-text APIs, typed scores and index provenance | Cypher 25 with capability and index checks; untracked DTOs; approximate vector results |
| [Paths](../features/BOUNDED_PATHS.md) | Typed native paths with required direction, depth and result bounds | Untracked; no shortest or weighted path promise, no EF query filters |
| [Schema and migrations](../features/SCHEMA_AND_MIGRATIONS.md) | Read-only inspection and drift checks, reviewed proposals, typed migration plans with journal and explicit recovery | No implicit DDL, whole-plan atomicity, automatic `Down` or `dotnet ef migrations` |
| Inspection and tracing | `ToQueryString()`, [explicit EXPLAIN plans](../features/QUERY_INSPECTION.md#real-server-plan-helper), [command tracing](../features/COMMAND_TRACING.md) | EXPLAIN needs explicit parameters and plan arguments may contain literals; PROFILE executes |
| Authoring | [Code-first, ontology/schema-first and database-first](../../samples/README.md); optional [mapping generator](../tools/MAPPING_GENERATOR.md) | The ontology is an explicit subset, not OWL reasoning; sampled graphs do not prove global integrity |
| [Design-time](../tools/DESIGN_TIME.md) | `dotnet ef dbcontext list/info`; application-hosted migration catalog, preview and read-only status | No relational migration commands or automatic model diff |
| Tools | Doctor, Verify, Query Lab and the authoring CLI ([packages and components](PACKAGES_AND_COMPONENTS.md)) | Diagnostics read metadata and plans; they never repair schema |

## Changes and upgrade policy

During `0.x` previews, public APIs, generated source, supported model shapes and persisted mapping contracts may change. Pin the exact preview and tested EF/driver versions. Each subsequent preview must identify changed APIs/mappings, affected applications, required regeneration or explicit data migration, and actual acceptance evidence. A breaking mapping change must never silently rewrite an existing database. Review the diff and migration preview, back up affected data and verify on a disposable copy before applying a reviewed plan.

Once published, an existing version's package contents must not be replaced. A changed artifact receives a new version. A 1.0 designation requires a separately declared stable support contract and operational evidence; completing the original private roadmap did not supply that promise.

Each version's upgrade notes are in the [changelog](../../CHANGELOG.md).

Evidence: [version and capability matrix](../../tests/verification/VERSION_CAPABILITY_MATRIX.md), [EF conformance matrix](../../tests/verification/EF_CONFORMANCE_MATRIX.md), [verification summary](../../tests/verification/VERIFICATION_SUMMARY.md), [Fluent Cypher acceptance](../../tests/verification/FLUENT_CYPHER_ACCEPTANCE.md), [central package pins](../../Directory.Packages.props).
