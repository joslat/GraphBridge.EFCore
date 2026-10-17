# GraphBridge Doctor

Doctor answers two questions: does my EF model finalize and do my registered queries compile, and what does the selected database's schema actually provide? Run it offline in a build or code review, and in connected read-only mode before a deployment. It is a C# API (`GraphBridge.Tooling.Diagnostics.GraphDoctor` in the optional `GraphBridge.Tooling` package) that you call from your own application host.

```csharp
using GraphBridge.Tooling.Diagnostics;

// Register the queries to check. The factory (here from workshop lesson 16) only builds a Fluent query; Doctor never runs it.
var budget = new GraphQueryBudget(maxRows: 100, deadline: TimeSpan.FromSeconds(10));
var catalog = new GraphQueryCatalog([
    GraphQueryContract.Fluent(new GraphQueryDefinition("workshop.coactors.v1",
        "Distinct co-actor names ordered by name.", "Lessons/16-FluentPatterns#Coactors",
        [new("name", GraphResultKind.String)], GraphResultComparison.Ordered, GraphDuplicatePolicy.Eliminate, budget),
        db => Lesson16FluentPatterns.Coactors(db, "Keanu Reeves"))
]);

// db is your existing DbContext configured with UseNeo4j. Offline: no connection is opened.
var report = GraphDoctor.InspectOffline(db, catalog);
Console.WriteLine(report.ToText());   // or report.ToJson()
return report.Status == GraphEvidenceStatus.Pass ? 0 : 1;
```

The text report starts like this (hashes, versions and counts shortened):

```text
GraphBridge evidence / format 1
Mode: offline | Status: pass
Database observation: not-run
Parameter values: excluded
Statement and schema text: excluded
Loaded provider: 0.1.0-preview.3…
…
Model fingerprint: …
Mapped nodes: … | relationships: …
GBD-MODEL-000 | Model | pass
…
workshop.coactors.v1 | compilation: pass | execution: not-run
```

Doctor is the first of three tools that share one query catalog and one evidence format. [GraphBridge Verify](VERIFY.md) turns the same catalog into pass/fail gates with EXPLAIN plans and result comparisons, and [Query Lab](QUERY_LAB.md) renders the captured evidence as HTML or in VS Code.

## Registered queries and offline Doctor

A **query contract** pairs a public declaration with a trusted factory that builds the query. `GraphQueryDefinition` takes:

| Argument | Meaning |
|---|---|
| `id` | Stable public identifier: lowercase ASCII segments separated by `.` or `-`, at most 100 characters, for example `movies.coactors.v1`. |
| `description`, `sourceReference` | Public text (1–1,024 characters, no control characters). Never put secrets in them. |
| `columns` | 1–64 distinct output fields, each a `GraphResultColumn(name, GraphResultKind, nullable)`. Kinds: `String`, `Integer`, `FloatingPoint`, `Boolean`. |
| `comparison` | `Ordered` or `Multiset`: how Verify V2 compares rows. |
| `duplicates` | `Preserve` or `Eliminate`: whether duplicate rows are part of the contract. |
| `budget` | `GraphQueryBudget(maxRows, deadline, maxPathHops)`: 1–10,000 rows, a deadline up to 5 minutes, optional 0–32 path hops. |
| `requiredFeatures` | Optional, up to 32 identifiers that Verify V1 checks against the server (see [Verify](VERIFY.md#graphbridge-verify-v0-and-v1)). |
| `referenceId`, `fixtureId` | Optional, declared together: the independent native reference and fixture that Verify V2 uses. |

Register the definition with `GraphQueryContract.Fluent(definition, factory)`. A second overload, `Fluent(definition, factory, rowProjection)`, adds the scalar-row projection that semantic verification needs; Doctor never invokes it. `GraphQueryCatalog` holds at most 256 contracts with distinct IDs and orders them by ID. The catalog accepts Fluent Cypher factories only; it does not discover assemblies or evaluate source strings. The [sample registrations](../../samples/05-FluentCypher/FluentMovieContracts.cs) register three real movie queries with projections.

`GraphDoctor.InspectOffline(context, catalog, includeStatements: false, cancellationToken)` then:

1. Finalizes the model through `GetGraphSchemaManifest()` and records its storage fingerprint and node/relationship counts (`GBD-MODEL-000`, or `GBD-MODEL-001` when the model is invalid).
2. Compiles every contract with the provider's own Fluent compiler. A failure stays attached to its contract ID with a [stable diagnosis](#stable-diagnoses).

If the model fails, no query is compiled and every contract stays `not-run`. Doctor creates no session, runs no DDL, executes nothing and leaves the context, its driver and its change tracker to you. Cancellation is checked between steps; it cannot interrupt your factory code. A catalog with no contracts still reports the model, which says nothing about queries or the database.

Factories are trusted application delegates, **not sandboxed code**. Register only functions that construct queries: do not execute database work inside them and never register untrusted delegates.

To capture one command without a catalog, call `GraphQueryEvidence.Capture(contractId, query.ToCommand(), includeStatement: false)` and wrap the result in `new GraphEvidenceReport(GraphEvidenceMode.Offline, [snapshot], [])`.

## Explicit connected, read-only Doctor

```csharp
var report = await GraphDoctor.InspectAsync(db, catalog, "myapp.staging.v1",
    maxSchemaRows: 1000, cancellationToken: cancellationToken);
Console.WriteLine(report.ToJson());
```

Connected mode runs the offline checks, then calls `InspectGraphSchemaAsync()` to read server components, constraints and indexes (plus the current graph type on a recognized Enterprise profile) and compares them with the finalized model. It performs no DDL, no data scan and no registered query execution. If the context already has an EF transaction, Doctor borrows it unchanged; otherwise the inspector rolls back and disposes its own read transaction.

| Parameter | Meaning |
|---|---|
| `profileId` | Your public name for the environment the evidence describes, such as `myapp.staging.v1`. Same format as a contract ID. It is not a hostname or connection string. |
| `maxSchemaRows` | 1–10,000. Bounds metadata rows read, not field size or server work. |
| `includeStatements` | Discloses compiled Cypher and typed snapshots; see [output](#report-output). |
| `cancellationToken` | Checked between inspector operations; it is not a server deadline. |

The report keeps the observed numeric server version, recognized edition (`community` or `enterprise`, otherwise `unrecognized`), advertised Cypher versions, inferred schema capabilities and finding codes with counts. It omits database and schema names, original messages, graph-type text and proposed DDL. When node-uniqueness or range-index support cannot be established, the database observation is `blocked` (`GBD-CAP-001`). Missing required schema fails (`GBD-SCHEMA-001`). Authentication or metadata denial, unreachable transport, exceeded row limits, cancellation and unknown failures keep no partial database success.

Warnings stay visible even when required expectations pass: `database.schemaStatus` is `warn`, the findings list them and the report carries `GBD-SCHEMA-002`. Typical warnings are application-enforced properties on Community and optional missing indexes. A pass does not prove data integrity, a consistent metadata snapshot, query planability or result correctness. For full schema names and proposed DDL, call `InspectGraphSchemaAsync()` yourself in a private context; Doctor does not keep them.

Use read-only credentials and select the intended database explicitly. Do not attach Doctor to every application request or run connected inspection on ordinary startup. Troubleshooting order: [Doctor decision tree](../reference/TROUBLESHOOTING.md#doctor-decision-tree).

## Report output

`GraphEvidenceReport.ToJson()` writes version 1 evidence with deterministic ordering (queries by contract ID, diagnostics by code). Enum values use kebab case, such as `"not-run"`. `ToText()` writes the same evidence as readable text. Neither call compiles or connects again. An abbreviated offline report (`…` marks omitted values):

```json
{
  "formatVersion": 1,
  "mode": "offline",
  "versions": { "provider": "…", "efCore": "…", "driver": "…", "tooling": "…" },
  "model": { "fingerprint": "…", "nodes": "…", "relationships": "…" },
  "queries": [ { "contractId": "workshop.coactors.v1", "statementFingerprint": "…", "parameters": [ { "position": 0, "kind": "string" } ], "compilation": "pass", "execution": "not-run", "…": "…" } ],
  "diagnostics": [ { "code": "GBD-MODEL-000", "status": "pass", "severity": "information", "phase": "model", "…": "…" } ],
  "parameterValuesIncluded": false,
  "sensitiveTextIncluded": false,
  "databaseObservation": "not-run",
  "status": "pass"
}
```

| Field | Content |
|---|---|
| `versions` | Informational versions of the loaded provider, EF Core, driver and tooling assemblies. |
| `model` | Storage fingerprint and counts; absent when the model failed. |
| `database` | Connected mode only: profile ID, server version, edition, capabilities, `schemaStatus`, grouped findings and its own `status`. |
| `queries[]` | Contract ID, SHA-256 statement fingerprint, parameter positions and broad kinds (`string`, `integer`, `floating-point`, `boolean`, `bytes`, `array`, `null`, `opaque`), the declared definition, `compilation`, an optional `diagnostic`, and `execution: not-run`. |
| `diagnostics[]` | Report-level diagnoses with explanation, suggested action and documentation link. |

By default a report contains **no Cypher text, parameter names or values, value lengths or exception messages**. With `includeStatements: true` (or `includeStatement: true` on `Capture`) each query also carries its parameterized `statement` and typed `semantics` snapshot, and `sensitiveTextIncluded` becomes `true`: labels and property names can reveal domain information. Parameter values are never included. A statement fingerprint proves equality, not protection against guessing.

## Status and exit codes

Every observation is `pass`, `fail`, `unsupported`, `blocked` or `not-run`. The report `status` is the worst of them, in that order of precedence: `fail`, then `blocked`, then `unsupported`, then `not-run`, otherwise `pass`. A blocked or unsupported observation can never become an overall pass. Connected mode without a completed database observation is `blocked`. A report you build yourself with `GraphEvidenceMode.Semantic` is always `blocked`: result comparisons come from [Verify V2](VERIFY.md#graphbridge-verify-v2-semantic-comparison), which produces its own report.

Doctor has no exit code of its own; your host decides. The usual mapping, used by the sample, is `0` for `Pass` and `1` otherwise, with `2` for invalid usage or configuration.

## Stable diagnoses

Every diagnosis has a fixed code, status, severity, phase, explanation, suggested action and documentation link. `GraphDiagnosticCatalog.Classify(exception, phase, fluentQuery: true)` maps a failure to a diagnosis without wrapping, retrying or changing the original exception; keep that exception locally for investigation, because reports never include its text. `GraphDiagnosticCatalog.Get(code)` returns a fixed diagnosis and `GraphDiagnosticCatalog.Codes` lists them all.

| Code | Status | Meaning / first action |
|---|---|---|
| GBD-MODEL-000 | pass | Finalized-model validation passed; the database was not observed. |
| GBD-MODEL-001 | fail | The model could not be finalized for graph storage. Check node labels, identity, scalar mappings and relationship endpoint roles. |
| GBD-QUERY-001 | unsupported | An EF LINQ query could not be translated. The `subcode` names the LINQ refusal (`GBQ###`, see [refusal codes](../reference/KNOWN_LIMITATIONS.md#refusal-codes)). |
| GBD-QUERY-002 | unsupported | A Fluent query could not be compiled. Check alias scope, direction, projection and finite bounds. The `subcode` names the Fluent code (`GBFC###`, see [Fluent Cypher](../features/FLUENT_CYPHER.md#predicates-as-c-lambdas)). |
| GBD-SCHEMA-000 | pass | The completed metadata comparison satisfies the schema expectations. |
| GBD-SCHEMA-001 | fail | A required constraint or index is missing. Provision the reviewed change explicitly; Doctor never repairs. |
| GBD-SCHEMA-002 | pass (warning) | Required expectations pass with warnings: application-enforced properties, optional indexes or unmanaged schema. |
| GBD-CAP-001 | blocked | A required capability is unknown. Check server version, edition and metadata permissions. |
| GBD-CONN-001 | blocked | Connection or session failure. Check endpoint, database, TLS and server readiness. |
| GBD-CONN-002 | blocked | Authentication or authorization prevented observation. Review credentials and grants privately. |
| GBD-EXEC-001 | blocked | The observation was canceled. Review the deadline before an explicit rerun. |
| GBD-EXEC-002 | blocked | Unclassified failure. Inspect the original exception locally. |

The `subcode` field appears only when the failure carried a `GraphBridge.QueryDiagnostic` or `GraphBridge.FluentDiagnostic` value in `Exception.Data`; it is omitted otherwise. `phase: Query` means local compilation, so server execution errors are never labeled as unsupported LINQ. Permission classification recognizes the driver's `AuthenticationException` and Neo4j's `Neo.ClientError.Security.*` [status codes](https://neo4j.com/docs/status-codes/current/errors/all-errors/). Schema, capability and model-pass diagnoses are added by the observing host; their presence in the catalog is not evidence that an observation ran.

To report an unsupported query, use the [sanitized unsupported-query template](../../.github/ISSUE_TEMPLATE/unsupported_query.md).

## Limits

- Doctor observes. It never repairs schema, runs migrations, executes registered queries, retries or certifies production readiness.
- Budgets in a definition are declarations; Doctor does not enforce them.
- There is no standalone `doctor` command and no assembly discovery: Doctor runs inside a host that knows how to build your context. `GraphBridge.Tool` does not include it.
- `GraphBridge.Tooling` brings its authoring dependencies (RDF, YAML, Roslyn) with it. The core provider does not depend on Tooling.
- To browse findings interactively, open the JSON report in the [Query Lab VS Code extension](QUERY_LAB.md#vs-code-extension), which has a Doctor view.

## Try it

- Workshop lesson [26 · GraphBridge Doctor](../../workshop/GraphBridge.Workshop/Lessons/26-Doctor/README.md) registers two queries and runs Doctor offline and connected.
- [Sample 05](../../samples/05-FluentCypher/README.md) is a runnable host. Offline modes need no database:

```powershell
dotnet run --project samples/05-FluentCypher/FluentCypher.Sample.csproj -- --doctor        # JSON
dotnet run --project samples/05-FluentCypher/FluentCypher.Sample.csproj -- --doctor-text   # text
dotnet run --project samples/05-FluentCypher/FluentCypher.Sample.csproj -- --doctor-inspect
```

`--doctor-inspect` reads `GRAPHBRIDGE_NEO4J_URI`, `GRAPHBRIDGE_NEO4J_DATABASE`, `GRAPHBRIDGE_NEO4J_USERNAME` and `GRAPHBRIDGE_NEO4J_PASSWORD` from the process environment. The URI must not embed credentials, and reports never print them. It passes only when the database has the movie model's constraints. Exit codes: `0` pass, `1` non-passing report, `2` invalid arguments or missing configuration.

Design decision: [ADR-0042](../architecture/adr/ADR-0042.md). Evidence: [verification summary](../../tests/verification/VERIFICATION_SUMMARY.md).
