using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.TestModels.Northwind;
using Neo4j.Driver;

namespace GraphBridge.SpecificationTests;

public sealed class NorthwindAggregatesFixtureModelTests
{
    [Fact]
    public void Aggregate_slice_retains_original_Order_IDs_and_customer_values_as_independent_nodes()
    {
        using var context = new AggregateContext(new DbContextOptionsBuilder<AggregateContext>()
            .UseNeo4j(DispatchProxy.Create<IDriver, NorthwindFixtureModelTests.RejectDriver>())
            .ConfigureWarnings(w => w.Default(WarningBehavior.Throw).Log(CoreEventId.MappedEntityTypeIgnoredWarning, CoreEventId.MappedPropertyIgnoredWarning)).Options);
        var order = Assert.Single(context.Model.GetEntityTypes());
        Assert.Equal(typeof(Order), order.ClrType);
        Assert.Equal(new[] { "CustomerID", "OrderID" }, order.GetProperties().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal));
        Assert.Empty(order.GetNavigations());
        Assert.Empty(order.GetForeignKeys());
        var key = Assert.Single(order.FindPrimaryKey()!.Properties);
        Assert.Equal("OrderID", key.Name);
        Assert.Equal(typeof(int), key.ClrType);
        Assert.Null(key.GetValueConverter());
        Assert.Equal(830, new NorthwindData().Orders.Select(o => o.OrderID).Distinct().Count());
    }
    private sealed class AggregateContext(DbContextOptions options) : NorthwindContext(options)
    {
        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);
            NorthwindAggregatesNeo4jFixture.ConfigureOrderModel(builder);
        }
    }
}
