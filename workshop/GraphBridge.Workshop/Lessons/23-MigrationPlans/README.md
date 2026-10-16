# 23 · Migration plans

**Goal:** change schema deliberately: preview a typed plan offline, validate it, apply it once, and see that reapplying is a no-op.
**Track:** full · **Mode:** offline + Neo4j · **Time:** ~15 min · **Builds on:** 03

## Concepts

- A `GraphMigrationPlan` is a typed, ordered list of steps (constraints, indexes, bounded data changes) with a checksum and an explicit rollback policy.
- `Preview()` is offline: it shows the exact Cypher, impact and preconditions for review.
- Applying is an explicit deployment operation. A journal in the database records completed plans, so reapplying the same plan does nothing.

## Run it

```powershell
dotnet run --project workshop/GraphBridge.Workshop -- run 23
```

## Walkthrough

1. **The plan.**

<!-- region: Plan -->
```csharp
public static GraphMigrationPlan Plan { get; } = new(
    "workshop-001-movie-released", predecessorId: null,
    [GraphMigrationStep.CreateIndex("released-index",
        new GraphMigrationSchemaDefinition("workshop_movie_released", GraphMigrationSchemaKind.RangeIndex, "NODE", "Movie", ["released"]))],
    GraphMigrationRollbackPolicy.NoRollback("Dropping the index is a separate, reviewed migration."));
```

2. **Preview offline.**

<!-- region: Preview -->
```csharp
var preview = Plan.Preview();
var journal = GraphMigrationJournal.PreviewInitialization();
```

<!-- snapshot: 23-step -->
```cypher
CREATE RANGE INDEX `workshop_movie_released` FOR (n:`Movie`) ON (n.`released`)
```

   The journal itself is three uniqueness constraints on `__GraphBridgeMigration*` labels; they are previewed too.

3. **Initialize, validate, apply.**

<!-- region: Apply -->
```csharp
await db.Database.InitializeGraphMigrationsAsync(cancellationToken: cancellationToken);
var validation = await db.Database.ValidateGraphMigrationAsync(Plan, cancellationToken: cancellationToken);
var applied = await db.Database.ApplyGraphMigrationAsync(Plan, cancellationToken: cancellationToken);
```

   On the first run the step is applied. On later runs validation reports that the plan was already applied, and applying again changes nothing.

## What you should see

A checksum, one step with its Cypher, the journal statements, and a successful (or already-applied) apply followed by a no-op.

## Try it yourself

1. Change the index name in the plan without changing its id, and apply. Why is it refused?
2. Add a second step `GraphMigrationStep.AssertPrecondition(...)` and preview the plan again.

## Limits

- A plan's schema steps are not one atomic transaction; each records its own completion, and recovery is explicit.
- No automatic `Down`. Existence and property-type constraints need Neo4j Enterprise.
- Migration APIs refuse to run inside an EF transaction.

## Read more

[Migration recovery example](../../../../docs/features/SCHEMA_AND_MIGRATIONS.md#migration-recovery-example) · [Explicit migration plans](../../../../docs/features/SCHEMA_AND_MIGRATIONS.md#typed-migration-plans)
