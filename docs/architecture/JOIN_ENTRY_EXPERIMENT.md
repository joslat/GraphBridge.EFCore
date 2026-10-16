# GB-009 — actual EF join-entry observation

Date: 2026-10-11. Target: .NET SDK **10.0.401**, EF Core **10.0.12**, Neo4j.Driver **6.3.0**. Exact EF source: tag `v10.0.12`, commit `001bfac21c1910cdf1110ca256c9ea0a06f374ca` in the ignored `.research/efcore` checkout.

**Execution status: focused build/test passed, 19/19 cases, exit 0.** Real EF join entries expose enough state, endpoint values and payload snapshots to proceed to an association-edge storage experiment. This is a **yes for the EF-entry seam**, not a claim of graph relationship persistence or completion of M3.

## Scope and isolation

[`JoinEntryExperimentTests.cs`](../../tests/GraphBridge.UnitTests/JoinEntryExperimentTests.cs) uses `DbContext` configured with the real GraphBridge `UseNeo4j` extension and its registered EF provider. The test context makes exactly two service replacements:

| Test replacement | Why it is necessary | What is still real |
|---|---|---|
| `IModelValidator` → EF `ModelValidator` | The production graph validator correctly rejects foreign keys, skip navigations and property bags until storage mapping is implemented. | EF core model validation, conventions, finalized `IModel`, GraphBridge scalar type mapping. |
| `IDatabase` → `RecordingDatabase : Database` | Capture the actual `IList<IUpdateEntry>` handed to the provider, before EF accepts changes, without executing storage. | EF state manager, snapshots, change detection, relationship fix-up, cascade, entry selection, SaveChanges lifecycle. |

The recorder copies scalar state into an audit record and returns `entries.Count` to exercise EF's success/acceptance branch. It can also throw to observe EF's failure branch. This return value is an **observation count**, not a persistence count. The recorder never changes entity state, accepts changes, creates entries, or simulates a graph store. An explicit test proves normal GraphBridge model validation still rejects the experimental relationship model. There are no production changes.

The supplied driver is the existing fail-on-call unit-test driver. Each test asserts its call list is empty. No InMemory provider, alternative `IQueryable`, alternate tracker, driver object mapper, direct EF internal API, reflection over EF metadata, database fixture, or Docker operation is used. In particular, `Database.SaveChanges` receiving actual entries is stronger evidence than constructing fake `IUpdateEntry` objects or calling a planner directly, but does not prove any storage behavior.

## Reproduction and executed evidence

The primary agent executed the focused suite, serialized under the shared-machine resource rules. No database was started. Reproduction command, from the repository root:

```powershell
dotnet test tests/GraphBridge.UnitTests --no-restore --filter 'FullyQualifiedName~JoinEntryExperimentTests' --logger 'console;verbosity=normal' --logger 'trx;LogFileName=join-entry-experiment.trx' --results-directory .artifacts/testresults -m:1 -p:UseSharedCompilation=false
```

There are **19 cases** (15 facts and two theories with two inputs each). Test output retained in the local, ignored TRX result (retained maintainer evidence) includes original/current scalar values, modified/temporary flags, state, and entity-type name immediately before saving and at the provider seam for the principal transitions. Assertions additionally compare the exact CLR instance and finalized entity-type metadata object across that seam. The TRX counters were inspected directly: total/executed/passed **19**, failed/error **0**.

| Command/check | Actual result |
|---|---|
| Pinned source inspection (`Get-Content`, `rg`) | Completed; exact source paths and findings below. |
| `git diff --check -- tests/GraphBridge.UnitTests/JoinEntryExperimentTests.cs` | Exit 0, no output. The newly created file is initially untracked; this is not a compilation check. |
| First focused compilation | Failed on xUnit2029: an `Assert.Empty(collection.Where(...))` assertion. Corrected to `Assert.DoesNotContain(collection, predicate)`; no tests executed in this failed attempt. |
| Focused experiment build/test rerun, command above | Exit 0, **19 passed**, 0 failed; compiled against the pinned stack. |
| Existing unit regression | This report records the focused run only; see the canonical ledger for centrally executed broader regression. |
| Live Neo4j or generated Cypher | Not applicable to the observation-only seam; no graph commands generated or executed. Required for GB-010. |

## Entry-shape and state-transition assertions

For this table, P is person ID `00000000-0000-0000-0000-000000000001`, M is movie ID ending `0002`, and M2 ends `0003`. New endpoints also test real EF client-generated nonempty GUIDs. Values below are CLR GUIDs; no driver conversion happens in this experiment.

| Scenario | Observed EF behavior, asserted by passing tests | Guard/limitation |
|---|---|---|
| New person + new movie + skip-navigation association | Three `Added` entries: two endpoints and one shared `Dictionary<string, object>` join. The join has two identifying/indexer FK properties, original=current endpoint IDs, no modified/temporary flags. | No join node or edge is created; both remain hypothetical storage choices. |
| Already tracked endpoints + association added | Only the join is `Added`; `SaveChangesAsync` itself detects the collection change. Endpoints remain `Unchanged`. | Endpoints are attached to establish real EF snapshots; they are not loaded from Neo4j. |
| Add/remove before first save, with no intervening detection | No changed join and no call to the database service; save returns 0. | EF snapshot change detection observes net membership. |
| Add/detect/remove/detect before first save | The previously `Added` join becomes `Detached`; no provider call. | Provider must not recreate a detached association from navigation contents. |
| Remove association after recorder success + acceptance | One `Deleted` join, original=current endpoint IDs, no scalar modified flags; it detaches after acceptance. | Endpoint entries stay `Unchanged`. |
| Explicit CLR `ActingCredit` with `(PersonId, MovieId)` primary key and `Role` payload | Added join reaches provider with payload. Later payload change yields one `Modified` entry: `Role` original=`lead`, current=`supporting`; only `Role` is modified. | Presence of payload does not give the association independent identity. |
| Direct edit of accepted composite join `MovieId` | EF throws `InvalidOperationException` mentioning a key before any provider save; no usable `Modified` join reaches storage. | A general promise to mutate identifying endpoint FKs in place would be incorrect. |
| Remove old movie + add new movie | One `Deleted` join for (P,M), one `Added` join for (P,M2). Deleted entry retains old current/original keys. | Storage needs dependency-aware delete/create; entry order is not asserted as write order. |
| Edit accepted principal key | EF throws before provider save. | Node/endpoint key mutation is not a relationship-rebinding feature. |
| Duplicate references in a `List` skip navigation | Two references still produce one EF join. Removing one reference saves 0 while another remains; removing the last deletes one join. | Association cardinality is not parallel-edge identity; applications should avoid duplicate list elements. |
| Remove/re-add accepted association before detection | Save returns 0; join remains `Unchanged`. | This is net collection membership, not database deduplication. |
| Remove/detect/re-add/detect accepted association | The existing join remains `Deleted` even when the collection contains the movie again. Both the before-save and provider captures confirm this pinned behavior. | Do not silently reinterpret a `Deleted` entry as creation based on navigation contents. A re-add after detected deletion is not an undo operation in this experiment. |
| Add two explicit CLR join instances with the same composite key | EF identity-conflict exception before provider save. | Two contexts can each have one join; this says nothing about concurrent graph writers. |
| Delete a tracked endpoint with accepted association | Deleted endpoint and deleted tracked join reach provider; opposite endpoint remains unchanged. | Only tracked dependent entries are observed. Unloaded incident edges and graph delete policy remain unproven. |
| Delete a tracked endpoint with still-Added association | Join detaches; only deleted endpoint reaches provider. | No unpersisted edge deletion should be invented. |
| `SaveChangesAsync(acceptAllChangesOnSuccess: false)` | Added/Modified/Deleted states and originals remain after recorder success; a repeated Added save delivers the same values. Manual EF `AcceptAllChanges` establishes new originals or detaches deletion. | Storage must not infer idempotence or perform acceptance itself. |
| Recorder throws, then caller retries | EF retains the `Added` join and supplies the same values on retry; normal acceptance follows later success. | No transaction, rollback, network failure, or retry policy is tested. |
| Explicit entity with its own `Id` primary key and non-key endpoint FKs | Endpoint edit yields one `Modified` entry: old MovieId in `GetOriginalValue`, new MovieId in `GetCurrentValue`, only FK modified. | This is a distinct identified-edge candidate, not the implicit association model. |
| Same identified entity after FK fix-up | At save, `GetRelationshipSnapshotValue(MovieId)` is already M2 while `GetOriginalValue(MovieId)` remains M. | A relationship snapshot is not the persisted old endpoint; lowering must use the original scalar value for the delete-old side. |

Original/current/modified assertions compare public `EntityEntry`/`PropertyEntry` observations before save with `IUpdateEntry` values copied inside `Database.SaveChanges[Async]`. The recorder also captures `GetRelationshipSnapshotValue` for key/FK slots. Its data is an audit of EF's state, not an independently maintained state model.

Representative captured values from the passing TRX (P/M/M2 abbreviate the GUIDs defined above):

```text
ActingCredit [Modified]
  PersonId: original=P, current=P, modified=False, temporary=False
  MovieId:  original=M, current=M, modified=False, temporary=False
  Role:     original=lead, current=supporting, modified=True, temporary=False

IdentifiedCredit [Modified]
  MovieId:  original=M, current=M2, modified=True, temporary=False
  GetRelationshipSnapshotValue(MovieId) == M2 (separate passing assertion)

ImplicitMovieImplicitPerson [Deleted], after remove/detect/re-add/detect
  ActorsId: original=P, current=P, modified=False, temporary=False
  MoviesId: original=M, current=M, modified=False, temporary=False
```

The observed implicit join name and property names describe this model only; tests resolve endpoint roles through `ISkipNavigation.ForeignKey` and its inverse rather than hardcoding these names. The endpoint-replacement trace supplied the Added join before the Deleted join, further illustrating why a future provider must choose write order from storage dependencies rather than assume EF's list is a graph execution plan.

## Pinned-source evidence and version risk

The provider-facing source seams are already mapped in [`EF_SERVICE_MAP.md`](EF_SERVICE_MAP.md). This experiment uses their exact signatures, DI lifetimes, and public metadata; inspection of internal implementations explains observations but introduces no calls to those internal classes.

| Source at `v10.0.12` | Inspected behavior |
|---|---|
| [`Storage/Database.cs`](https://github.com/dotnet/efcore/blob/v10.0.12/src/EFCore/Storage/Database.cs) | Scoped service; `SaveChanges(IList<IUpdateEntry>)` and `SaveChangesAsync(IList<IUpdateEntry>, CancellationToken)` are provider overrides. |
| [`Update/IUpdateEntry.cs`](https://github.com/dotnet/efcore/blob/v10.0.12/src/EFCore/Update/IUpdateEntry.cs) | State, metadata, scalar original/current values, modified and temporary flags, relationship snapshot, and `ToEntityEntry`. |
| [`Infrastructure/EntityFrameworkServicesBuilder.cs`](https://github.com/dotnet/efcore/blob/v10.0.12/src/EFCore/Infrastructure/EntityFrameworkServicesBuilder.cs) | `IDatabase` is scoped; `IModelValidator` is singleton; core validator is the default EF implementation. |
| [`DbContextOptionsBuilder.cs`](https://github.com/dotnet/efcore/blob/v10.0.12/src/EFCore/DbContextOptionsBuilder.cs) | `ReplaceService<TService,TImplementation>` supports the two test-only replacements. |
| [`Metadata/ISkipNavigation.cs`](https://github.com/dotnet/efcore/blob/v10.0.12/src/EFCore/Metadata/ISkipNavigation.cs) | Public `JoinEntityType`, `ForeignKey`, `Inverse` identify association endpoint roles. No reliance on generated join names or `Metadata.Internal.IsImplicitlyCreatedJoinEntityType`. |
| [`ChangeTracking/Internal/StateManager.cs`](https://github.com/dotnet/efcore/blob/v10.0.12/src/EFCore/ChangeTracking/Internal/StateManager.cs) | `GetEntriesToSave` cascades and selects Added/Modified/Deleted entries, then calls the database service; acceptance only follows success when requested. Source reference only. |
| [`ChangeTracking/Internal/NavigationFixer.cs`](https://github.com/dotnet/efcore/blob/v10.0.12/src/EFCore/ChangeTracking/Internal/NavigationFixer.cs) | Collection removal detaches Added joins or deletes existing joins; `FindOrCreateJoinEntry` produces/locates EF-owned joins; FK propagation advances relationship snapshots. Source reference only. |
| [`ChangeTracking/Internal/ChangeDetector.cs`](https://github.com/dotnet/efcore/blob/v10.0.12/src/EFCore/ChangeTracking/Internal/ChangeDetector.cs) and [`InternalEntryBase.cs`](https://github.com/dotnet/efcore/blob/v10.0.12/src/EFCore/ChangeTracking/Internal/InternalEntryBase.cs) | Snapshot comparison and `SetPropertyModified` reject key changes; Added properties do not imply per-property Modified flags. Source reference only. |

EF patch upgrades must rerun the whole observation suite, especially key immutability, remove/re-add after detection, original versus relationship snapshots, cascade and acceptance behavior. No shipping dependency on internal join detection is justified by this research.

## Decision and prerequisites for GB-010

**Decision: yes for an association-entry lowering experiment**, supported by all 19 passing cases. Current endpoint keys are available for Added joins and original endpoint keys for Deleted joins. No pivot to a separate tracker or mandatory explicit edge entity is warranted by these EF observations. Graph persistence, reads and skip-navigation materialization remain independent, unproven gates.

No second tracker or private join-detection API was needed for these shapes. Explicit mapping must select the join entity and the source/target FK roles from finalized EF metadata. The runtime must not classify every property bag or every two-FK entity as an edge, nor infer relationship direction from alphabetical metadata ordering.

The planned endpoint-update rule needs narrowing: composite association keys are immutable after acceptance. Collection endpoint replacement yields a delete/add pair. A stable-ID entity allows non-key endpoint edits, but recreating a physical relationship while preserving application identity and payload is a separate storage feature. Any such implementation must use original scalar values rather than the already advanced relationship snapshot.

GB-010 retains these dependencies and acceptance gates:

1. Close the remaining M2 combined regression/Blogging sample gate when the shared-machine resource allowance permits; this observation-only scheduling adjustment does not bypass that prerequisite for production edge work.
2. Add explicit association storage metadata and validate endpoint role, node binding, key slots, direction, relationship type, payload scope and deletion policy. Keep unsupported shapes rejected. Do not globally disable graph validation.
3. Plan node creation before edge creation and edge deletion before permitted node deletion. Use validated identifiers and parameterized values; verify both endpoint existence and affected cardinality. No synthetic join nodes and no `DETACH DELETE`.
4. Run live disposable-Neo4j add/remove, atomic endpoint/edge creation, rollback/failure, saved-entry-count, missing endpoint, duplicate graph-state and concurrent-writer tests. EF's per-context join uniqueness is not a cross-context or database uniqueness guarantee.
5. Prove fresh-context graph reads and relationship materialization/fix-up before advertising skip navigation loading, `Include`, or relationship round trips. The attached snapshots in this experiment are not a substitute for those tests. Choose an explicit edge-read scope if implicit shaping cannot be achieved cleanly.
6. Keep identified/parallel edges and non-key FK rebinding under their separate identity/materialization gate. The isolated stable-ID observation provides input to that design, not an implementation of M4.

There is no generated Cypher to review in GB-009. Live storage, concurrent writers, fresh-context graph reads, navigation shaping, unloaded cascades, graph constraints, physical-edge identity and performance all remain unverified. The primary agent records this experiment's executed result and any narrowed assumptions in the canonical implementation ledger and ADR.
