# 29 · Design-time migration commands

**Goal:** give your application list, preview and status commands for its migration plans, with machine-readable output.
**Track:** full · **Mode:** offline + Neo4j · **Time:** ~10 min · **Builds on:** 23

## Concepts

- `GraphMigrationCommands.RunAsync` hosts `list`, `preview` and `status` commands inside your own application, over your own plan catalog.
- `list` and `preview` never touch the database. `status` reads the migration journal, read-only.
- Output is JSON and exit codes are stable (0 ready, 2 usage or error, 3 needs attention), so pipelines and reviewers can rely on them.

## Run it

```powershell
dotnet run --project workshop/GraphBridge.Workshop -- run 29
```

## Walkthrough

1. **Host the commands.**

<!-- region: Commands -->
```csharp
IReadOnlyList<GraphMigrationPlan> catalog = [Lesson23MigrationPlans.Plan];
async Task<(int ExitCode, string Json)> Run(params string[] args)
{
    var output = new StringWriter();
    var errors = new StringWriter();
    var exitCode = await GraphMigrationCommands.RunAsync(args, catalog, () => lesson.CreateContext(), output, errors, cancellationToken);
    if (errors.ToString().Trim() is { Length: > 0 } message) lesson.Note($"{args[0]} reported: {message}");
    return (exitCode, output.ToString());
}
```

2. **list and preview** run offline and print JSON that includes the plan id and checksum.
3. **status** reads the journal. After lesson 23 it reports the plan as applied (exit 0); on a database where the journal was never initialized it exits 3.

## What you should see

Two exit codes of 0 offline, JSON containing the plan's checksum, and a status exit code of 0 or 3.

## Try it yourself

1. Run `preview` with `--include-values`. What extra information appears, and why is it off by default?
2. Read [samples/04-DesignTime](../../../../samples/04-DesignTime/README.md) to see the same commands wired to `dotnet ef dbcontext info`.

## Limits

- These commands never initialize or apply a plan; applying is an explicit deployment step.
- Relational `dotnet ef migrations` commands are not supported.

## Read more

[Design-time commands](../../../../docs/tools/DESIGN_TIME.md)
