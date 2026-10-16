namespace GraphBridge.Benchmarks.Tests;

public sealed class BenchmarkMetricsTests
{
    [Fact]
    public void Nearest_rank_percentiles_preserve_outliers_and_input_order_does_not_matter()
    {
        var summary = Distribution.From(Enumerable.Range(1, 100).Reverse().Select(x => (double)x));
        Assert.Equal(new Distribution(100, 1, 50, 95, 99, 100), summary);
        Assert.Equal(new Distribution(2, 1, 1, 10000, 10000, 10000), Distribution.From([10000, 1]));
    }

    [Fact]
    public void Empty_nonfinite_or_negative_measurements_are_rejected()
    {
        foreach (var values in new double[][] { [], [double.NaN], [double.PositiveInfinity], [-1] })
            Assert.Throws<ArgumentException>(() => Distribution.From(values));
    }

    [Fact]
    public void Phase_boundaries_do_not_treat_first_row_latency_as_pure_transport()
    {
        var sample = new BenchmarkSample("read", "first-observed", 0, 100, 30, 20, 1, 0,
            new CommandCounts(1, 101, 1, 0, 0, 0), 100, 4950);
        Assert.Equal(80, sample.NonCompilationMilliseconds);
        Assert.Equal(70, sample.AfterFirstRowMilliseconds);
        Assert.Equal(101, sample.Commands.FetchAsync); // Deliberately no conversion to network pulls/round trips.
    }

    [Fact]
    public void Default_matrix_retains_every_promised_graph_and_batch_size()
    {
        var options = BenchmarkOptions.Parse([]);
        Assert.Equal([1, 100, 10000, 100000], options.GraphSizes);
        Assert.Equal([1, 100, 10000], options.EdgeSizes);
        Assert.Equal([1, 100, 1000], options.WriteSizes);
    }

    [Theory]
    [InlineData("--sizes=1000000")]
    [InlineData("--sizes=0")]
    [InlineData("--sizes=1,1")]
    [InlineData("--sizes=100,100")]
    [InlineData("--edge-sizes=0")]
    [InlineData("--edge-sizes=100000")]
    [InlineData("--edge-sizes=1,1")]
    [InlineData("--edge-sizes=")]
    [InlineData("--writes=10000")]
    [InlineData("--samples=1")]
    [InlineData("--samples=201")]
    [InlineData("--warmups=21")]
    [InlineData("--fetch-size=0")]
    [InlineData("--unknown=1")]
    public void Unbounded_or_unknown_configuration_is_rejected(string argument)
        => Assert.ThrowsAny<ArgumentException>(() => BenchmarkOptions.Parse([argument]));

    [Fact]
    public void Small_smoke_matrix_is_explicit_and_does_not_rewrite_defaults()
    {
        var options = BenchmarkOptions.Parse(["--sizes=100", "--edge-sizes=1,100", "--writes=1", "--samples=2", "--warmups=0"]);
        Assert.Equal([100], options.GraphSizes);
        Assert.Equal([1, 100], options.EdgeSizes);
        Assert.Equal([1], options.WriteSizes);
        Assert.Equal(2, options.Samples);
        Assert.Equal(0, options.Warmups);
        Assert.Throws<ArgumentException>(() => BenchmarkOptions.Parse(["--samples=2", "--samples=3"]));
    }

    [Fact]
    public void Single_node_graph_is_supported_with_every_bounded_write_batch()
    {
        var options = BenchmarkOptions.Parse(["--sizes=1", "--writes=1,100,1000"]);
        Assert.Equal([1], options.GraphSizes);
        Assert.Equal([1, 100, 1000], options.WriteSizes);
    }

    [Fact]
    public void Node_and_edge_size_selection_remains_independent()
    {
        var options = BenchmarkOptions.Parse(["--sizes=100000", "--edge-sizes=1"]);
        Assert.Equal([100000], options.GraphSizes);
        Assert.Equal([1], options.EdgeSizes);
        Assert.Equal([1, 100, 10000], BenchmarkOptions.Parse([]).EdgeSizes);
    }
}
