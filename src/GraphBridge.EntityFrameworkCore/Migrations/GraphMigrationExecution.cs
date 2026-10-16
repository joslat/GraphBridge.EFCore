namespace GraphBridge.EntityFrameworkCore.Migrations;

/// <summary>Finite bounds for explicit migration operations and schema-readiness polling; no automatic write retry or lease expiry is configured.</summary>
public sealed class GraphMigrationOptions
{
    public GraphMigrationOptions(TimeSpan? transactionTimeout = null, TimeSpan? indexWaitTimeout = null,
        int maxSchemaRows = 10_000)
    {
        TransactionTimeout = transactionTimeout ?? TimeSpan.FromMinutes(2);
        IndexWaitTimeout = indexWaitTimeout ?? TimeSpan.FromSeconds(30);
        if (TransactionTimeout < TimeSpan.FromMilliseconds(1) || TransactionTimeout > TimeSpan.FromHours(24))
            throw new ArgumentOutOfRangeException(nameof(transactionTimeout), "Transaction timeout must be between one millisecond and 24 hours.");
        if (IndexWaitTimeout < TimeSpan.FromMilliseconds(1) || IndexWaitTimeout > TimeSpan.FromHours(24))
            throw new ArgumentOutOfRangeException(nameof(indexWaitTimeout), "Index wait timeout must be between one millisecond and 24 hours.");
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxSchemaRows);
        if (maxSchemaRows > 1_000_000) throw new ArgumentOutOfRangeException(nameof(maxSchemaRows));
        MaxSchemaRows = maxSchemaRows;
    }
    public TimeSpan TransactionTimeout { get; }
    public TimeSpan IndexWaitTimeout { get; }
    public int MaxSchemaRows { get; }
}

/// <summary>An operator declaration, not automatic proof that a former process is fenced.</summary>
public sealed class GraphMigrationRecovery
{
    public GraphMigrationRecovery(string expectedOwnerToken, long expectedGeneration, bool priorOwnerQuiesced, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedOwnerToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedGeneration);
        if (!priorOwnerQuiesced)
            throw new ArgumentException("Recovery requires confirmation that the prior process is stopped/fenced and outstanding transactions are resolved.", nameof(priorOwnerQuiesced));
        if (reason.Length > 2048) throw new ArgumentOutOfRangeException(nameof(reason));
        ExpectedOwnerToken = expectedOwnerToken;
        ExpectedGeneration = expectedGeneration;
        PriorOwnerQuiesced = priorOwnerQuiesced;
        Reason = reason;
    }
    public string ExpectedOwnerToken { get; }
    public long ExpectedGeneration { get; }
    public bool PriorOwnerQuiesced { get; }
    public string Reason { get; }
}

public sealed class GraphMigrationValidation
{
    internal GraphMigrationValidation(bool initialized, IEnumerable<string> issues, string? ownerToken,
        long ownerGeneration, string? migrationStatus, IEnumerable<string> completedStepIds, bool alreadyApplied)
    {
        IsInitialized = initialized;
        Issues = Array.AsReadOnly(issues.ToArray());
        OwnerToken = ownerToken;
        OwnerGeneration = ownerGeneration;
        MigrationStatus = migrationStatus;
        CompletedStepIds = Array.AsReadOnly(completedStepIds.ToArray());
        AlreadyApplied = alreadyApplied;
    }
    public bool IsInitialized { get; }
    public bool CanApply => IsInitialized && Issues.Count == 0 && (OwnerToken is null || AlreadyApplied);
    public IReadOnlyList<string> Issues { get; }
    public string? OwnerToken { get; }
    public long OwnerGeneration { get; }
    public string? MigrationStatus { get; }
    public IReadOnlyList<string> CompletedStepIds { get; }
    public bool AlreadyApplied { get; }
}

public sealed record GraphMigrationResult(string MigrationId, string Checksum, bool AlreadyApplied, int CompletedSteps);

internal enum GraphMigrationCheckpointStage { OwnerAcquired, StepPrepared, SchemaCommitted, DataExecuted, DataCommitted, BeforeOwnerReleased }
internal sealed record GraphMigrationCheckpoint(GraphMigrationCheckpointStage Stage, string MigrationId,
    string? StepId, string OwnerToken, long Generation);
