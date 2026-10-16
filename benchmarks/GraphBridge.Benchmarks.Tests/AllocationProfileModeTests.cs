using GraphBridge.Benchmarks;

namespace GraphBridge.Benchmarks.Tests;

public sealed class AllocationProfileModeTests
{
    [Theory]
    [InlineData("driver")]
    [InlineData("ef")]
    public void Extracts_explicit_backend_without_changing_bounded_benchmark_options(string backend)
    {
        string[] arguments = ["--sizes=100000", "--allocation-backend=" + backend, "--samples=2"];
        Assert.Equal(backend, AllocationProfileMode.Extract(ref arguments));
        var options = BenchmarkOptions.Parse(arguments);
        Assert.Equal(new[] { 100000 }, options.GraphSizes);
        Assert.Equal(2, options.Samples);
    }

    [Fact]
    public void Ordinary_matrix_is_unchanged_and_invalid_or_duplicated_backend_rejects()
    {
        string[] ordinary = ["--samples=3"];
        Assert.Null(AllocationProfileMode.Extract(ref ordinary));
        Assert.Equal(new[] { "--samples=3" }, ordinary);
        foreach (var invalid in new[] { new[] { "--allocation-backend=arbitrary" }, new[] { "--allocation-backend=ef", "--allocation-backend=driver" } })
        {
            var args = invalid;
            Assert.Throws<ArgumentException>(() => AllocationProfileMode.Extract(ref args));
        }
    }
}
