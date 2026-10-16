using GraphBridge.IntegrationTests;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.TestModels.Northwind;
using Microsoft.EntityFrameworkCore.TestUtilities;

namespace GraphBridge.SpecificationTests;

/// <summary>Original Northwind Order IDs/customer IDs as plain nodes, for aggregate semantics only.</summary>
public sealed class NorthwindAggregatesNeo4jFixture : NorthwindQueryFixtureBase<NoopModelCustomizer>
{
    private readonly DisposableNeo4jFixture _database = new();
    private readonly ITestStoreFactory _factory;
    private bool _connected;
    private bool _initializationStarted;
    public NorthwindAggregatesNeo4jFixture() => _factory = new Neo4jSpecificationTestStoreFactory(_database, SpecificationStoreProfile.NorthwindAggregates);
    protected override ITestStoreFactory TestStoreFactory => _factory;
    protected override string StoreName => "GraphBridgeNorthwindAggregateSpecifications";
    protected override bool RecreateStore => true;
    protected override bool UsePooling => false;
    protected override bool ShouldLogCategory(string category) => category == DbLoggerCategory.Database.Command.Name;
    public override DbContextOptionsBuilder AddOptions(DbContextOptionsBuilder builder)
        => base.AddOptions(builder).ConfigureWarnings(w => w.Log(CoreEventId.MappedEntityTypeIgnoredWarning, CoreEventId.MappedPropertyIgnoredWarning));

    public override async Task InitializeAsync()
    {
        if (!RequiresDisposableNeo4jAttribute.Enabled) return;
        await _database.InitializeAsync();
        _connected = true;
        _initializationStarted = true;
        await base.InitializeAsync();
    }
    public override async Task DisposeAsync()
    {
        try { if (_initializationStarted) await base.DisposeAsync(); }
        finally { if (_connected) await _database.DisposeAsync(); }
    }
    protected override void OnModelCreating(ModelBuilder builder, DbContext context)
    {
        base.OnModelCreating(builder, context);
        ConfigureOrderModel(builder);
    }
    internal static void ConfigureOrderModel(ModelBuilder builder)
    {
        foreach (var type in builder.Model.GetEntityTypes().Select(t => t.ClrType).Where(t => t != typeof(Order)).ToArray())
            builder.Ignore(type);
        var order = builder.Entity<Order>().ToGraphNode("GraphBridgeSpecAggregateOrder");
        order.Ignore(o => o.Customer);
        order.Ignore(o => o.OrderDetails);
        foreach (var property in order.Metadata.GetProperties().Where(p => p.Name is not (nameof(Order.OrderID) or nameof(Order.CustomerID))).ToArray())
            order.Ignore(property.Name);
        order.HasKey(o => o.OrderID);
        order.Property(o => o.OrderID).ValueGeneratedNever();
    }
    protected override async Task SeedAsync(NorthwindContext context)
    {
        context.Orders.AddRange(new NorthwindData().Orders);
        await context.SaveChangesAsync();
    }
}
