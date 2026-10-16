using GraphBridge.EntityFrameworkCore.Cypher;
using GraphBridge.EntityFrameworkCore.Diagnostics;
using GraphBridge.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore;

namespace GraphBridge.UnitTests;

public sealed class IntegerExtremumTests
{
    [Fact]
    public void Renderer_validates_every_required_int_input_in_the_same_aggregate_query()
    {
        var plan = new CypherQueryPlan("Person", [])
        {
            Aggregate = CypherAggregate.Min,
            Extremum = new(new("age"), false, true)
        };
        Assert.Equal("MATCH (n:`Person`)\nRETURN min(n.`age`) AS aggregate, count(CASE WHEN n.`age` IS :: INTEGER NOT NULL AND n.`age` >= -2147483648 AND n.`age` <= 2147483647 THEN null ELSE 1 END) AS __gb_aggregate_invalid",
            CypherQueryRenderer.Render(plan));
        var optional = plan with { Aggregate = CypherAggregate.Max, Extremum = new(new("wide"), true, false) };
        Assert.Equal("MATCH (n:`Person`)\nRETURN max(n.`wide`) AS aggregate, count(CASE WHEN n.`wide` IS NULL OR (n.`wide` IS :: INTEGER NOT NULL) THEN null ELSE 1 END) AS __gb_aggregate_invalid",
            CypherQueryRenderer.Render(optional));
    }

    [Fact]
    public void Renderer_rejects_invalid_extremum_identifiers_and_missing_input()
    {
        var plan = new CypherQueryPlan("Person", []) { Aggregate = CypherAggregate.Min };
        Assert.Throws<InvalidOperationException>(() => CypherQueryRenderer.Render(plan));
        Assert.Throws<InvalidOperationException>(() => CypherQueryRenderer.Render(plan with { Extremum = new(new("bad`name"), false, true) }));
    }

    [Fact]
    public void Reader_preserves_int_long_and_nullable_boxing_without_coercion()
    {
        Assert.Equal(7, Assert.IsType<int>(GraphIntegerExtremumReader.Read(Row(7L), typeof(int))));
        Assert.Equal(7, Assert.IsType<int>(GraphIntegerExtremumReader.Read(Row(7L), typeof(int?))));
        Assert.Equal(long.MinValue, Assert.IsType<long>(GraphIntegerExtremumReader.Read(Row(long.MinValue), typeof(long))));
        Assert.Equal(long.MaxValue, Assert.IsType<long>(GraphIntegerExtremumReader.Read(Row(long.MaxValue), typeof(long?))));
        Assert.Throws<OverflowException>(() => GraphIntegerExtremumReader.Read(Row((long)int.MaxValue + 1), typeof(int)));
        Assert.Throws<InvalidOperationException>(() => GraphIntegerExtremumReader.Read(Row("7"), typeof(int)));
    }

    [Fact]
    public void Empty_input_returns_null_only_for_nullable_results()
    {
        Assert.Null(GraphIntegerExtremumReader.Read(Row(null), typeof(int?)));
        Assert.Null(GraphIntegerExtremumReader.Read(Row(null), typeof(long?)));
        Assert.Equal("Sequence contains no elements.", Assert.Throws<InvalidOperationException>(() => GraphIntegerExtremumReader.Read(Row(null), typeof(int))).Message);
        Assert.Throws<InvalidOperationException>(() => GraphIntegerExtremumReader.Read(Row(null), typeof(long)));
    }

    [Fact]
    public void Invalid_or_absent_input_diagnostic_rejects_even_an_otherwise_valid_extremum()
    {
        foreach (var invalid in new object?[] { 1L, -1L, null, 0, "0" })
        {
            var row = Row(7L); row[CypherQueryPlan.AggregateInvalidAlias] = invalid;
            Assert.Contains("aggregate validation failed", Assert.Throws<InvalidOperationException>(() => GraphIntegerExtremumReader.Read(row, typeof(int))).Message);
        }
        var missing = Row(7L); missing.Remove(CypherQueryPlan.AggregateInvalidAlias);
        Assert.Throws<InvalidOperationException>(() => GraphIntegerExtremumReader.Read(missing, typeof(int)));
    }

    [Fact]
    public async Task Unsupported_extrema_reject_before_transport()
    {
        var driver = ProviderTestDriver.Create();
        using var db = new ProviderModelContext(driver, b => b.Entity<Item>().ToGraphNode("ExtremumItem"));
        var root = db.Set<Item>();
        await Assert.ThrowsAsync<Neo4jQueryTranslationException>(() => root.MinAsync(p => p.Text));
        await Assert.ThrowsAsync<Neo4jQueryTranslationException>(() => root.MaxAsync(p => p.Floating));
        await Assert.ThrowsAsync<Neo4jQueryTranslationException>(() => root.MaxAsync(p => p.Id));
        await Assert.ThrowsAsync<Neo4jQueryTranslationException>(() => root.MinAsync(p => p.Number + 1));
        await Assert.ThrowsAsync<Neo4jQueryTranslationException>(() => root.MinAsync(p => (int)p.Optional!));
        await Assert.ThrowsAsync<Neo4jQueryTranslationException>(() => root.Take(2).MaxAsync(p => p.Number));
        await Assert.ThrowsAsync<Neo4jQueryTranslationException>(() => root.Select(p => new { p.Number }).MaxAsync());
        Assert.Empty(((ProviderTestDriver)(object)driver).Calls);
    }

    [Fact]
    public async Task Explicit_converter_extrema_reject_before_transport()
    {
        var driver = ProviderTestDriver.Create();
        using var db = new ProviderModelContext(driver, b =>
        {
            b.Entity<Item>().ToGraphNode("ExtremumConverted");
            b.Entity<Item>().Property(p => p.Number).HasConversion<string>();
        });
        await Assert.ThrowsAsync<Neo4jQueryTranslationException>(() => db.Set<Item>().MaxAsync(p => p.Number));
        Assert.Empty(((ProviderTestDriver)(object)driver).Calls);
    }

    private static Dictionary<string, object?> Row(object? value)
        => new() { ["aggregate"] = value, [CypherQueryPlan.AggregateInvalidAlias] = 0L };
    public sealed class Item
    {
        public Guid Id { get; set; }
        public int Number { get; set; }
        public int? Optional { get; set; }
        public string? Text { get; set; }
        public double Floating { get; set; }
    }
}
