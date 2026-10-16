# GraphBridge concepts

GraphBridge is an EF Core provider for Neo4j. EF Core owns the model, entity identity and change tracking; the official Neo4j .NET driver owns the connection. This page explains the few ideas everything else builds on. The [guided workshop](../workshop/README.md) shows each one running.

## Four ways to store data

| You write in EF | Stored in Neo4j | Configure with | Use it for |
|---|---|---|---|
| An entity | A **node** with a label and an application key | `ToGraphNode("Person")` | Things: people, movies, reviews |
| A one-to-many reference (`Review.MovieId`) | A **relationship** from parent to child; no foreign-key property on the child | `.ToGraphReference("HAS_REVIEW")` | Ownership and membership, required or optional |
| A many-to-many join entity (`Like`) | One **relationship** per endpoint pair, with payload properties | `.ToGraphRelationship("LIKES", source FK, target FK)` | Links between things: likes, follows |
| A relationship entity with its own key (`Credit`) | A **relationship** with an application key, so several can connect the same nodes | `.ToGraphIdentifiedRelationship("ACTED_IN", key, source FK, target FK)` | Parallel links: two roles in one movie |

Keys are **application keys** that you assign. Every key needs a reviewed uniqueness constraint before `SaveChangesAsync` writes; GraphBridge never creates schema on its own. See [schema preflight](features/SCHEMA_AND_MIGRATIONS.md) and workshop lessons 02–03.

Reads **validate** the graph rather than trusting it. For example, a child read through a reference proves it has exactly one valid parent link, and an identified relationship proves its key is unique. Corrupt data fails before EF shapes an entity.

## Three ways to query

| Surface | Semantics | Results | Read more |
|---|---|---|---|
| **EF Core LINQ** (`db.People.Where(...)`) | C# semantics, including null comparisons | Tracked entities, DTOs, supported aggregates | [Query inspection](features/QUERY_INSPECTION.md), [known limitations](reference/KNOWN_LIMITATIONS.md) |
| **Fluent Cypher** (`db.Cypher().Match(...)`) | Native Cypher; C# lambdas only where they agree with it | Untracked scalars, DTOs and bounded paths | [Fluent Cypher guide](features/FLUENT_CYPHER.md) |
| **Native Cypher** (`db.Database.QueryCypherAsync`) | Exactly what you write, with parameters | Untracked, through your projector | [Native Cypher](features/NATIVE_CYPHER.md) |

Fluent Cypher has two predicate forms, C# lambdas and explicit native methods; [choosing a level](features/FLUENT_CYPHER.md#choosing-a-level) shows when to use each. All three share the context's transactions and driver; LINQ and Fluent Cypher also use the EF mappings, while native Cypher uses the names you write. Unsupported LINQ shapes are refused while the query is compiled. There is no client-side fallback that could silently read a whole graph.

## Writing

`SaveChangesAsync` writes all pending EF changes in one transaction: nodes, references, associations and identified relationships together. Deletion is restrictive: a node with remaining relationships is not deleted, and there is no silent `DETACH DELETE`. Row versions (`IsRowVersion()`) and concurrency tokens give optimistic concurrency. Persistence and query enumeration are async only. See [transactions and concurrency](features/TRANSACTIONS_AND_CONCURRENCY.md) and [bulk mutations](features/BULK_MUTATIONS.md).

## Developer tools

| Tool | What it answers | Where |
|---|---|---|
| `ToQueryString()`, `Inspect()` | What Cypher will run, and its typed pattern | Provider |
| Doctor | Do my model and registered queries compile, and what does this database's schema provide? | `GraphBridge.Tooling`; [query evidence](tools/DOCTOR.md) |
| Verify | Do registered queries plan, and do they match independent references? | `GraphBridge.Tooling`; [query evidence](tools/DOCTOR.md) |
| Query Lab | Show me C#, Cypher, pattern and plan side by side | HTML report or VS Code; [Query Lab](tools/QUERY_LAB.md) |
| Migration plans | Change schema and data deliberately, with preview and recovery | Provider; [migration recovery](features/SCHEMA_AND_MIGRATIONS.md#migration-recovery-example) |
| Authoring tools | Start from an ontology or an existing database | `GraphBridge.Tool`; [samples](../samples/README.md) |
