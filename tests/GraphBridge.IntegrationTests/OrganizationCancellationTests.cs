using System.Reflection;
using GraphBridge.Samples.OrganizationDemo;
using Neo4j.Driver;

namespace GraphBridge.IntegrationTests;

/// <summary>Offline entry-point checks complement the live no-write/journal-retention cases.</summary>
public sealed class OrganizationCancellationTests
{
    [Fact]
    public async Task Every_pre_cancelled_async_entrypoint_preserves_the_token_without_contacting_the_driver()
    {
        var driver = DispatchProxy.Create<IDriver, NoOrganizationTransport>();
        var token = new CancellationToken(canceled: true);
        Func<Task>[] operations =
        [
            () => OrganizationWorkflow.RunAsync(driver, "neo4j", token),
            () => OrganizationWorkflow.PrepareSchemaAsync(driver, "neo4j", token),
            () => OrganizationWorkflow.SeedMigrationDemoAsync(driver, "neo4j", token),
            () => OrganizationWorkflow.ApplyVersionTwoAsync(driver, "neo4j", token),
            () => OrganizationWorkflow.RunMigrationDemoAsync(driver, "neo4j", token),
            () => OrganizationWorkflow.ReadVersionTwoAsync(driver, "neo4j", token)
        ];
        foreach (var operation in operations)
        {
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(operation);
            Assert.Equal(token, error.CancellationToken);
        }
        Assert.Equal(0, ((NoOrganizationTransport)(object)driver).Calls);
    }

    [Fact]
    public void Both_generated_mapping_versions_match_their_compiler_fingerprints_without_transport()
    {
        var driver = DispatchProxy.Create<IDriver, NoOrganizationTransport>();
        var v1 = OrganizationWorkflow.GetSchemaFingerprints(driver, "neo4j");
        var v2 = OrganizationWorkflow.GetSchemaFingerprints(driver, "neo4j", versionTwo: true);
        Assert.Equal(v1.Source, v1.Target);
        Assert.Equal(v2.Source, v2.Target);
        Assert.NotEqual(v1.Target, v2.Target);
        Assert.Equal(0, ((NoOrganizationTransport)(object)driver).Calls);
    }
}

public class NoOrganizationTransport : DispatchProxy
{
    public int Calls { get; private set; }
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        Calls++;
        throw new InvalidOperationException("Pre-cancelled or metadata-only Organization workflow contacted the driver.");
    }
}
