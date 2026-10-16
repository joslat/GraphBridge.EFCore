using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace GraphBridge.Generators.Tests;

public sealed class MappingDiagnosticTests
{
    [Theory]
    [InlineData(", SchemaPolicy = GraphSchemaPolicy.ExistingConstraints", "")]
    [InlineData("GraphSchemaPolicy.ExistingConstraints", "GraphSchemaPolicy.Unspecified")]
    [InlineData("GraphSchemaPolicy.ExistingConstraints", "(GraphSchemaPolicy)999")]
    public void Missing_or_unknown_schema_policy_reports_actionable_error(string from, string to)
    {
        var run = GeneratorHarness.Run(GeneratorHarness.Minimal.Replace(from, to, StringComparison.Ordinal));
        var error = Assert.Single(run.Diagnostics);
        Assert.Equal("GBSG008", error.Id);
        Assert.Equal(DiagnosticSeverity.Error, error.Severity);
        Assert.Contains("ExistingConstraints", error.GetMessage());
        Assert.Contains("never deploys or verifies", error.GetMessage());
        Assert.Equal("Model.cs", error.Location.SourceTree!.FilePath);
        Assert.StartsWith("GraphModel(", error.Location.SourceTree.GetText().ToString(error.Location.SourceSpan));
        AssertNoMapping(run.Driver);
    }

    [Theory]
    [InlineData("string? Title", "System.Collections.Generic.List<string>? Title")]
    [InlineData("string? Title", "System.DateTime? Title")]
    [InlineData("string? Title", "decimal? Title")]
    public void Unsupported_properties_name_the_member_and_repair(string from, string to)
    {
        var run = GeneratorHarness.Run(GeneratorHarness.Minimal.Replace(from, to, StringComparison.Ordinal));
        var error = Assert.Single(run.Diagnostics);
        Assert.Equal("GBSG006", error.Id);
        Assert.Contains("Title", error.GetMessage());
        Assert.Contains("GraphIgnore", error.GetMessage());
        Assert.Contains("GraphProperty", error.Location.SourceTree!.GetText().ToString(error.Location.SourceSpan));
        AssertNoMapping(run.Driver);
    }

    [Theory]
    [InlineData("[GraphNode", "[Microsoft.EntityFrameworkCore.PrimaryKey(nameof(Id))] [GraphNode")]
    [InlineData("[GraphNode", "[Microsoft.EntityFrameworkCore.Keyless] [GraphNode")]
    [InlineData("[GraphProperty(\"id\"", "[System.ComponentModel.DataAnnotations.Key] [GraphProperty(\"id\"")]
    public void Conflicting_key_authorities_have_a_specific_located_error(string from, string to)
    {
        var run = GeneratorHarness.Run(GeneratorHarness.Minimal.Replace(from, to, StringComparison.Ordinal));
        var error = Assert.Single(run.Diagnostics);
        Assert.Equal("GBSG007", error.Id);
        Assert.Contains("Remove", error.GetMessage());
        Assert.Contains("GraphNode/GraphIdentifiedEdge", error.GetMessage());
        Assert.Equal("Model.cs", error.Location.SourceTree!.FilePath);
        AssertNoMapping(run.Driver);
    }

    [Fact]
    public void Unsupported_edge_endpoint_reports_its_location_and_required_shape()
    {
        var run = GeneratorHarness.Run(GeneratorTests.EdgeSource.Replace("public long SourceId", "public int SourceId", StringComparison.Ordinal));
        var error = Assert.Single(run.Diagnostics);
        Assert.Equal("GBSG003", error.Id);
        Assert.Contains("principal key type/conversion", error.GetMessage());
        Assert.Contains("Source", error.Location.SourceTree!.GetText().ToString(error.Location.SourceSpan));
        AssertNoMapping(run.Driver);
    }

    [Fact]
    public void Separate_file_edit_invalidates_diagnostics_and_fix_restores_identical_output()
    {
        // GraphModel itself is unchanged: the entity declaration is in another syntax tree.
        var input = GeneratorHarness.Compilation("""
            using GraphBridge.Generated;
            namespace Fixture;
            [GraphModel(typeof(Item), SchemaPolicy = GraphSchemaPolicy.ExistingConstraints)]
            public static partial class Mapping { }
            """);
        var valid = CSharpSyntaxTree.ParseText("""
            #nullable enable
            using GraphBridge.Generated;
            namespace Fixture;
            [GraphNode("Item", nameof(Id))]
            public class Item { [GraphProperty("id", true)] public long Id { get; set; } }
            """, GeneratorHarness.ParseOptions, "Entity.cs");
        input = input.AddSyntaxTrees(valid);
        var driver = GeneratorHarness.Driver(track: true).RunGenerators(input);
        var expected = GeneratorHarness.MappingSource(driver);
        var invalid = CSharpSyntaxTree.ParseText(valid.ToString().Replace("long Id", "decimal Id", StringComparison.Ordinal),
            GeneratorHarness.ParseOptions, "Entity.cs");
        var changed = input.ReplaceSyntaxTree(valid, invalid);
        driver = driver.RunGenerators(changed);
        Assert.Contains(driver.GetRunResult().Diagnostics, d => d.Id == "GBSG006" && d.Location.SourceTree!.FilePath == "Entity.cs");
        AssertNoMapping(driver);
        driver = driver.RunGenerators(changed.ReplaceSyntaxTree(invalid, valid));
        Assert.Empty(driver.GetRunResult().Diagnostics);
        Assert.Equal(expected, GeneratorHarness.MappingSource(driver));
    }

    private static void AssertNoMapping(GeneratorDriver driver)
        => Assert.DoesNotContain(driver.GetRunResult().Results.Single().GeneratedSources,
            source => source.HintName.EndsWith(".GraphBridge.g.cs", StringComparison.Ordinal));
}
