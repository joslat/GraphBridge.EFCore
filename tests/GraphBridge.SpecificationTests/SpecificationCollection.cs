using Microsoft.EntityFrameworkCore.TestUtilities.Xunit;

namespace GraphBridge.SpecificationTests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SpecificationCollection
{
    public const string Name = "GraphBridge upstream specifications";
}

/// <summary>Opt-in only; when enabled, fixture or provider failures remain test failures.</summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class RequiresDisposableNeo4jAttribute : Attribute, ITestCondition
{
    public static bool Enabled => Environment.GetEnvironmentVariable("GRAPHBRIDGE_INTEGRATION") == "1";
    public ValueTask<bool> IsMetAsync() => ValueTask.FromResult(Enabled);
    public string SkipReason => "Requires the repository's marker-checked disposable Neo4j fixture (GRAPHBRIDGE_INTEGRATION=1).";
}
