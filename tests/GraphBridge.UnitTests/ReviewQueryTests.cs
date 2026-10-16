using GraphBridge.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace GraphBridge.UnitTests;

public sealed class ReviewQueryTests
{
    [Fact]
    public void Provider_type_conversion_rejects_non_order_preserving_range()
    {
        var driver = ProviderTestDriver.Create();
        using var context = ConvertedContext(driver);
        var age = context.Model.FindEntityType(typeof(ReviewPerson))!.FindProperty(nameof(ReviewPerson.Age))!;
        Assert.Null(age.GetValueConverter());
        Assert.Equal(typeof(string), age.GetTypeMapping().Converter!.ProviderClrType);
        Assert.Throws<Neo4jQueryTranslationException>(
            () => context.Set<ReviewPerson>().Where(p => p.Age > 9).ToQueryString());
        Assert.Empty(((ProviderTestDriver)(object)driver).Calls);
    }

    [Fact]
    public void Provider_type_conversion_rejects_non_order_preserving_ordering()
    {
        var driver = ProviderTestDriver.Create();
        using var context = ConvertedContext(driver);
        Assert.Throws<Neo4jQueryTranslationException>(
            () => context.Set<ReviewPerson>().OrderBy(p => p.Age).ToQueryString());
        Assert.Empty(((ProviderTestDriver)(object)driver).Calls);
    }

    [Fact]
    public void Different_provider_type_conversions_reject_property_comparison()
    {
        var driver = ProviderTestDriver.Create();
        using var context = ConvertedContext(driver);
        Assert.Throws<Neo4jQueryTranslationException>(
            () => context.Set<ReviewPerson>().Where(p => p.Age == p.NativeAge).ToQueryString());
        Assert.Empty(((ProviderTestDriver)(object)driver).Calls);
    }

    [Fact]
    public void Model_rejects_unsupported_ignore_before_save_for_client_value()
    {
        var driver = ProviderTestDriver.Create();
        using var context = new ProviderModelContext(driver, builder =>
        {
            var person = builder.Entity<ReviewWritePerson>().ToGraphNode("GraphBridgeReviewPerson");
            person.Property(p => p.Ignored).Metadata.SetBeforeSaveBehavior(PropertySaveBehavior.Ignore);
        });
        var exception = Assert.Throws<InvalidOperationException>(() => context.Model);
        Assert.Contains("save behavior", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(((ProviderTestDriver)(object)driver).Calls);
    }

    [Fact]
    public void Model_rejects_unsupported_ignore_after_save_for_client_value()
    {
        var driver = ProviderTestDriver.Create();
        using var context = new ProviderModelContext(driver, builder =>
        {
            var person = builder.Entity<ReviewWritePerson>().ToGraphNode("GraphBridgeReviewPerson");
            person.Property(p => p.Ignored).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Ignore);
        });
        var exception = Assert.Throws<InvalidOperationException>(() => context.Model);
        Assert.Contains("save behavior", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(((ProviderTestDriver)(object)driver).Calls);
    }

    private static ProviderModelContext ConvertedContext(Neo4j.Driver.IDriver driver)
        => new(driver, builder =>
        {
            var person = builder.Entity<ReviewPerson>().ToGraphNode("GraphBridgeReviewPerson");
            person.Property(p => p.Age).HasConversion<string>();
        });

    public sealed class ReviewPerson
    {
        public Guid Id { get; set; }
        public int Age { get; set; }
        public int NativeAge { get; set; }
    }

    public sealed class ReviewWritePerson
    {
        public Guid Id { get; set; }
        public string? Ignored { get; set; }
        public string? Mutable { get; set; }
    }
}
