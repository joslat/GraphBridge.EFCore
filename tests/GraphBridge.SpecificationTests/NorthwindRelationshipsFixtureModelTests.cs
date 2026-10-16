using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.TestModels.Northwind;
using Neo4j.Driver;

namespace GraphBridge.SpecificationTests;

public sealed class NorthwindRelationshipsFixtureModelTests
{
    [Fact]
    public void Adapted_model_retains_upstream_scalars_and_one_required_inverse_relationship()
    {
        using var context = new RelationshipContext(new DbContextOptionsBuilder<RelationshipContext>()
            .UseNeo4j(DispatchProxy.Create<IDriver, NorthwindFixtureModelTests.RejectDriver>())
            .ConfigureWarnings(w => w.Default(WarningBehavior.Throw).Log(CoreEventId.MappedEntityTypeIgnoredWarning)
                .Log(CoreEventId.MappedNavigationIgnoredWarning)).Options);
        Assert.Equal(2, context.Model.GetEntityTypes().Count());
        var customer = context.Model.FindEntityType(typeof(Customer))!;
        var order = context.Model.FindEntityType(typeof(Order))!;
        Assert.Equal(new[] { "Address", "City", "CompanyName", "ContactName", "ContactTitle", "Country", "CustomerID", "Fax", "Phone", "PostalCode", "Region" },
            customer.GetProperties().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal));
        Assert.Equal(new[] { "CustomerID", "EmployeeID", "OrderDate", "OrderID" },
            order.GetProperties().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal));
        var reference = Assert.Single(order.GetForeignKeys());
        Assert.True(reference.IsRequired);
        Assert.False(reference.IsUnique);
        Assert.Equal(DeleteBehavior.Restrict, reference.DeleteBehavior);
        Assert.Same(customer, reference.PrincipalEntityType);
        Assert.Equal("CustomerID", Assert.Single(reference.Properties).Name);
        Assert.Equal("Orders", Assert.Single(customer.GetNavigations()).Name);
        Assert.Equal("Customer", Assert.Single(order.GetNavigations()).Name);
        Assert.Equal("GRAPHBRIDGE_SPEC_CUSTOMER_ORDER", reference.FindAnnotation("GraphBridge:NodeReferenceType")?.Value);
        foreach (var entity in new[] { customer, order })
            Assert.Equal(ValueGenerated.Never, Assert.Single(entity.FindPrimaryKey()!.Properties).ValueGenerated);
        var employee = order.FindProperty(nameof(Order.EmployeeID))!.GetTypeMapping().Converter!;
        Assert.Equal(uint.MaxValue, employee.ConvertFromProvider(employee.ConvertToProvider(uint.MaxValue)));
        var date = new DateTime(1997, 5, 12, 1, 2, 3, DateTimeKind.Unspecified);
        var temporal = order.FindProperty(nameof(Order.OrderDate))!.GetTypeMapping().Converter!;
        Assert.Equal(date, temporal.ConvertFromProvider(temporal.ConvertToProvider(date)));
    }

    [Fact]
    public void Complete_upstream_data_fits_the_explicit_required_reference_adapter()
    {
        var data = new NorthwindData();
        Assert.Equal(91, data.Customers.Length);
        Assert.Equal(830, data.Orders.Length);
        Assert.All(data.Orders, order =>
        {
            var customer = Assert.Single(data.Customers, c => c.CustomerID == order.CustomerID);
            Assert.Same(customer, order.Customer);
            Assert.Contains(order, customer.Orders);
        });
        Assert.Equal(830, data.Customers.Sum(c => c.Orders.Count));
        Assert.Contains(data.Customers, c => c.Orders.Count == 0);
    }

    private sealed class RelationshipContext(DbContextOptions options) : NorthwindContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            NorthwindRelationshipsNeo4jFixture.ConfigureRelationshipModel(modelBuilder);
        }
    }
}
