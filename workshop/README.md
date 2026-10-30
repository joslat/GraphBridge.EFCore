# GraphBridge workshop

Learn GraphBridge by running it. The workshop is one menu-driven console application with 36 short lessons, from a first `DbContext` to native relationships, Fluent Cypher, Doctor and Query Lab, and an experimental GraphRAG track. Each lesson has a script to read and code that prints what it does, the Cypher it generates and what it observed. The core track (★) takes about three hours.

```powershell
# The interactive menu
dotnet run --project workshop/GraphBridge.Workshop

# The core track, in order
dotnet run --project workshop/GraphBridge.Workshop -- run --track core
```

Lessons marked ◇ run without a database. For the others, [get a disposable Neo4j](GraphBridge.Workshop/README.md#get-a-database): the workshop only uses a database on this machine that holds nothing but its own data, unless you explicitly allow more.

**Start with the [lesson list and setup](GraphBridge.Workshop/README.md).**

## What is where

| Folder | Contents |
|---|---|
| [GraphBridge.Workshop/Lessons](GraphBridge.Workshop/Lessons) | One folder per lesson: `README.md` is the script, `Lesson.cs` is the code that runs |
| [GraphBridge.Workshop/Domain](GraphBridge.Workshop/Domain) | The movie graph model and seed data every lesson shares |
| [GraphBridge.Workshop/Mapping](GraphBridge.Workshop/Mapping) | A separate project built by the mapping source generator (lesson 25) |
| [GraphBridge.Workshop/snapshots](GraphBridge.Workshop/snapshots) | Reviewed Cypher that each lesson must still generate |
| [GraphBridge.Workshop/Runner](GraphBridge.Workshop/Runner) | The menu, connection settings, safety guard and lesson runner |
| [tests/GraphBridge.Workshop.Tests](../tests/GraphBridge.Workshop.Tests) | Runs every lesson offline and, with a database, live |

## After the workshop

The [samples](../samples/README.md) are complete applications to copy from: the three ways to author a model (code-first, ontology-first and database-first), design-time commands, a Fluent Cypher query gallery, vector search through VectorData, and three GraphRAG applications. The [documentation index](../docs/README.md) lists the contracts and limits behind every lesson.
