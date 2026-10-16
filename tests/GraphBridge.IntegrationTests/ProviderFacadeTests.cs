using Microsoft.EntityFrameworkCore;

namespace GraphBridge.IntegrationTests;

[Collection(Neo4jIntegrationCollection.Name)]
public sealed class ProviderFacadeTests(DisposableNeo4jFixture fixture)
{
    [Neo4jIntegrationFact]
    public async Task Activation_connects_to_selected_database_and_borrows_driver()
    {
        var options = new DbContextOptionsBuilder().UseNeo4j(fixture.Driver, fixture.Database).Options;
        await using (var context = new DbContext(options))
        {
            Assert.Equal("GraphBridge.EntityFrameworkCore", context.Database.ProviderName);
            Assert.True(await context.Database.CanConnectAsync());
        }

        // Context disposal must not dispose an application-owned shared driver.
        await fixture.Driver.VerifyConnectivityAsync();
        await using var next = new DbContext(options);
        Assert.True(await next.Database.CanConnectAsync());
    }

    [Neo4jIntegrationFact]
    public async Task Connectivity_uses_database_option_and_preserves_cancellation()
    {
        await using var missing = new DbContext(new DbContextOptionsBuilder()
            .UseNeo4j(fixture.Driver, "graphbridge_missing_" + Guid.NewGuid().ToString("N")).Options);
        Assert.False(await missing.Database.CanConnectAsync());

        await using var valid = new DbContext(new DbContextOptionsBuilder()
            .UseNeo4j(fixture.Driver, fixture.Database).Options);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => valid.Database.CanConnectAsync(cancellation.Token));
        Assert.True(await valid.Database.CanConnectAsync());
    }

    [Neo4jIntegrationFact]
    public async Task Database_creation_and_deletion_require_explicit_schema_operations()
    {
        await using var context = new DbContext(new DbContextOptionsBuilder()
            .UseNeo4j(fixture.Driver, fixture.Database).Options);
        await Assert.ThrowsAsync<NotSupportedException>(() => context.Database.EnsureCreatedAsync());
        await Assert.ThrowsAsync<NotSupportedException>(() => context.Database.EnsureDeletedAsync());
        Assert.True(await context.Database.CanConnectAsync());
    }
}
