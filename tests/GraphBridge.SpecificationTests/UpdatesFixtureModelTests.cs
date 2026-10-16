using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.TestModels.UpdatesModel;
using Neo4j.Driver;
using Category = Microsoft.EntityFrameworkCore.TestModels.UpdatesModel.Category;

namespace GraphBridge.SpecificationTests;

public sealed class UpdatesFixtureModelTests
{
    [Fact]
    public void Update_slice_retains_scalar_and_concurrency_contract_without_transport()
    {
        using var context = new VerificationContext(new DbContextOptionsBuilder<VerificationContext>()
            .UseNeo4j(DispatchProxy.Create<IDriver, RejectDriver>())
            .ConfigureWarnings(w => w.Default(WarningBehavior.Throw).Log(
                CoreEventId.MappedEntityTypeIgnoredWarning, CoreEventId.MappedNavigationIgnoredWarning)).Options);
        var entities = context.Model.GetEntityTypes().ToArray();
        Assert.Equal(new[] { typeof(AFewBytes), typeof(Category), typeof(Product), typeof(ProductWithBytes) },
            entities.Select(e => e.ClrType).OrderBy(t => t.Name, StringComparer.Ordinal));
        foreach (var entity in entities)
        {
            Assert.Null(entity.BaseType);
            Assert.Empty(entity.GetForeignKeys());
            Assert.Empty(entity.GetNavigations());
            Assert.Equal(ValueGenerated.Never, Assert.Single(entity.FindPrimaryKey()!.Properties).ValueGenerated);
        }
        var product = context.Model.FindEntityType(typeof(Product))!;
        Assert.Equal(new[] { "DependentId", "Id", "IsPrimary", "IsPrimaryNormalized", "Name", "Price" },
            product.GetProperties().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal));
        var price = product.FindProperty(nameof(Product.Price))!;
        Assert.True(price.IsConcurrencyToken);
        Assert.Equal("1.49", price.GetTypeMapping().Converter!.ConvertToProvider(1.49m));
        Assert.True(context.Model.FindEntityType(typeof(ProductWithBytes))!.FindProperty(nameof(ProductWithBytes.Bytes))!.IsConcurrencyToken);
        foreach (var type in new[] { typeof(ProductWithBytes), typeof(AFewBytes) })
        {
            var property = context.Model.FindEntityType(type)!.FindProperty("Bytes")!;
            byte[] value = [1, 2, 3, 4];
            var comparer = property.GetValueComparer()!;
            var snapshot = Assert.IsType<byte[]>(comparer.Snapshot(value));
            Assert.Same(value, snapshot);
            Assert.True(comparer.Equals(value, snapshot));
            Assert.True(comparer.Equals(value, value.ToArray()));
            var converter = property.GetTypeMapping().Converter!;
            var decoded = Assert.IsType<byte[]>(converter.ConvertFromProvider(converter.ConvertToProvider(value)));
            Assert.NotSame(value, decoded);
            Assert.Equal(value, decoded);
            value[0] = 99;
            Assert.Equal(1, decoded[0]);
            Assert.False(comparer.Equals(value, decoded));
        }
    }

    private sealed class VerificationContext(DbContextOptions options) : UpdatesContext(options)
    {
        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);
            new UpdatesNeo4jFixture().ConfigureModelForVerification(builder, this);
        }
    }

    public class RejectDriver : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method, object?[]? args) => throw new InvalidOperationException("Offline update model attempted transport.");
    }
}
