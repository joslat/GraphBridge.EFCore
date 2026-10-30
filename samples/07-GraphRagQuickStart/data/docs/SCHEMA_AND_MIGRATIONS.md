# Schema and migrations

GraphBridge never changes your database schema on its own. You compare the schema your EF model needs with what the database has, review the proposed constraints and indexes, and apply changes deliberately: by hand, in a deployment gate, or with a typed migration plan that has an offline preview, a journal and explicit recovery.

```csharp
using GraphBridge.EntityFrameworkCore.Schema;
using Microsoft.EntityFrameworkCore;

var manifest = db.Database.GetGraphSchemaManifest();          // offline: what the model needs
Console.WriteLine(manifest.Fingerprint);

var inspection = await db.Database.InspectGraphSchemaAsync();  // metadata reads only
Console.WriteLine(inspection.Comparison.Status);               // Pass, Warn or Fail
foreach (var proposal in inspection.Comparison.Proposals.Where(p => p.IsRequired))
    Console.WriteLine(proposal.Cypher);                        // text to review; never executed
```

On an empty database, the workshop model reports `Fail`: each node key and the `ACTED_IN` relationship key lacks its uniqueness constraint. Each missing constraint comes with a proposal such as:

```cypher
CREATE CONSTRAINT `<name>` FOR (n:`Review`) REQUIRE (n.`id`) IS UNIQUE
```

## What GraphBridge does not do

- It runs no DDL on startup, during queries or during `SaveChangesAsync`.
- `SaveChangesAsync` checks, inside its transaction, that a uniqueness constraint covers the complete key of every label and relationship type it writes. If one is missing, it throws `InvalidOperationException` ("GraphBridge requires an existing uniqueness constraint for :Label (key) ...") and leaves the entries pending. Reconnecting or retrying does not help: deploy the constraint.
- Inspection never repairs, drops or replaces schema, and never scans graph data.
- Migrations never run unless you call them.

## Storage manifest

`GetGraphSchemaManifest()` builds a deterministic witness of the finalized EF model without touching the database: labels and relationship types, keys, relationship roles and direction, stored properties, concurrency semantics and index intent. Its `Fingerprint` (SHA-256 of `CanonicalJson`) ignores CLR names and declaration order, so you can store it with a deployment and compare it later. Opaque value converters and unavailable index facets are listed in `Caveats`: equal fingerprints do not prove that two arbitrary converters are equivalent. [Mapping nodes](https://github.com/joslat/GraphBridge.EFCore/blob/main/docs/features/MAPPING_NODES.md#the-storage-manifest) shows the manifest's members for the workshop model.

## Inspect the database schema

`InspectGraphSchemaAsync(maxSchemaRows = 10_000, cancellationToken)` reads the server version and edition (`dbms.components()`), `SHOW CONSTRAINTS` and `SHOW INDEXES` in the database selected by `UseNeo4j`, and compares them with the manifest. The result has the manifest, the detected `Capabilities`, the raw `Observation` and a `Comparison` with `Status`, `Issues` and `Proposals`.

| Status | Meaning | Effect |
|---|---|---|
| `Fail` | A required key constraint is missing, or existing schema conflicts with it (for example a range index that occupies the required slot) | Writes to that label or type are refused until you fix it |
| `Warn` | Something you should decide on: a mapped index is missing, requiredness or property types are enforced by the application rather than the database (always the case on Community), relationship cardinality is checked by the provider, or the database has schema the model does not use | Writes still work |
| `Pass` | Every expectation is met | None |

Each issue has a `Status`, a `Category` (`Identity`, `Existence`, `PropertyType`, `Index`, `Conflict`, `Capability`, `UnmanagedSchema`, `Manifest`), a stable `Code`, a `Subject` and a `Message`. Proposals are `CREATE CONSTRAINT` or `CREATE RANGE INDEX` text, with `IsRequired` set for those that unblock writes. On Community they are uniqueness constraints and range indexes; existence and property-type proposals appear only where the detected server supports them (see [capability detection](https://github.com/joslat/GraphBridge.EFCore/blob/main/docs/reference/COMPATIBILITY.md#version-and-edition-capability-detection)).

Inspection borrows an active EF transaction unchanged; otherwise it opens and disposes its own read transaction. If any metadata result exceeds `maxSchemaRows`, it throws instead of returning a partial report. The cap limits rows, not server work.

### Permissions

The account needs to see constraints and indexes (`SHOW CONSTRAINT` and `SHOW INDEX` privileges) in the selected database. Permission and transport errors propagate as exceptions: missing visibility is never reported as an empty schema. Writers need the same schema visibility, because every save checks its key constraints. The [verified Enterprise profiles](https://github.com/joslat/GraphBridge.EFCore/blob/main/eng/enterprise/README.md) list exact reader and writer grants. Doctor reports incomplete inspection as `GBD-CONN-002`.

## Apply reviewed schema

Read the proposals, decide, and apply them outside the application's request path:

- run the reviewed statements with an administrative account in your deployment tooling, Neo4j Browser or `cypher-shell`; or
- turn a proposal into a typed migration step, so it is previewed, journaled and applied once:

```csharp
using GraphBridge.EntityFrameworkCore.Migrations;

var steps = inspection.Comparison.Proposals.Where(p => p.IsRequired)
    .Select(p => GraphMigrationStep.CreateConstraint("create-" + p.Name,
        GraphMigrationSchemaDefinition.FromProposal(p)));   // copies metadata; proposal text is never executed
```

A uniqueness constraint does not make a property required. Creating one validates existing data and fails if duplicates exist.

## Read-only schema drift gate

Run inspection as a deployment gate with read-only credentials. The smallest gate fails on `Fail` and lets warnings through:

```csharp
var inspection = await db.Database.InspectGraphSchemaAsync(cancellationToken: cancellationToken);
var deploymentExitCode = inspection.Comparison.Status == GraphSchemaStatus.Fail ? 1 : 0;   // warnings do not block
```

The Blogging sample's `--check-schema` command is a stricter, copyable policy. It emits one JSON report and returns a real process exit code. It never creates constraints, repairs data or runs CRUD, and combining it with write modes is rejected before connecting.

| Exit | Meaning | CI action |
|---|---|---|
| 0 | Required identity constraints and mapped indexes present/online | Continue; review warnings |
| 3 | Drift or unknown required capability | Stop deployment and review report |
| 2 | Configuration, permissions, cancellation or inspection failure | Stop; incomplete inspection is not compatibility |

Application policy `required-identities-and-mapped-indexes-v1` promotes mapped-index warnings to blockers. It retains Community existence/type warnings, relationship cardinality caveats and unmanaged-schema warnings. Success is **not** a data-integrity scan, proof of endpoint cardinality, snapshot isolation or blanket compatibility certification. Metadata may change after inspection.

The versioned JSON includes the observed server version, edition and capabilities, the database, the expected model fingerprint, the policy, issues and blockers. It contains no query values, credentials or exception text. Schema and database names are application metadata: keep reports under appropriate access control. Failure JSON uses stable error categories. Unknown required capabilities fail closed.

From a built checkout, with the fixture settings loaded into the process environment:

```powershell
dotnet run --project samples/01-Blogging-CodeFirst/Blogging.App --no-build -- --check-schema > schema-report.json
$schemaExit = $LASTEXITCODE
if ($schemaExit -ne 0) { throw "Schema preflight failed with exit $schemaExit; inspect schema-report.json." }
```

The sample entry point accepts only the disposable localhost fixture. For your deployment, copy [SchemaPreflight.cs](https://github.com/joslat/GraphBridge.EFCore/blob/main/samples/01-Blogging-CodeFirst/Blogging.App/SchemaPreflight.cs) into your application, pass its configured context and use read-only credentials with metadata visibility. Select the database explicitly. The helper does not read environment variables or own a second model.

A CI step after configuring the context and credentials; schema provisioning stays a separately reviewed step:

```yaml
- name: Validate graph schema
  shell: pwsh
  run: |
    dotnet run --project samples/01-Blogging-CodeFirst/Blogging.App --no-build -- --check-schema > schema-report.json
    $schemaExit = $LASTEXITCODE
    if ($schemaExit -ne 0) { throw "Schema preflight exited $schemaExit. Review schema-report.json." }
```

[Doctor](https://github.com/joslat/GraphBridge.EFCore/blob/main/docs/tools/DOCTOR.md) runs the same inspection and reports it as `GBD-SCHEMA-000` (pass), `GBD-SCHEMA-002` (pass with warnings), `GBD-SCHEMA-001` (a required expectation fails) or `GBD-CAP-001` (capabilities unknown).

## Typed migration plans

A `GraphMigrationPlan` is an immutable, ordered list of typed steps with an ID, a predecessor, a checksum and an explicit rollback policy. It accepts no raw Cypher.

```csharp
using GraphBridge.EntityFrameworkCore.Migrations;

public static GraphMigrationPlan Plan { get; } = new(
    "workshop-001-movie-released", predecessorId: null,
    [GraphMigrationStep.CreateIndex("released-index",
        new GraphMigrationSchemaDefinition("workshop_movie_released", GraphMigrationSchemaKind.RangeIndex, "NODE", "Movie", ["released"]))],
    GraphMigrationRollbackPolicy.NoRollback("Dropping the index is a separate, reviewed migration."));
```

Preview it offline. `Preview()` returns the checksum and, for each step, its exact Cypher and parameters, impact, required capabilities, precondition and postcondition:

```csharp
var preview = Plan.Preview();
var journal = GraphMigrationJournal.PreviewInitialization();
```

```cypher
CREATE RANGE INDEX `workshop_movie_released` FOR (n:`Movie`) ON (n.`released`)
```

The journal is three uniqueness constraints on reserved labels; review them before initializing:

```cypher
CREATE CONSTRAINT `gb_migration_control_key` FOR (n:`__GraphBridgeMigrationControl`) REQUIRE n.`id` IS UNIQUE
CREATE CONSTRAINT `gb_migration_history_key` FOR (n:`__GraphBridgeMigration`) REQUIRE n.`id` IS UNIQUE
CREATE CONSTRAINT `gb_migration_step_key` FOR (n:`__GraphBridgeMigrationStep`) REQUIRE n.`id` IS UNIQUE
```

Then initialize, validate and apply, as explicit deployment operations:

```csharp
await db.Database.InitializeGraphMigrationsAsync(cancellationToken: cancellationToken);
var validation = await db.Database.ValidateGraphMigrationAsync(Plan, cancellationToken: cancellationToken);
var applied = await db.Database.ApplyGraphMigrationAsync(Plan, cancellationToken: cancellationToken);
```

Applying the same plan again returns `AlreadyApplied` and changes nothing.

### Steps

| Step | Factory | Behavior |
|---|---|---|
| Create constraint | `GraphMigrationStep.CreateConstraint(id, definition)` | Uniqueness, existence or property type (`STRING`, `INTEGER`, `FLOAT`, `BOOLEAN`). Existence and type need Enterprise |
| Create index | `GraphMigrationStep.CreateIndex(id, definition)` | Range, vector or full-text index; waits until it is `ONLINE`, up to `IndexWaitTimeout` |
| Drop constraint or index | `DropConstraint(id, definition, destructiveReason)`, `DropIndex(...)` | The named object must exactly match the expected definition |
| Backfill | `Backfill(id, scope, property, value, expectedCount)` | Sets an absent property on exactly `expectedCount` elements |
| Rename property | `RenameProperty(id, scope, from, to, expectedCount, destructiveReason)` | Copies to an absent destination, then removes the source |
| Assert precondition | `AssertPrecondition(id, scope, expectedCount)` | Checks a bounded count; changes nothing |

`GraphMigrationSchemaDefinition(name, kind, "NODE" or "RELATIONSHIP", labelOrType, properties, propertyType)` names one exact constraint or range index. Vector and full-text indexes have their own factories, below. `GraphMigrationDataScope(entityType, labelOrType, filters, maxRows)` selects elements by label or type and parameterized equality filters, with a cap of 1 to 100,000 candidates. Selected elements are locked and rechecked before they change. The cap bounds candidates and mutations, not the work of finding matches. Concurrent insertions are not fenced: stop application writers when you need a complete label or type snapshot.

### Vector and full-text indexes

[Typed search](https://github.com/joslat/GraphBridge.EFCore/blob/main/docs/features/GRAPH_SEARCH.md) reads vector and full-text indexes; a migration creates them with the same preview, journal and readiness wait as a range index:

```csharp
var embedding = GraphMigrationSchemaDefinition.VectorIndex("chunk_embedding", "NODE", "Chunk", "embedding",
    dimensions: 1536, GraphVectorSimilarity.Cosine);
var text = GraphMigrationSchemaDefinition.FullTextIndex("chunk_text", "NODE", ["Chunk", "Section"], ["text", "title"],
    analyzer: "english");

var searchIndexes = new GraphMigrationPlan("app-002-search-indexes", "app-001-keys",
    [GraphMigrationStep.CreateIndex("embedding-index", embedding), GraphMigrationStep.CreateIndex("text-index", text)],
    GraphMigrationRollbackPolicy.NoRollback("Dropping a search index is a separate, reviewed migration."));
```

```cypher
CREATE VECTOR INDEX `chunk_embedding` FOR (n:`Chunk`) ON (n.`embedding`)
OPTIONS {indexConfig: {`vector.dimensions`: 1536, `vector.similarity_function`: 'cosine'}}
CREATE FULLTEXT INDEX `chunk_text` FOR (n:`Chunk`|`Section`) ON EACH [n.`text`, n.`title`]
OPTIONS {indexConfig: {`fulltext.analyzer`: 'english', `fulltext.eventually_consistent`: false}}
```

- **Definitions:** a vector index covers one label or relationship type and one property, with 1 to 4,096 dimensions and `Cosine` or `Euclidean` similarity. A full-text index covers 1 to 16 labels or types and 1 to 16 properties. Its analyzer defaults to `standard-no-stop-words`, Neo4j's own default, and is always written, so the result doesn't depend on the server's default setting.
- **Matching:** an index under the step's name is the declared one only when its type, target, properties and declared options match: the dimensions and similarity for a vector index, and the analyzer and consistency for a full-text index. Labels and properties of a full-text index compare as sets. Anything else under that name is a conflict that stops the step for review.
- **One per schema:** if an index of the same kind already covers the same labels and properties under another name, creation is refused and the message names it, whatever its options. For vector indexes this is Neo4j's rule: one per label and property. For full-text indexes Neo4j refuses only the same labels and properties in the same order, and accepts a reordered list as a second index; GraphBridge refuses that too, because a full-text index's order doesn't change what it indexes.
- **Options left to the server:** options a definition doesn't declare, such as vector quantization, which Neo4j 2026.09 enables by default, are neither written nor compared.
- **Eventual consistency:** an eventually consistent full-text index can be `ONLINE` before recent writes are searchable. The migration doesn't wait for that.
- **Capabilities:** `VectorIndexes` and `FullTextIndexes` are `Supported` on recognized 2025 and 2026 releases and `Unknown` on Neo4j 5, whose index DDL GraphBridge hasn't verified. A plan that needs an `Unknown` capability doesn't run.

Adding these kinds didn't change the checksums of plans made of the earlier kinds, so journals of applied migrations still match them.

### Apply contract

| Rule | Detail |
|---|---|
| Journal | `InitializeGraphMigrationsAsync` creates and verifies the journal. `ValidateGraphMigrationAsync` reads it, and the first unfinished step, without writing; check `CanApply`, `Issues`, `AlreadyApplied`, `OwnerToken` and `OwnerGeneration` |
| Order | Plans form a chain: a plan's `predecessorId` must equal the recorded head (`null` for the first plan) |
| Checksums | A completed plan replays as a no-op only with the same checksum. A changed plan under an existing ID is refused |
| Atomicity | Each data step and its completion record commit together. Each schema step uses separate prepared, schema and completion transactions, because Neo4j cannot mix schema and data writes in one transaction. The whole plan is **not** atomic |
| Ownership | One migrator owns the journal at a time. Ownership has no expiry: a failed run keeps it until explicit recovery |
| Unrecorded schema | A step does not adopt a constraint or index that its journal did not create; equivalent or conflicting existing schema needs review |
| Transactions | Migration calls are refused inside an active EF transaction. Use a fresh context afterwards, because native changes do not refresh tracked entities |
| Bounds | `GraphMigrationOptions(transactionTimeout, indexWaitTimeout, maxSchemaRows)` defaults to 2 minutes, 30 seconds and 10,000 rows |

There is no automatic retry, compensation or `Down`. Concurrent journal initialization can return Neo4j's deadlock error to one caller; rerun initialization explicitly and it verifies the exact stored definitions. The [design-time commands](https://github.com/joslat/GraphBridge.EFCore/blob/main/docs/tools/DESIGN_TIME.md) list, preview and report the status of plans that an application declares; they never initialize or apply them.

## Migration recovery example

Steps commit separately, so a failed plan can leave completed changes and an owner in the journal. Recovery finishes the plan from that state. It does not turn several transactions into one atomic migration or compensate for completed steps.

The Organization sample has two real plans: `organization_sample_schema_v1` (three uniqueness constraints) and `organization_sample_employment_v2`, whose predecessor is the first. Preview both without a database, from a built checkout:

```powershell
dotnet run --project samples/02-Organization-OntologyFirst/Organization.App --no-build -- --preview
```

Every statement, parameter and precondition is printed, with each plan's checksum. The sample's parameters are public constants; previews of your own plans contain application data. The [compiled plans](https://github.com/joslat/GraphBridge.EFCore/blob/main/samples/02-Organization-OntologyFirst/Organization.App/OrganizationPlans.cs) restrict every v2 data step to one exact employment ID and at most one relationship. They backfill `department`, then copy `role` to an absent `position` before removing `role`. Review the output and the no-rollback policy before running the sample's explicit `--prepare-schema` and `--migrate-demo` modes; the [sample instructions](https://github.com/joslat/GraphBridge.EFCore/blob/main/samples/02-Organization-OntologyFirst/README.md) describe the disposable fixture and ownership guard. Preview checks the plan, not the database; `ValidateGraphMigrationAsync` is the separate read-only observation.

Recovery takes the exact observed owner token and generation, and your declaration that the previous process is stopped or fenced and its outstanding transactions are resolved:

```csharp
using GraphBridge.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore;

// db is a fresh GraphBridge DbContext; plan is the identical reviewed plan.
// Call only AFTER stopping/fencing the former worker and resolving its transactions.
var failed = await db.Database.ValidateGraphMigrationAsync(plan);
if (failed.OwnerToken is null)
    throw new InvalidOperationException("There is no abandoned owner to recover.");

var recovery = new GraphMigrationRecovery(
    failed.OwnerToken, failed.OwnerGeneration,
    priorOwnerQuiesced: true,
    reason: "Prior worker stopped; transactions resolved and durable journal reviewed.");
var recovered = await db.Database.RecoverGraphMigrationAsync(plan, recovery);
```

**Do not put this in a general exception handler that always declares the prior owner quiescent.** The declaration must be true: a generation number cannot fence a paused process that later sends DDL, and an old timestamp does not authorize takeover. `GraphMigrationRecovery` rejects `priorOwnerQuiesced: false`. Changed checksums under an existing ID, stale generations, conflicting schema and unresolved preconditions need review. Never erase the journal to make a retry succeed.

| Failure point | What recovery does |
|---|---|
| Preview or validation | Nothing to recover: neither writes the journal or schema |
| A data step fails before commit | The data change and its completion record roll back together; recovery runs the step once |
| Interrupted after a data step committed | The data and its journal record committed together; recovery recognizes the step and does not backfill twice |
| A schema step was prepared, then interrupted | If the exact postcondition holds, completion is recorded without repeating the DDL; if the exact precondition holds, the step runs; otherwise it is a conflict, and recovery never drops and recreates schema automatically |

## Limits

- No whole-plan atomicity, automatic retry, ownership expiry, compensation or `Down`.
- No `dotnet ef migrations` integration and no arbitrary data scripts or procedures; data changes are typed, bounded steps.
- Enterprise existence and type proposals, and Graph Type observation, have unit evidence only; no Enterprise schema deployment is claimed.
- Inspection is not a data scan: a passing report does not prove there are no duplicates, missing values or wrong relationship counts.
- Recovery is exercised at deterministic checkpoints around real Neo4j transactions, not with network disconnects. For uncertain `SaveChangesAsync` commits, see [transactions and concurrency](https://github.com/joslat/GraphBridge.EFCore/blob/main/docs/features/TRANSACTIONS_AND_CONCURRENCY.md).

## Try it

- Workshop lessons [03 · Schema before data](https://github.com/joslat/GraphBridge.EFCore/blob/main/workshop/GraphBridge.Workshop/Lessons/03-SchemaFirst/README.md), [23 · Migration plans](https://github.com/joslat/GraphBridge.EFCore/blob/main/workshop/GraphBridge.Workshop/Lessons/23-MigrationPlans/README.md) and [24 · Deployment preflight and command tracing](https://github.com/joslat/GraphBridge.EFCore/blob/main/workshop/GraphBridge.Workshop/Lessons/24-PreflightAndTracing/README.md).
- Samples [01 Blogging](https://github.com/joslat/GraphBridge.EFCore/blob/main/samples/01-Blogging-CodeFirst/README.md) (`--check-schema`), [02 Organization](https://github.com/joslat/GraphBridge.EFCore/blob/main/samples/02-Organization-OntologyFirst/README.md) (plans and recovery) and [04 Design-time](https://github.com/joslat/GraphBridge.EFCore/blob/main/samples/04-DesignTime/README.md).

Evidence: [version and capability matrix](https://github.com/joslat/GraphBridge.EFCore/blob/main/tests/verification/VERSION_CAPABILITY_MATRIX.md), [schema tests](https://github.com/joslat/GraphBridge.EFCore/blob/main/tests/GraphBridge.IntegrationTests/ProviderSchemaTests.cs), [migration tests](https://github.com/joslat/GraphBridge.EFCore/blob/main/tests/GraphBridge.IntegrationTests/ProviderMigrationTests.cs). Decisions: [ADR-0017](https://github.com/joslat/GraphBridge.EFCore/blob/main/docs/architecture/adr/ADR-0017.md), [ADR-0018](https://github.com/joslat/GraphBridge.EFCore/blob/main/docs/architecture/adr/ADR-0018.md).
