using GraphBridge.EntityFrameworkCore.Cypher;

namespace GraphBridge.EntityFrameworkCore.Query;

internal static class GraphIntegerExtremumReader
{
    internal static object? Read(IReadOnlyDictionary<string, object?> row, Type resultType)
    {
        if (!row.TryGetValue(CypherQueryPlan.AggregateInvalidAlias, out var invalid)
            || invalid is not long count || count != 0)
            throw new InvalidOperationException("GraphBridge Min/Max input contains a missing required value, non-integer value or out-of-range integer; aggregate validation failed.");
        var raw = row["aggregate"];
        if (raw is null)
        {
            if (Nullable.GetUnderlyingType(resultType) is not null) return null;
            throw new InvalidOperationException("Sequence contains no elements.");
        }
        if (raw is not long value)
            throw new InvalidOperationException("GraphBridge Min/Max returned an invalid integer result.");
        // Keep the two branches separate: a numeric conditional expression would
        // promote the boxed int to long and break the compiled int shaper.
        if ((Nullable.GetUnderlyingType(resultType) ?? resultType) == typeof(int)) return checked((int)value);
        return value;
    }
}
