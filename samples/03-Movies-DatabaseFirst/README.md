# Movies: inspected graph to reviewed EF model

[appsettings.example.json](appsettings.example.json) documents the application and inspector environment variable mappings and fixture guards without credential values; neither executable loads this file.

The current checklist increment compares the checked-in reviewed scaffold fingerprint with the finalized runtime EF model before executing `--run`, and prints both. Ctrl+C forwards cancellation through that workflow; explicit rollback uses an uncanceled token. New assertions verify fingerprint parity and pre-canceled browse/run operations without transport. These additions pass the focused owned-fixture acceptance run recorded in implementation status (maintainer-local record). Fingerprint parity describes the storage mapping, not an exhaustive validation of database contents.

The executable builds against generated source captured from actual Neo4j inspection. Hosted run 38115255813 verifies fixture setup, deterministic scaffolding, generated EF reads/updates/rollback and the separate identity-backfill demonstration. Both saved-snapshot regeneration checks pass on Windows and Linux. See implementation status (maintainer-local record) for actual evidence and limits.

The [original fixture](PROVENANCE.md) has three fictional people, two movies, four acting credits and one directing credit. Graph storage uses `GraphBridgeMoviesPerson`, `GraphBridgeMoviesMovie`, `GRAPHBRIDGE_MOVIES_ACTED_IN` and `GRAPHBRIDGE_MOVIES_DIRECTED`. Two acting credits share their endpoints, and each retains its own application GUID and native `roles` string list.

## Source artifacts

| File | Purpose |
|---|---|
| `source/movies.fixture.cypher` | Parameterized creation statement with an empty-sample precondition. |
| `source/movies.fixture.parameters.json` | Exact original fixture values and stable GUID strings. |
| `source/schema.reviewed.cypher` | Four separately reviewed uniqueness constraints; never deployed by scaffolding. |
| `source/graphbridge.readonly.policy.json` | No tracked mappings or assumed keys. Produces DTO proposals and an inference report. |
| `source/graphbridge.reviewed.policy.json` | Explicit keyed nodes and identified edges after actual constraint inspection. |

`MoviesFixture.SeedAsync` requires an empty sample namespace and verifies exactly one result row with creation counts `3, 2, 4, 1` before committing its explicit transaction. Zero rows means sample data already exists; it never overwrites or deletes that data. The helper checks the selected database's disposable marker and verifies LF-normalized SHA-256 hashes of both source inputs before execution. Edited fixture text is refused until its new version is reviewed and the helper updated. Its empty-state precondition is not a concurrency lock: serialize setup.

The schema file is a reviewable proposal, separate from fixture creation. `MoviesFixture.PrepareSchemaAsync` assesses all four reserved names, definitions and backing indexes before the first write, then applies missing constraints in separate finite transactions. Exact existing constraints with their expected owned `ONLINE` indexes can be reused; conflicting names or equivalent objects under other names require review. Fresh transactions verify committed definitions. This is stepwise DDL, so completed steps can remain after failure; there is no automatic retry, rollback compensation or global migration-journal reset. A uniqueness constraint does not prove that every entity has an ID; `approveRequiredKey` records a reviewed application assumption. The complete authored fixture supplies its keys, but bounded inspection alone cannot certify all data in another graph.

## Inspection and the read-only proposal

Use this repository's owned disposable Neo4j fixture and its resource checks. The application accepts only `bolt://127.0.0.1:17687`, database `neo4j`, and the exact disposable marker. It reads the fixture script's `GRAPHBRIDGE_NEO4J_URI`, `GRAPHBRIDGE_NEO4J_DATABASE`, `GRAPHBRIDGE_NEO4J_USERNAME`, `GRAPHBRIDGE_NEO4J_PASSWORD` and `GRAPHBRIDGE_FIXTURE_ID` environment values. No credentials belong in command arguments or committed files.

The general tooling `inspect` command uses shorter environment names. After loading the owned fixture's environment, copy its values within the current PowerShell process without printing them:

```powershell
$env:GRAPHBRIDGE_URI = $env:GRAPHBRIDGE_NEO4J_URI
$env:GRAPHBRIDGE_DATABASE = $env:GRAPHBRIDGE_NEO4J_DATABASE
$env:GRAPHBRIDGE_USERNAME = $env:GRAPHBRIDGE_NEO4J_USERNAME
$env:GRAPHBRIDGE_PASSWORD = $env:GRAPHBRIDGE_NEO4J_PASSWORD
```

Inspection uses a selected-database read transaction, finite limits and no deployment or data mutation. Its output must name a new file in an existing directory. The application enforces the disposable fixture; the general inspector is a read-only tool with an explicit connection configuration.

Create the original fixture explicitly, browse its raw relationship payloads, and inspect before deploying the constraint proposal:

```powershell
dotnet run --project samples/03-Movies-DatabaseFirst/Movies.App -- --seed-fixture

dotnet run --project samples/03-Movies-DatabaseFirst/Movies.App -- --browse

dotnet run --project src/GraphBridge.Tool -- inspect --labels GraphBridgeMoviesPerson,GraphBridgeMoviesMovie --relationship-types GRAPHBRIDGE_MOVIES_ACTED_IN,GRAPHBRIDGE_MOVIES_DIRECTED --output movies.before-review.snapshot.json

dotnet run --project src/GraphBridge.Tool -- scaffold --snapshot movies.before-review.snapshot.json --policy samples/03-Movies-DatabaseFirst/source/graphbridge.readonly.policy.json --output samples/03-Movies-DatabaseFirst/generated-readonly
```

The snapshot retains schema metadata, labels, relationship endpoint label sets and property storage kinds. It does not retain raw values, passwords or durable element IDs. Samples remain incomplete even when fewer than the configured limit are observed. No key is chosen from names, titles or apparent sampled uniqueness. Indexes alone are insufficient evidence for a tracked identity.

The read-only policy produces DTO source and `inference-report.md`; it produces no tracked `DbContext` or storage fingerprint. `--browse` uses an empty EF model and the bounded native DTO query lane to return actual relationship property bags, including `roles` lists. It does not materialize or track generated entities. A snapshot only reports storage kinds and cannot itself populate raw values. Hosted acceptance verifies this browsing before identity constraints are prepared.

## Explicit review and tracked generation

Review the four constraints in `source/schema.reviewed.cypher`, invoke their separate explicit preparation command, and obtain a new snapshot from the actual database:

```powershell
dotnet run --project samples/03-Movies-DatabaseFirst/Movies.App -- --prepare-schema

dotnet run --project src/GraphBridge.Tool -- inspect --labels GraphBridgeMoviesPerson,GraphBridgeMoviesMovie --relationship-types GRAPHBRIDGE_MOVIES_ACTED_IN,GRAPHBRIDGE_MOVIES_DIRECTED --output movies.after-review.snapshot.json

dotnet run --project src/GraphBridge.Tool -- scaffold --snapshot movies.after-review.snapshot.json --policy samples/03-Movies-DatabaseFirst/source/graphbridge.reviewed.policy.json --output samples/03-Movies-DatabaseFirst/generated
```

Append `--check` to either scaffold command for a byte-for-byte regeneration check that changes no files. Review the inference report, generated C#, manifest and schema proposal. Tracked generation requires observed uniqueness/key constraints, explicit stable-key review and supported property mappings. The real finalized EF model supplies the binding manifest. Editing the JSON policy cannot create constraints or convert existing data. Generated schema files describe an empty target, not an upgrade of the inspected database; do not apply them alongside the equivalent sample constraints under different names.

The reviewed policy maps `Person.PersonId`, `Movie.MovieId`, `ActingCredit.ActingCreditId` and `DirectingCredit.DirectingCreditId` through canonical GUID string conversion. `ActingCredit` and `DirectingCredit` are native identified relationships directed from person to movie, with restrictive endpoint deletion. Collection cardinality and association-edge uniqueness are not inferred. Names and titles are ordinary payload.

Native string arrays are not automatically supported tracked scalar properties. The `roles` decision therefore sets `ignore: true` explicitly, producing a diagnostic. It does not convert, clear or rewrite the stored array; the DTO browsing lane returns the real list. The ignored entry's `scalarType: String` is a required policy-format placeholder and does not declare a string conversion for the list. Inserting a new tracked acting credit would not populate ignored roles automatically.

```powershell
dotnet run --project samples/03-Movies-DatabaseFirst/Movies.App -- --run
```

The `--run` workflow filters and orders movies with generated EF mappings, reads both parallel acting credits, verifies endpoint reference fixup and EF identity resolution, and updates one `BillingOrder` in an explicit transaction. A native DTO read in the same transaction checks that its roles array remains intact. The transaction is rolled back, and a fresh context verifies the original billing order and roles. It also reads the independently identified directing credit. Hosted acceptance invokes this exact workflow and verifies the graph remains unchanged afterward.

## Saved snapshots and reproducible generation

The hosted capture artifacts are `snapshots/before-schema.snapshot.json` and `snapshots/reviewed.snapshot.json`, captured in run `38114205564` from commit `ca8af31`. They contain type-only observations from the actual owned disposable graph. The capture filters schema metadata to the four sample-owned constraint/index objects, retaining the before/after distinction without unrelated test or migration-journal schema. This filtering is an explicit fixture-artifact preparation step; the general `inspect` command does not silently filter schema metadata.

Verify regeneration from the committed captured files and the same saved inputs:

```powershell
dotnet run --project src/GraphBridge.Tool -- scaffold --snapshot samples/03-Movies-DatabaseFirst/snapshots/before-schema.snapshot.json --policy samples/03-Movies-DatabaseFirst/source/graphbridge.readonly.policy.json --output samples/03-Movies-DatabaseFirst/generated-readonly --check

dotnet run --project src/GraphBridge.Tool -- scaffold --snapshot samples/03-Movies-DatabaseFirst/snapshots/reviewed.snapshot.json --policy samples/03-Movies-DatabaseFirst/source/graphbridge.reviewed.policy.json --output samples/03-Movies-DatabaseFirst/generated --check
```

Fresh full inspection can observe additional schema, a different server version or different bounded samples. Its report and source fingerprint can therefore differ from the saved fixture snapshots even when the reviewed EF storage model is unchanged. Use saved inputs for deterministic `--check`; inspect live input separately when evaluating drift. Actual capture, artifact generation, byte checks and application/runtime fingerprint parity pass acceptance.

## Graphs without stable application IDs

Use a read-only policy when durable application identity is absent. The original Movies fixture already contains authored IDs. A separate demonstration uses one dedicated `GraphBridgeMoviesLegacyMovie` node with reviewed selector `legacyCatalogKey`, title `The Uncatalogued Reel`, and no initial `appId`. Its typed migration assigns the fixed reviewed GUID `55000000-0000-0000-0000-000000000001` to that exact bounded scope. It does not infer identity from a title or `elementId()`, generate random IDs for a graph, or run as part of scaffolding.

The application exposes separate preview, journal initialization, schema preparation, seed, validation, scoped backup and apply operations:

```powershell
dotnet run --project samples/03-Movies-DatabaseFirst/Movies.App -- --backfill-preview

dotnet run --project samples/03-Movies-DatabaseFirst/Movies.App -- --backfill-initialize --confirm

dotnet run --project samples/03-Movies-DatabaseFirst/Movies.App -- --backfill-prepare --confirm

dotnet run --project samples/03-Movies-DatabaseFirst/Movies.App -- --backfill-seed --confirm

dotnet run --project samples/03-Movies-DatabaseFirst/Movies.App -- --backfill-validate

New-Item -ItemType Directory -Force .artifacts | Out-Null

dotnet run --project samples/03-Movies-DatabaseFirst/Movies.App -- --backfill-backup --backup .artifacts/movies-legacy-before.json

dotnet run --project samples/03-Movies-DatabaseFirst/Movies.App -- --backfill-apply --backup .artifacts/movies-legacy-before.json --confirm
```

`--backfill-preview` is offline and prints journal bootstrap commands, exact schema/data plans, checksums, parameters, preconditions and rollback limitations. The mutation commands require their explicit `--confirm` flag. `--backfill-validate` is read-only and does not initialize the journal. The dedicated legacy schema plan is `movies_sample_legacy_schema_v1`; its successor data plan is `movies_sample_legacy_app_id_v1`.

The sequence above assumes the schema plan has no predecessor. If the database already has migration history, review its exact head and supply that same original `--predecessor ID` to `--backfill-preview`, `--backfill-prepare` and `--backfill-seed`. Never substitute a guessed head or silently advance past another migration. The data plan always requires the completed Movies legacy schema plan. Changing the original predecessor later changes the reviewed schema plan's checksum and is not a valid replay.

The demonstration preserves existing history and never resets the journal, steals ownership, automatically recovers an interrupted operation, overwrites conflicting legacy rows or reseeds missing data after a completed migration. Partial schema/data steps and the legacy row remain for inspection; plans declare no automatic rollback compensation. Replay checks both completed history and current data. An uncertain seed commit must be resolved from retained graph state, not automatically repeated.

The backup command is read-only against Neo4j and preserves the exact dedicated legacy row before identity assignment in a bounded JSON file outside the database. It includes labels, original properties and the absent-ID state, plus fixture/database identity and schema/migration context. Existing files are never overwritten. Apply requires a matching persisted artifact; retain and reuse the same preimage for replay rather than recapturing an already migrated row. Seven focused backup/backfill checks pass against the owned fixture (one pre-cancellation check is offline); see the implementation tracker for precise evidence. Serialize sample commands and external writers: read-committed preflight checks are not a global snapshot or lock.

This artifact preserves the one-row demonstration scope. It is not a database dump, a backup of neighboring graph data or a restore command. For a real deployment, the operator must also take and test an edition-appropriate full database backup before migration. A disposable fixture seed or schema observation snapshot does not preserve original database contents. Recovery remains an explicit review of data and durable journal state; the sample does not automatically restore data, reverse the migration or remove its history.

After any real application identity backfill, inspect again and review completeness, uniqueness, storage conversions and the tracking policy before enabling tracked entities. The legacy demonstration does not automatically extend the main generated Movies model. Four live tests verify preview/validation, confirmation, assignment and replay, conflicting/missing IDs and an unrelated journal head; reinspection rejects tracked compilation before the ID exists and accepts the reviewed proposal afterward.

## Review versions offline

The new [scaffold-review command](../../docs/tools/GRAPHBRIDGE_TOOL.md) compares explicit baseline/current snapshot and policy pairs, reports key/type/endpoint ambiguity with actions, verifies generated-artifact round trips and shows stable paths through actual EF storage-model changes. DTO-only or invalid proposals never imply tracked-model equivalence. Output is JSON on stdout; the command neither publishes generated files nor deploys schema. Offline and expanded live Movies review acceptance pass in focused run 38164485726; the review contract links retained observations, reports and generated artifacts.
