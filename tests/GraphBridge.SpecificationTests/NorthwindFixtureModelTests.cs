using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.TestModels.Northwind;
using Neo4j.Driver;

namespace GraphBridge.SpecificationTests;

public sealed class NorthwindFixtureModelTests
{
    [Fact]
    public void Customer_slice_finalizes_all_upstream_scalars_without_other_entities_or_transport()
    {
        using var context = new CustomerContext(new DbContextOptionsBuilder<CustomerContext>()
            .UseNeo4j(DispatchProxy.Create<IDriver, RejectDriver>())
            .ConfigureWarnings(w => w.Default(WarningBehavior.Throw).Log(CoreEventId.MappedEntityTypeIgnoredWarning)).Options);
        var customer = Assert.Single(context.Model.GetEntityTypes());
        Assert.Equal(typeof(Customer), customer.ClrType);
        Assert.Equal(new[] { "Address", "City", "CompanyName", "ContactName", "ContactTitle", "Country", "CustomerID", "Fax", "Phone", "PostalCode", "Region" },
            customer.GetProperties().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal));
        Assert.Empty(customer.GetNavigations());
        Assert.Empty(customer.GetForeignKeys());
        Assert.Equal("CustomerID", Assert.Single(customer.FindPrimaryKey()!.Properties).Name);
    }

    private sealed class CustomerContext(DbContextOptions options) : NorthwindContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            NorthwindCustomersNeo4jFixture.ConfigureCustomerModel(modelBuilder);
        }
    }

    public class RejectDriver : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method, object?[]? args) => throw new InvalidOperationException("Offline model validation attempted transport.");
    }
}
