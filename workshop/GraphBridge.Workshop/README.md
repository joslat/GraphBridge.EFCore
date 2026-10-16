# GraphBridge workshop

A guided tour of GraphBridge, one topic per lesson. Each lesson is a folder under [Lessons](Lessons) with a script (`README.md`) and the code that runs (`Lesson.cs`). Follow the lessons in order and you will have used every important feature: EF Core mappings and LINQ, native relationships, Fluent Cypher, and the developer tools.

Lessons print their own code, the Cypher it generates and what they observed. Code shown in a script is checked against the running code, and generated Cypher is checked against reviewed snapshots in [snapshots](snapshots).

## Start

You need the .NET SDK version pinned in [global.json](../../global.json) (10.0.401): `dotnet --version` must print it. Install it from [dotnet.microsoft.com](https://dotnet.microsoft.com/download/dotnet/10.0) if not.

```powershell
# The interactive menu
dotnet run --project workshop/GraphBridge.Workshop

# The lessons, by part
dotnet run --project workshop/GraphBridge.Workshop -- list

# One lesson
dotnet run --project workshop/GraphBridge.Workshop -- run 01

# The core track, in order
dotnet run --project workshop/GraphBridge.Workshop -- run --track core
```

Lessons marked ◇ need no database. Add `--offline` to any command to ignore a configured database. `check` runs every lesson offline and compares the Cypher of each offline part with the reviewed snapshots; lessons that need a database are reported as not run. Lessons 03, 04, 08, 10 and 11 compare their Cypher when they run against Neo4j, as the live test suite does.

## Get a database

The workshop writes Person, Movie and Review nodes and can reset them, so give it a **disposable** Neo4j. With Docker and PowerShell 7, this keeps the password out of command lines and shell history:

```powershell
$env:GRAPHBRIDGE_WORKSHOP_PASSWORD = Read-Host -MaskInput 'Choose a Neo4j password (8+ characters)'
$env:NEO4J_AUTH = "neo4j/$env:GRAPHBRIDGE_WORKSHOP_PASSWORD"
docker run -d --name graphbridge-workshop -p 127.0.0.1:7687:7687 -p 127.0.0.1:7474:7474 `
  --memory 2g -e NEO4J_AUTH neo4j:2026.09.0-community
$env:GRAPHBRIDGE_WORKSHOP_URI = 'bolt://localhost:7687'
```

`-e NEO4J_AUTH` without a value passes the variable from your session. Neo4j refuses passwords shorter than eight characters. Optional: `GRAPHBRIDGE_WORKSHOP_USERNAME` (default `neo4j`) and `GRAPHBRIDGE_WORKSHOP_DATABASE` (default `neo4j`). Settings come only from environment variables, never from command arguments or files in the repository. Lesson 20 (search) needs Neo4j 2026.09 or later; the other lessons run on any version the provider supports. Repository maintainers can use the owned fixture instead: the workshop also reads the fixture's `GRAPHBRIDGE_NEO4J_*` settings. If another Neo4j already uses port 7687 or 7474, map different host ports, for example `-p 127.0.0.1:7688:7687 -p 127.0.0.1:7478:7474`, and set `GRAPHBRIDGE_WORKSHOP_URI` to `bolt://localhost:7688`.

**Safety.** The workshop refuses a database that is not on this machine (pass `--allow-remote` for a disposable remote one), and a database that holds other data (pass `--allow-other-data` for a disposable shared one). It refuses a database whose Person, Movie or Review nodes it did not create. Once it has created its ownership marker, it treats every Person, Movie and Review node in that database as workshop data, so with `--allow-other-data` use a database where no other application writes those labels. `reset` shows how much it will delete, asks you to type `reset` (in scripts, `reset --confirm`), and deletes nothing if the data changed in the meantime. Run one workshop process per database at a time; a second one is refused, because two runs change each other's data.

The first lesson that needs data applies the reviewed uniqueness constraints (lesson 03) and loads a small movie graph through EF Core. If a lesson is interrupted or an experiment changes the data, the next run warns that the graph differs from the seed; `reset` restores it.

## The movie graph

Nine people, five movies, four reviews and the relationships between them:

```text
(:Person)-[:ACTED_IN {role, billing}]->(:Movie)    identified relationship: Hugo Weaving and Tom Hanks each have two roles in Cloud Atlas
(:Person)-[:DIRECTED]->(:Movie)                    optional reference: John Wick has no director
(:Movie)-[:HAS_REVIEW]->(:Review)                  required reference
(:Person)-[:LIKES {stars}]->(:Movie)               association with skip navigations
(:Person)-[:FOLLOWS]->(:Person)                    association: Keanu → Carrie-Anne → Laurence → Keanu is a cycle
```

## Lessons

★ core track (about three hours to read and run, by the lessons' own estimates; longer with the exercises) · ◇ offline · ● needs Neo4j

| # | Lesson | Mode | You learn |
|---|---|---|---|
| **A** | **Foundations** | | |
| 00 ★ | [Setup check](Lessons/00-SetupCheck/README.md) | ◇● | Packages, connection settings and the safety guard |
| 01 ★ | [Your first context](Lessons/01-FirstContext/README.md) | ◇ | `UseNeo4j` with an application-owned driver; the first query; async only |
| 02 ★ | [Mapping nodes](Lessons/02-MappingNodes/README.md) | ◇ | Labels, application keys, property names, the storage manifest |
| 03 ★ | [Schema before data](Lessons/03-SchemaFirst/README.md) | ● | Inspection, reviewed proposals, explicit constraints |
| 04 ★ | [Create, read, update, delete](Lessons/04-Crud/README.md) | ● | `SaveChangesAsync`, tracking, row versions |
| 05 ★ | [LINQ queries and visible Cypher](Lessons/05-LinqQueries/README.md) | ◇● | Filters, ordering, aggregates, `Contains`, C# null semantics |
| 06 ★ | [The failure lab](Lessons/06-FailureLab/README.md) | ◇ | Refused shapes and why there is no client-side fallback |
| 07 | [Projections and aggregates](Lessons/07-Projections/README.md) | ◇● | DTOs, dependents' integrity checks, integer `Min`/`Max` |
| **B** | **Relationships** | | |
| 08 ★ | [One-to-many references and Include](Lessons/08-References/README.md) | ● | `ToGraphReference`, `Include`, fixup, a documented limit |
| 09 | [Optional references](Lessons/09-OptionalReferences/README.md) | ● | Attach, replace and remove a parent through the foreign key |
| 10 ★ | [Associations (many-to-many)](Lessons/10-Associations/README.md) | ● | Join entity as one relationship; skip navigations; payload |
| 11 | [Identified relationships](Lessons/11-IdentifiedRelationships/README.md) | ● | Relationship keys and parallel relationships |
| **C** | **Correctness** | | |
| 12 | [Optimistic concurrency](Lessons/12-Concurrency/README.md) | ● | Row versions, refused stale saves, recovery |
| 13 | [Transactions](Lessons/13-Transactions/README.md) | ● | Shared transactions, read-your-writes, what rollback does not undo |
| 14 | [Nulls, types and time](Lessons/14-NullsTypesTime/README.md) | ◇● | C# vs Cypher null semantics, Guid storage, timestamp offsets |
| **D** | **Graph-native querying** | | |
| 15 | [Native Cypher with a projector](Lessons/15-NativeCypher/README.md) | ● | Parameterized Cypher, projectors, row caps |
| 16 ★ | [Fluent Cypher I: patterns](Lessons/16-FluentPatterns/README.md) | ◇● | Symbols, direction, lambda and explicit predicates, lists and text, relationship uniqueness |
| 17 ★ | [Fluent Cypher II: optional matches and counts](Lessons/17-FluentOptionalAndCounts/README.md) | ◇● | Predicate ownership, rows vs distinct counts, aggregates, a refused predicate |
| 18 | [Fluent Cypher III: existence](Lessons/18-FluentExists/README.md) | ◇● | Correlated `EXISTS` and `NOT EXISTS`, and EXISTS inside a lambda with `Graph.Is` |
| 19 | [Bounded paths](Lessons/19-BoundedPaths/README.md) | ◇● | Repeated patterns, cycles, `QueryPathsAsync`, `HasMorePaths` |
| 20 | [Full-text and vector search](Lessons/20-Search/README.md) | ● | Explicit indexes, scores, provenance (Neo4j 2026.09+) |
| 21 | [Bulk update and delete](Lessons/21-BulkMutations/README.md) | ● | `ExecuteUpdateAsync`, `ExecuteDeleteAsync`, stale tracked entities |
| 22 ★ | [Capstone: one question, three query surfaces](Lessons/22-ThreeSurfaces/README.md) | ◇● | LINQ, Fluent and native Cypher side by side |
| **E** | **Operating the graph** | | |
| 23 | [Migration plans](Lessons/23-MigrationPlans/README.md) | ◇● | Typed plans, offline preview, journal, idempotent apply |
| 24 | [Deployment preflight and command tracing](Lessons/24-PreflightAndTracing/README.md) | ● | A schema gate; ActivitySource tracing without leaking data |
| **F** | **Developer extensions** | | |
| 25 | [Mapping source generator](Lessons/25-MappingGenerator/README.md) | ◇ | Attributed mappings, build-time diagnostics, `Verify` |
| 26 ★ | [GraphBridge Doctor](Lessons/26-Doctor/README.md) | ◇● | Query contracts, offline and connected evidence |
| 27 | [GraphBridge Verify](Lessons/27-Verify/README.md) | ◇● | Compile and EXPLAIN every registered contract |
| 28 ★ | [Query Lab](Lessons/28-QueryLab/README.md) | ◇ | HTML report and VS Code evidence for your queries |
| 29 | [Design-time migration commands](Lessons/29-DesignTime/README.md) | ◇● | In-app `list`, `preview` and `status` with JSON output |
| 30 | [Authoring toolchains: database-first](Lessons/30-AuthoringToolchains/README.md) | ● | Inspect your graph, review keys, scaffold an EF model |

The full track is 31 lessons (00–30); `list` shows them all with their modes.

## Experimenting

The "Try it yourself" exercises ask you to change code. When you do, a lesson's Cypher no longer matches its reviewed snapshot and its checks may fail. That is expected: the checkpoints describe the original lesson. Restore it with `git checkout -- workshop/GraphBridge.Workshop`, and run `reset` if an experiment left data behind.

## For maintainers

- `python workshop/GraphBridge.Workshop/fill-excerpts.py` refreshes README code excerpts from the marked source regions; `--check` reports drift.
- `dotnet run --project workshop/GraphBridge.Workshop -- run --track full --update-snapshots <empty folder>` against a database writes every lesson's Cypher, including the connected-only lessons; `check --update-snapshots <folder>` writes only the offline parts. Compare with `snapshots/`, review every change, then copy it over.
- To remove everything the workshop created, including its marker, constraints and indexes, delete the container: `docker rm -f graphbridge-workshop`.
- `tests/GraphBridge.Workshop.Tests` runs every lesson offline and, with the owned fixture, live: `pwsh -File eng/scripts/Run-IntegrationTests.ps1 -Suite Workshop`.
