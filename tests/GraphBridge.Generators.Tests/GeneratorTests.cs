using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace GraphBridge.Generators.Tests;

public sealed class GeneratorTests
{
    [Fact]
    public void Output_is_deterministic_and_does_not_execute_user_model_code()
    {
        var source = GeneratorHarness.Minimal + """

            public sealed class UntrustedContext : Microsoft.EntityFrameworkCore.DbContext
            {
                public UntrustedContext() => throw new System.InvalidOperationException("Must never run at compilation");
                protected override void OnModelCreating(Microsoft.EntityFrameworkCore.ModelBuilder model)
                    => throw new System.InvalidOperationException("Must never run at compilation");
            }
            """;
        var first = GeneratorHarness.Run(source);
        var second = GeneratorHarness.Run(source);
        GeneratorHarness.AssertClean(first.Output, first.Diagnostics);
        Assert.Equal(GeneratorHarness.MappingSource(first.Driver), GeneratorHarness.MappingSource(second.Driver));
        Assert.DoesNotContain("\r", GeneratorHarness.MappingSource(first.Driver));
        Assert.DoesNotContain(typeof(GraphMappingGenerator).Assembly.GetReferencedAssemblies(), a =>
            a.Name!.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal) || a.Name == "GraphBridge.Tooling" || a.Name == "Neo4j.Driver");
    }

    [Fact]
    public void Unchanged_and_unrelated_edits_reuse_incremental_contract_output()
    {
        var input = GeneratorHarness.Compilation(GeneratorHarness.Minimal);
        var driver = GeneratorHarness.Driver(track: true).RunGenerators(input);
        var expected = GeneratorHarness.MappingSource(driver);
        driver = driver.RunGenerators(input);
        Assert.All(driver.GetRunResult().Results.Single().TrackedSteps["GraphMappingContract"].SelectMany(step => step.Outputs),
            output => Assert.True(output.Reason is IncrementalStepRunReason.Cached or IncrementalStepRunReason.Unchanged));
        AssertCachedSourceOutput(driver);
        var unrelated = input.AddSyntaxTrees(CSharpSyntaxTree.ParseText("namespace Other; public sealed class Unrelated { public int Value { get; set; } }", GeneratorHarness.ParseOptions, "Other.cs"));
        driver = driver.RunGenerators(unrelated);
        Assert.Equal(expected, GeneratorHarness.MappingSource(driver));
        Assert.All(driver.GetRunResult().Results.Single().TrackedSteps["GraphMappingContract"].SelectMany(step => step.Outputs),
            output => Assert.True(output.Reason is IncrementalStepRunReason.Cached or IncrementalStepRunReason.Unchanged));
        AssertCachedSourceOutput(driver);
    }

    private static void AssertCachedSourceOutput(GeneratorDriver driver)
    {
        var outputs = driver.GetRunResult().Results.Single().TrackedOutputSteps.Values
            .SelectMany(steps => steps).SelectMany(step => step.Outputs).ToArray();
        Assert.NotEmpty(outputs);
        Assert.All(outputs, output => Assert.Equal(IncrementalStepRunReason.Cached, output.Reason));
    }

    [Fact]
    public void Property_order_is_irrelevant_but_storage_changes_change_the_expected_fingerprint()
    {
        var original = GeneratorHarness.Run(GeneratorHarness.Minimal);
        var lines = GeneratorHarness.Minimal.Split('\n');
        var id = Array.FindIndex(lines, line => line.Contains("public System.Guid Id", StringComparison.Ordinal));
        var title = Array.FindIndex(lines, line => line.Contains("public string? Title", StringComparison.Ordinal));
        (lines[id], lines[title]) = (lines[title], lines[id]);
        var reversed = GeneratorHarness.Run(string.Join('\n', lines));
        Assert.Equal(GeneratorHarness.MappingSource(original.Driver), GeneratorHarness.MappingSource(reversed.Driver));
        var changed = GeneratorHarness.Run(GeneratorHarness.Minimal.Replace("GeneratorItem", "ChangedItem", StringComparison.Ordinal));
        Assert.NotEqual(Fingerprint(original.Driver), Fingerprint(changed.Driver));
    }

    [Fact]
    public void Logical_renaming_and_IRI_changes_preserve_storage_fingerprint()
    {
        var first = GeneratorHarness.Run(GeneratorHarness.Minimal);
        var renamed = GeneratorHarness.Run(GeneratorHarness.Minimal.Replace("Item", "OtherItem", StringComparison.Ordinal)
            .Replace("GeneratorOtherItem", "GeneratorItem", StringComparison.Ordinal)
            .Replace("nameof(Id)", "nameof(Key)", StringComparison.Ordinal).Replace("Guid Id", "Guid Key", StringComparison.Ordinal)
            .Replace("nameof(Key))]", "nameof(Key), Iri = \"urn:changed\")]", StringComparison.Ordinal));
        GeneratorHarness.AssertClean(renamed.Output, renamed.Diagnostics);
        Assert.Equal(Fingerprint(first.Driver), Fingerprint(renamed.Driver));
        Assert.NotEqual(GeneratorHarness.MappingSource(first.Driver), GeneratorHarness.MappingSource(renamed.Driver));
    }

    [Theory]
    [InlineData("nameof(Id)", "new string[0]", "GBSG002")]
    [InlineData("nameof(Id)", "\"Missing\"", "GBSG002")]
    [InlineData("GeneratorItem", "bad-token", "GBSG004")]
    [InlineData("GraphProperty(\"title\", false)", "GraphProperty(\"id\", false)", "GBSG004")]
    [InlineData("[GraphProperty(\"title\", false)]", "", "GBSG001")]
    [InlineData("GraphProperty(\"title\", false)", "GraphProperty(\"title\", true)", "GBSG006")]
    [InlineData("Conversion = GraphConversion.GuidString", "Conversion = GraphConversion.Native", "GBSG006")]
    [InlineData("Conversion = GraphConversion.GuidString", "Conversion = GraphConversion.GuidString, Concurrency = GraphConcurrency.OriginalValue", "GBSG007")]
    [InlineData("GraphProperty(\"title\", false)", "GraphProperty(\"title\", false, Concurrency = GraphConcurrency.IncrementingInt64)", "GBSG006")]
    [InlineData("string? Title", "decimal? Title", "GBSG006")]
    [InlineData("get; set; }", "get; init; }", "GBSG006")]
    [InlineData("public static partial class Mapping", "public static class Mapping", "GBSG001")]
    [InlineData("class Mapping { }", "class Mapping { public const string Fingerprint = \"collision\"; }", "GBSG001")]
    [InlineData("typeof(Item)", "typeof(Item), typeof(Item)", "GBSG001")]
    [InlineData("public sealed class Item", "[Microsoft.EntityFrameworkCore.Keyless] public sealed class Item", "GBSG007")]
    public void Invalid_closed_contract_reports_located_diagnostics_and_emits_no_mapping(string from, string to, string code)
    {
        var run = GeneratorHarness.Run(GeneratorHarness.Minimal.Replace(from, to, StringComparison.Ordinal));
        Assert.Contains(run.Diagnostics, diagnostic => diagnostic.Id == code && diagnostic.Location.IsInSource
            && diagnostic.Location.SourceTree!.FilePath == "Model.cs");
        Assert.DoesNotContain(run.Driver.GetRunResult().Results.Single().GeneratedSources,
            source => source.HintName.EndsWith(".GraphBridge.g.cs", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Analyzer_reports_misplaced_property_attributes_without_a_model_registration()
    {
        var run = GeneratorHarness.Run("""
            #nullable enable
            using GraphBridge.Generated;
            public sealed class NotAnEntity { [GraphProperty("id", true)] public long Id { get; set; } }
            """);
        var diagnostics = await run.Output.WithAnalyzers([new GraphMappingAnalyzer()]).GetAnalyzerDiagnosticsAsync();
        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("GBSG005", diagnostic.Id);
        Assert.True(diagnostic.Location.IsInSource);
    }

    [Theory]
    [InlineData("typeof(Node), typeof(Edge)", "typeof(Edge)")]
    [InlineData("[GraphTarget(nameof(TargetId))]", "[GraphSource(nameof(TargetId))]")]
    [InlineData("GraphTarget(nameof(TargetId))", "GraphTarget(nameof(SourceId))")]
    [InlineData("GraphSource(nameof(SourceId))", "GraphSource(\"Missing\")")]
    [InlineData("public long SourceId", "public int SourceId")]
    [InlineData("public Node Source", "public Node? Source")]
    public void Invalid_or_unknown_endpoints_are_diagnosed(string from, string to)
    {
        var run = GeneratorHarness.Run(EdgeSource.Replace(from, to, StringComparison.Ordinal));
        Assert.Contains(run.Diagnostics, diagnostic => diagnostic.Id == "GBSG003" && diagnostic.Location.IsInSource);
        Assert.DoesNotContain(run.Driver.GetRunResult().Results.Single().GeneratedSources,
            source => source.HintName.EndsWith(".GraphBridge.g.cs", StringComparison.Ordinal));
    }

    [Fact]
    public void Self_type_identified_edge_with_distinct_roles_emits_valid_registration()
    {
        var run = GeneratorHarness.Run(EdgeSource);
        GeneratorHarness.AssertClean(run.Output, run.Diagnostics);
        Assert.Contains("ToGraphIdentifiedRelationship", GeneratorHarness.MappingSource(run.Driver));
    }

    [Fact]
    public void Same_simple_names_in_different_namespaces_have_distinct_identifier_containers()
    {
        var source = """
            #nullable enable
            using GraphBridge.Generated;
            [GraphModel(typeof(A.Node), typeof(B.Node), SchemaPolicy = GraphSchemaPolicy.ExistingConstraints)] public static partial class Mapping { }
            namespace A { [GraphNode("A", nameof(Id))] public sealed class Node { [GraphProperty("id",true)] public int Id { get; set; } } }
            namespace B { [GraphNode("B", nameof(Id))] public sealed class Node { [GraphProperty("id",true)] public int Id { get; set; } } }
            """;
        var run = GeneratorHarness.Run(source);
        GeneratorHarness.AssertClean(run.Output, run.Diagnostics);
        Assert.Contains("Label = \"A\"", GeneratorHarness.MappingSource(run.Driver));
        Assert.Contains("Label = \"B\"", GeneratorHarness.MappingSource(run.Driver));
    }

    [Fact]
    public void Keyword_names_and_IRI_literals_compile_without_escaping_ambiguity()
    {
        var source = GeneratorHarness.Minimal.Replace("namespace Fixture", "namespace @namespace", StringComparison.Ordinal)
            .Replace("class Mapping", "class @class", StringComparison.Ordinal)
            .Replace("Title", "@event", StringComparison.Ordinal)
            .Replace("GraphProperty(\"title\", false)", "GraphProperty(\"title\", false, Iri = \"urn:graphbridge:quoted\\\"value\")", StringComparison.Ordinal);
        var run = GeneratorHarness.Run(source);
        GeneratorHarness.AssertClean(run.Output, run.Diagnostics);
    }

    private static string Fingerprint(GeneratorDriver driver)
        => GeneratorHarness.MappingSource(driver).Split('\n').Single(line => line.Contains("const string Fingerprint", StringComparison.Ordinal));
    internal const string EdgeSource = """
        #nullable enable
        using GraphBridge.Generated;
        namespace Fixture;
        [GraphModel(typeof(Node), typeof(Edge), SchemaPolicy = GraphSchemaPolicy.ExistingConstraints)] public static partial class Mapping { }
        [GraphNode("GeneratorNode", nameof(Id))]
        public sealed class Node { [GraphProperty("id", true)] public long Id { get; set; } }
        [GraphIdentifiedEdge("GENERATOR_LINK", nameof(Id))]
        public sealed class Edge
        {
            [GraphProperty("id", true)] public long Id { get; set; }
            [GraphProperty("sourceId", true)] public long SourceId { get; set; }
            [GraphProperty("targetId", true)] public long TargetId { get; set; }
            [GraphSource(nameof(SourceId))] public Node Source { get; set; } = null!;
            [GraphTarget(nameof(TargetId))] public Node Target { get; set; } = null!;
        }
        """;
}
