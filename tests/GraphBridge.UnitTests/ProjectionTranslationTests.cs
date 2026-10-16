using GraphBridge.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore;

namespace GraphBridge.UnitTests;

public sealed class ProjectionTranslationTests
{
    [Fact]
    public void DTO_record_initializer_nested_and_duplicate_fields_select_only_required_columns()
    {
        var driver = ProviderTestDriver.Create();
        using var db = Context(driver);
        const string expected = "MATCH (n:`ProjectionPerson`)\nRETURN n.`Age` AS `c1`, n.`Name` AS `c2`";
        Assert.Equal(expected, db.Set<Person>().Select(p => new Summary(p.Name, p.Age)).ToQueryString());
        Assert.Equal(expected, db.Set<Person>().Select(p => new MutableSummary { Name = p.Name, Age = p.Age }).ToQueryString());
        Assert.Equal(expected, db.Set<Person>().Select(p => new { p.Name, Other = p.Name, Nested = new { p.Age } }).ToQueryString());
        Assert.Empty(((ProviderTestDriver)(object)driver).Calls);
    }

    [Fact]
    public void Constant_empty_and_captured_scalar_results_use_row_witness_without_wire_values()
    {
        var driver = ProviderTestDriver.Create();
        using var db = Context(driver);
        var secret = "private-captured-value";
        const string expected = "MATCH (n:`ProjectionPerson`)\nRETURN 1 AS __gb_row";
        Assert.Equal(expected, db.Set<Person>().Select(p => new { Text = secret }).ToQueryString());
        Assert.Equal(expected, db.Set<Person>().Select(p => new { }).ToQueryString());
        Assert.Equal(expected, db.Set<Person>().Select(p => true).ToQueryString());
        var captured = new Summary(secret, 42);
        Assert.Equal(expected, db.Set<Person>().Select(p => captured).ToQueryString());
        Assert.Empty(((ProviderTestDriver)(object)driver).Calls);
    }

    [Fact]
    public void Entity_member_in_DTO_retains_full_EF_materialization_projection()
    {
        var driver = ProviderTestDriver.Create();
        using var db = Context(driver);
        Assert.Equal(db.Set<Person>().ToQueryString(), db.Set<Person>().Select(p => new { p.Name, Entity = p }).ToQueryString());
        Assert.Empty(((ProviderTestDriver)(object)driver).Calls);
    }

    [Fact]
    public void Unsupported_computed_projection_and_predicate_never_execute_client_code()
    {
        var driver = ProviderTestDriver.Create();
        using var db = Context(driver);
        Assert.Throws<Neo4jQueryTranslationException>(() => db.Set<Person>().Select(p => new { Value = Dangerous(p.Name) }).ToQueryString());
        Assert.Throws<Neo4jQueryTranslationException>(() => db.Set<Person>().Select(p => new { Value = p.Age + 1 }).ToQueryString());
        Assert.Throws<Neo4jQueryTranslationException>(() => db.Set<Person>().Select(p => new { Value = p.Name }).Where(p => Dangerous(p.Value) == "x").ToQueryString());
        Assert.Empty(((ProviderTestDriver)(object)driver).Calls);
    }

    [Fact]
    public async Task Contains_over_single_field_DTO_does_not_inherit_scalar_null_equality()
    {
        var driver = ProviderTestDriver.Create();
        using var db = Context(driver);
        var query = db.Set<Person>().Select(p => new { p.Name });
        var error = await Assert.ThrowsAsync<Neo4jQueryTranslationException>(() => query.ContainsAsync(null!));
        Assert.Contains("direct scalar Select", error.Message);
        await Assert.ThrowsAsync<Neo4jQueryTranslationException>(() => query.ContainsAsync(new { Name = (string?)"value" }));
        Assert.Empty(((ProviderTestDriver)(object)driver).Calls);
    }

    private static ProviderModelContext Context(Neo4j.Driver.IDriver driver)
        => new(driver, b => b.Entity<Person>().ToGraphNode("ProjectionPerson"));
    private static string Dangerous(string? value) => throw new InvalidOperationException("Must not execute.");
    public sealed class Person { public Guid Id { get; set; } public int Age { get; set; } public string? Name { get; set; } }
    public sealed record Summary(string? Name, int Age);
    public sealed class MutableSummary { public string? Name { get; set; } public int Age { get; set; } }
}
