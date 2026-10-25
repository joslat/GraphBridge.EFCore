using System.Linq.Expressions;
using GraphBridge.EntityFrameworkCore.Diagnostics;
using GraphBridge.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace GraphBridge.UnitTests;

public sealed class FindQueryTests
{
    [Theory]
    [InlineData(nameof(Row.Id))]
    [InlineData(nameof(Row.Number))]
    [InlineData(nameof(Row.Name))]
    public void Original_EF_finder_predicate_is_parameterized(string keyName)
    {
        var driver = ProviderTestDriver.Create();
        using var db = Context(driver, keyName);
        object key = keyName switch { nameof(Row.Id) => Guid.NewGuid(), nameof(Row.Number) => 91273, _ => "private-key" };
        var entity = Expression.Parameter(typeof(Row), "e");
#pragma warning disable EF1001 // Exact pinned EntityFinder predicate, not a hand-written substitute.
        var predicate = Microsoft.EntityFrameworkCore.Internal.ExpressionExtensions.BuildPredicate(
            db.Model.FindEntityType(typeof(Row))!.FindPrimaryKey()!.Properties, new ValueBuffer([key]), entity);
#pragma warning restore EF1001
        var text = db.Set<Row>().Where(Expression.Lambda<Func<Row, bool>>(predicate, entity)).ToQueryString();
        Assert.Contains($"n.`{keyName}` = $p0", text);
        Assert.DoesNotContain(key.ToString()!, text);
        Assert.Empty(((ProviderTestDriver)(object)driver).Calls);
    }

    [Fact]
    public async Task Actual_FindAsync_translates_before_reaching_transport()
    {
        var driver = ProviderTestDriver.Create();
        await using var db = Context(driver);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => db.Set<Row>().FindAsync(Guid.NewGuid()).AsTask());
        Assert.Contains("Unexpected driver call", error.Message);
        Assert.NotEmpty(((ProviderTestDriver)(object)driver).Calls);
    }

    [Fact]
    public void Static_value_equality_and_negation_preserve_null_compensation_and_privacy()
    {
        var driver = ProviderTestDriver.Create();
        using var db = Context(driver);
        var id = Guid.NewGuid();
        var text = db.Set<Row>().Where(r => !object.Equals(r.Id, id)).ToQueryString();
        Assert.Contains("NOT coalesce", text);
        Assert.Contains("n.`Id` = $p0", text);
        Assert.DoesNotContain(id.ToString(), text);
        Assert.Contains("n.`Name` IS NULL AND $p0 IS NULL",
            db.Set<Row>().Where(r => object.Equals(r.Name, null)).ToQueryString());
        Assert.Empty(((ProviderTestDriver)(object)driver).Calls);
    }

    [Fact]
    public void Object_equality_does_not_enable_reference_equality_or_arbitrary_methods()
    {
        var driver = ProviderTestDriver.Create();
        using var db = Context(driver);
        object value = Guid.NewGuid();
        Assert.Throws<Neo4jQueryTranslationException>(() => db.Set<Row>().Where(r => (object)r.Id == value).ToQueryString());
        Assert.Throws<Neo4jQueryTranslationException>(() => db.Set<Row>().Where(r => object.Equals(r.Float, double.NaN)).ToQueryString());
        Assert.Throws<Neo4jQueryTranslationException>(() => db.Set<Row>().Where(r => object.Equals(r.Id, UserMethod(r.Id))).ToQueryString());
        Assert.Empty(((ProviderTestDriver)(object)driver).Calls);
    }

    [Fact]
    public void Instance_value_equality_translates_as_static_equality_does()
    {
        var driver = ProviderTestDriver.Create();
        using var db = Context(driver);
        var id = Guid.NewGuid();
        var name = "private-name";
        var text = db.Set<Row>().Where(r => r.Id.Equals(id) && r.Name.Equals(name)).ToQueryString();
        Assert.Contains("n.`Id` = $p0", text);
        Assert.Contains("n.`Name` = $p1", text);
        Assert.DoesNotContain(id.ToString(), text);
        Assert.DoesNotContain(name, text);
        Assert.Contains("n.`Name` = $p0", db.Set<Row>().Where(r => name.Equals(r.Name)).ToQueryString());
        Assert.Contains("n.`Number` = $p0", db.Set<Row>().Where(r => r.Number.Equals(7)).ToQueryString());
        Assert.Contains("n.`Count` = $p0", db.Set<Row>().Where(r => r.Count.Equals(7L)).ToQueryString());
        Assert.Contains("n.`Active` = $p0", db.Set<Row>().Where(r => r.Active.Equals(true)).ToQueryString());
        Assert.Contains("n.`Mood` = $p0", db.Set<Row>().Where(r => r.Mood.Equals(Mood.Busy)).ToQueryString());
        Assert.Contains("n.`Maybe` = $p0", db.Set<Row>().Where(r => r.Maybe.Equals(3)).ToQueryString());
        Assert.Contains("NOT coalesce", db.Set<Row>().Where(r => !r.Id.Equals(id)).ToQueryString());
        Assert.Empty(((ProviderTestDriver)(object)driver).Calls);
    }

    [Fact]
    public void Instance_equality_keeps_the_limits_of_static_equality()
    {
        var driver = ProviderTestDriver.Create();
        using var db = Context(driver);
        var name = "ada";
        Assert.Equal("GBQ120", Code(() => db.Set<Row>().Where(r => r.Float.Equals(double.NaN)).ToQueryString()));
        Assert.Equal("GBQ120", Code(() => db.Set<Row>().Where(r => r.Name.Equals(r.Name)).ToQueryString()));
        Assert.Equal("GBQ120", Code(() => db.Set<Row>().Where(r => r.Id.Equals(UserMethod(r.Id))).ToQueryString()));
        // A comparison overload is not value equality.
        Assert.Equal("GBQ120", Code(() => db.Set<Row>().Where(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToQueryString()));
        Assert.Empty(((ProviderTestDriver)(object)driver).Calls);

        static string? Code(Action action) => Assert.Throws<Neo4jQueryTranslationException>(action).Code;
    }

    [Fact]
    public async Task Boxed_cross_type_parameter_is_rejected_before_transport()
    {
        var driver = ProviderTestDriver.Create();
        await using var db = Context(driver);
        object value = "not-a-guid";
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => db.Set<Row>()
            .Where(r => object.Equals(EF.Property<object>(r, nameof(Row.Id)), value)).ToListAsync());
        Assert.Contains("CLR type", error.Message);
        Assert.Empty(((ProviderTestDriver)(object)driver).Calls);
    }

    [Fact]
    public void Runtime_type_guard_does_not_coerce_boxed_numbers_or_expose_values()
    {
        var parameter = new Neo4jQueryParameter("p0", "key", null, ExactValueType: typeof(int));
        Assert.Equal(7, parameter.Resolve(new Dictionary<string, object?> { ["key"] = 7 }));
        Assert.Null(parameter.Resolve(new Dictionary<string, object?> { ["key"] = null }));
        var error = Assert.Throws<InvalidOperationException>(() => parameter.Resolve(new Dictionary<string, object?> { ["key"] = 998172L }));
        Assert.DoesNotContain("998172", error.Message);
    }

    private static Guid UserMethod(Guid value) => throw new InvalidOperationException("Must never execute.");
    private static ProviderModelContext Context(Neo4j.Driver.IDriver driver, string key = nameof(Row.Id))
        => new(driver, b =>
        {
            var entity = b.Entity<Row>();
            entity.ToGraphNode("FindRow");
            entity.HasKey(key);
            entity.Property(key).ValueGeneratedNever();
        });

    public sealed class Row
    {
        public Guid Id { get; set; }
        public int Number { get; set; }
        public string Name { get; set; } = "";
        public double Float { get; set; }
        public long Count { get; set; }
        public bool Active { get; set; }
        public Mood Mood { get; set; }
        public int? Maybe { get; set; }
    }

    public enum Mood { Calm, Busy }
}
