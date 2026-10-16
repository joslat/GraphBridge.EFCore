# Design-time commands

GraphBridge supports a small design-time workflow. EF's own `dotnet ef dbcontext list` and `dbcontext info` work with a GraphBridge context, and the optional `GraphMigrationCommands` helper adds `list`, `preview` and `status` commands for your declared graph migration plans to your own application. Use them to review migrations in code review and to check, read-only, whether a database is ready for a plan. Relational `dotnet ef migrations` and `database update` do not apply to this non-relational provider.

```csharp
using GraphBridge.EntityFrameworkCore.Migrations;
using GraphBridge.Tooling.DesignTime;

var catalog = new[]
{
    new GraphMigrationPlan("initial", null,
        [GraphMigrationStep.CreateConstraint("person-key", new GraphMigrationSchemaDefinition(
            "gb_design_person_key", GraphMigrationSchemaKind.UniquenessConstraint, "NODE", "GraphBridgeDesignPerson", ["Id"]))],
        GraphMigrationRollbackPolicy.NoRollback("Review graph changes explicitly; this host never applies them."))
};
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
return await GraphMigrationCommands.RunAsync(args, catalog, DesignContextFactory.CreateStatusContext,
    Console.Out, Console.Error, cancellation.Token);
```

This is the complete `Program.cs` of the [design-time sample](../../samples/04-DesignTime/README.md). Running it with `list` prints the declared catalog without touching a database:

```json
{
  "formatVersion": 1,
  "kind": "declared-catalog",
  "databaseObserved": false,
  "migrations": [
    { "Id": "initial", "PredecessorId": null, "Checksum": "…", "stepCount": 1 }
  ]
}
```

## Commands

| Command | Behavior | Database access |
|---|---|---|
| `dotnet ef dbcontext list` | Discover the sample context | None in the supplied sample |
| `dotnet ef dbcontext info --context DesignContext` | Report context, provider and options | None in the supplied sample |
| `list` | List declared migration IDs, predecessors, checksums and step counts | None; this is the declared catalog, not applied history |
| `preview ID` | Render the typed plan: each step's kind, impact, preconditions, postconditions, required capabilities, Cypher and parameter names | None; no context is created |
| `preview ID --include-values` | Also show the parameter values | None; output may contain application data |
| `status ID` | Read the migration journal, schema and capabilities and the next unfinished step's precondition through the existing validation | Read-only; no initialization, ownership acquisition or apply |
| `help` | Print the usage line | None |

`GraphMigrationCommands.RunAsync(args, catalog, createContext, output, error, cancellationToken)` takes a trusted catalog of immutable `GraphMigrationPlan` objects (at most 256, with distinct case-sensitive IDs), a factory that creates a fresh context, and the output and error writers. Only `status` calls the factory, and it disposes the context it gets; your application still owns dependencies such as a borrowed `IDriver`. Output is versioned JSON. Unknown IDs, extra options and write commands are rejected before any context is created.

| Exit code | Meaning |
|---|---|
| `0` | Success: `list`, `preview` or `help` completed, or `status` reports the plan can be applied (including an already-applied plan). |
| `2` | Usage error, invalid catalog, unknown migration ID, or a failed inspection (configuration, authentication, database or metadata permissions). |
| `3` | `status` completed and found readiness issues, for example an uninitialized migration journal. |
| `130` | Canceled by the caller. |

`status` reports whether a migration owner exists, never the owner token. Error messages name only the exception type, because messages from your factory or the driver may contain connection secrets.

Status is an observation, not permission to apply or a lock against concurrent change. It is not a complete history listing, a model diff, a data-integrity scan or a validation of later dependent steps. Initialization, apply and recovery remain explicit application operations through the [migration APIs](../features/SCHEMA_AND_MIGRATIONS.md#typed-migration-plans) and the [recovery example](../features/SCHEMA_AND_MIGRATIONS.md#migration-recovery-example). Rendering a step's Cypher does not replace journaling, precondition checks or migration ownership.

## Actual EF CLI behavior and boundaries

The tested CLI is **dotnet-ef 10.0.12**, matching EF Core **10.0.12** on **.NET 10**. In the sample, an `IDesignTimeDbContextFactory<DesignContext>` supplies the model without reading credentials. It builds a lazy driver aimed at `bolt://127.0.0.1:1`, so the supported metadata commands never contact a server, create schema or seed data. The context owns and disposes that driver. Your own factories and startup code run as application code; the provider cannot make them offline or side-effect free.

`dbcontext info --json` reports provider `GraphBridge.EntityFrameworkCore`. EF reports database and connection details only for relational providers; their absence does not mean GraphBridge lost its configuration. Check your application configuration instead.

Relational `dotnet ef migrations add/list/script/remove`, `database update/drop`, built-in scaffolding and compiled-model generation are outside this subset. `dotnet ef migrations list --no-connect` exits 1 because no `IMigrator` is registered. EF's generic message suggests checking provider configuration; here the cause is the unsupported relational migration service, not a missing `UseNeo4j`. Use the graph `list`/`preview`/`status` host instead, and [GraphBridge.Tool](GRAPHBRIDGE_TOOL.md) for database-first scaffolding.

`Microsoft.EntityFrameworkCore.Design` brings Roslyn workspace packages into your application. The sample also references `GraphBridge.Tooling`, which pins the compiler to 5.9.0, so it aligns `Microsoft.CodeAnalysis.CSharp.Workspaces` and `Microsoft.CodeAnalysis.Workspaces.MSBuild` to 5.9.0 as private references; otherwise restore selects 5.0.0 workspaces that are incompatible with that compiler. These are design-time dependencies of the sample, not provider runtime dependencies. Inspect your own resolved dependency graph before copying these overrides.

## Limits

- No command initializes, applies, repairs or drops anything.
- `status` checks one plan's readiness at one moment; it is not a lock.
- The commands live in your application. There is no `dotnet ef` plugin and no generic assembly loader.

## Try it

- Workshop lesson [29 · Design-time migration commands](../../workshop/GraphBridge.Workshop/Lessons/29-DesignTime/README.md) hosts the commands over the plan from lesson 23.
- The [design-time sample](../../samples/04-DesignTime/README.md) runs the real `dotnet ef dbcontext list/info` commands, then `list`, `preview initial` and live `status initial`. Live status reads `GRAPHBRIDGE_URI`, `GRAPHBRIDGE_DATABASE`, `GRAPHBRIDGE_USERNAME` and `GRAPHBRIDGE_PASSWORD`; an untouched database exits `3`.

Design decision: [ADR-0036](../architecture/adr/ADR-0036.md). Evidence: [capability evidence, schema and migrations row](../../tests/verification/CAPABILITY_EVIDENCE.md).
