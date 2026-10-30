# Architecture decision records

Each record states one decision, the alternatives that were considered and the evidence that accepted it. All records listed here are accepted and in force; a later record that amends an earlier one says so. Records 0001–0010 predate the public repository and are not published; their invariants are summarized in the [architecture overview](ARCHITECTURE.md).

## Foundation and EF Core integration

| Record | Decision |
|---|---|
| [ADR-0011](adr/ADR-0011.md) | Version baseline and execution gates: exact .NET, EF Core and Neo4j.Driver pins, and what must pass before a feature counts |
| [ADR-0012](adr/ADR-0012.md) | EF query adapter and the initial compatibility boundary |
| [ADR-0013](adr/ADR-0013.md) | Async node persistence, optimistic concurrency and transaction ownership |
| [ADR-0025](adr/ADR-0025.md) | EF finder value equality |
| [ADR-0029](adr/ADR-0029.md) | Prepare immutable read slots before enumeration |
| [ADR-0035](adr/ADR-0035.md) | Retain prepared Cypher in EF compiled delegates |
| [ADR-0037](adr/ADR-0037.md) | Retain asynchronous-only persistence |

## Graph storage

| Record | Decision |
|---|---|
| [ADR-0014](adr/ADR-0014.md) | EF join-entry observation supports native relationships |
| [ADR-0015](adr/ADR-0015.md) | Explicit directed association metadata |
| [ADR-0016](adr/ADR-0016.md) | Identified relationships and native Cypher results |
| [ADR-0021](adr/ADR-0021.md) | Native storage for ordinary node foreign keys (references) |
| [ADR-0032](adr/ADR-0032.md) | Optional native references remain dependent-owned graph membership |
| [ADR-0054](adr/ADR-0054.md) | Embeddings as EF properties, opted in with their dimensions |
| [ADR-0055](adr/ADR-0055.md) | Additional node labels per entity type, written and matched |
| [ADR-0056](adr/ADR-0056.md) | Identified relationship types shared by owners with different endpoints |

## Schema and migrations

| Record | Decision |
|---|---|
| [ADR-0017](adr/ADR-0017.md) | Storage witnesses and explicit schema comparison |
| [ADR-0018](adr/ADR-0018.md) | Migration journal and recovery boundaries |
| [ADR-0036](adr/ADR-0036.md) | A bounded design-time command subset |
| [ADR-0053](adr/ADR-0053.md) | Vector and full-text index steps in migrations |

## Querying

| Record | Decision |
|---|---|
| [ADR-0023](adr/ADR-0023.md) | Explicit server EXPLAIN without query execution |
| [ADR-0024](adr/ADR-0024.md) | Bounded scalar `Contains` with explicit equality semantics |
| [ADR-0027](adr/ADR-0027.md) | Nullable scalar contracts and literal Guid construction |
| [ADR-0030](adr/ADR-0030.md) | Construct DTO projections through the existing EF shaper |
| [ADR-0031](adr/ADR-0031.md) | Validate integer extrema inputs within the aggregate statement |
| [ADR-0033](adr/ADR-0033.md) | Explicit typed native search with observed index provenance |
| [ADR-0034](adr/ADR-0034.md) | Bounded native paths with typed projections and explicit direction |
| [ADR-0038](adr/ADR-0038.md) | Bounded untracked bulk mutation batches |
| [ADR-0051](adr/ADR-0051.md) | Typed batch upserts of nodes, keyed by a constrained EF key, and of relationships between existing nodes (extends 0038) |
| [ADR-0045](adr/ADR-0045.md) | Native `DateTimeOffset` payloads with explicit query semantics |

## Fluent Cypher

| Record | Decision |
|---|---|
| [ADR-0041](adr/ADR-0041.md) | Explicit native Cypher clauses beside EF LINQ |
| [ADR-0047](adr/ADR-0047.md) | C# predicate lambdas under an agree-or-reject rule (amends 0041) |
| [ADR-0048](adr/ADR-0048.md) | Aggregates and bounded `collect` (extends 0041) |
| [ADR-0049](adr/ADR-0049.md) | Node and relationship entities returned untracked (amends 0041, extends 0048) |

## Transactions and observability

| Record | Decision |
|---|---|
| [ADR-0026](adr/ADR-0026.md) | Uncertain commit verification and replay contract |
| [ADR-0028](adr/ADR-0028.md) | Correlated runtime tracing with value-free events |
| [ADR-0050](adr/ADR-0050.md) | Query tags as Cypher comments, command timeouts and write counters |
| [ADR-0057](adr/ADR-0057.md) | Search inside Fluent Cypher: a vector or full-text search as the first clause |

## Authoring toolchains

| Record | Decision |
|---|---|
| [ADR-0019](adr/ADR-0019.md) | Optional ontology compilation into the EF model |
| [ADR-0020](adr/ADR-0020.md) | Database-first observations and reviewed tracking proposals |
| [ADR-0022](adr/ADR-0022.md) | Optional attributed mapping generation with finalized-model verification |
| [ADR-0039](adr/ADR-0039.md) | Explicit mapping diagnostics and schema responsibility |
| [ADR-0040](adr/ADR-0040.md) | Offline database-first review between explicit versions |

## Developer tools and compatibility

| Record | Decision |
|---|---|
| [ADR-0042](adr/ADR-0042.md) | Application-hosted evidence tools (Doctor and Verify) |
| [ADR-0043](adr/ADR-0043.md) | Typed compiler snapshots for the static Query Lab report |
| [ADR-0044](adr/ADR-0044.md) | A read-only compatibility probe for Neo4j 5.26 |
| [ADR-0046](adr/ADR-0046.md) | Integrated VS Code Query Lab with application-owned capture |

## GraphRAG toolkit

| Record | Decision |
|---|---|
| [ADR-0052](adr/ADR-0052.md) | The `GraphBridge.GraphRag` toolkit: packages, lexical model, API of the first waves |

Investigations behind some records: [EF service map](EF_SERVICE_MAP.md) (internal EF APIs the provider depends on) and the [join-entry experiment](JOIN_ENTRY_EXPERIMENT.md) behind ADR-0014.
