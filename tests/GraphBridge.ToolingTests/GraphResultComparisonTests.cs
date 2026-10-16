using System.Text.Json;
using GraphBridge.Tooling.Diagnostics;

namespace GraphBridge.ToolingTests;

public sealed class GraphResultComparisonTests
{
    [Fact]
    public void Multisets_preserve_duplicates_nulls_and_integer_equivalence()
    {
        var definition = Definition(GraphResultComparison.Multiset);
        var reference = Rows(["Ada", null], ["Bob", 2L], ["Bob", 2L]);
        Assert.Equal(GraphEvidenceStatus.Pass, GraphResultComparer.Compare(definition, reference, Rows(["Bob", 2], ["Ada", null], ["Bob", 2])).Status);
        Assert.Equal(GraphResultDifference.RowCount, GraphResultComparer.Compare(definition, reference, Rows(["Ada", null], ["Bob", 2])).Difference);
        Assert.Equal(GraphResultDifference.Values, GraphResultComparer.Compare(definition, reference, Rows(["Ada", null], ["Ada", null], ["Bob", 2])).Difference);
        Assert.Equal(GraphResultDifference.Values, GraphResultComparer.Compare(definition, reference, Rows(["Ada", 0], ["Bob", 2], ["Bob", 2])).Difference);
    }

    [Fact]
    public void Ordered_comparison_detects_order_separately_and_distinct_contract_never_silently_deduplicates()
    {
        var ordered = Definition(GraphResultComparison.Ordered);
        var reversed = GraphResultComparer.Compare(ordered, Rows(["Ada", 1], ["Bob", 2]), Rows(["Bob", 2], ["Ada", 1]));
        Assert.Equal(GraphEvidenceStatus.Fail, reversed.Status);
        Assert.Equal(GraphResultDifference.Order, reversed.Difference);
        Assert.Equal(0, reversed.FirstDifferentRow);
        var unique = Definition(GraphResultComparison.Multiset, GraphDuplicatePolicy.Eliminate);
        var duplicate = GraphResultComparer.Compare(unique, Rows(["Bob", 2], ["Bob", 2]), Rows(["Bob", 2], ["Bob", 2]));
        Assert.Equal(GraphResultDifference.DuplicateRows, duplicate.Difference);
        Assert.Equal(GraphEvidenceStatus.Fail, duplicate.Status);
    }

    [Fact]
    public void Invalid_shapes_opaque_values_and_nullability_fail_without_serializing_values()
    {
        var definition = Definition(GraphResultComparison.Ordered);
        Assert.Equal(GraphResultDifference.InvalidExpectedShape, GraphResultComparer.Compare(definition, Rows([null, 1]), Rows(["Ada", 1])).Difference);
        Assert.Equal(GraphResultDifference.InvalidActualShape, GraphResultComparer.Compare(definition, Rows(["Ada", 1]), Rows(["Ada"])).Difference);
        var unknown = GraphResultComparer.Compare(definition, Rows(["secret-name", 1]), Rows([new Hostile(), 1]));
        Assert.Equal(GraphResultDifference.InvalidActualShape, unknown.Difference);
        Assert.DoesNotContain("secret-name", JsonSerializer.Serialize(unknown));
        Assert.Equal(GraphEvidenceStatus.Fail, GraphResultComparer.Compare(definition, Rows(["Ada", 1]), Rows(["Ada", ulong.MaxValue])).Status);
    }

    [Fact]
    public void Bounded_comparison_stops_infinite_rows_and_oversized_values_with_nonpassing_evidence()
    {
        var observed = 0;
        IEnumerable<IReadOnlyList<object?>> Infinite()
        {
            while (true) { observed++; yield return new object?[] { "Ada", 1 }; }
        }
        var result = GraphResultComparer.Compare(Definition(GraphResultComparison.Multiset), Infinite(), Rows());
        Assert.Equal(GraphEvidenceStatus.Blocked, result.Status);
        Assert.Equal(GraphResultDifference.BudgetExceeded, result.Difference);
        Assert.Equal(11, observed);
        Assert.Null(result.ExpectedRows);
        var large = GraphResultComparer.Compare(Definition(GraphResultComparison.Multiset), Rows([new string('x', 4097), 1]), Rows());
        Assert.Equal(GraphEvidenceStatus.Blocked, large.Status);
        var many = new GraphQueryDefinition("many.v1", "Synthetic", "test.cs", [new("value", GraphResultKind.String)],
            GraphResultComparison.Multiset, GraphDuplicatePolicy.Preserve, new(1000, TimeSpan.FromSeconds(1)));
        var cells = Enumerable.Repeat<IReadOnlyList<object?>>(new object?[] { new string('x', 4096) }, 300);
        Assert.Equal(GraphEvidenceStatus.Blocked, GraphResultComparer.Compare(many, cells, Rows()).Status);
    }

    [Fact]
    public void Finite_floating_point_zero_and_types_have_explicit_semantics()
    {
        var definition = new GraphQueryDefinition("float.v1", "Synthetic", "test.cs", [new("value", GraphResultKind.FloatingPoint)],
            GraphResultComparison.Ordered, GraphDuplicatePolicy.Preserve, new(10, TimeSpan.FromSeconds(1)));
        Assert.Equal(GraphEvidenceStatus.Pass, GraphResultComparer.Compare(definition, Rows([-0d], [1f]), Rows([0d], [1d])).Status);
        Assert.Equal(GraphEvidenceStatus.Fail, GraphResultComparer.Compare(definition, Rows([double.NaN]), Rows([double.NaN])).Status);
        Assert.Equal(GraphEvidenceStatus.Fail, GraphResultComparer.Compare(definition, Rows([1d]), Rows([1m])).Status);
        Assert.Equal(GraphResultDifference.Values, GraphResultComparer.Compare(definition, Rows([0.1d]), Rows([Math.BitIncrement(0.1d)])).Difference);
    }

    [Fact]
    public void String_delimiters_quotes_and_null_text_cannot_collide_with_other_rows()
    {
        var definition = new GraphQueryDefinition("strings.v1", "Synthetic", "test.cs", [new("a", GraphResultKind.String, true), new("b", GraphResultKind.String, true)],
            GraphResultComparison.Multiset, GraphDuplicatePolicy.Preserve, new(10, TimeSpan.FromSeconds(1)));
        Assert.Equal(GraphResultDifference.Values, GraphResultComparer.Compare(definition, Rows(["a|b", "c"]), Rows(["a", "b|c"])).Difference);
        Assert.Equal(GraphResultDifference.Values, GraphResultComparer.Compare(definition, Rows(["null", null]), Rows([null, "null"])).Difference);
        Assert.Equal(GraphEvidenceStatus.Pass, GraphResultComparer.Compare(definition, Rows(["\"|\n", "value"]), Rows(["\"|\n", "value"])).Status);
        // Preserve ordinal UTF-16 equality; JSON encoders can replace malformed surrogate input.
        Assert.Equal(GraphResultDifference.Values, GraphResultComparer.Compare(definition, Rows(["\ud800", "x"]), Rows(["\ufffd", "x"])).Difference);
    }

    private static GraphQueryDefinition Definition(GraphResultComparison comparison, GraphDuplicatePolicy duplicate = GraphDuplicatePolicy.Preserve)
        => new("rows.v1", "Synthetic", "test.cs", [new("name", GraphResultKind.String), new("value", GraphResultKind.Integer, true)],
            comparison, duplicate, new(10, TimeSpan.FromSeconds(1)));
    private static IReadOnlyList<object?>[] Rows(params object?[][] rows) => rows;
    private sealed class Hostile { public override string ToString() => throw new InvalidOperationException("Must not stringify unknown values."); }
}
