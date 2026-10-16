using GraphBridge.Tooling.Generation;
using GraphBridge.Tooling.Semantics;

namespace GraphBridge.ToolingTests;

public sealed class ShaclTests
{
    [Fact]
    public void Actual_SHACL_engine_reports_conforming_data_separately_from_compilation()
    {
        var result = ShaclValidator.Validate(SemanticFixture.Shapes, Data("ex:alice a ex:Person; ex:name \"Alice\" ."));
        Assert.True(result.Validated);
        Assert.True(result.Conforms);
        Assert.Empty(result.Diagnostics);
        Assert.True(OntologyCompiler.Compile(SemanticFixture.Ontology, SemanticFixture.Policy, SemanticFixture.Shapes).Success);
    }

    [Theory]
    [InlineData("ex:alice a ex:Person .", "MinCountConstraintComponent")]
    [InlineData("ex:alice a ex:Person; ex:name \"Alice\", \"Alicia\" .", "MaxCountConstraintComponent")]
    [InlineData("ex:alice a ex:Person; ex:name 7 .", "DatatypeConstraintComponent")]
    public void Real_missing_multivalued_and_wrong_datatype_values_are_nonconforming(string data, string component)
    {
        var result = ShaclValidator.Validate(SemanticFixture.Shapes, Data(data));
        Assert.True(result.Validated);
        Assert.False(result.Conforms);
        Assert.Contains(result.Diagnostics, d => d.Code == "ShaclViolation" && d.Subject == "https://example.org/org#alice"
            && d.Message.Contains(component, StringComparison.Ordinal));
        Assert.All(result.Diagnostics, d => Assert.Equal(GraphGenerationSeverity.Error, d.Severity));
    }

    [Fact]
    public void Class_and_node_kind_constraints_use_the_SHACL_engine()
    {
        var shapes = SemanticFixture.Shapes.Replace("sh:path ex:name; sh:minCount 1; sh:maxCount 1; sh:datatype xsd:string",
            "sh:path ex:worksFor; sh:minCount 1; sh:class ex:Organization; sh:nodeKind sh:IRI", StringComparison.Ordinal);
        Assert.True(ShaclValidator.Validate(shapes, Data("ex:alice a ex:Person; ex:worksFor ex:acme . ex:acme a ex:Organization .")).Conforms);
        var invalid = ShaclValidator.Validate(shapes, Data("ex:alice a ex:Person; ex:worksFor \"Acme\" ."));
        Assert.True(invalid.Validated);
        Assert.False(invalid.Conforms);
        Assert.Contains(invalid.Diagnostics, d => d.Message.Contains("NodeKindConstraintComponent", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("sh:minCount 1", "sh:minCount -1", "InvalidShaclCount")]
    [InlineData("sh:minCount 1", "sh:minCount 1, 2", "AmbiguousShaclParameter")]
    [InlineData("sh:datatype xsd:string", "sh:datatype xsd:decimal", "UnsupportedShaclDatatype")]
    [InlineData("sh:path ex:name", "sh:path [sh:inversePath ex:name]", "ShaclPath")]
    [InlineData("sh:targetClass ex:Person;", "", "ShaclTargets")]
    [InlineData("sh:minCount 1", "sh:pattern \".*\"", "UnsupportedShacl")]
    public void Unsupported_or_invalid_shapes_do_not_produce_a_misleading_conformance_result(string before, string after, string code)
    {
        var shapes = SemanticFixture.Shapes.Replace(before, after, StringComparison.Ordinal);
        var result = ShaclValidator.Validate(shapes, Data("ex:alice a ex:Person; ex:name \"Alice\" ."));
        Assert.False(result.Validated);
        Assert.Null(result.Conforms);
        Assert.Contains(result.Diagnostics, d => d.Code == code);
        Assert.False(OntologyCompiler.Compile(SemanticFixture.Ontology, SemanticFixture.Policy, shapes).Success);
    }

    [Fact]
    public void Arbitrary_SPARQL_rules_imports_and_recursive_shapes_are_not_executed()
    {
        var sparql = SemanticFixture.Shapes + "\nex:PersonShape sh:sparql [sh:select \"SELECT $this WHERE { SERVICE <http://127.0.0.1:1> {?s ?p ?o} }\"] .";
        var result = ShaclValidator.Validate(sparql, Data("ex:alice a ex:Person ."));
        Assert.False(result.Validated);
        Assert.Contains(result.Diagnostics, d => d.Code == "UnsupportedShacl");
        var recursive = SemanticFixture.Shapes + "\nex:PersonShape sh:property ex:PersonShape .";
        var recursion = ShaclValidator.Validate(recursive, Data("ex:alice a ex:Person ."));
        Assert.False(recursion.Validated);
        Assert.Contains(recursion.Diagnostics, d => d.Code == "RecursiveShaclShape");
    }

    [Fact]
    public void Empty_and_malformed_shapes_are_distinct_from_data_nonconformance()
    {
        Assert.Null(ShaclValidator.Validate("", Data("ex:alice a ex:Person .")).Conforms);
        var invalid = ShaclValidator.Validate("not Turtle", "");
        Assert.False(invalid.Validated);
        Assert.Contains(invalid.Diagnostics, d => d.Code == "InvalidTurtle");
        Assert.Null(ShaclValidator.Validate(SemanticFixture.Shapes, "not Turtle").Conforms);
    }

    [Fact]
    public void A_dual_NodeShape_and_PropertyShape_is_invalid_before_engine_execution()
    {
        var shapes = SemanticFixture.Shapes.Replace("a sh:NodeShape;", "a sh:NodeShape, sh:PropertyShape;", StringComparison.Ordinal);
        var result = ShaclValidator.Validate(shapes, Data("ex:alice a ex:Person; ex:name \"Alice\" ."));
        Assert.False(result.Validated);
        Assert.Null(result.Conforms);
        Assert.Contains(result.Diagnostics, d => d.Code == "ConflictingShaclKind");
    }

    [Fact]
    public void Report_order_is_deterministic_and_does_not_expose_internal_blank_node_names()
    {
        var first = ShaclValidator.Validate(SemanticFixture.Shapes, Data("ex:zoe a ex:Person . ex:alice a ex:Person ."));
        var second = ShaclValidator.Validate(SemanticFixture.Shapes, Data("ex:alice a ex:Person . ex:zoe a ex:Person ."));
        Assert.Equal(first.Diagnostics, second.Diagnostics);
        Assert.Equal("https://example.org/org#alice", first.Diagnostics[0].Subject);
        Assert.DoesNotContain(first.Diagnostics, d => d.Message.Contains("_:", StringComparison.Ordinal));
        Assert.Throws<NotSupportedException>(() => ((IList<GraphGenerationDiagnostic>)first.Diagnostics).Clear());
    }

    private static string Data(string body) => "@prefix ex: <https://example.org/org#> .\n" + body;
}
