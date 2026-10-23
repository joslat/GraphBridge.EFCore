using Neo4j.Driver;

namespace GraphBridge.EntityFrameworkCore.Transport;

/// <summary>Command timeouts are driver transaction timeouts (ADR 0050); bounds match migration transactions.</summary>
internal static class GraphCommandTimeout
{
    internal static TimeSpan? Validate(TimeSpan? timeout, string parameterName)
        => timeout is { } value && (value < TimeSpan.FromMilliseconds(1) || value > TimeSpan.FromHours(24))
            ? throw new ArgumentOutOfRangeException(parameterName, "A command timeout must be between one millisecond and 24 hours, or null for the server default.")
            : timeout;

    /// <summary>Transaction settings for a timeout; null leaves the server's default in place.</summary>
    internal static Action<TransactionConfigBuilder>? Configure(TimeSpan? timeout)
        => timeout is { } value ? configuration => configuration.WithTimeout(value) : null;
}
