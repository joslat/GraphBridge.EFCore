# Feature evidence

What each feature's tests cover and the acceptance recorded when it was accepted. The feature pages in [docs/features](../../docs/README.md#features) and [docs/tools](../../docs/README.md#tools) state the contract; this page records how it was verified. The [verification summary](VERIFICATION_SUMMARY.md) holds the latest full-suite checkpoints.

## Nulls, types and time

- [Live truth table](../GraphBridge.IntegrationTests/ProviderNullSemanticsTests.cs): 87 predicates over all sixteen pairs drawn from null, -1, 0 and 1, plus nullable booleans and missing, empty and case-distinct strings. Expected results come from the same expression evaluated independently over the in-memory test data; this oracle is test code, not a provider fallback. Another case inspects the actual graph property keys after a null assignment.
- Converted values ([ProviderConvertedValueSemanticsTests](../GraphBridge.IntegrationTests/ProviderConvertedValueSemanticsTests.cs)): equal `DateTime` ticks with different `Kind` values and equal `DateTimeOffset` instants with different offsets; 63 equality, inequality and negation predicates across these temporal values, nullable Guid and an Int64 enum, including changed and null parameters.
- Original EF tests execute seven enum equality methods and two Guid construction methods, each with an exact Cypher snapshot ([upstream inventory](EF_UPSTREAM_INVENTORY.md)).
- Accepted on 2026-10-11: 618 provider units, 26 query/integration cases and 61 selected EF checks (57 original upstream cases and four adapter checks), zero failures and skips.
- This is a tested scalar contract, not complete EF query compatibility or proof for every custom converter or collation.

```powershell
dotnet test tests/GraphBridge.UnitTests --no-restore -m:1 --filter FullyQualifiedName~NullTypeSemanticsTests
pwsh -File eng/scripts/Run-IntegrationTests.ps1 -NoBuild -Filter 'FullyQualifiedName~ProviderNullSemanticsTests|FullyQualifiedName~ProviderConvertedValueSemanticsTests'
pwsh -File eng/scripts/Run-IntegrationTests.ps1 -NoBuild -Suite Specification
```

## Optional references

[Live cases](../GraphBridge.IntegrationTests/ProviderOptionalReferenceTests.cs) cover optional Guid and nullable integer and composite keys, both Include directions, all tracking modes, fresh lifecycle round trips, concurrency and rollback, missing endpoints, corruption, local edits and restrictive deletion. [Unit cases](../GraphBridge.UnitTests/OptionalReferenceTests.cs) verify model admission, schema versions, hidden guards, comparer restrictions and mutation planning.

## LINQ `Contains`

Five membership unit tests (`ContainsQueryTests`) and four live cases (`ProviderQueryTests.Contains`) cover the 1,024-value boundary; null, missing, empty and negated cases; Guid, enum and bool conversion; scalar `ContainsAsync`; and one EF compilation reused across changed values and lengths. The targeted 27-unit and 24-live query and EXPLAIN regression runs passed when the feature was accepted.

## DTO projections

`ProjectionTranslationTests` and `ProviderQueryTests.Projection`: five provider DTO facts and eight original upstream methods pass; two association facts verify the DTO and empty-projection guards. A follow-up guard prevents a one-field DTO from passing as a scalar for `Contains`, with a pre-transport rejection test for null and value DTO membership. Accepted on 2026-10-11 with 648 provider units, 218 live integration tests and 69 selected EF checks.

## Integer aggregates

`IntegerExtremumTests` check exact Cypher, identifier validation, boxing and overflow, empty results and corrupt or missing-value diagnostics. `ProviderIntegerExtremumTests` cover both widths and nullable forms, selector and scalar forms, filtering, corrupt input, cancellation, parameter rebinding, transaction borrowing and rollback. The Northwind adapter maps 830 orders as independent nodes. Accepted with 656 provider units, 222 integration tests and 81 selected EF checks.

## Query inspection

`ProviderQueryTests.Where_uses_server_parameters_and_reuses_shape_with_changed_closure_values` retains the exact generated statement; the first run returns ages 913 and 914, the second (minimum 914) only 914, with unchanged Cypher text. The VS Code Cypher grammar was checked at neo4j/cypher-language-support commit `cd2744a`.

## Bulk mutations

Fourteen live cases: paging, simultaneous setters, null removal, unknown-property preservation, generated versions and stale saves, atomic restrictive deletion, explicit rollback, checked overflow, cancellation after mutation consumption, a competing write between selection and locking, a post-selection duplicate association, absent and present parents, modeled-link rollback, and the 10,001-row cap with an explicit 10-row batch. In the measured environment, multirow node batches beat tracked saves; a single-row update does not. Native batches remain faster with a narrower validation contract.

## Graph search

The pinned server rejects an ONLINE eventual full-text index through `SEARCH`; the full-text procedure is its documented alternate access path, and the tests use it.

## Command tracing

`CommandActivityTests`, `NativeQueryTests.Activity`, `ExplainQueryTests.Activity` and the integration activity tests cover pending calls, early cursor disposal, one stop per span, independent trace contexts, fixed parameter categories, original error identity, cancellation, acknowledged and unconfirmed commits and cleanup errors. Live assertions check matched start and stop sets, real outcomes and that no fixture secret appears in telemetry.

## Mapping source generator

Eleven focused diagnostic cases include a separate-file entity edit that invalidates errors even when the `GraphModel` syntax is unchanged; fixing it restores the original generated output. A fresh analyzer package consumer and a build-cost experiment (six forced builds per arm) observed 111.96 ms (4.59%) mean overhead against exact frozen generated source; actual compiler GBSG008 failure and recovery pass. This does not measure IDE rendering or prove support in older compiler hosts.

## GraphBridge.Tool and database-first review

The first complete checkpoint passed 192 tooling tests, including ten review and CLI cases; a follow-up passed all eight review facts after adding distinct observation-order coverage and a directed-edge conflict case. An early regression detected platform-dependent JSON newlines; explicit LF output fixed it. The actual CLI passes offline over the checked-in Movies snapshots: a conflicting baseline exits 2, and an unchanged reviewed model exits 0 with no observation, model or artifact changes. The compiled fingerprint matches the committed Movies manifest; captured review reports show four missing-constraint conflicts in the invalid baseline, and every captured generated file matches the committed Movies output byte for byte.

## Design-time commands

Actual CLI `list` and `info`, offline sample preview, the expected relational-command rejection and 182 tooling unit cases pass (deterministic rendering, no context creation for offline or invalid commands, parameter-value redaction and explicit disclosure, duplicate catalog IDs, cancellation and sanitized factory failure). All 15 tooling integration cases pass, including status for uninitialized, ready and applied plans with unchanged node and constraint snapshots; the sample process reports an uninitialized store with exit 3. A package-only consumer restored freshly packed provider and tooling previews and passed EF `list`/`info`, graph catalog/preview and invalid-command exit 2, with loaded assembly bytes matching the packages.

## Schema preflight

`SchemaPreflightTests` inspect an isolated mapped-key and index model before and after reviewed DDL, then remove the index and verify drift without repair. Another case launches the real executable, verifies exit 0, removes a sample identity constraint, verifies exit 3 and rejects mixed read/write arguments with exit 2. Cancellation and missing-database failures verify redacted output. Deliberate DDL belongs to test setup on the marker-verified disposable fixture, never to the gate. All three cases passed locally against real Neo4j on 2026-10-11 and in hosted run 38130513594 at `19d106e`, with complete owned cleanup.

## Migration plans and recovery

`ProviderMigrationTests` include `Preview_and_validation_do_not_initialize_or_mutate_database`, `Data_failure_rolls_back_mutation_and_completion_record_before_explicit_recovery`, `Crash_after_schema_commit_recovers_exact_postcondition_without_repeating_DDL` and `Crash_after_data_commit_uses_atomic_journal_to_avoid_duplicate_backfill`. All 17 migration cases passed again locally on 2026-10-11, together with the three schema-preflight cases, with complete owned teardown. The executed Organization preview emits schema checksum `77364d3b6769e6ebf0170422214c074fbe29634585b7595e868b7613cdf8835d` and v2 checksum `f471e0627ad8d5bfdbcb03a14fd1d5a27157f5b07dff5543ea1ef71d7bbef4f7`.

```powershell
pwsh -File eng/scripts/Start-Neo4jFixture.ps1
pwsh -File eng/scripts/Run-IntegrationTests.ps1 -NoBuild -Filter 'FullyQualifiedName~ProviderMigrationTests'
pwsh -File eng/scripts/Stop-Neo4jFixture.ps1
```

## Transaction recovery

Hosted run 38142030670 passes the twelve receipt-parser cases and five live commit-cut, receipt and cancellation cases; focused run 38142624691 additionally passes the real deadlock case and rechecks all six live cases.

## Package quickstart

The package-only consumer verifies the fixture URI, marker, server version and edition before writing, then fresh reads, an update with a version bump, explicit rollback, delete and absence. Success requires the exact terminal marker `Package-only live CRUD verified` and `liveCrudPassed: true`; the runner forces recompilation after a first implementation was found to reuse an older offline binary. Timing evidence covers every stage and fixture cleanup; no time guarantee or independent human onboarding result is implied. `Test-TemplatePackage.ps1 -Live` verifies actual template installation and generated-project execution.
