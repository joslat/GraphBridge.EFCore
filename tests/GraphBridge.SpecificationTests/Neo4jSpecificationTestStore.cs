using GraphBridge.IntegrationTests;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.TestUtilities;
using Microsoft.Extensions.DependencyInjection;

namespace GraphBridge.SpecificationTests;

internal enum SpecificationStoreProfile { BasicTypes, NorthwindCustomers, Updates, NorthwindRelationships, NorthwindAggregates }

internal sealed class Neo4jSpecificationTestStoreFactory(
    DisposableNeo4jFixture fixture, SpecificationStoreProfile profile = SpecificationStoreProfile.BasicTypes) : ITestStoreFactory
{
    private readonly TestStoreIndex _index = new();

    public TestStore Create(string storeName) => new Neo4jSpecificationTestStore(storeName, false, fixture, _index, profile);
    public TestStore GetOrCreate(string storeName) => new Neo4jSpecificationTestStore(storeName, true, fixture, _index, profile);
    public IServiceCollection AddProviderServices(IServiceCollection services) => services.AddEntityFrameworkNeo4j();
    public ListLoggerFactory CreateListLoggerFactory(Func<string, bool> shouldLogCategory) => new(shouldLogCategory);
}

/// <summary>Logical test store on the existing disposable server, never a server/database creator.</summary>
internal sealed class Neo4jSpecificationTestStore(
    string name, bool shared, DisposableNeo4jFixture fixture, TestStoreIndex index, SpecificationStoreProfile profile) : TestStore(name, shared)
{
    public const string BasicLabel = "GraphBridgeSpecBasicTypes";
    public const string NullableLabel = "GraphBridgeSpecNullableBasicTypes";

    // Fixed test-owned identifiers, never derived from user input or discovered database names.
    private (string Label, string Key, string Constraint)[] OwnedNodes => profile switch
    {
        SpecificationStoreProfile.BasicTypes =>
        [
            (BasicLabel, "Id", "graphbridge_spec_basic_key"),
            (NullableLabel, "Id", "graphbridge_spec_nullable_key")
        ],
        SpecificationStoreProfile.NorthwindCustomers =>
        [("GraphBridgeSpecCustomer", "CustomerID", "graphbridge_spec_customer_key")],
        SpecificationStoreProfile.NorthwindAggregates =>
        [("GraphBridgeSpecAggregateOrder", "OrderID", "graphbridge_spec_aggregate_order_key")],
        SpecificationStoreProfile.NorthwindRelationships =>
        [
            ("GraphBridgeSpecIncludeOrder", "OrderID", "graphbridge_spec_include_order_key"),
            ("GraphBridgeSpecIncludeCustomer", "CustomerID", "graphbridge_spec_include_customer_key")
        ],
        SpecificationStoreProfile.Updates =>
        [
            ("GraphBridgeSpecUpdateProduct", "Id", "graphbridge_spec_update_product_key"),
            ("GraphBridgeSpecUpdateCategory", "Id", "graphbridge_spec_update_category_key"),
            ("GraphBridgeSpecUpdateProductBytes", "Id", "graphbridge_spec_update_product_bytes_key"),
            ("GraphBridgeSpecUpdateBytes", "Id", "graphbridge_spec_update_bytes_key")
        ],
        _ => throw new ArgumentOutOfRangeException(nameof(profile))
    };

    public override DbContextOptionsBuilder AddProviderOptions(DbContextOptionsBuilder builder)
        => builder.UseNeo4j(fixture.Driver, fixture.Database);

    protected override TestStoreIndex GetTestStoreIndex(IServiceProvider? serviceProvider) => index;

    public override async Task CleanAsync(DbContext context)
    {
        await DeleteOwnedNodesAsync();
        foreach (var node in OwnedNodes)
            await ExecuteAsync($"CREATE CONSTRAINT {node.Constraint} IF NOT EXISTS FOR (n:{node.Label}) REQUIRE n.{node.Key} IS UNIQUE");
    }

    public override async ValueTask DisposeAsync()
    {
        try
        {
            await DeleteOwnedNodesAsync();
            foreach (var node in OwnedNodes)
                await ExecuteAsync($"DROP CONSTRAINT {node.Constraint} IF EXISTS");
        }
        finally
        {
            // Keep the upstream shared-store cache consistent even if cleanup fails.
            index.RemoveShared(GetType().Name + Name);
        }
    }

    private async Task DeleteOwnedNodesAsync()
    {
        if (profile == SpecificationStoreProfile.NorthwindRelationships)
            await ExecuteAsync("MATCH (:GraphBridgeSpecIncludeCustomer)-[r:GRAPHBRIDGE_SPEC_CUSTOMER_ORDER]->(:GraphBridgeSpecIncludeOrder) DELETE r");
        // These labels are exclusive to this serialized suite. Plain DELETE detects unexpected edges.
        foreach (var node in OwnedNodes)
            await ExecuteAsync($"MATCH (n:{node.Label}) DELETE n");
    }

    private async Task ExecuteAsync(string command)
    {
        await using var session = fixture.OpenSession();
        var cursor = await session.RunAsync(command);
        await cursor.ConsumeAsync();
    }
}
