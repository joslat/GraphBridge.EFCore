# Fluent Cypher: compiled query gallery

[FluentMovieQueries.cs](FluentMovieQueries.cs) contains ordinary C# models, their EF graph mapping, and nine reusable query factories written with C# lambda predicates, lambda ordering and anonymous edges (`actor.Out<Credit>(movie)`). The standalone sample compiles them without connecting to Neo4j. The repository integration tests use the same source with independent handwritten native controls. Where a factory also has an explicit predicate-method form, both compile to the same Cypher; [choosing a level](../../docs/features/FLUENT_CYPHER.md#choosing-a-level) explains when to use which.

```powershell
dotnet restore samples/05-FluentCypher/FluentCypher.Sample.csproj --locked-mode --disable-parallel
dotnet run --project samples/05-FluentCypher/FluentCypher.Sample.csproj --no-restore
# Compare actual compiler output with the reviewed .cypher files:
dotnet run --project samples/05-FluentCypher/FluentCypher.Sample.csproj --no-build -- --check
```

These commands only inspect queries; they require no credentials or running server. JSON output contains parameterized Cypher, not parameter values. Graph labels/property names still describe the model. An application must configure its own context and explicitly execute a factory to retrieve data.

[FluentMovieContracts.cs](FluentMovieContracts.cs) registers three of these same factories for the optional Tooling evidence API. Run `--doctor` for redacted JSON or `--doctor-text` for a readable model/query report. These modes omit compiled Cypher and parameter values by default; they compile only and do not certify live results. The catalog records null/duplicate/order expectations and declared execution budgets. [Contract and Doctor API](../../docs/tools/DOCTOR.md).

`--doctor-inspect` is the explicit connected exception: it reads metadata using the four documented `GRAPHBRIDGE_NEO4J_*` environment variables and compares it with this movie model. It executes no registered results or DDL. Missing constraints produce a failing report and exit 1; missing configuration exits 2. [Connected mode and warnings](../../docs/tools/DOCTOR.md#explicit-connected-read-only-doctor).

`--verify` and `--verify-text` run the same three registered contracts through GraphBridge Verify V0, producing JSON or a summary table without connecting. `--verify-inspect` explicitly enables V1: metadata inspection followed by EXPLAIN for each required compiled query. It returns exit 0 only when the selected tier passes, 1 for blocked/failed/unobserved requirements, or 2 for invalid connected configuration. V1 retains a redacted operator tree; it executes no query results, PROFILE or DDL and does not prove semantic equivalence. [Verify API and limits](../../docs/tools/VERIFY.md#graphbridge-verify-v0-and-v1).

| Factory / actual generated Cypher | Meaning | Independent live control |
|---|---|---|
| [Coactors](queries/coactors.cypher) | DISTINCT coactor names; parallel credits can create duplicate physical matches | `C1_coactors_distinct_rows_and_relationship_reuse_match_native_clauses` |
| [SharedMovies](queries/shared-movies.cypher) | Distinct movie count alongside physical match count | `C4_count_rows_and_distinct_movies_preserve_recommendation_scores` |
| [OptionalMovies](queries/optional-movies.cypher) | Movie predicate belongs to OPTIONAL MATCH; unmatched people survive with null movie slots | `C3_optional_pattern_filter_and_post_WITH_filter_match_different_native_controls` |
| [FilteredOptionalMovies](queries/filtered-optional-movies.cypher) | Same predicate after WITH; null movie rows are filtered out | Same C3 control; deliberately different result cardinality |
| [FriendsReaching](queries/nested-exists.cypher) | Nested EXISTS finds a friend who reaches the named person; local aliases do not escape. Written with the EXISTS lambda overload and `Graph.Is` | `Reusable_nested_EXISTS_is_lexically_scoped_and_does_not_export_bindings` |
| [CastSearch](queries/cast-search.cypher) | A captured name array becomes `IN` with one list parameter; ordinal `StartsWith` becomes `STARTS WITH` | `C7_list_membership_and_ordinal_prefix_match_native_IN_and_STARTS_WITH` |
| [Careers](queries/careers.cypher) | `min`, `max` and a bounded `collect(DISTINCT)` returned directly, so Cypher groups by name; people without credits keep null years and an empty list | `C8_returned_aggregates_group_by_name_and_keep_people_without_credits` |
| [Filmographies](queries/filmographies.cypher) | Whole entities: each person and a bounded, distinct list of their movies, read as untracked EF entities from a map of their mapped properties | `C9_whole_entities_and_collected_entity_lists_match_native_nodes` |
| [FriendPaths](queries/friend-paths.cypher) | One to three hops, at most 500 paths, explicit direction | `C6_bounded_paths_preserve_cycles_parallel_relationships_self_loops_and_direction` |

`--verify-semantic` and `--verify-semantic-text` run three independent V2 controls from [FluentMovieReferences.cs](FluentMovieReferences.cs). They compare the actual coactor, optional-movie and shared-count factories with hand-written Cypher and fixed expected rows. The V2 fixture uses year threshold **2000**, so the optional case includes both duplicate old-movie credits and unmatched people. This differs intentionally from the gallery's 2020 parameter.

`--lab` produces the self-contained Query Lab HTML report offline. `--lab-inspect` explicitly adds V1 metadata/plans; `--lab-semantic` explicitly adds V2 results under the same disposable-fixture requirements. Lab modes deliberately disclose the synthetic mapped Cypher and source excerpts embedded from this sample's actual build, while keeping parameters/results redacted. Build first, then redirect the DLL's output to a UTF-8 `.html` file; do not mix build logs into HTML. [Query Lab commands, presentation and bounds](../../docs/tools/QUERY_LAB.md).

`--lab-json` exports the corresponding offline JSON/source envelope for the optional [VS Code Query Lab viewer](../../extensions/graphbridge-query-lab/README.md). Run the built DLL and redirect stdout to a `.json` file. It includes statement/schema text and C# excerpts, but no parameter values or database connection.

The built DLL also implements the one-shot `--lab-host` [capture protocol](../../extensions/graphbridge-query-lab/HOST_PROTOCOL.md). The extension can explicitly request offline V0, connected V1 or owned-fixture V2 through this application-owned entrypoint. Requests contain no query text, parameters or credentials. Standard input/output is reserved for bounded protocol JSON; unlike ordinary CLI modes, a successfully delivered response exits zero even when its nested verification report fails. Inspect the report's status/exit code. Existing environment and fixture requirements remain mandatory; the command does not provision or seed anything.

These commands require a separately provisioned disposable loopback fixture and explicit acknowledgment; they do not seed or repair data. The deterministic recipe, identity constraints, unique owner/recipe markers and selected run partition must already exist. Configure the four connection variables, `GRAPHBRIDGE_VERIFY_DISPOSABLE=1`, `GRAPHBRIDGE_FIXTURE_ID` and `GRAPHBRIDGE_VERIFY_RUN_ID`; the latter two use nonempty GUID N format. Only `bolt://127.0.0.1:17687` / `neo4j` is accepted by this sample. Exit codes are 0 for pass, 1 for non-passing evidence, 2 for configuration/acknowledgment failure. The harness owns provisioning and final cleanup. [V2 API, fixture contract and limits](../../docs/tools/VERIFY.md#graphbridge-verify-v2-semantic-comparison).

The [recorded acceptance](../../tests/verification/FLUENT_CYPHER_ACCEPTANCE.md) describes the existing semantic evidence. The gallery factories pass all 15 live cases in `ProviderFluentCypherTests` on the pinned Community fixture (16 October 2026). This is separate from snapshot matching and does not qualify all provider/package behavior. Results within an ordering tie are not further ordered; a LIMIT is not a database-work budget.

In an application context configured with `UseNeo4j`, call `FluentMovieQueries.Configure(modelBuilder)` from `OnModelCreating`. With data under these mappings:

```csharp
using GraphBridge.Samples.FluentCypher;

var coactors = FluentMovieQueries.Coactors(db, runId, "Ada");
Console.WriteLine(coactors.ToQueryString());
var names = await coactors.ToListAsync(cancellationToken);

var recommendations = await FluentMovieQueries.SharedMovies(db, runId, "Ada")
    .ToListAsync(cancellationToken);
foreach (var row in recommendations)
    Console.WriteLine($"{row.Name}: {row.Score} distinct shared movies across {row.Rows} physical matches");
```

The run identifier scopes the disposable example. It is an explicit predicate, not an automatic application authorization policy. Two parallel acting credits can increase physical row counts without increasing the count of distinct shared movies. That difference is part of the native contract.

The disposable C1–C8 acceptance cases are in [ProviderFluentCypherTests](../../tests/GraphBridge.IntegrationTests/ProviderFluentCypherTests.cs), and the lambda agreement oracle is in [ProviderFluentLambdaTests](../../tests/GraphBridge.IntegrationTests/ProviderFluentLambdaTests.cs). They need PowerShell 7, the .NET SDK pinned by `global.json` and the owned fixture; see the [acceptance record](../../tests/verification/FLUENT_CYPHER_ACCEPTANCE.md#reproduction-and-scope) for commands.

Restore the solution's locked dependencies first if needed. Respect the shared-machine memory rules before the build and fixture start. The tests create their own randomly scoped graph inside a transaction and roll it back; they never use production data. Verified results are recorded in the acceptance record, not inferred from this README.

The [fluent guide](../../docs/features/FLUENT_CYPHER.md) explains optional matches, counts, scope, friend exclusions, bounded paths and deliberate limitations. These examples return native scalar DTOs; use ordinary provider LINQ when you need EF entity tracking.
