using System.Text.Json;
using GraphBridge.EntityFrameworkCore.Migrations;
using GraphBridge.Samples.MoviesDemo;
using Microsoft.EntityFrameworkCore;
using Neo4j.Driver;

var modes = new[] { "--seed-fixture", "--prepare-schema", "--browse", "--run", "--backfill-preview",
    "--backfill-initialize", "--backfill-prepare", "--backfill-seed", "--backfill-validate", "--backfill-backup", "--backfill-apply" };
if (args.Length == 0 || !modes.Contains(args[0], StringComparer.Ordinal)) return Usage();
var mode = args[0];
var confirmed = false;
string? predecessor = null;
string? backupPath = null;
for (var index = 1; index < args.Length; index++)
{
    if (args[index] == "--confirm" && !confirmed) confirmed = true;
    else if (args[index] == "--predecessor" && predecessor is null && index + 1 < args.Length)
        predecessor = args[++index];
    else if (args[index] == "--backup" && backupPath is null && index + 1 < args.Length)
        backupPath = args[++index];
    else return Usage();
}
if (predecessor is not null && mode is not ("--backfill-preview" or "--backfill-prepare" or "--backfill-seed")) return Usage();
if (mode is "--backfill-backup" or "--backfill-apply")
{
    if (string.IsNullOrWhiteSpace(backupPath)) return Usage();
}
else if (backupPath is not null) return Usage();
var requiresConfirmation = mode is "--backfill-initialize" or "--backfill-prepare" or "--backfill-seed" or "--backfill-apply";
if (confirmed != requiresConfirmation)
{
    Console.Error.WriteLine(requiresConfirmation
        ? "Run --backfill-preview first, then add --confirm for the selected explicit legacy sample mutation."
        : "--confirm is reserved for the separate backfill mutation commands.");
    return 2;
}

if (mode == "--backfill-preview")
{
    Console.WriteLine("Explicit optional identity backfill. Scaffolding never invokes this workflow.");
    Console.WriteLine("Initialize journal, apply legacy schema, seed one legacy row, validate, preserve its scoped backup, then apply the data plan as separate reviewed operations.");
    Console.WriteLine("--backfill-backup --backup PATH preserves the one-row preimage outside Neo4j without overwriting files. Apply requires that same artifact; no automatic restore is provided.");
    foreach (var command in GraphMigrationJournal.PreviewInitialization()) Console.WriteLine(command + ";");
    foreach (var plan in new[] { MoviesBackfill.SchemaPlan(predecessor), MoviesBackfill.Plan() })
    {
        Console.WriteLine($"Plan {plan.Id}; checksum {plan.Checksum}; predecessor {plan.PredecessorId ?? "none"}.");
        Console.WriteLine(plan.RollbackPolicy.Reason);
        foreach (var step in plan.Preview().Steps)
        {
            Console.WriteLine($"{step.Id}: {step.Precondition}");
            Console.WriteLine(step.Command.Cypher);
            Console.WriteLine(JsonSerializer.Serialize(step.Command.Parameters));
        }
    }
    Console.WriteLine($"Dedicated seed: label={MoviesBackfill.Label}; legacyCatalogKey={MoviesBackfill.LegacyKey}; title={MoviesBackfill.Title}; no initial appId.");
    Console.WriteLine("Existing journal history is preserved. Supply --predecessor only after explicitly reviewing its exact head; no head is inferred or adopted.");
    return 0;
}

static string Required(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
    ? value : throw new InvalidOperationException($"Missing {name}; load the owned disposable fixture environment.");
var uri = Required("GRAPHBRIDGE_NEO4J_URI");
var database = Required("GRAPHBRIDGE_NEO4J_DATABASE");
var fixtureId = Required("GRAPHBRIDGE_FIXTURE_ID");
if (uri != "bolt://127.0.0.1:17687" || database != "neo4j" || !Guid.TryParseExact(fixtureId, "N", out _))
    throw new InvalidOperationException("This sample is restricted to the repository's owned disposable fixture.");
await using var driver = GraphDatabase.Driver(uri,
    AuthTokens.Basic(Required("GRAPHBRIDGE_NEO4J_USERNAME"), Required("GRAPHBRIDGE_NEO4J_PASSWORD")),
    configure => configure.WithConnectionTimeout(TimeSpan.FromSeconds(10)));
await MoviesFixture.VerifyFixtureAsync(driver, database, fixtureId);

switch (mode)
{
    case "--seed-fixture":
        await MoviesFixture.SeedAsync(driver, database, fixtureId, Path.Combine(AppContext.BaseDirectory, "source"));
        Console.WriteLine("Original pinned Movies fixture created. Existing sample data is never overwritten or cleared.");
        break;
    case "--prepare-schema":
        await MoviesFixture.PrepareSchemaAsync(driver, database, fixtureId);
        Console.WriteLine("Reviewed Movies application identity constraints prepared. No scaffolding operation applies schema automatically.");
        break;
    case "--browse":
        foreach (var relationship in await MoviesWorkflow.BrowseAsync(driver, database))
            Console.WriteLine(JsonSerializer.Serialize(relationship));
        Console.WriteLine("Native DTO results remain untracked and preserve roles arrays; no stable application identity is required for this browsing lane.");
        break;
    case "--run":
    {
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        var result = await MoviesWorkflow.RunAsync(driver, database, cancellation.Token);
        Console.WriteLine(result.Cypher);
        Console.WriteLine($"Storage fingerprints: reviewed scaffold={result.SourceFingerprint}; finalized runtime={result.TargetFingerprint}.");
        Console.WriteLine($"Generated EF movies={result.MoviesRead}; parallel acting credits={result.ParallelActingCredits}; edited entries={result.UpdatedEntries}.");
        Console.WriteLine($"Reference fixup={result.ReferenceFixup}; identity resolution={result.IdentityResolution}; roles preserved={result.RolesPreserved}; rollback verified={result.RollbackVerified}.");
        Console.WriteLine("The demonstration update was rolled back; the sample dataset remains intact.");
        break;
    }
    case "--backfill-initialize":
        await MoviesBackfill.InitializeAsync(driver, database, confirmed);
        Console.WriteLine("Migration journal initialized/verified explicitly; existing history retained.");
        break;
    case "--backfill-prepare":
        WriteMigration(await MoviesBackfill.PrepareSchemaAsync(driver, database, confirmed, predecessor));
        break;
    case "--backfill-seed":
        var seeded = await MoviesBackfill.SeedLegacyAsync(driver, database, confirmed, predecessor);
        Console.WriteLine($"Dedicated legacy row created={seeded}; selector={MoviesBackfill.LegacyKey}. No application identity is guessed from a title or elementId.");
        break;
    case "--backfill-validate":
        var validation = await MoviesBackfill.ValidateAsync(driver, database);
        Console.WriteLine($"Read-only backfill validation: can apply={validation.CanApply}; already applied={validation.AlreadyApplied}.");
        break;
    case "--backfill-apply":
        WriteMigration(await MoviesBackfill.ApplyAsync(driver, database, confirmed, backupPath!));
        Console.WriteLine($"Retained reviewed legacy row appId={MoviesBackfill.ApplicationId:D}. Replay checks both journal history and current data; no automatic recovery.");
        break;
    case "--backfill-backup":
        await MoviesBackfillBackup.SaveAsync(driver, database, fixtureId, backupPath!);
        Console.WriteLine("Scoped legacy-row backup preserved. Keep it outside the disposable fixture; it is not a full database backup or an automatic restore plan.");
        break;
}
return 0;

static void WriteMigration(GraphMigrationResult result)
    => Console.WriteLine($"Migration {result.MigrationId}; steps={result.CompletedSteps}; already applied={result.AlreadyApplied}.");

static int Usage()
{
    Console.Error.WriteLine("Choose --seed-fixture, --prepare-schema, --browse, --run, --backfill-preview [--predecessor ID], --backfill-initialize --confirm, --backfill-prepare --confirm [--predecessor ID], --backfill-seed --confirm [--predecessor ID], --backfill-validate, --backfill-backup --backup PATH, or --backfill-apply --backup PATH --confirm.");
    return 2;
}
