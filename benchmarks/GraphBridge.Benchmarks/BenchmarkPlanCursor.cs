using Neo4j.Driver;

namespace GraphBridge.Benchmarks;

/// <summary>Drains a plan result before requesting its summary, without retaining records.</summary>
public static class BenchmarkPlanCursor
{
    public static async Task<IResultSummary> ConsumeAsync(
        IResultCursor cursor, int expectedRows, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedRows);
        token.ThrowIfCancellationRequested();

        var rows = 0;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var available = await cursor.FetchAsync().ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (!available) break;
            if (rows == expectedRows)
                throw new InvalidOperationException($"Plan result exceeded the expected {expectedRows} rows.");
            rows++;
        }

        if (rows != expectedRows)
            throw new InvalidOperationException($"Plan result returned {rows} rows; expected {expectedRows}.");
        token.ThrowIfCancellationRequested();
        // Direct ConsumeAsync can discard unread rows and omit the complete PROFILE statistics.
        return await cursor.ConsumeAsync().ConfigureAwait(false);
    }
}
