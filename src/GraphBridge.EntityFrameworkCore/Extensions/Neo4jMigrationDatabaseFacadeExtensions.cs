using GraphBridge.EntityFrameworkCore.Infrastructure;
using GraphBridge.EntityFrameworkCore.Migrations;
using GraphBridge.EntityFrameworkCore.Transport;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace Microsoft.EntityFrameworkCore;

/// <summary>Explicit graph migration operations; never invoked by provider startup or ordinary queries/saves.</summary>
public static class Neo4jMigrationDatabaseFacadeExtensions
{
    /// <summary>Explicitly initializes and verifies the migration journal schema and control record.</summary>
    /// <remarks>Review GraphMigrationJournal.PreviewInitialization before invoking this schema/data-writing operation.</remarks>
    public static Task InitializeGraphMigrationsAsync(this DatabaseFacade database,
        GraphMigrationOptions? options = null, CancellationToken cancellationToken = default)
        => ExecuteAsync(database, async (executor, token) =>
        {
            await executor.InitializeAsync(token).ConfigureAwait(false);
            return true;
        }, options, cancellationToken);

    /// <summary>Reads capabilities and migration/schema state without initializing or modifying the journal.</summary>
    public static Task<GraphMigrationValidation> ValidateGraphMigrationAsync(this DatabaseFacade database,
        GraphMigrationPlan plan, GraphMigrationOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return ExecuteAsync(database, (executor, token) => executor.ValidateAsync(plan, token), options, cancellationToken);
    }

    /// <summary>Applies an explicitly reviewed plan using durable single-writer ownership and per-step transactions.</summary>
    /// <remarks>
    /// The complete plan is not atomic. Data changes and their completion record share a transaction; schema changes
    /// use separate prepared/executed/verified transitions. Failure or uncertain commit may retain ownership and
    /// require explicit recovery. There is no automatic retry, rollback compensation, lease expiry or tracker refresh.
    /// Existing EF and ambient transactions are rejected. Use a fresh context for entity data after migrations.
    /// </remarks>
    public static Task<GraphMigrationResult> ApplyGraphMigrationAsync(this DatabaseFacade database,
        GraphMigrationPlan plan, GraphMigrationOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return ExecuteAsync(database, (executor, token) => executor.ApplyAsync(plan, token), options, cancellationToken);
    }

    /// <summary>Reconciles an interrupted plan after explicit confirmation that the previous owner is quiescent.</summary>
    /// <remarks>
    /// The caller must stop/fence the old process and resolve its in-flight transactions before confirming quiescence.
    /// A stale timestamp or token alone cannot prevent a paused process from issuing later DDL. Recovery checks the
    /// expected owner/generation and exact journal/schema state; it does not silently steal an active lease.
    /// </remarks>
    public static Task<GraphMigrationResult> RecoverGraphMigrationAsync(this DatabaseFacade database,
        GraphMigrationPlan plan, GraphMigrationRecovery recovery, GraphMigrationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(recovery);
        return ExecuteAsync(database, (executor, token) => executor.RecoverAsync(plan, recovery, token), options, cancellationToken);
    }

    private static async Task<T> ExecuteAsync<T>(DatabaseFacade database,
        Func<GraphMigrationExecutor, CancellationToken, Task<T>> execute,
        GraphMigrationOptions? options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        cancellationToken.ThrowIfCancellationRequested();
        var context = ((IDatabaseFacadeDependenciesAccessor)database).Context;
        var providerOptions = context.GetService<IDbContextOptions>().FindExtension<Neo4jDbContextOptionsExtension>()
            ?? throw new InvalidOperationException("Graph migrations require a GraphBridge context configured with UseNeo4j.");
        using var concurrency = context.GetService<IConcurrencyDetector>().EnterCriticalSection();
        var manager = context.GetService<Neo4jTransactionManager>();
        using var operation = manager.EnterNativeOperation();
        if (manager.CurrentDriverTransaction is not null)
            throw new NotSupportedException("Graph migrations require separate step transactions and cannot run inside an active EF transaction.");
        return await execute(new GraphMigrationExecutor(providerOptions, options), cancellationToken).ConfigureAwait(false);
    }
}
