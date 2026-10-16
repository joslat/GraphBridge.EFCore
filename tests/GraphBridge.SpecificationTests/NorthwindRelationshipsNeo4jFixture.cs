using GraphBridge.IntegrationTests;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.EntityFrameworkCore.TestModels.Northwind;
using Microsoft.EntityFrameworkCore.TestUtilities;

namespace GraphBridge.SpecificationTests;

/// <summary>Customer/Order slice with an explicitly required native reference; upstream data/assertions retained.</summary>
public sealed class NorthwindRelationshipsNeo4jFixture : NorthwindQueryFixtureBase<NoopModelCustomizer>
{
    private readonly DisposableNeo4jFixture _database = new();
    private readonly ITestStoreFactory _factory;
    private bool _connected;
    private bool _initializationStarted;
    public NorthwindRelationshipsNeo4jFixture()
        => _factory = new Neo4jSpecificationTestStoreFactory(_database, SpecificationStoreProfile.NorthwindRelationships);
    protected override ITestStoreFactory TestStoreFactory => _factory;
    protected override string StoreName => "GraphBridgeNorthwindRelationshipSpecifications";
    protected override bool RecreateStore => true;
    protected override bool UsePooling => false;
    protected override bool ShouldLogCategory(string category) => category == DbLoggerCategory.Database.Command.Name;
    public override DbContextOptionsBuilder AddOptions(DbContextOptionsBuilder builder)
        => base.AddOptions(builder).ConfigureWarnings(w => w.Log(CoreEventId.MappedEntityTypeIgnoredWarning)
            .Log(CoreEventId.MappedNavigationIgnoredWarning));

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
        ConfigureRelationshipModel(builder);
    }

    internal static void ConfigureRelationshipModel(ModelBuilder builder)
    {
        foreach (var type in builder.Model.GetEntityTypes().Select(t => t.ClrType)
            .Where(t => t != typeof(Customer) && t != typeof(Order)).ToArray())
            builder.Ignore(type);
        var customer = builder.Entity<Customer>().ToGraphNode("GraphBridgeSpecIncludeCustomer");
        customer.Ignore(c => c.Context);
        customer.HasKey(c => c.CustomerID);
        customer.Property(c => c.CustomerID).ValueGeneratedNever();
        var order = builder.Entity<Order>().ToGraphNode("GraphBridgeSpecIncludeOrder");
        order.Ignore(o => o.OrderDetails);
        order.HasKey(o => o.OrderID);
        order.Property(o => o.OrderID).ValueGeneratedNever();
        // The original Northwind model/asserter retains precisely these four Order scalars.
        // Explicit declarations prevent unsupported convention types from silently disappearing.
        order.Property(nameof(Order.EmployeeID)).HasConversion(new ValueConverter<uint, long>(v => v, v => checked((uint)v)));
        order.Property(nameof(Order.OrderDate)).HasConversion(new DateTimeToBinaryConverter());
        // All 830 original Orders have a Customer. This adapter does not prove optional FKs.
        order.HasOne(o => o.Customer).WithMany(c => c.Orders).HasForeignKey(o => o.CustomerID)
            .IsRequired().OnDelete(DeleteBehavior.Restrict).ToGraphReference("GRAPHBRIDGE_SPEC_CUSTOMER_ORDER");
    }

    protected override async Task SeedAsync(NorthwindContext context)
    {
        var data = new NorthwindData();
        context.Customers.AddRange(data.Customers);
        context.Orders.AddRange(data.Orders);
        await context.SaveChangesAsync();
    }
}
