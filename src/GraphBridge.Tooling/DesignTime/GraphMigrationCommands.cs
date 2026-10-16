using System.Text.Json;
using System.Text.Json.Serialization;
using GraphBridge.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore;

namespace GraphBridge.Tooling.DesignTime;

/// <summary>Optional application-hosted graph migration inspection. No command initializes or changes a database.</summary>
public static class GraphMigrationCommands
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true,
        Converters = { new JsonStringEnumConverter() } };

    /// <summary>Runs list, preview ID [--include-values], or status ID. Only status creates/disposes a context.</summary>
    /// <remarks>
    /// The application supplies a trusted declared catalog and owns factory dependencies such as IDriver.
    /// Status performs read-only validation, not a complete history listing or a reservation to apply.
    /// Returns 0 for success, 2 for usage/operation errors, 3 for migration readiness issues and 130 for cancellation.
    /// Preview contains schema identifiers; parameter values require explicit --include-values.
    /// </remarks>
    public static async Task<int> RunAsync(string[] args, IReadOnlyList<GraphMigrationPlan> catalog,
        Func<DbContext> createContext, TextWriter output, TextWriter error, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(createContext);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (args is ["help"])
            {
                await output.WriteLineAsync("Graph migrations: list | preview ID [--include-values] | status ID. No apply, init, drop or automatic model diff.");
                return 0;
            }
            if (!(args is ["list"] or ["preview", _] or ["preview", _, "--include-values"] or ["status", _]))
            {
                await error.WriteLineAsync("Unsupported graph migration command. Use list, preview ID [--include-values], or status ID.");
                return 2;
            }
            // Snapshot a finite application-owned catalog once; never resolve IDs from external assemblies.
            if (catalog.Count > 256) throw new ArgumentException("A design-time catalog supports at most 256 plans.");
            var plans = catalog.ToArray();
            if (plans.Any(p => p is null) || plans.Select(p => p.Id).Distinct(StringComparer.Ordinal).Count() != plans.Length)
            {
                await error.WriteLineAsync("Migration catalog contains null plans or duplicate IDs.");
                return 2;
            }
            if (args is ["list"])
            {
                await Write(new { formatVersion = 1, kind = "declared-catalog", databaseObserved = false,
                    migrations = plans.Select(p => new { p.Id, p.PredecessorId, p.Checksum, stepCount = p.Steps.Count }) });
                return 0;
            }
            var plan = plans.SingleOrDefault(p => string.Equals(p.Id, args[1], StringComparison.Ordinal));
            if (plan is null)
            {
                await error.WriteLineAsync("Migration ID is not present in the declared catalog.");
                return 2;
            }
            if (args[0] == "preview")
            {
                var preview = plan.Preview();
                var includeValues = args.Length == 3;
                object Command(GraphMigrationCommand command) => new { command.Cypher,
                    parameterNames = command.Parameters.Keys.ToArray(),
                    valuesIncluded = includeValues,
                    parameters = includeValues ? command.Parameters : null };
                await Write(new { formatVersion = 1, kind = "declared-plan-preview", databaseObserved = false,
                    preview.Id, preview.PredecessorId, preview.Checksum, preview.RollbackPolicy,
                    steps = preview.Steps.Select(s => new { s.Id, s.Kind, s.Impact, s.Precondition, s.Postcondition,
                        s.RequiredCapabilities, s.DestructiveReason, command = Command(s.Command),
                        preconditionCommand = s.PreconditionCommand is null ? null : Command(s.PreconditionCommand) }) });
                return 0;
            }
            cancellationToken.ThrowIfCancellationRequested();
            await using var context = createContext() ?? throw new InvalidOperationException("Context factory returned null.");
            var validation = await context.Database.ValidateGraphMigrationAsync(plan, cancellationToken: cancellationToken).ConfigureAwait(false);
            await Write(new { formatVersion = 1, kind = "read-only-migration-status", databaseObserved = true,
                plan.Id, plan.Checksum, validation.IsInitialized, validation.AlreadyApplied, validation.CanApply,
                validation.MigrationStatus, validation.CompletedStepIds, validation.Issues,
                hasOwner = validation.OwnerToken is not null });
            return validation.CanApply ? 0 : 3;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await error.WriteLineAsync("Graph migration inspection canceled.");
            return 130;
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            // Driver/user-factory exception messages may contain credentials or query values.
            await error.WriteLineAsync($"Graph migration inspection failed ({failure.GetType().Name}). Check the catalog, connection, database and metadata permissions.");
            return 2;
        }

        async Task Write(object value)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await output.WriteLineAsync(JsonSerializer.Serialize(value, Json)).ConfigureAwait(false);
        }
    }
}
