using GraphBridge.EntityFrameworkCore.Diagnostics;
using GraphBridge.EntityFrameworkCore.Metadata;
using GraphBridge.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace GraphBridge.UnitTests;

public sealed class ContainsQueryTests
{
    [Fact]
    public void Captured_array_list_and_inline_array_use_one_collection_parameter()
    {
        var driver = ProviderTestDriver.Create();
        using var db = Context(driver);
        var numbers = new int?[] { 9123, null, 4567 };
        var list = new List<string?> { "private-value", null };
        foreach (var query in new[]
        {
            db.Set<Row>().Where(r => numbers.Contains(r.Number)),
            db.Set<Row>().Where(r => list.Contains(r.Name)),
            db.Set<Row>().Where(r => new int?[] { 9123, null }.Contains(r.Number))
        })
        {
            var text = query.ToQueryString();
            Assert.Contains(" IN $p0", text);
            Assert.Contains("any(__gb_contains_value IN $p0 WHERE __gb_contains_value IS NULL)", text);
            Assert.DoesNotContain("9123", text);
            Assert.DoesNotContain("private-value", text);
            Assert.DoesNotContain("$p1", text);
        }
        Assert.Empty(((ProviderTestDriver)(object)driver).Calls);
    }

    [Fact]
    public void Negation_retains_null_compensation()
    {
        using var db = Context(ProviderTestDriver.Create());
        string?[] values = [null, "first"];
        var text = db.Set<Row>().Where(r => !values.Contains(r.Name)).ToQueryString();
        Assert.Contains("(NOT coalesce((coalesce(n.`Name` IN $p0, false)", text);
        Assert.Contains("n.`Name` IS NULL AND any(", text);
    }

    [Fact]
    public async Task Oversized_null_and_comparer_collections_fail_before_transport()
    {
        var driver = ProviderTestDriver.Create();
        await using var db = Context(driver);
        var huge = Enumerable.Range(0, 1025).Select(n => (int?)n).ToArray();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => db.Set<Row>().Where(r => huge.Contains(r.Number)).ToListAsync());
        Assert.Contains("1024", error.Message);
        var comparer = new HashSet<string?>(StringComparer.OrdinalIgnoreCase) { "abc" };
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.Set<Row>().Where(r => comparer.Contains(r.Name)).ToListAsync());
        int?[] missing = null!;
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.Set<Row>().Where(r => missing.Contains(r.Number)).ToListAsync());
        Assert.Empty(((ProviderTestDriver)(object)driver).Calls);
    }

    [Fact]
    public void Comparer_overload_floating_point_and_custom_converters_reject()
    {
        using var db = Context(ProviderTestDriver.Create());
        string?[] words = ["abc"];
        double[] floats = [double.NaN];
        Assert.Throws<Neo4jQueryTranslationException>(() => db.Set<Row>().Where(r => Enumerable.Contains(words, r.Name, StringComparer.OrdinalIgnoreCase)).ToQueryString());
        Assert.Throws<Neo4jQueryTranslationException>(() => db.Set<Row>().Where(r => Enumerable.Contains(floats, r.Float)).ToQueryString());
        using var converted = new ProviderModelContext(ProviderTestDriver.Create(), b =>
        {
            b.Entity<Row>().ToGraphNode("ContainsRow");
            b.Entity<Row>().Property(r => r.Name).HasConversion(v => v!.ToUpperInvariant(), v => v);
        });
        Assert.Throws<Neo4jQueryTranslationException>(() => converted.Set<Row>().Where(r => Enumerable.Contains(words, r.Name)).ToQueryString());
    }

    [Fact]
    public void Binding_snapshots_converted_values_and_never_enumerates_lazy_input()
    {
        using var db = Context(ProviderTestDriver.Create());
        var binding = db.GetService<GraphBindingCompiler>().GetNode(db.Model.FindEntityType(typeof(Row))!);
        var property = binding.Properties.Single(p => p.Property.Name == nameof(Row.Number));
        var parameter = new Neo4jQueryParameter("p0", "values", null, property, Collection: true);
        var source = new List<int?> { 1, null, 2 };
        var snapshot = Assert.IsType<object?[]>(parameter.Resolve(new Dictionary<string, object?> { ["values"] = source }));
        source[0] = 99;
        Assert.Equal(new object?[] { 1L, null, 2L }, snapshot);
        var lazy = new Unenumerable();
        Assert.Throws<InvalidOperationException>(() => parameter.Resolve(new Dictionary<string, object?> { ["values"] = lazy }));
        Assert.False(lazy.Enumerated);
    }

    private sealed class Unenumerable : IEnumerable<int?>
    {
        public bool Enumerated { get; private set; }
        public IEnumerator<int?> GetEnumerator() { Enumerated = true; throw new InvalidOperationException("Must never enumerate."); }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private static ProviderModelContext Context(Neo4j.Driver.IDriver driver)
        => new(driver, b => b.Entity<Row>().ToGraphNode("ContainsRow"));
    public sealed class Row
    {
        public Guid Id { get; set; }
        public string? Name { get; set; }
        public int? Number { get; set; }
        public double Float { get; set; }
    }
}
