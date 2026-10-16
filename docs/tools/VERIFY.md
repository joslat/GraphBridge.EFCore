# GraphBridge Verify

Verify turns the query contracts you registered for [Doctor](DOCTOR.md) into pass/fail evidence with an exit code, so a build or deployment pipeline can gate on it. It works in three tiers: V0 compiles offline, V1 adds read-only schema inspection and `EXPLAIN`, and V2 compares real results with independent native references on a disposable fixture you own. It is a C# API (`GraphBridge.Tooling.Diagnostics.GraphVerify`) in the optional `GraphBridge.Tooling` package.

```csharp
using GraphBridge.Tooling.Diagnostics;

// V0: no database connection; finalized model plus compilation of every contract.
var offline = GraphVerify.Offline(db, catalog);
Console.WriteLine(offline.ToText());

// V1: explicit metadata inspection and EXPLAIN of every contract; no query results are executed.
var inspected = await GraphVerify.InspectAsync(db, catalog, "myapp.staging.v1",
    maxSchemaRows: 1000, maxPlanNodes: 256, cancellationToken: cancellationToken);
Console.WriteLine(inspected.ToJson());
return inspected.ExitCode;   // 0 only when the selected tier passes
```

`ToText()` prints a summary table followed by the full Doctor evidence:

```text
GraphBridge Verify V0 | status: pass | exit: 0
Every registered contract is required. Result semantics remain not-run.
Contract | Compile | Plan | Semantics
workshop.coactors.v1 | Pass | NotRun | NotRun
workshop.recent-work.v1 | Pass | NotRun | NotRun
```

| Tier | Call | Database access | What it establishes |
|---|---|---|---|
| V0 | `GraphVerify.Offline(context, catalog, includeStatements, ct)` | None | The model finalizes and every contract compiles. |
| V1 | `GraphVerify.InspectAsync(context, catalog, profileId, maxSchemaRows, maxPlanNodes, includeStatements, ct)` | Read-only metadata and `EXPLAIN` | V0, plus required schema and capabilities exist and the server can plan every query. |
| V2 | `GraphVerify.SemanticAsync(contextFactory, catalog, profileId, fixture, references, …)` | Reads on an owned disposable fixture, always rolled back | V1, plus each query returns exactly the rows its independent reference returns. |

## GraphBridge Verify V0 and V1

Every registered contract is required: there is no switch that marks a contract optional or ignored. Compilation, planning and result semantics are separate observations. V0 leaves planning and semantics `not-run`; V1 leaves semantics `not-run`. A plan does not prove correct rows, performance or production readiness.

| Exit code | Meaning |
|---|---|
| `0` | The selected tier passed for every contract. |
| `1` | Anything else: a failure, a blocked or unsupported observation, or a `not-run` requirement. An empty catalog is `not-run` and exits `1`. |

V1 compiles each trusted factory once and keeps the command privately for planning. If the model, schema or any compilation does not pass, planning is skipped and every contract stays visible with `Plan: NotRun`. Before planning a contract, V1 checks its declared `requiredFeatures`:

| Feature identifier | Requirement |
|---|---|
| `identified-relationships` | The inspected server must support relationship uniqueness constraints. |
| `distinct`, `optional-match`, `aggregation`, `with` | Recognized; planning must still succeed. |
| Any other identifier | The contract is `blocked` with `GBD-CAP-001`. |

V1 then calls the provider's `ExplainCypherAsync` with the contract's budget deadline. Owned read transactions are rolled back and disposed; an active application transaction is borrowed unchanged. No `PROFILE` and no result execution happen. The first planning failure or cancellation stops planning, leaves later contracts `not-run` and is never retried. Deadlines are checked between operations: they cannot interrupt factory code or in-flight driver I/O and are not server timeouts. `maxPlanNodes` accepts 1–4,096 and is checked after the driver buffers the plan, so it does not bound raw plan size or server work. Row and path budgets remain declarations in V0 and V1.

Plan evidence is a bounded preorder operator tree plus statement and shape fingerprints. Arguments, identifiers, parameter values and timings are omitted. Known operator names lose their database suffixes; unknown names become `redacted-operator`. The shape fingerprint identifies only this redacted shape, so different full plans can share it; it is not a speed comparison.

### Report output

`GraphVerificationReport.ToJson()` writes version 1 evidence:

| Field | Content |
|---|---|
| `tier` | `v0` or `v1`. |
| `evidence` | The complete Doctor report this tier captured (see [Doctor output](DOCTOR.md#report-output)). |
| `contracts[]` | Per contract: `contractId`, `compilation`, `planning`, `semantics` (always `not-run` here), an optional `plan` (`statementFingerprint`, `shapeFingerprint`, `operators[]` with `index`, `parent`, `operator`) and an optional `diagnostic`. |
| `requiredContractCount`, `status`, `exitCode` | The gate result. |

`includeStatements: true` discloses compiled Cypher and typed snapshots under the same sensitive-text rules as Doctor. Parameter values are never included.

Statement fingerprints identify exact generated text. A provider upgrade that changes generated Cypher changes them; for example, 0.1.0-preview.2 changed the text of every Fluent query with a WHERE clause. Review and re-record stored baselines after upgrading ([release notes](../../CHANGELOG.md)).

## GraphBridge Verify V2 semantic comparison

`GraphVerify.SemanticAsync` executes registered Fluent queries and independently written native read queries on an explicitly acknowledged disposable fixture, then compares both with fixed expected rows. Use it to prove that a specific query keeps the rows you intend, such as nulls from an OPTIONAL MATCH or duplicates from parallel relationships. It proves the named cases on that fixture, not general equivalence.

```csharp
// The harness provisions the fixture, its ownership markers and its data beforehand. Verify never seeds or cleans.
var fixture = new GraphVerificationFixture("movies.parallel-credits.v1", ownerId, recipeFingerprint,
    acknowledgeDisposable: true);
var reference = new GraphNativeReference("movies.coactors.native.v1", "movies.parallel-credits.v1",
    FluentMovieReferences.Coactors,                          // hand-written Cypher, not compiler output
    new Dictionary<string, object?> { ["run"] = runId, ["actor"] = "Ada", ["year"] = 2000 },
    record => [record["name"]],                              // read each record in declared column order
    [["Bob"], ["Cara"]]);                                    // fixed expected rows for this fixture recipe

// The factory returns a fresh context of your own type; the runner disposes it, the driver stays yours.
var report = await GraphVerify.SemanticAsync(() => new MoviesContext(driver), catalog,
    "myapp.disposable.v1", fixture, [reference], cancellationToken: cancellationToken);
Console.WriteLine(report.ToJson());   // or ToText(); both exclude raw values
return report.ExitCode;               // 0 only if every observation and cleanup passes
```

**Register executable contracts.** Use the three-argument `GraphQueryContract.Fluent(definition, factory, rowProjection)`. The trusted projection turns each result into scalar values in declared column order, preserving nulls and duplicates. Every contract must declare a `referenceId` and `fixtureId`, and each reference must exist for that fixture; otherwise nothing runs and the report is `blocked` with issue `reference-registration`. A compile-only registration is reported `unsupported` at compilation and can never become a semantic pass. The query's `Limit` must fit the declared row budget.

**Write independent references.** `GraphNativeReference` takes a stable ID, the fixture ID, a hand-written statement (up to 65,536 characters), its parameters, a record reader and up to 10,000 fixed expected rows of 1–64 columns. Do not derive the statement or the expectations from the Fluent compiler. References are trusted, reviewed reads, not a sandbox for arbitrary Cypher.

**Provide a fresh context.** The factory must return a new context with no transaction and no tracked entities; the runner owns and disposes it, while the driver stays yours. A context that is not fresh is rejected (issue `context-not-fresh`) and left alone.

**Own the fixture.** `GraphVerificationFixture(fixtureId, ownerId, fingerprint, acknowledgeDisposable: true)` requires a nonempty owner GUID and a lowercase SHA-256 fingerprint of your deterministic fixture recipe. Your harness must create exactly one `GraphBridgeFixture` node with `id = ownerId.ToString("N")` and exactly one `GraphBridgeVerificationFixture` node with matching `ownerId`, `fixtureId` and `fingerprint` properties, provision schema and data, exclude concurrent writers and remove everything afterwards. The markers guard against targeting the wrong database by accident; they are not authentication.

The run, in order:

1. Inspect metadata and `EXPLAIN` every contract (V1). Each factory is prepared once; the same command, parameters and reader are used for planning and execution.
2. Open one owned transaction and check the two fixture markers. A mismatch blocks with issue `fixture-mismatch`.
3. For each contract: run the native reference and compare it with the fixed expectations; only if that passes, run the Fluent query and compare it with the native rows.
4. Roll back and dispose the transaction and context. A cleanup failure blocks success.

A bad reference, missing fixture data, a changed null, a lost duplicate or a wrong direction cannot pass merely because both paths agree on an empty result: the native stage must first match the fixed rows. There are no retries. Read-committed transactions are not a universal snapshot. Deadlines are cooperative and cannot interrupt in-flight I/O; row limits do not bound server work or payload size.

`GraphSemanticVerificationReport` (JSON `tier: "V2"`) keeps the fixture ID and fingerprint, the nested V1 `inspection` report, `fixtureStatus`, `cleanup`, `issue` and per-case `referenceComparison` and `queryComparison` with sanitized differences. It excludes raw values, owner tokens, native statements and exception text. The nested V1 evidence still says semantics `not-run`, because it describes the prerequisite inspection; the top-level cases hold the result observations. An empty catalog is `not-run` and exits `1`.

## Typed query snapshots

With `includeStatements: true`, Doctor and Verify also keep `query.Semantics`, captured from the same compiler pass that produced the statement. V2 binds it to the exact prepared command used for execution. Default exports omit both statement text and snapshot, and do not capture snapshots at all. `GraphQueryEvidence.Capture` keeps a snapshot only when `includeStatement` is `true`.

The immutable, versioned snapshot records aliases, pattern occurrences, direction, clause order, attached predicates, WITH scope and aggregate expressions, and generated line spans. It contains mapped names, so the text-disclosure warning applies. It is not a database graph, a C# source map or an execution result; a missing or incomplete snapshot is never shown as a complete graph. [Snapshot boundaries](../features/FLUENT_CYPHER.md#supported-clauses-and-values).

[Query Lab](QUERY_LAB.md) renders these snapshots, plans and comparisons. It neither recompiles nor executes queries; planning and semantics must have been captured explicitly.

## Bounded scalar-result comparison

`GraphResultComparer.Compare(definition, expectedRows, actualRows)` compares two sets of rows you supply, in declared column order. It does no database work and makes no claim about where the rows came from; V2 adds the binding to executed queries, references and fixtures.

| Rule | Behavior |
|---|---|
| Ordering | `Ordered` compares sequences; `Multiset` keeps duplicate counts and ignores order. |
| Duplicates | With `Eliminate`, duplicate projected rows in either input fail; the helper never deduplicates silently. |
| Types | Checks column count, declared kind and nullability. Integers normalize to signed 64-bit. Finite `float`/`double` compare exactly as `double`, with no tolerance. `decimal`, non-finite and other values fail shape validation. Strings use ordinal equality. |
| Bounds | Both inputs must fit the row budget. Strings are limited to 4,096 characters and all normalized keys together to 1,048,576 characters. Exceeding a bound returns `blocked`; nothing passes by truncation. |

`GraphResultComparisonReport` keeps only `status`, `difference` (`None`, `RowCount`, `Values`, `Order`, `DuplicateRows`, `InvalidExpectedShape`, `InvalidActualShape`, `BudgetExceeded`), row counts and the first differing row position. No values are retained. A passing comparison on its own is not V2 evidence.

## Limits

- V0 and V1 never execute registered queries; only V2 does, and only on a fixture you acknowledge as disposable.
- Plans describe one server and configuration at one moment, not performance.
- Verify never provisions, seeds, repairs or cleans a fixture, and has no retries.
- There is no standalone `verify` command; call the API from your application or test host.

## Try it

- Workshop lesson [27 · GraphBridge Verify](../../workshop/GraphBridge.Workshop/Lessons/27-Verify/README.md) runs V0 and V1 on two workshop contracts.
- [Sample 05](../../samples/05-FluentCypher/README.md) is a runnable host:

```powershell
dotnet run --project samples/05-FluentCypher/FluentCypher.Sample.csproj -- --verify          # V0 JSON
dotnet run --project samples/05-FluentCypher/FluentCypher.Sample.csproj -- --verify-text     # V0 table
dotnet run --project samples/05-FluentCypher/FluentCypher.Sample.csproj -- --verify-inspect  # V1 JSON
dotnet run --project samples/05-FluentCypher/FluentCypher.Sample.csproj -- --verify-semantic # V2 JSON (or --verify-semantic-text)
```

`--verify-inspect` reads the four `GRAPHBRIDGE_NEO4J_*` connection variables. The V2 modes additionally need `GRAPHBRIDGE_VERIFY_DISPOSABLE=1`, `GRAPHBRIDGE_FIXTURE_ID` and `GRAPHBRIDGE_VERIFY_RUN_ID` (nonempty GUIDs in `N` format), accept only `bolt://127.0.0.1:17687` with database `neo4j`, and expect the [fixture recipe and references](../../samples/05-FluentCypher/FluentMovieReferences.cs) (actor Ada, year threshold 2000) to be provisioned already. The sample never seeds data. Exit codes: `0` pass, `1` non-passing evidence, `2` missing configuration or acknowledgment.

Design decision: [ADR-0042](../architecture/adr/ADR-0042.md). Evidence: [verification summary](../../tests/verification/VERIFICATION_SUMMARY.md).
