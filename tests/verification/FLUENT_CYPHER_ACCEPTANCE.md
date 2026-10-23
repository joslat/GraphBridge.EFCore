# Fluent Cypher acceptance — FGQ-0 through FGQ-3 and v2 waves 1–3

Accepted 2026-10-12 within the [documented read-only contract](../../docs/features/FLUENT_CYPHER.md). Core live/package checkpoint: `7bebda6c86d2edb54eb5e736d8e3bb5cf189dadb`. Hosted run 38181225124 completed successfully on that commit. Initial reader correction: `dde143e447bdba74a8a951cdf43a460e40ff6244` preserves converter-produced nulls and passes **747 provider units, zero failures/skips**, in unit-only run 38181777149. This correction changes only scalar result conversion; generated Cypher and execution ownership are unchanged. Live/package checks were not rerun at that narrow reader checkpoint; the subsequent architecture review below reruns them for the corrected compiler.

## v2 wave 3 — whole entities (23 October 2026)

[ADR 0049](../../docs/architecture/adr/ADR-0049.md) returns mapped nodes and relationships as untracked entities, alone or in DTOs, with bounded `Graph.Collect`/`CollectDistinct` lists and ordering by a returned entity's member. ADR 0050's Fluent `TagWith` and `WithTimeout` were verified in the same run.

| Gate | Actual result |
|---|---|
| Provider units | **844 passed**, zero failures/skips, including `FluentCypherEntityReturnTests` (11), the updated diagnostic-code, compiler and aggregate tests, and `OperationalControlsTests` |
| Compiled gallery | Sample 05 `--check`: nine compiled snapshots match, including the new `filmographies` |
| Live entity returns | The five `ProviderFluentEntityReturnTests` pass on the owned Neo4j 2026.09.0 Community fixture: node entities equal `AsNoTracking` LINQ for every mapped property type in the model (`Guid` key with a store name, strings, nullable string, `int`, `long` extremes, `double`, `bool`, enum, `DateTimeOffset` at three offsets, nullable `int`, a converted string, an optional native-reference foreign key present and absent); identified-edge and association entities equal LINQ, including endpoint keys after `With(edge)`; OPTIONAL MATCH gives null; collected lists group by the returned entity; ordering by a returned member works after aggregation and after `ReturnDistinct`; an ambiguous native reference is refused with LINQ's message; nothing is tracked afterwards |
| Gallery oracle | `C9_whole_entities_and_collected_entity_lists_match_native_nodes` passes against native Cypher, with parallel credits collapsed by `CollectDistinct` |
| Defect found live | Relationship returns first rendered endpoint keys as `` startNode(r) {.`id`} ``, which Neo4j refuses (42I06: a map projection needs a variable). The unit tests had pinned that text. Endpoint keys are now map literals, `` {`id`: startNode(r).`id`} `` |
| Fluent tags and timeouts | A Fluent query with `TagWith` and `WithTimeout` runs; `WithTimeout` inside an application transaction is refused (`ProviderOperationalControlsTests`) |
| Regressions | Whole Integration suite **305**, Specification **81**, Tooling **23** and Workshop **9** (all lessons live, including lesson 16's entity returns) pass with zero failures/skips |

## v2 wave 2 — language depth, aggregates, codes and readable predicates (16 October 2026)

[ADR 0047 Amendment 1](../../docs/architecture/adr/ADR-0047.md#amendment-1-language-depth-and-embedded-predicates) adds `values.Contains(x)` → `IN`, ordinal `StartsWith`/`EndsWith`/`Contains`, `!` over operands that can never be null, enum ordering and `Graph.Is(predicate)`, with explicit native twins (`In`, `StartsWith`, `EndsWith`, `Contains`). [ADR 0048](../../docs/architecture/adr/ADR-0048.md) adds `Min`, `Max`, `Sum`, `Avg` and server-bounded `Collect`. Every Fluent refusal now has a stable code (GBFC1xx–4xx), carried into Doctor as `subcode`. Generated predicates are flat (`a AND b AND c`) with parentheses only where AND and OR mix and around a `NOT` operand. Local evidence, pinned SDK 10.0.401, commit `a89929c` and its follow-up commits of 16 October 2026 on top of `89f7ff3`:

| Gate | Actual result |
|---|---|
| Independent review | A review agent read the lambda surface against ADR 0047 and found three shapes where C# and Cypher could disagree (inexact double-to-integer narrowing, enum truncation, a converted property compared with a WITH value) plus user-code and uncoded-exception risks. All were fixed with regression tests before acceptance |
| Provider units | **789 passed**, zero failures/skips, including `FluentCypherLambdaDepthTests`, `FluentCypherAggregateTests` and `FluentCypherDiagnosticCodeTests` |
| Live agreement oracle | 18 rebound predicates (10 new: `IN` with and without null elements, a `Guid` set, string operators, negation) equal C# evaluation over the seed rows; an unguarded string test on a null member agrees with EF-style null semantics; embedded EXISTS under `\|\|` and `!`; per-person aggregates after OPTIONAL MATCH equal a C# oracle including all-null groups |
| Live suites | On pinned Neo4j 2026.09.0 Community, zero failures/skips: **20** Fluent cases (`FullyQualifiedName~ProviderFluent`, including aggregates, returned predicates and the agreement oracle) and the **full integration suite (280)**, which includes the native-query and path cases |
| Compiled gallery | The six reviewed `.cypher` snapshots were regenerated for readable predicates; every diff removes parentheses only, and `--check` passes |
| Workshop | All 31 lessons pass live; nine Fluent snapshots changed by parentheses only and were reviewed; lessons 16–18 teach `Contains`, ordinal `StartsWith`, `!`, aggregates and `Graph.Is`; README Cypher blocks are now checked against the snapshots |
| Tooling units | **265 passed**, including the Doctor subcode assertion |
| Gallery follow-up | `samples/05-FluentCypher` gains `CastSearch` (`IN`, ordinal `STARTS WITH`) and `Careers` (returned `min`, `max` and bounded `collect(DISTINCT)` after OPTIONAL MATCH). `FriendsReaching` now uses the EXISTS lambda overload and `Graph.Is`, with byte-identical Cypher. Live controls C7 and C8 equal hand-written native Cypher; all eight snapshots match |
| Enum live case | `Enum_comparisons_and_ordering_use_the_numeric_value_like_CSharp`: six enum predicates, including a nullable enum and `!`, and descending enum ordering equal C# over values whose numeric order differs from their name and text order |
| Focused live rerun | **23** Fluent cases (`FullyQualifiedName~ProviderFluent`) and the 6 workshop cases pass, zero failures/skips, after the workshop moved to `workshop/` |

**Fingerprint change.** Readable predicates change the text, and therefore the statement fingerprint, of every Fluent query with a WHERE clause. Verify baselines recorded before this change report those statements as changed and need one review. This happens before the first package release.

## v2 wave 1 — C# lambda predicates (16 October 2026)

[ADR 0047](../../docs/architecture/adr/ADR-0047.md) adds `Where(() => ...)`, `OptionalMatch(pattern, where)`, lambda ordering and anonymous edge overloads (`Out<TEdge>(node)` / `In<TEdge>(node)`). A lambda is accepted only where its native WHERE result equals its C# meaning; every other shape is rejected before transport with a `GBFC1xx` code. Local evidence, pinned SDK 10.0.401, working tree later committed as `a89929c` on top of `89f7ff3`:

| Gate | Actual result |
|---|---|
| Provider units | **775 passed**, zero failures/skips. Nine new `FluentCypherLambdaTests`: identical text and parameters for lambda and explicit forms; every accepted shape's Cypher; WITH-value comparison and ordering; inline optional predicate; never-null rules inside and after OPTIONAL MATCH and after WITH; nine rejection codes; captured-value snapshot; a captured getter is never invoked; EXISTS composition; anonymous edge role validation |
| Compiled gallery | All six `samples/05-FluentCypher` factories rewritten with lambdas, anonymous edges and the inline optional predicate; `--check` matches the six unchanged reviewed `.cypher` snapshots byte for byte |
| Tooling units | **265 passed**, zero failures/skips (catalog, Doctor, Verify, Query Lab and snapshot tests consume the rewritten gallery) |
| Focused live suite | **28 passed**, zero failures/skips, on pinned Neo4j 2026.09.0 Community: 13 existing Fluent cases (C1–C6 now run the rewritten gallery), 4 new `ProviderFluentLambdaTests`, 7 native-query and 4 path cases |
| Live agreement oracle | Eight accepted predicates, each written once as `Expression<Func<Person, bool>>`, rebound onto a Fluent symbol for the server and compiled for in-memory evaluation over the seed rows: identical results, including missing properties and captured nulls. A control shows the rejected `Nickname != "Bob"` really disagrees on the server (C# keeps 5 people, native keeps 3), and the explicit forms reproduce each meaning |
| Optional and keys-to-EF cases | Inline OPTIONAL MATCH predicate and post-WITH filter equal a left-join oracle; Fluent keys loaded through bounded EF `Contains` produce two tracked, unchanged entities |
| Tooling live suite | **22 passed**, zero failures/skips |
| Offline package consumer | `Test-ProviderPackage.ps1` packs, restores and builds a fresh PackageReference consumer with zero warnings; a new check proves lambda Where and ordering compile to the explicit form from the nupkg. Nothing was published |
| Fixture | Owned disposable fixture started for these runs and removed afterwards |

Not rerun for this checkpoint: the full integration suite (not enough memory headroom above the shared 5 GiB reserve while the fixture ran), specification, Enterprise and benchmark suites, and the Query Lab TypeScript suite (it requires Node 24; Node 22 is installed). The changes are confined to the Fluent namespace and its compiler; ordinary LINQ translation is unchanged and covered by the full provider unit suite.

Wave 1 did not translate `!`, enum ordering, `IN` through `list.Contains(x)` or string operators; wave 2 (above) adds them. Explicit predicate methods remain the native escape hatch.

## Review checkpoint — 12 October 2026

The subsequent correctness and architecture review corrects optional endpoint FK reads, lexical EXISTS reuse and unsupported capture diagnostics. Final local evidence: **751 provider units**, **24 live tests** (13 fluent, seven native, four path), zero failures/skips, and integration build with zero warnings/errors. A fresh package-only offline/live consumer passes for `0.1.0-packagecheck.c7238363d1c945b7a1d7b2dd9e1415c7`, including fluent DTOs and existing CRUD/version/rollback checks. The owned fixture was removed. No hosted workflow or package publication was used for this review.

The four new unit cases and four new live cases extend earlier acceptance with absent endpoint nulls/counts, renamed principal properties, incoming roles, edge-only WITH, repeated/nested EXISTS and native clause-order parity. The supported experimental scope remains unchanged.

## Original implementation evidence

| Gate | Actual result |
|---|---|
| Locked restore and serialized provider unit/integration project builds | Passed; zero warnings/errors |
| Full provider unit regression | Core checkpoint: **746 passed**, 28 new fluent cases. Final reader checkpoint: **747 passed**, 29 new fluent cases. Zero failures/skips in both. |
| Focused live suite | **20 passed**: 9 fluent, 7 existing native-query, 4 existing path tests |
| Fresh package-only offline consumer | Pack, actual PackageReference restore/build, fluent compilation/parameter isolation, existing EF registration/translation checks passed |
| Fresh package-only live consumer | Fluent DTO read observed a committed EF write; remained untracked and closed its owned transaction; existing CRUD/version/rollback/delete smoke passed |
| Disposable fixture | Pinned Neo4j 2026.09.0 Community; unique fixture container and network removed successfully |
| Local precursor | 6 syntax tests, then 18 syntax/compiler tests passed before memory fell below the local reserve; these are subsets, not additional unique tests |

The initial 29 added unit cases comprise 6 syntax, 13 compiler/projection, 6 bounded-path and 4 native lifecycle cases. They cover alias identity and scope, immutable patterns, parameters/converters, role validation, EF filter rejection, projection errors, optional null slots, path shape/relationship reuse, transaction ownership and uncertain commit handling. The full provider unit suite also exercises the shared native cancellation/concurrency/diagnostic pipeline.

The provider package used a unique local verification version `0.1.0-packagecheck.717fd97ad0374c70b68982b2c17f7c54`; it was consumed rather than published. This acceptance does not imply a new public package or production release.

## Semantic acceptance map

| Case / phase | Working behavior and independent control |
|---|---|
| FGQ-0 | Compiled explicit native predicates, opaque typed aliases, immutable stages and DTO expressions; [ADR 0041](../../docs/architecture/adr/ADR-0041.md) records the selected semantics |
| C1 / FGQ-1 | Co-actors over incoming/outgoing credits equal the handwritten native ordered DISTINCT result, including parallel credits; one MATCH versus separate MATCH clauses preserve relationship-reuse differences |
| C2 / FGQ-1 | Identified credit IDs, derived endpoint FK, role/billing predicates and GUID parameters equal the native control; parallel physical edges remain distinct |
| C3 / FGQ-2 | Optional movie predicates attached to OPTIONAL MATCH versus after WITH equal their separate native controls; null DTO slots and row counts differ as intended |
| C4 / FGQ-2 | Shared-movie scores equal the native count(DISTINCT movie)/count(*) control; parallel credits do not inflate distinct-movie scores |
| C5 / FGQ-2 | Two-hop friends-of-friends excludes self and direct friends using correlated NOT EXISTS; exact ordered results equal the native control |
| C6 / FGQ-3 | Outgoing/incoming/undirected path signatures equal native controls over cycles, self-loops and parallel physical edges; zero hops, missing/optional paths, finite depth/rows and detached lifetime verified |
| C7 / v2 | A captured name list (`IN`) and an ordinal title prefix (`STARTS WITH`) equal the native control in five cases, including an empty list and a case-sensitive miss |
| C8 / v2 | Returned `min`, `max` and `collect(DISTINCT)` after OPTIONAL MATCH group by name like the native control; people without credits keep null years and an empty list |
| Additional fluent live checks | Native null equality/NOT/order and borrowed read-own-write/rollback/cancellation behavior |

C2 and C3 use one small movie graph instead of adding separate employment/organization fixtures. This changes the example domain, not the acceptance requirement: mapped edge payload/identity and optional relationship predicate placement are exercised independently against native Cypher. C1 and C4 invoke the exact [sample query factory source](../../samples/05-FluentCypher/FluentMovieQueries.cs) compiled into the integration project.

Live facts are in [ProviderFluentCypherTests](../GraphBridge.IntegrationTests/ProviderFluentCypherTests.cs) and, for the lambda agreement oracle, [ProviderFluentLambdaTests](../GraphBridge.IntegrationTests/ProviderFluentLambdaTests.cs). The existing [native execution](../GraphBridge.IntegrationTests/ProviderNativeQueryTests.cs) and [bounded-path](../GraphBridge.IntegrationTests/ProviderPathTests.cs) suites protect the shared transaction and validation behavior.

## Reproduction and scope

The historical hosted checks used the `fluent` and `fluent-units` profiles. Current public CI builds and packages only; it does not rerun these results. To reproduce locally with the pinned SDK, an owned disposable fixture and the memory rules in [TESTING.md](../README.md):

```powershell
dotnet test tests/GraphBridge.UnitTests --no-restore -m:1 -p:UseSharedCompilation=false -nodeReuse:false
pwsh -File eng/scripts/Start-Neo4jFixture.ps1
pwsh -File eng/scripts/Run-IntegrationTests.ps1 -NoBuild -Filter 'FullyQualifiedName~ProviderFluentCypherTests|FullyQualifiedName~ProviderFluentLambdaTests|FullyQualifiedName~ProviderNativeQueryTests|FullyQualifiedName~ProviderPathTests'
pwsh -File eng/scripts/Stop-Neo4jFixture.ps1
```

The hosted run used one fresh VM and its owned disposable fixture; local foreign containers were untouched. It did not rerun all unrelated tooling, generator, upstream specification, security, Enterprise or benchmark suites. Existing broader acceptance remains historical, not silently relabeled as a new full-suite run. No new benchmark or production-scale path-cost claim is made.

This closes all four FGQ stages, not all Cypher or all EF Core features. Writes, tracked entity returns, arbitrary procedure/raw clause composition, shortest/weighted paths, heterogeneous repeated relationships and general language coverage remain outside this contract. Wave 1 of the v2 ergonomics is recorded above and in [ADR 0047](../../docs/architecture/adr/ADR-0047.md).
