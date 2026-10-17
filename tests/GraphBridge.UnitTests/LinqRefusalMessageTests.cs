using GraphBridge.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore;

namespace GraphBridge.UnitTests;

/// <summary>FUT-28: a refused LINQ shape names itself in LINQ terms, carries a stable GBQ code and offers an alternative.</summary>
public sealed class LinqRefusalMessageTests
{
    [Fact]
    public void Refusals_name_the_LINQ_shape_a_code_and_an_alternative()
    {
        using var db = Context();
        var people = db.Set<Person>();
        Expect("GBQ101", "GroupBy", "Fluent Cypher", () => people.GroupBy(p => p.Name).Select(g => g.Key).ToQueryString());
        Expect("GBQ102", "Distinct", "ReturnDistinct", () => people.Select(p => p.Name).Distinct().ToQueryString());
        Expect("GBQ106", "Last or LastOrDefault", "opposite direction", () => people.OrderBy(p => p.Name).LastOrDefault());
        Expect("GBQ110", "A filter (Where, or a predicate passed to Any, First, Single or Count) after Skip or Take", "before Skip/Take", () => people.Take(3).Where(p => p.Age > 1).ToQueryString());
        Expect("GBQ120", "the method String.ToUpper as a query value", "Methods over entity members", () => people.Where(p => p.Name.ToUpper() == "ADA").ToQueryString());
        var error = Assert.Throws<Neo4jQueryTranslationException>(() => people.GroupBy(p => p.Name).Select(g => g.Key).ToQueryString());
        Assert.StartsWith("GraphBridge cannot translate GroupBy [GBQ101]. ", error.Message);
        Assert.DoesNotContain("TranslateGroupBy", error.Message);
        Assert.Equal("GBQ101", error.Code);
        Assert.Equal(error.Code, error.Data["GraphBridge.QueryDiagnostic"]);
        Assert.Null(new Neo4jQueryTranslationException("message").Code);
        Assert.Throws<ArgumentException>(() => new Neo4jQueryTranslationException("message", " "));
    }

    [Fact]
    public void Every_classified_feature_maps_to_a_code_and_unknown_features_fall_back()
    {
        Assert.Equal("GBQ100", Neo4jQueryRefusals.Classify("Unmapped query roots").Code);
        Assert.Equal("GBQ130", Neo4jQueryRefusals.Classify("Contains item must be one directly mapped scalar property").Code);
        Assert.Equal("GBQ140", Neo4jQueryRefusals.Classify("multiple Includes").Code);
        Assert.Equal("GBQ150", Neo4jQueryRefusals.Classify("Projection conversion").Code);
        Assert.Equal("GBQ160", Neo4jQueryRefusals.Classify("a bulk setter that changes a key, an endpoint or a generated property").Code);
        Assert.Equal("GBQ170", Neo4jQueryRefusals.Classify("Native-reference aggregates require preservation of per-row membership diagnostics").Code);
        // Messages that merely mention Include keep their own family.
        Assert.Equal("GBQ160", Neo4jQueryRefusals.Classify("Bulk mutations require a mapped entity root without Include or aggregation").Code);
        Assert.Equal("GBQ150", Neo4jQueryRefusals.Classify("Non-identity projections combined with Include").Code);
        Assert.Equal("GBQ140", Neo4jQueryRefusals.Classify("filtered, nested, or string Include").Code);
        Assert.Equal("GBQ140", Neo4jQueryRefusals.Classify("a reference Include whose target also has its own native parent, because its membership cannot be verified yet").Code);
        Assert.Contains("aggregate it in memory", Neo4jQueryRefusals.Classify("An aggregate after Skip or Take").Alternative);
        Assert.Contains("before the projection", Neo4jQueryRefusals.Classify("Subqueries").Alternative);
        Assert.Equal(("GBQ103", "SelectMany"), (Neo4jQueryRefusals.Classify("TranslateSelectMany").Code, Neo4jQueryRefusals.Classify("TranslateSelectMany").Shape));
    }

    private static void Expect(string code, string shape, string alternative, Action action)
    {
        var (actualCode, actualShape, message) = Refusal(action);
        Assert.Equal(code, actualCode);
        Assert.Equal(shape, actualShape);
        Assert.Contains(alternative, message, StringComparison.Ordinal);
    }

    private static (string? Code, string Shape, string Message) Refusal(Action action)
    {
        var error = Assert.Throws<Neo4jQueryTranslationException>(action);
        var code = error.Data[Neo4jQueryRefusals.DataKey] as string;
        var shape = error.Message["GraphBridge cannot translate ".Length..error.Message.IndexOf(" [", StringComparison.Ordinal)];
        return (code, shape, error.Message);
    }

    private static ProviderModelContext Context() => new(ProviderTestDriver.Create(), b =>
    {
        b.Entity<Person>().ToGraphNode("RefusalPerson");
        b.Entity<Person>().Property(p => p.Id).ValueGeneratedNever();
    });

    public sealed class Person { public int Id { get; set; } public string Name { get; set; } = ""; public int Age { get; set; } }
}
