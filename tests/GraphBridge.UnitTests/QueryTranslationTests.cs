using GraphBridge.EntityFrameworkCore.Cypher;
using GraphBridge.EntityFrameworkCore.Diagnostics;
using GraphBridge.EntityFrameworkCore.Metadata;
using GraphBridge.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace GraphBridge.UnitTests;

public sealed class QueryTranslationTests
{
    [Fact]
    public void Renderer_emits_parameterized_predicate_projection_and_null_first_ordering()
    {
        var plan = new CypherQueryPlan("Person", [new("name", "c0")],
            new CypherBinary(new CypherProperty("age"), CypherOperator.GreaterThanOrEqual, new CypherParameter("p0")),
            [new("name", true)], "p1", "p2");
        Assert.Equal("MATCH (n:`Person`)\nWHERE coalesce(n.`age` >= $p0, false)\nRETURN n.`name` AS `c0`\nORDER BY n.`name` IS NULL DESC, n.`name` ASC\nSKIP $p1\nLIMIT $p2",
            CypherQueryRenderer.Render(plan));
    }

    [Fact]
    public void Renderer_compensates_nullable_equality_under_negation()
    {
        var predicate = new CypherNot(new CypherBinary(new CypherProperty("name"), CypherOperator.Equal, new CypherParameter("p0")));
        Assert.Equal("MATCH (n:`Person`)\nWHERE (NOT coalesce(coalesce(n.`name` = $p0, (n.`name` IS NULL AND $p0 IS NULL)), false))\nRETURN n.`name` AS `c0`",
            CypherQueryRenderer.Render(new("Person", [new("name", "c0")], predicate)));
    }

    [Theory]
    [InlineData("Person`) MATCH (victim) //")]
    [InlineData("")]
    [InlineData("two words")]
    [InlineData("1Person")]
    public void Renderer_rejects_unvalidated_identifier_tokens(string token)
        => Assert.Throws<InvalidOperationException>(() => CypherQueryRenderer.Render(new(token, [new("name", "c0")])));

    [Fact]
    public void ToQueryString_contains_no_closure_or_literal_values_and_never_opens_transport()
    {
        var driver = ProviderTestDriver.Create();
        using var context = Context(driver);
        var secret = "literal ' $ ` value";
        var text = context.Set<QueryPerson>().Where(p => p.Name == secret && p.Age > 7391).ToQueryString();
        Assert.Contains("$p0", text);
        Assert.Contains("$p1", text);
        Assert.DoesNotContain(secret, text);
        Assert.DoesNotContain("7391", text);
        Assert.Empty(((ProviderTestDriver)(object)driver).Calls);
    }

    [Fact]
    public void Custom_predicate_is_rejected_before_driver_or_client_execution()
    {
        var driver = ProviderTestDriver.Create();
        using var context = Context(driver);
        Assert.Throws<Neo4jQueryTranslationException>(() => context.Set<QueryPerson>().Where(p => ClientFunction(p.Name)).ToQueryString());
        Assert.Empty(((ProviderTestDriver)(object)driver).Calls);
    }

    [Fact]
    public void Pagination_does_not_allow_silently_reordered_filters_or_aggregates()
    {
        var driver = ProviderTestDriver.Create();
        using var context = Context(driver);
        var people = context.Set<QueryPerson>();
        Assert.Throws<Neo4jQueryTranslationException>(() => people.Take(2).Where(p => p.Age > 10).ToQueryString());
        Assert.Throws<Neo4jQueryTranslationException>(() => people.Take(2).OrderBy(p => p.Age).ToQueryString());
        Assert.Throws<Neo4jQueryTranslationException>(() => people.Skip(2).Count());
        Assert.Throws<Neo4jQueryTranslationException>(() => people.Take(2).Any());
        Assert.Empty(((ProviderTestDriver)(object)driver).Calls);
    }

    [Fact]
    public void Null_comparison_parameter_is_valid_for_required_property()
    {
        var driver = ProviderTestDriver.Create();
        using var context = Context(driver);
        var binding = context.GetService<GraphBindingCompiler>().GetNode(context.Model.FindEntityType(typeof(QueryPerson))!);
        var requiredName = binding.Properties.Single(p => p.Property.Name == nameof(QueryPerson.Name));
        Assert.False(requiredName.Property.IsNullable);
        Assert.Null(new Neo4jQueryParameter("p0", "value", null, requiredName)
            .Resolve(new Dictionary<string, object?> { ["value"] = null }));
    }

    [Fact]
    public void Enum_comparison_uses_model_converter_after_compiler_integer_conversion()
    {
        var driver = ProviderTestDriver.Create();
        using var context = Context(driver);
        var state = QueryState.Active;
        var text = context.Set<QueryPerson>().Where(p => p.State == state).ToQueryString();
        Assert.Contains("n.`State` = $p0", text);
        var binding = context.GetService<GraphBindingCompiler>().GetNode(context.Model.FindEntityType(typeof(QueryPerson))!);
        var stateProperty = binding.Properties.Single(p => p.Property.Name == nameof(QueryPerson.State));
        Assert.Equal(1L, new Neo4jQueryParameter("p0", "value", null, stateProperty)
            .Resolve(new Dictionary<string, object?> { ["value"] = 1 }));
        Assert.Empty(((ProviderTestDriver)(object)driver).Calls);
    }

    [Fact]
    public void Lossy_numeric_cast_and_boxed_reference_comparison_are_rejected()
    {
        var driver = ProviderTestDriver.Create();
        using var context = Context(driver);
        Assert.Throws<Neo4jQueryTranslationException>(() => context.Set<QueryPerson>().Where(p => (byte)p.Age == 1).ToQueryString());
        object boxed = 3;
        Assert.Throws<Neo4jQueryTranslationException>(() => context.Set<QueryPerson>().Where(p => (object)p.Age == boxed).ToQueryString());
        Assert.Empty(((ProviderTestDriver)(object)driver).Calls);
    }

    [Fact]
    public void Non_order_preserving_converter_is_not_used_for_server_ranges_or_ordering()
    {
        var driver = ProviderTestDriver.Create();
        using var context = new ProviderModelContext(driver, b =>
        {
            b.Entity<QueryPerson>().ToGraphNode("QueryPerson");
            b.Entity<QueryPerson>().Property(p => p.Age).HasConversion(value => -value, value => -value);
        });
        Assert.Throws<Neo4jQueryTranslationException>(() => context.Set<QueryPerson>().Where(p => p.Age > 10).ToQueryString());
        Assert.Throws<Neo4jQueryTranslationException>(() => context.Set<QueryPerson>().OrderBy(p => p.Age).ToQueryString());
        Assert.Contains("n.`Age` = $p0", context.Set<QueryPerson>().Where(p => p.Age == 10).ToQueryString());
        Assert.Empty(((ProviderTestDriver)(object)driver).Calls);
    }

    [Theory]
    [InlineData(-1, null, 0)]
    [InlineData(0, 1, 0)]
    [InlineData(5, 1, 1)]
    [InlineData(5, 2, 2)]
    public void Paging_parameters_preserve_LINQ_nonpositive_and_terminal_limits(int count, int? maximum, long expected)
        => Assert.Equal(expected, new Neo4jQueryParameter("p0", "count", null, NonNegative: true, Maximum: maximum)
            .Resolve(new Dictionary<string, object?> { ["count"] = count }));

    private static ProviderModelContext Context(Neo4j.Driver.IDriver driver)
        => new(driver, b => b.Entity<QueryPerson>().ToGraphNode("QueryPerson"));

    private static bool ClientFunction(string value) => throw new InvalidOperationException("Must never execute.");

    public sealed class QueryPerson
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
        public int Age { get; set; }
        public QueryState State { get; set; }
    }

    public enum QueryState { Pending, Active }
}
