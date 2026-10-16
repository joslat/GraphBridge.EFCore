using GraphBridge.Benchmarks;

namespace GraphBridge.Benchmarks.Tests;

public sealed class BulkProfileModeTests
{
    [Fact]
    public void Bulk_mode_is_explicit_and_does_not_change_the_selected_batch_matrix()
    {
        string[] args = ["--bulk", "--sizes=1", "--edge-sizes=1", "--writes=1,100", "--samples=2"];
        Assert.True(BulkProfileMode.Extract(ref args));
        Assert.Equal(new[] { 1, 100 }, BenchmarkOptions.Parse(args).WriteSizes);
        Assert.False(BulkProfileMode.Extract(ref args));
        string[] duplicate = ["--bulk", "--bulk"];
        Assert.Throws<ArgumentException>(() => BulkProfileMode.Extract(ref duplicate));
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("missing-sample")]
    [InlineData("duplicate-iteration")]
    [InlineData("wrong-phase")]
    [InlineData("warm-compilation")]
    [InlineData("missing-metadata-call")]
    [InlineData("rollback")]
    [InlineData("wrong-result")]
    [InlineData("invalid-protocol")]
    [InlineData("missing-protocol")]
    public void Acceptance_rejects_incomplete_or_inconsistent_measurement_evidence(string mutation)
    {
        var options = new BenchmarkOptions([1], [1], [100], 2, 1, 1000, 1729, "unused");
        var samples = new List<BenchmarkSample>();
        var protocols = new List<BenchmarkProtocolSample>();
        foreach (var operation in BulkProfileMode.Operations)
        foreach (var backend in BulkProfileMode.Backends)
        {
            var name = BulkProfileMode.Case(100, operation, backend);
            for (var i = -2; i < 2; i++) samples.Add(new(name, i == -2 ? "first-observed" : i == -1 ? "warmup" : "warm",
                i, 1, null, 0, 0, 100, new(backend == "tracked" ? 202 : backend == "bulk" ? 4 : 2, 5, 0, 1, 1, 0), 100, 0));
            // Synthetic fixture for the acceptance validator, not live protocol evidence.
            protocols.Add(new(name, new(true, null, 1, 1, 1, 1, 0, 0, 100, new Dictionary<string, long>())));
        }
        switch (mutation)
        {
            case "missing-sample": samples.RemoveAt(0); break;
            case "duplicate-iteration": samples[1] = samples[0]; break;
            case "wrong-phase": samples[0] = samples[0] with { Phase = "warm" }; break;
            case "warm-compilation": samples[2] = samples[2] with { CompilationCount = 1 }; break;
            case "missing-metadata-call": samples[0] = samples[0] with { Commands = samples[0].Commands with { RunAsync = 201 } }; break;
            case "rollback": samples[0] = samples[0] with { Commands = samples[0].Commands with { Rollback = 1 } }; break;
            case "wrong-result": samples[0] = samples[0] with { ResultCount = 99 }; break;
            case "invalid-protocol": protocols[0] = protocols[0] with { Measurement = protocols[0].Measurement with { IsValid = false } }; break;
            case "missing-protocol": protocols.RemoveAt(0); break;
        }
        if (mutation == "valid") BulkProfileMode.Validate(options, samples, protocols);
        else Assert.Throws<InvalidOperationException>(() => BulkProfileMode.Validate(options, samples, protocols));
    }
}
