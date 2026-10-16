using GraphBridge.IntegrationTests;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.TestModels.Northwind;
using Microsoft.EntityFrameworkCore.TestUtilities;

namespace GraphBridge.SpecificationTests;

/// <summary>Explicit Customer-only adaptation; original data/scalars/asserters remain upstream-owned.</summary>
public sealed class NorthwindCustomersNeo4jFixture : NorthwindQueryFixtureBase<NoopModelCustomizer>
{
    private readonly DisposableNeo4jFixture _database = new();
    private readonly ITestStoreFactory _factory;
    private bool _connected;
    private bool _initializationStarted;
    public NorthwindCustomersNeo4jFixture() => _factory = new Neo4jSpecificationTestStoreFactory(_database, SpecificationStoreProfile.NorthwindCustomers);
    protected override ITestStoreFactory TestStoreFactory => _factory;
    protected override string StoreName => "GraphBridgeNorthwindCustomersSpecifications";
    protected override bool RecreateStore => true;
    protected override bool UsePooling => false;
    protected override bool ShouldLogCategory(string category) => category == DbLoggerCategory.Database.Command.Name;
    public override DbContextOptionsBuilder AddOptions(DbContextOptionsBuilder builder)
        => base.AddOptions(builder).ConfigureWarnings(w => w.Log(CoreEventId.MappedEntityTypeIgnoredWarning));

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
        ConfigureCustomerModel(builder);
    }

    internal static void ConfigureCustomerModel(ModelBuilder builder)
    {
        // This named slice makes no claim about Northwind's Order/Product/keyless models.
        foreach (var type in builder.Model.GetEntityTypes().Select(t => t.ClrType).Where(t => t != typeof(Customer)).ToArray())
            builder.Ignore(type);
        var customer = builder.Entity<Customer>().ToGraphNode("GraphBridgeSpecCustomer");
        customer.Ignore(c => c.Orders);
        customer.Ignore(c => c.Context);
        customer.HasKey(c => c.CustomerID);
        customer.Property(c => c.CustomerID).ValueGeneratedNever();
    }

    protected override async Task SeedAsync(NorthwindContext context)
    {
        context.Customers.AddRange(new NorthwindData().Customers);
        await context.SaveChangesAsync();
    }
}
