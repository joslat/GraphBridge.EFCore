# Selected upstream EF Core specifications

Latest FS-13 aggregate acceptance (2026-10-11): **76 upstream cases covering 75 original methods plus five offline fixture checks; all 81 pass locally with zero failures/skips**. The new `NorthwindAggregatesNeo4jTest` invokes eight original Min/Max methods and three Count methods unchanged. Its explicit independent-node adapter retains all 830 original OrderID/CustomerID pairs, ignoring unrelated properties/navigations and logging only the expected mapped-entity/property removal warnings. The original control data and assertions remain authoritative; relationship-root aggregates remain unsupported. See [integer aggregate acceptance](../../docs/features/LINQ_QUERIES.md#integer-aggregates) and [the named inventory](../verification/EF_UPSTREAM_INVENTORY.md).

Earlier FS-13 projection acceptance (2026-10-11): **65 upstream cases covering 64 original methods, plus four offline fixture checks; all 69 pass locally with zero failures/skips**. `NorthwindCustomersNeo4jTest` now delegates eight additional unchanged async methods: `Select_anonymous_one`, `two`, `three`, `bool_constant_true`, `with_object`, `nested`, `empty` and `literal` (each has the `Select_anonymous_` prefix). These exercise scalar/DTO columns, nested entities and constant cardinality against the original 91-Customer control data. No fixture adaptation or upstream assertion was changed for this increment. Exact names and log reconciliation are in [the inventory](../verification/EF_UPSTREAM_INVENTORY.md); local evidence is `.artifacts/projection/live.log`. Historical checkpoint tables below retain their original scope.

This project adapts the actual `Microsoft.EntityFrameworkCore.Specification.Tests` package, now 10.0.13; its test sources are identical to 10.0.12, where the totals below were recorded. The provider remains non-relational. The package, the fixture adapter, and the reference to the existing integration-test safety harness are test-only dependencies.

The initial selection contains both complete upstream classes: `ComparisonOperatorTranslationsTestBase` and `LogicalOperatorTranslationsTestBase` (six asynchronous facts each). Every override awaits the upstream base method and then asserts the entire logged Cypher command, including parameter placeholders and the complete scalar projection. No inherited assertion or expected result is replaced.

`BasicTypesQueryFixtureBase` seeds its unchanged seven ordinary and eight nullable entities through GraphBridge `SaveChangesAsync`. Its original entity asserters compare every scalar of the returned ordinary entities. Unsupported native scalar representations use explicit reversible fixture conversions: byte/short to integers, float to double, decimal to invariant string, DateTime to binary, DateOnly to day number, TimeOnly/TimeSpan to ticks, DateTimeOffset to invariant round-trip string, and byte arrays to Base64 with structural comparison. Guid and enums retain the provider mappings. These mappings do not establish temporal, decimal or array operator support. The selected queries do not exercise nullable-entity reads.

Both classes run in one nonparallel collection. Each class fixture opens and disposes a driver to the same disposable server using `DisposableNeo4jFixture`, preserving its localhost/database/marker checks. The adapter creates no container or database. The two `GraphBridgeSpec*` labels and `graphbridge_spec_*` constraints belong exclusively to this serialized suite; cleanup uses plain `DELETE` and drops only those named constraints. Pooling and shared-store reuse are outside this first selection.

Without `GRAPHBRIDGE_INTEGRATION=1`, the upstream `ConditionalFact` runner reports explicit fixture opt-in skips. With it enabled, missing configuration, failed safety checks, setup failures and provider failures fail the tests. Use the repository's centrally coordinated fixture runner; do not point this project at a production database.

Source evidence: `.research/efcore` at `v10.0.12` (`001bfac21c1910cdf1110ca256c9ea0a06f374ca`), particularly `test/EFCore.Specification.Tests/Query/Translations/Operators`, `Query/Translations/BasicTypesQueryFixtureBase.cs`, `SharedStoreFixtureBase.cs`, and `TestUtilities/{ITestStoreFactory,TestStore,QueryAsserter}.cs`.

## Coverage boundary

| Area | Initial status |
| --- | --- |
| Six comparisons and six Boolean operator tests | All 12 pass against the disposable Neo4j fixture in hosted run 38111298123, commit `4b70e34`; zero skipped |
| Full returned scalar values in those upstream queries | Original upstream asserters retained |
| Nullable enum equality | Three methods now pass locally; see the feature increment below |
| Upstream tracking suites, transactions and updates | Existing custom tests are separate evidence; upstream suites not selected yet |
| Synchronous queries, Include, joins and broader LINQ | Not supported by this selection; no passing upstream claim |
| Temporal/decimal/array operators, pooling and concurrency suites | Not selected or claimed |

Passing these twelve facts establishes only this selected upstream contract, not full EF Core provider conformance. Broader unsupported-behavior tracking remains an M6 acceptance obligation.

## Feature increment: three nullable-enum cases

`NullableEnumTranslationsNeo4jTest` directly invokes three unmodified methods from the pinned `EnumTranslationsTestBase`: `Equality_nullable_enum_to_null_constant`, `Equality_nullable_enum_to_null_parameter` and `Equality_nullable_enum_to_nullable_parameter`. Composition exposes only that explicit slice; bitwise and other enum cases are not implicitly claimed. Upstream data and full nullable-entity scalar assertions are retained. Exact generated Cypher is asserted after each upstream method, including parameterized null-safe equality for null constants.

The 2026-10-11 local run passes all **15 selected upstream cases**, zero failures/skips, in 11.11 seconds on the owned Community fixture, followed by complete teardown. The first run had 14 passes and one incorrect new snapshot expectation (`IS NULL` instead of the existing parameterized null-safe equality); upstream result assertions passed, the snapshot was corrected, and no provider behavior changed. Logs: `.artifacts/nullable-specs/` and `.artifacts/nullable-specs-verified/`. Build: zero warnings/errors. Hosted acceptance is tracked separately.

## Feature increment: Customer-only Northwind adapters

The new adapter invokes 18 original EF 10.0.12 async methods across `NorthwindWhereQueryTestBase`, `NorthwindSelectQueryTestBase` and `NorthwindAsNoTrackingQueryTestBase`. It retains the 91 upstream Customer records, all eleven scalar properties and upstream result assertions. It explicitly removes Order/Product/keyless entities and Customer navigations; it therefore makes no relationship or full-Northwind claim. Application string IDs map to a suite-owned Customer label and explicit uniqueness constraint, with plain DELETE cleanup.

`NorthwindFixtureModelTests` independently finalizes the adapted model without permitting driver access and verifies exact scalar/key/navigation scope. The fixture logs only `MappedEntityTypeIgnoredWarning`, expected because the original context first configures entities excluded from this named slice. Other warning policy remains unchanged. The initial live attempt passed the prior 15 cases and failed all 18 new cases during fixture setup on that warning; no query acceptance was claimed. After the correction, the full live rerun passes all 34 discovered cases: 33 upstream methods plus one offline model-scope check, zero failures/skips, in 14.8811 seconds. Build has zero warnings/errors. Two earlier retries were correctly deferred at 6.71/6.90 GiB available RAM; the passing run used the unchanged resource guard and removed its owned fixture. Logs: `.artifacts/northwind-specs-verified/`. Hosted run 38136227623, clean `7f2fea6`, also passes all 34 cases in 10.8424 seconds with complete owned cleanup.

| EF 10.0.12 selected family | Upstream cases passed | Skipped | Failed | Scope |
|---|---:|---:|---:|---|
| Comparison operators | 6 | 0 | 0 | Complete original selected class |
| Logical operators | 6 | 0 | 0 | Complete original selected class |
| Nullable enum equality | 3 | 0 | 0 | Three named methods, not bitwise family |
| Northwind Where | 11 | 0 | 0 | Customer equality, captured array/dictionary/tuple values, constants and changed closure/cache values |
| Northwind Select | 4 | 0 | 0 | Scalar City, root entity, identity projection and Select-into with predicate |
| Northwind no tracking | 3 | 0 | 0 | Both API forms and fresh database values despite a tracked local edit |
| Additional fixture-model check | 1 local-only test | 0 | 0 | Exact adapted model without driver access; not counted as an upstream method |

All delegated Northwind calls select the original async branch. Unselected synchronous, joins, keyless/other entity models, DTO projections and navigation cases are outside this slice, not silently counted as skipped/passed. Broader upstream update/relationship adapters and explicit unsupported-case inventory remain FS-04 work.

## Verified increment: scalar updates and concurrency

`UpdatesNeo4jTest` invokes eleven original methods from EF 10.0.12 `UpdatesTestBase`: six partial-update/delete and concurrency cases, four byte-array concurrency cases, and mutable tracked-array isolation. Hosted run 38139246843 at `6229339` passes all eleven methods and both offline model contracts: 44 upstream cases (43 distinct methods) plus two fixture checks, 46/46 total with zero failures/skips. The earlier table above records the preceding 33-method checkpoint. `UpdatesFixtureModelTests` adds one offline contract check, not an upstream case.

The adapter starts from the upstream model configuration and retains Product, Category, ProductWithBytes and AFewBytes as independent node roots. It removes all relationship, owned and inheritance models from this named scalar slice. The original `UpdatesContext.SeedAsync` runs unchanged; its single Category uses its default integer ID as an explicit stable fixture key. Product.Price retains its concurrency annotation with the same invariant whole-value decimal/string conversion used elsewhere in the specification suite. Byte arrays use reversible Base64 storage with an explicitly configured EF default comparer (structural equality, shallow snapshots) for change detection. Fresh decoded values do not share mutable storage. The original mutation test explicitly marks one array modified and expects the other in-place edit not to save. These are fixture representations, not native decimal/array operator support.

The original transaction helper assumes cross-context transaction sharing and synchronous disposal. The adapter instead runs each original delegate in a fresh async-disposed context, lets each SaveChangesAsync perform its real atomic transaction, and reseeds the four exclusive test labels after each serialized test. It does not claim cross-context transaction enlistment, upstream retry semantics, relationships or the complete update class. Existing tests remain responsible for GraphBridge's explicit-transaction contract. The offline model check asserts retained types, keys, scalar properties, concurrency flags and byte-array snapshot/conversion behavior before any driver access. CI executes model checks before starting a database; live specification execution remains marker-guarded.

Initial hosted attempts: `ed32837` failed compilation on an ambiguous Category name and nullable converter overload; `a91b0d7` compiles but its early model check failed on the intentionally removed Product.DefaultCategory navigation warning. The adapter now logs only the two expected mapped-entity/navigation removal warnings; other warnings still throw. The next run (`c08e807`) required an explicit comparer for converted arrays; the fixture now configures `ValueComparer.CreateDefault(typeof(byte[]), favorStructuralComparisons: false)` rather than suppressing that validation. Run `2a29345` then finalized the model but exposed a test assumption that EF enumerates properties alphabetically; the property-set assertion now sorts names explicitly (EF enumerates the key first). Run `8092e8a` exposed a second over-specified fixture assertion: the pinned EF generic ValueComparer always uses structural equality for arrays, while `favorStructuralComparisons: false` keeps snapshots shallow. The contract check now verifies both of those source-confirmed behaviors and fresh decoded storage. These failed attempts count as no additional passing upstream cases.

Hosted run 38138174769 at `41cddff` passes 42 of 46 discovered specification checks. Seven new original update methods pass; the four byte-array concurrency methods fail at their final GUID `FindAsync` verification because the provider rejects EF's static `object.Equals` predicate. This is a runtime translation gap, not grounds to alter the original assertions. The subsequent 38139246843 run passes all 46 checks after the ADR-0025 finder fix; the original assertions remain unchanged.

## Verified increment: direct Customer/Order Includes

`NorthwindRelationshipsNeo4jTest` invokes seven unchanged async methods from EF 10.0.12 `NorthwindIncludeQueryTestBase`: collection ordering with Take, collection no-result SingleOrDefault, collection root filtering in both operator orders, reference no-result SingleOrDefault, and reference root filtering in both orders. Hosted run 38139977914 at `a875ac7` passes all seven original methods and both new fixture checks; all 55 discovered specification cases pass with zero failures/skips.

The fixture retains all 91 Customers and 830 Orders from `NorthwindData`, the original eleven Customer scalars, the four Order scalars retained by the original Northwind model and its full upstream asserters. EmployeeID uses a checked uint/long converter; OrderDate uses EF's reversible binary DateTime converter. Other entities and OrderDetails are explicitly excluded. The native relationship is Customer-to-Order and derives CustomerID from the principal key rather than storing a duplicate FK property.

This adapter explicitly requires the originally optional Customer reference and uses restrictive deletion. Every original Order has a Customer; a separate offline dataset check verifies that premise, complete counts and inverse membership. Consequently these tests verify the populated-reference contract only; optional-reference behavior remains FS-14. The offline finalized-model check verifies retained scalars, keys, inverses and conversions. Cleanup deletes only this fixture's named edges between its named labels, then uses plain node DELETE. It reuses the marker-guarded server and serialized specification collection. Test bodies, original expected data and Include assertions are not rewritten.

The [named invocation inventory](../verification/EF_UPSTREAM_INVENTORY.md) separates argument variants, distinct methods, adapter checks and unselected cases. Initial relationship build 38139797171 failed xUnit2031 on Assert.Single(Where(...)); the same dataset assertion now uses the analyzer-required predicate overload. No relationship acceptance follows from that build.

## Current accepted totals: EF 10.0.12

Hosted run 38139977914, commit `a875ac7`, passes the following scope with complete fixture cleanup. Every upstream case is listed individually in the [named inventory](../verification/EF_UPSTREAM_INVENTORY.md) and matched to the retained hosted output.

| Selected upstream family | Passed cases | Skipped | Failed |
|---|---:|---:|---:|
| Comparison operators | 6 | 0 | 0 |
| Logical operators | 6 | 0 | 0 |
| Nullable enum equality | 3 | 0 | 0 |
| Northwind Where | 11 | 0 | 0 |
| Northwind Select | 4 | 0 | 0 |
| Northwind no tracking | 3 | 0 | 0 |
| Scalar partial updates/delete, concurrency and mutable values | 11 | 0 | 0 |
| Direct Customer/Order Includes | 7 | 0 | 0 |
| **Upstream total** | **51** | **0** | **0** |
| Separate local fixture-model/data checks | 4 | 0 | 0 |

The 51 cases invoke 50 distinct original methods: two no-tracking cases select different arguments of the same method. CI runs the four offline checks before fixture startup and repeats them in the complete 55-case suite; they are not counted twice as distinct tests. Initial failed attempts remain documented above, not hidden as passing acceptance. Full provider conformance, original optional relationships and excluded methods remain outside this explicit slice.

## Verified increment: null/type semantics and literal Guid construction

Local FS-07 acceptance on 2026-10-11 passes **61/61 checks**: 57 original upstream cases covering 56 distinct methods, plus four adapter checks. The seven enum equality methods and two Guid construction methods each assert exact generated Cypher. Six of these cases are new; the earlier tables retain their historical counts. The first attempt passed eight of these nine selected methods and exposed the Guid literal NewExpression gap. The original test now passes after narrow constructor-only parameterization; no assertions were weakened. The full 61-case rerun, 26 related provider query/integration cases and all 618 provider units pass, and the owned fixture is removed in the same shell. See the [named inventory](../verification/EF_UPSTREAM_INVENTORY.md) and [scalar contract](../../docs/features/NULLS_TYPES_AND_TIME.md).
