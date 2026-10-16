using GraphBridge.EntityFrameworkCore.Diagnostics;
using GraphBridge.EntityFrameworkCore.Metadata;
using GraphBridge.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace GraphBridge.UnitTests;

public sealed class NullTypeSemanticsTests
{
    [Fact]
    public void Literal_Guid_construction_is_parameterized_and_malformed_literal_fails_before_transport()
    {
        var driver = ProviderTestDriver.Create();
        using var context = new ProviderModelContext(driver, b => b.Entity<GuidRow>().ToGraphNode("GuidRow"));
        const string literal = "DF36F493-463F-4123-83F9-6B135DEEB7BA";
        Assert.Equal("MATCH (n:`GuidRow`)\nWHERE coalesce(n.`Id` = $p0, (n.`Id` IS NULL AND $p0 IS NULL))\nRETURN n.`Id` AS `c0`",
            context.Set<GuidRow>().Where(r => r.Id == new Guid(literal)).ToQueryString());
        var error = Assert.Throws<Neo4jQueryTranslationException>(() =>
            context.Set<GuidRow>().Where(r => r.Id == new Guid("invalid-private-value")).ToQueryString());
        Assert.DoesNotContain("invalid-private-value", error.ToString());
        Assert.Empty(((ProviderTestDriver)(object)driver).Calls);
    }

    [Fact]
    public void Negated_nullable_range_has_an_exact_compensated_Cypher_snapshot()
    {
        var driver = ProviderTestDriver.Create();
        using var context = new ProviderModelContext(driver, b => b.Entity<Pair>().ToGraphNode("NullPair"));
        Assert.Equal("MATCH (n:`NullPair`)\nWHERE (NOT coalesce(coalesce(n.`Left` > n.`Right`, false), false))\n"
            + "RETURN n.`Id` AS `c0`, n.`Left` AS `c1`, n.`Right` AS `c2`",
            context.Set<Pair>().Where(p => !(p.Left > p.Right)).ToQueryString());
        Assert.Empty(((ProviderTestDriver)(object)driver).Calls);
    }

    [Fact]
    public void Nullable_parameter_rebinding_keeps_the_same_snapshot_for_null_and_nonnull_values()
    {
        var driver = ProviderTestDriver.Create();
        using var context = new ProviderModelContext(driver, b => b.Entity<Pair>().ToGraphNode("NullPair"));
        int? value = null;
        var query = context.Set<Pair>().Where(p => p.Left != value);
        const string expected = "MATCH (n:`NullPair`)\nWHERE (NOT coalesce(n.`Left` = $p0, (n.`Left` IS NULL AND $p0 IS NULL)))\n"
            + "RETURN n.`Id` AS `c0`, n.`Left` AS `c1`, n.`Right` AS `c2`";
        Assert.Equal(expected, query.ToQueryString());
        value = int.MinValue;
        Assert.Equal(expected, query.ToQueryString());
        value = int.MaxValue;
        Assert.Equal(expected, query.ToQueryString());
        Assert.Empty(((ProviderTestDriver)(object)driver).Calls);
    }

    [Fact]
    public void Temporal_parameters_use_the_model_converter_and_retain_null_as_null()
    {
        var driver = ProviderTestDriver.Create();
        using var context = new ProviderModelContext(driver, ConfigureConverted);
        var binding = context.GetService<GraphBindingCompiler>().GetNode(context.Model.FindEntityType(typeof(Converted))!);
        var duration = binding.Properties.Single(p => p.Property.Name == nameof(Converted.Duration));
        var parameter = new Neo4jQueryParameter("p0", "value", null, duration);
        Assert.Equal(-9L, parameter.Resolve(new Dictionary<string, object?> { ["value"] = TimeSpan.FromTicks(-9) }));
        Assert.Null(parameter.Resolve(new Dictionary<string, object?> { ["value"] = null }));
        Assert.Equal(long.MaxValue, parameter.Resolve(new Dictionary<string, object?> { ["value"] = TimeSpan.MaxValue }));
        Assert.Equal(TimeSpan.MinValue, duration.FromStore(long.MinValue));
        Assert.Empty(((ProviderTestDriver)(object)driver).Calls);
    }

    [Fact]
    public void Nullable_numeric_store_boundaries_are_checked_and_invalid_store_types_are_rejected()
    {
        var driver = ProviderTestDriver.Create();
        using var context = new ProviderModelContext(driver, b => b.Entity<Pair>().ToGraphNode("NullPair"));
        var binding = context.GetService<GraphBindingCompiler>().GetNode(context.Model.FindEntityType(typeof(Pair))!);
        var number = binding.Properties.Single(p => p.Property.Name == nameof(Pair.Left));
        Assert.Equal((long)int.MaxValue, number.ToStore(int.MaxValue));
        Assert.Equal(int.MinValue, number.FromStore((long)int.MinValue));
        Assert.Null(number.FromStore(null));
        Assert.Throws<OverflowException>(() => number.FromStore((long)int.MaxValue + 1));
        Assert.Throws<OverflowException>(() => number.FromStore((long)int.MinValue - 1));
        Assert.Throws<InvalidOperationException>(() => number.FromStore("1"));
        Assert.Throws<InvalidOperationException>(() => number.FromStore(1.5d));
        Assert.Empty(((ProviderTestDriver)(object)driver).Calls);
    }

    [Fact]
    public void Nullable_enum_parameter_and_store_conversion_check_unsigned_and_narrow_boundaries()
    {
        var driver = ProviderTestDriver.Create();
        using var context = new ProviderModelContext(driver, ConfigureConverted);
        var binding = context.GetService<GraphBindingCompiler>().GetNode(context.Model.FindEntityType(typeof(Converted))!);
        var narrow = binding.Properties.Single(p => p.Property.Name == nameof(Converted.Narrow));
        var unsigned = binding.Properties.Single(p => p.Property.Name == nameof(Converted.Unsigned));
        var parameter = new Neo4jQueryParameter("p0", "value", null, narrow);
        Assert.Equal(255L, parameter.Resolve(new Dictionary<string, object?> { ["value"] = NarrowState.Maximum }));
        Assert.Null(parameter.Resolve(new Dictionary<string, object?> { ["value"] = null }));
        Assert.Equal(NarrowState.Maximum, narrow.FromStore(255L));
        Assert.Throws<OverflowException>(() => narrow.FromStore(256L));
        Assert.Throws<OverflowException>(() => narrow.FromStore(-1L));
        Assert.Throws<OverflowException>(() => unsigned.ToStore(UnsignedState.Maximum));
        Assert.Throws<OverflowException>(() => unsigned.FromStore(-1L));
        Assert.Empty(((ProviderTestDriver)(object)driver).Calls);
    }

    [Fact]
    public void Temporal_converter_ordering_and_lossy_casts_fail_before_transport()
    {
        var driver = ProviderTestDriver.Create();
        using var context = new ProviderModelContext(driver, ConfigureConverted);
        var value = TimeSpan.Zero;
        Assert.Throws<Neo4jQueryTranslationException>(() => context.Set<Converted>().Where(p => p.Duration > value).ToQueryString());
        Assert.Throws<Neo4jQueryTranslationException>(() => context.Set<Converted>().OrderBy(p => p.Duration).ToQueryString());
        Assert.Empty(((ProviderTestDriver)(object)driver).Calls);
        using var integers = new ProviderModelContext(driver, b => b.Entity<Pair>().ToGraphNode("NullPair"));
        Assert.Throws<Neo4jQueryTranslationException>(() => integers.Set<Pair>().Where(p => (byte?)p.Left == 1).ToQueryString());
        Assert.Empty(((ProviderTestDriver)(object)driver).Calls);
    }

    private static void ConfigureConverted(ModelBuilder builder)
    {
        builder.Entity<Converted>().ToGraphNode("NullConverted");
        builder.Entity<Converted>().Property(p => p.Duration).HasConversion(new TimeSpanToTicksConverter());
    }
    public sealed class Pair
    {
        public int Id { get; set; }
        public int? Left { get; set; }
        public int? Right { get; set; }
    }
    public sealed class GuidRow { public Guid Id { get; set; } }
    public enum NarrowState : byte { Maximum = byte.MaxValue }
    public enum UnsignedState : ulong { Maximum = ulong.MaxValue }
    public sealed class Converted
    {
        public int Id { get; set; }
        public TimeSpan? Duration { get; set; }
        public NarrowState? Narrow { get; set; }
        public UnsignedState? Unsigned { get; set; }
    }
}
