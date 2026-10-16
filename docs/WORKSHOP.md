# Workshop

The workshop teaches GraphBridge by running it. It is one menu-driven console application in [`workshop/`](../workshop/README.md) with 31 short lessons. Each lesson has a script to read and code that prints what it does, the Cypher it generates and what it observed. The core track (14 lessons) takes about three hours, longer with the exercises.

```powershell
# The interactive menu
dotnet run --project workshop/GraphBridge.Workshop

# The core track, in order
dotnet run --project workshop/GraphBridge.Workshop -- run --track core
```

Offline lessons need nothing else. The others need a disposable Neo4j on your machine; the [lesson list and setup](../workshop/GraphBridge.Workshop/README.md) shows a one-command Docker setup. The workshop refuses remote databases and databases that hold other data, and allows one workshop process per database.

## What each part teaches

| Part | Lessons | You learn | Read more |
|---|---|---|---|
| A · Foundations | 00–07 | A first context, mapping nodes, schema before data, CRUD, LINQ and its refusals, projections | [Getting started](GETTING_STARTED.md), [mapping nodes](features/MAPPING_NODES.md), [LINQ queries](features/LINQ_QUERIES.md) |
| B · Relationships | 08–11 | References and Include, optional references, associations, identified relationships | [References](features/REFERENCES.md), [associations](features/ASSOCIATIONS.md), [identified relationships](features/IDENTIFIED_RELATIONSHIPS.md) |
| C · Correctness | 12–14 | Optimistic concurrency, transactions, nulls, types and time | [Transactions and concurrency](features/TRANSACTIONS_AND_CONCURRENCY.md), [nulls, types and time](features/NULLS_TYPES_AND_TIME.md) |
| D · Graph-native querying | 15–22 | Native Cypher, Fluent Cypher, bounded paths, search, bulk mutations, and one question answered three ways | [Native Cypher](features/NATIVE_CYPHER.md), [Fluent Cypher](features/FLUENT_CYPHER.md) |
| E · Operating the graph | 23–24 | Migration plans, deployment preflight and command tracing | [Schema and migrations](features/SCHEMA_AND_MIGRATIONS.md), [command tracing](features/COMMAND_TRACING.md) |
| F · Developer extensions | 25–30 | The mapping generator, Doctor, Verify, Query Lab, design-time commands, database-first authoring | [Tools](README.md#tools) |

The core track is lessons 00–06, 08, 10, 16, 17, 22, 26 and 28.

## How the workshop stays correct

Code shown in each script is the code that runs, and every lesson's generated Cypher is compared with a reviewed snapshot. The repository's tests run every lesson offline and, with a database, live. If an experiment or an interrupted run changes the data, the next run says so and `reset` restores the seed.
