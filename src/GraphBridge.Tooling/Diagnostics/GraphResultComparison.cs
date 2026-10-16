using System.Globalization;
using System.Text;

namespace GraphBridge.Tooling.Diagnostics;

public enum GraphResultDifference { None, RowCount, Values, Order, DuplicateRows, InvalidExpectedShape, InvalidActualShape, BudgetExceeded }

/// <summary>Sanitized comparison of caller-supplied rows; contains no values and makes no database/provenance claim.</summary>
public sealed record GraphResultComparisonReport
{
    internal GraphResultComparisonReport(GraphEvidenceStatus status, GraphResultDifference difference, int? expectedRows, int? actualRows, int? firstDifferentRow = null)
        => (Status, Difference, ExpectedRows, ActualRows, FirstDifferentRow) = (status, difference, expectedRows, actualRows, firstDifferentRow);
    public GraphEvidenceStatus Status { get; }
    public GraphResultDifference Difference { get; }
    public int? ExpectedRows { get; }
    public int? ActualRows { get; }
    public int? FirstDifferentRow { get; }
}

/// <summary>Bounded scalar-row comparison for independent query controls. Does not execute either query.</summary>
public static class GraphResultComparer
{
    // Bound comparison working data as well as rows; buffers/allocations made by the caller are outside this bound.
    private const int MaxStringCharacters = 4096;
    private const int MaxNormalizedCharacters = 1_048_576;

    /// <summary>Preserves nulls and multiplicity; honors ordered versus multiset comparison and declared scalar shapes.</summary>
    /// <remarks>Rows use declared column order. Integers normalize to signed 64-bit; finite float/double values compare exactly
    /// after conversion to double, without tolerance. Decimal, non-finite and opaque values fail shape validation. Strings use
    /// ordinal equality. No arbitrary value ToString/serialization occurs. Inputs are trusted application enumerables, not sandboxed.
    /// A comparison pass alone does not prove the inputs came from the intended query, oracle or owned fixture.</remarks>
    public static GraphResultComparisonReport Compare(GraphQueryDefinition definition,
        IEnumerable<IReadOnlyList<object?>> expected, IEnumerable<IReadOnlyList<object?>> actual)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);
        var remaining = MaxNormalizedCharacters;
        var reference = Read(expected, definition, ref remaining);
        if (reference.Error is not null)
            return new(reference.Error == GraphResultDifference.BudgetExceeded ? GraphEvidenceStatus.Blocked : GraphEvidenceStatus.Fail,
                reference.Error.Value, null, null);
        var observed = Read(actual, definition, ref remaining);
        if (observed.Error is not null)
            return new(observed.Error == GraphResultDifference.BudgetExceeded ? GraphEvidenceStatus.Blocked : GraphEvidenceStatus.Fail,
                observed.Error == GraphResultDifference.InvalidExpectedShape ? GraphResultDifference.InvalidActualShape : observed.Error.Value,
                reference.Rows.Count, null);
        var left = reference.Rows;
        var right = observed.Rows;
        var countsLeft = Counts(left);
        var countsRight = Counts(right);
        if (definition.Duplicates == GraphDuplicatePolicy.Eliminate && (countsLeft.Count != left.Count || countsRight.Count != right.Count))
            return new(GraphEvidenceStatus.Fail, GraphResultDifference.DuplicateRows, left.Count, right.Count);
        if (left.Count != right.Count)
            return new(GraphEvidenceStatus.Fail, GraphResultDifference.RowCount, left.Count, right.Count);
        var sameMultiset = countsLeft.Count == countsRight.Count && countsLeft.All(p => countsRight.TryGetValue(p.Key, out var n) && n == p.Value);
        if (definition.Comparison == GraphResultComparison.Multiset)
            return new(sameMultiset ? GraphEvidenceStatus.Pass : GraphEvidenceStatus.Fail,
                sameMultiset ? GraphResultDifference.None : GraphResultDifference.Values, left.Count, right.Count);
        for (var i = 0; i < left.Count; i++)
            if (!StringComparer.Ordinal.Equals(left[i], right[i]))
                return new(GraphEvidenceStatus.Fail, sameMultiset ? GraphResultDifference.Order : GraphResultDifference.Values,
                    left.Count, right.Count, i);
        return new(GraphEvidenceStatus.Pass, GraphResultDifference.None, left.Count, right.Count);
    }

    private static (List<string> Rows, GraphResultDifference? Error) Read(IEnumerable<IReadOnlyList<object?>> source,
        GraphQueryDefinition definition, ref int remaining)
    {
        var rows = new List<string>();
        foreach (var row in source)
        {
            if (rows.Count >= definition.Budget.MaxRows) return (rows, GraphResultDifference.BudgetExceeded);
            if (row is null || row.Count != definition.Columns.Count) return (rows, GraphResultDifference.InvalidExpectedShape);
            var key = new StringBuilder();
            for (var i = 0; i < row.Count; i++)
            {
                var value = row[i];
                if (value is string text && text.Length > MaxStringCharacters) return (rows, GraphResultDifference.BudgetExceeded);
                var cell = Normalize(value, definition.Columns[i]);
                if (cell is null) return (rows, GraphResultDifference.InvalidExpectedShape);
                remaining -= cell.Length + 12; // Conservative allowance for an invariant length prefix.
                if (remaining < 0) return (rows, GraphResultDifference.BudgetExceeded);
                key.Append(cell.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(cell);
            }
            rows.Add(key.ToString());
        }
        return (rows, null);
    }

    private static string? Normalize(object? value, GraphResultColumn column)
    {
        if (value is null) return column.Nullable ? "null" : null;
        return column.Kind switch
        {
            GraphResultKind.String when value is string text => "s" + text,
            GraphResultKind.Boolean when value is bool flag => flag ? "true" : "false",
            GraphResultKind.Integer when value is sbyte or byte or short or ushort or int or uint or long
                => Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
            GraphResultKind.Integer when value is ulong number && number <= long.MaxValue => number.ToString(CultureInfo.InvariantCulture),
            GraphResultKind.FloatingPoint when value is float or double => Floating(Convert.ToDouble(value, CultureInfo.InvariantCulture)),
            _ => null
        };
    }
    private static string? Floating(double number) => !double.IsFinite(number) ? null
        : (number == 0 ? 0d : number).ToString("R", CultureInfo.InvariantCulture);
    private static Dictionary<string, int> Counts(IEnumerable<string> rows)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var row in rows) counts[row] = counts.GetValueOrDefault(row) + 1;
        return counts;
    }
}
