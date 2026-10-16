using System.Globalization;
using System.Text.Json;
using GraphBridge.Tooling.Generation;
using GraphBridge.Tooling.Semantics;

namespace GraphBridge.ToolingTests;

public sealed class SemanticsTests
{
    [Fact]
    public void Declared_vocabulary_and_explicit_policy_resolve_nodes_identified_edge_and_calendar_conversion()
    {
        var result = OntologyCompiler.Compile(SemanticFixture.Ontology, SemanticFixture.Policy);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var model = Assert.IsType<GraphGenerationModel>(result.Model);
        Assert.Equal("Generated.Organization", model.Namespace);
        Assert.Equal("OrganizationContext", model.ContextName);
        Assert.Equal(2, model.Nodes.Count);
        var person = Assert.Single(model.Nodes, n => n.ClrName == "Person");
        Assert.Equal("https://example.org/org#Person", person.Iri);
        Assert.Equal(["Id"], person.KeyProperties);
        Assert.Equal(GraphScalarConversion.GuidString, Assert.Single(person.Properties, p => p.ClrName == "Id").Conversion);
        var edge = Assert.Single(model.IdentifiedEdges);
        Assert.Equal("WORKS_FOR", edge.RelationshipType);
        Assert.Equal("EmploymentId", edge.KeyProperty);
        Assert.Equal(GraphEdgeDirection.SourceToTarget, edge.Direction);
        Assert.Equal("Person", edge.Source.NodeClrName);
        Assert.Equal("Organization", edge.Target.NodeClrName);
        Assert.Equal(["PersonId"], edge.Source.ForeignKeyProperties);
        Assert.Equal(GraphDeletionPolicy.Restrict, edge.Source.Deletion);
        var date = Assert.Single(edge.Properties, p => p.ClrName == "ValidFrom");
        Assert.Equal(GraphScalarType.DateOnly, date.ScalarType);
        Assert.Equal(GraphScalarConversion.DateOnlyIsoString, date.Conversion);
        Assert.Contains(result.Diagnostics, d => d.Code == "IdentifiedEdgeReification");
        Assert.Contains(result.Diagnostics, d => d.Code == "CalendarDateConversion");
        Assert.Contains(result.Diagnostics, d => d.Code == "PolicyPropertyEnrichment");
        Assert.Matches("^[0-9a-f]{64}$", result.SourceFingerprint);
        Assert.Throws<NotSupportedException>(() => ((IList<GraphGenerationDiagnostic>)result.Diagnostics).Clear());
    }

    [Fact]
    public void Triple_order_prefix_spelling_and_culture_do_not_change_resolved_model_or_diagnostics()
    {
        var baseline = OntologyCompiler.Compile(SemanticFixture.Ontology, SemanticFixture.Policy);
        var lines = SemanticFixture.Ontology.Split('\n');
        var reordered = string.Join('\n', lines.Where(l => l.StartsWith("@", StringComparison.Ordinal))
            .Concat(lines.Where(l => !l.StartsWith("@", StringComparison.Ordinal)).Reverse()));
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            var second = OntologyCompiler.Compile(reordered.Replace("ex:", "other:", StringComparison.Ordinal),
                SemanticFixture.Policy.Replace("ex:", "other:", StringComparison.Ordinal));
            Assert.True(second.Success, string.Join("\n", second.Diagnostics));
            Assert.Equal(JsonSerializer.Serialize(baseline.Model), JsonSerializer.Serialize(second.Model));
            Assert.Equal(baseline.Diagnostics, second.Diagnostics);
            Assert.NotEqual(baseline.SourceFingerprint, second.SourceFingerprint);
        }
        finally { CultureInfo.CurrentCulture = originalCulture; }
    }

    [Fact]
    public void Provenance_fingerprint_normalizes_line_endings_but_changes_with_semantic_inputs()
    {
        var first = OntologyCompiler.Compile(SemanticFixture.Ontology, SemanticFixture.Policy, SemanticFixture.Shapes);
        var second = OntologyCompiler.Compile(SemanticFixture.Ontology.Replace("\n", "\r\n", StringComparison.Ordinal),
            SemanticFixture.Policy.Replace("\n", "\r", StringComparison.Ordinal), SemanticFixture.Shapes.Replace("\n", "\r\n", StringComparison.Ordinal));
        Assert.Equal(first.SourceFingerprint, second.SourceFingerprint);
        Assert.NotEqual(first.SourceFingerprint, OntologyCompiler.Compile(SemanticFixture.Ontology,
            SemanticFixture.Policy.Replace("storeName: role", "storeName: position", StringComparison.Ordinal), SemanticFixture.Shapes).SourceFingerprint);
    }

    [Fact]
    public void Domain_range_and_SHACL_minimum_do_not_implicitly_make_EF_properties_required()
    {
        var result = OntologyCompiler.Compile(SemanticFixture.Ontology, SemanticFixture.Policy, SemanticFixture.Shapes);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var name = Assert.Single(Assert.Single(result.Model!.Nodes, n => n.ClrName == "Person").Properties, p => p.ClrName == "Name");
        Assert.False(name.IsRequired);
        Assert.Contains(result.Diagnostics, d => d.Code == "ShaclSeparateValidation");
        Assert.Contains(result.Diagnostics, d => d.Code == "SemanticBoundary");
    }

    [Theory]
    [InlineData("contractVersion: 1", "contractVersion: 2", "PolicyVersion")]
    [InlineData("direction: outgoing", "direction: incoming", "EdgeDirection")]
    [InlineData("deletion: restrict", "deletion: cascade", "DeletionPolicy")]
    [InlineData("storage: node", "storage: table", "NodeStorage")]
    [InlineData("storage: identified-edge", "storage: association-edge", "EdgeStorage")]
    [InlineData("type: guid", "type: decimal", "UnsupportedScalar")]
    [InlineData("conversion: guid-string", "conversion: System.IO.File.Delete", "UnsupportedConversion")]
    [InlineData("conversion: guid-string", "conversion: native", "ScalarConversion")]
    [InlineData("keyProperties: [Id]", "keyProperties: []", "ExplicitIdentity")]
    [InlineData("keyProperty: EmploymentId", "keyProperty: PersonId", "EdgeIdentityOverlap")]
    [InlineData("entity: ex:Organization", "entity: ex:Missing", "UnmappedEndpoint")]
    [InlineData("foreignKeyProperties: [PersonId]", "foreignKeyProperties: []", "EndpointKeyArity")]
    [InlineData("keyProperty: EmploymentId", "keyProperty: Missing", "RequiredKeyProperty")]
    public void Unsupported_or_incomplete_persistence_decisions_are_errors(string before, string after, string code)
    {
        var result = OntologyCompiler.Compile(SemanticFixture.Ontology, SemanticFixture.Policy.Replace(before, after, StringComparison.Ordinal));
        Assert.False(result.Success);
        Assert.Null(result.Model);
        Assert.Contains(result.Diagnostics, d => d.Code == code && d.Severity == GraphGenerationSeverity.Error);
    }

    [Theory]
    [InlineData("contextName: OrganizationContext\n", "")]
    [InlineData("direction: outgoing\n", "")]
    [InlineData("deletion: restrict\n", "")]
    [InlineData("required: false", "wrongRequired: false")]
    [InlineData("contractVersion: 1", "contractVersion: 1\ncontractVersion: 1")]
    public void Unknown_duplicate_and_missing_YAML_members_are_rejected(string before, string after)
    {
        var result = OntologyCompiler.Compile(SemanticFixture.Ontology, SemanticFixture.Policy.Replace(before, after, StringComparison.Ordinal));
        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, d => d.Code == "InvalidPolicyYaml");
    }

    [Theory]
    [InlineData("contextName: OrganizationContext", "contextName: &alias OrganizationContext")]
    [InlineData("contextName: OrganizationContext", "contextName: !!str OrganizationContext")]
    public void YAML_anchors_and_explicit_tags_are_outside_the_policy_format(string before, string after)
    {
        var result = OntologyCompiler.Compile(SemanticFixture.Ontology, SemanticFixture.Policy.Replace(before, after, StringComparison.Ordinal));
        Assert.Contains(result.Diagnostics, d => d.Code == "PolicyAliasesOrTags");
        Assert.False(result.Success);
    }

    [Fact]
    public void Policy_QNames_require_declared_prefixes_and_duplicate_expanded_IRIs_fail()
    {
        var unresolved = OntologyCompiler.Compile(SemanticFixture.Ontology, SemanticFixture.Policy.Replace("ex:Person:", "missing:Person:", StringComparison.Ordinal));
        Assert.Contains(unresolved.Diagnostics, d => d.Code == "UnresolvedPolicyIri");
        var aliasOntology = "@prefix alias: <https://example.org/org#> .\n" + SemanticFixture.Ontology;
        var duplicatePolicy = SemanticFixture.Policy.Replace("  ex:Organization:", "  alias:Person:", StringComparison.Ordinal);
        var duplicate = OntologyCompiler.Compile(aliasOntology, duplicatePolicy);
        Assert.Contains(duplicate.Diagnostics, d => d.Code == "DuplicatePolicyIri");
        Assert.False(duplicate.Success);
    }

    [Fact]
    public void Declared_prefix_cannot_reinterpret_an_explicit_absolute_policy_IRI()
    {
        var ontology = SemanticFixture.Ontology + "\n@prefix https: <https://other.example/> .\n<https://other.example///example.org/org#Person> a owl:Class .";
        var policy = SemanticFixture.Policy.Replace("ex:Person", "https://example.org/org#Person", StringComparison.Ordinal);
        var result = OntologyCompiler.Compile(ontology, policy);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Equal("https://example.org/org#Person", Assert.Single(result.Model!.Nodes, n => n.ClrName == "Person").Iri);
    }

    [Fact]
    public void Multiple_domain_or_range_statements_are_intersections_and_never_arbitrarily_selected()
    {
        foreach (var predicate in new[] { "domain", "range" })
        {
            var result = OntologyCompiler.Compile(SemanticFixture.Ontology + $"\nex:worksFor rdfs:{predicate} ex:Other .\n", SemanticFixture.Policy);
            Assert.False(result.Success);
            Assert.Contains(result.Diagnostics, d => d.Code == "AmbiguousDomainRange");
        }
        var mismatch = OntologyCompiler.Compile(SemanticFixture.Ontology.Replace("rdfs:range ex:Organization", "rdfs:range ex:Person", StringComparison.Ordinal), SemanticFixture.Policy);
        Assert.Contains(mismatch.Diagnostics, d => d.Code == "EndpointPolicyMismatch");
    }

    [Fact]
    public void Missing_explicit_declaration_cannot_be_recovered_by_inference_or_imports()
    {
        var ontology = SemanticFixture.Ontology.Replace("ex:Person a owl:Class .", "ex:Person rdfs:subClassOf ex:Organization .", StringComparison.Ordinal)
            + "\n<urn:ontology> owl:imports <http://127.0.0.1:1/never-fetch> .";
        var result = OntologyCompiler.Compile(ontology, SemanticFixture.Policy);
        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, d => d.Code == "MissingDeclaration");
        Assert.Contains(result.Diagnostics, d => d.Code == "UnsupportedOntologyConstruct" && d.Message.Contains("imports", StringComparison.Ordinal));
    }

    [Fact]
    public void Unsupported_restrictions_report_stable_diagnostics_without_blank_node_identifiers()
    {
        var first = OntologyCompiler.Compile(SemanticFixture.Ontology + "\nex:Person rdfs:subClassOf [ a owl:Restriction; owl:onProperty ex:name; owl:minCardinality 1 ] .", SemanticFixture.Policy);
        var second = OntologyCompiler.Compile(SemanticFixture.Ontology + "\n_:other a owl:Restriction; owl:minCardinality 1; owl:onProperty ex:name . ex:Person rdfs:subClassOf _:other .", SemanticFixture.Policy);
        Assert.True(first.Success);
        Assert.Equal(first.Diagnostics, second.Diagnostics);
        Assert.Contains(first.Diagnostics, d => d.Code == "UnsupportedOntologyConstruct" && d.Severity == GraphGenerationSeverity.Warning);
        Assert.DoesNotContain(first.Diagnostics, d => d.Subject.Contains("_:", StringComparison.Ordinal));
    }

    [Fact]
    public void Inputs_are_bounded_and_Turtle_is_parsed_by_a_real_parser()
    {
        var syntax = OntologyCompiler.Compile("not valid Turtle", SemanticFixture.Policy);
        Assert.Contains(syntax.Diagnostics, d => d.Code == "InvalidTurtle");
        var size = OntologyCompiler.Compile(new string('a', 1_048_577), SemanticFixture.Policy);
        Assert.Contains(size.Diagnostics, d => d.Code == "InputSize");
        Assert.Empty(size.SourceFingerprint);
        Assert.Empty(OntologyCompiler.Compile(SemanticFixture.Ontology, SemanticFixture.Policy, new string('a', 1_048_577)).SourceFingerprint);
        var deep = OntologyCompiler.Compile("<urn:s> <urn:p> " + string.Concat(Enumerable.Repeat("[ <urn:p> ", 33)) + "<urn:o>" + new string(']', 33) + " .", SemanticFixture.Policy);
        Assert.Contains(deep.Diagnostics, d => d.Code == "InputDepth");
        var excessive = OntologyCompiler.Compile(string.Concat(Enumerable.Repeat("<urn:s> <urn:p> <urn:o> .\n", 20_001)), SemanticFixture.Policy);
        Assert.False(excessive.Success);
        var quotedBrackets = OntologyCompiler.Compile(SemanticFixture.Ontology + "\nex:Person rdfs:label \"" + new string('[', 40) + "\" .", SemanticFixture.Policy);
        Assert.True(quotedBrackets.Success);
    }

    [Fact]
    public void Scalar_IRI_must_be_declared_and_have_compatible_supported_datatype()
    {
        var mismatch = OntologyCompiler.Compile(SemanticFixture.Ontology.Replace("ex:name a owl:DatatypeProperty; rdfs:domain ex:Person; rdfs:range xsd:string", "ex:name a owl:DatatypeProperty; rdfs:domain ex:Person; rdfs:range xsd:integer", StringComparison.Ordinal), SemanticFixture.Policy);
        Assert.Contains(mismatch.Diagnostics, d => d.Code == "DatatypePolicyMismatch");
        var missing = OntologyCompiler.Compile(SemanticFixture.Ontology, SemanticFixture.Policy.Replace("iri: ex:name", "iri: ex:absent", StringComparison.Ordinal));
        Assert.Contains(missing.Diagnostics, d => d.Code == "MissingDeclaration");
    }
}

internal static class SemanticFixture
{
    internal const string Ontology = """
        @prefix ex: <https://example.org/org#> .
        @prefix owl: <http://www.w3.org/2002/07/owl#> .
        @prefix rdfs: <http://www.w3.org/2000/01/rdf-schema#> .
        @prefix xsd: <http://www.w3.org/2001/XMLSchema#> .
        ex:Person a owl:Class .
        ex:Organization a owl:Class .
        ex:worksFor a owl:ObjectProperty; rdfs:domain ex:Person; rdfs:range ex:Organization .
        ex:name a owl:DatatypeProperty; rdfs:domain ex:Person; rdfs:range xsd:string .
        ex:role a owl:DatatypeProperty; rdfs:range xsd:string .
        ex:validFrom a owl:DatatypeProperty; rdfs:range xsd:date .
        """;

    internal const string Policy = """
        contractVersion: 1
        namespace: Generated.Organization
        contextName: OrganizationContext
        entities:
          ex:Person:
            clrType: Person
            storage: node
            label: OntologyPerson
            deletion: restrict
            keyProperties: [Id]
            properties:
              Id: {storeName: id, type: guid, required: true, conversion: guid-string}
              Name: {iri: ex:name, storeName: name, type: string, required: false, conversion: native}
          ex:Organization:
            clrType: Organization
            storage: node
            label: OntologyOrganization
            deletion: restrict
            keyProperties: [Id]
            properties:
              Id: {storeName: id, type: guid, required: true, conversion: guid-string}
        relationships:
          ex:worksFor:
            clrType: Employment
            storage: identified-edge
            relationshipType: WORKS_FOR
            direction: outgoing
            keyProperty: EmploymentId
            source:
              entity: ex:Person
              navigation: Person
              foreignKeyProperties: [PersonId]
              deletion: restrict
            target:
              entity: ex:Organization
              navigation: Organization
              foreignKeyProperties: [OrganizationId]
              deletion: restrict
            properties:
              EmploymentId: {storeName: employmentId, type: guid, required: true, conversion: guid-string}
              PersonId: {storeName: personId, type: guid, required: true, conversion: guid-string}
              OrganizationId: {storeName: organizationId, type: guid, required: true, conversion: guid-string}
              Role: {iri: ex:role, storeName: role, type: string, required: false, conversion: native}
              ValidFrom: {iri: ex:validFrom, storeName: validFrom, type: date-only, required: true, conversion: date-only-iso-string}
        """;

    internal const string Shapes = """
        @prefix ex: <https://example.org/org#> .
        @prefix sh: <http://www.w3.org/ns/shacl#> .
        @prefix xsd: <http://www.w3.org/2001/XMLSchema#> .
        ex:PersonShape a sh:NodeShape; sh:targetClass ex:Person;
          sh:property [sh:path ex:name; sh:minCount 1; sh:maxCount 1; sh:datatype xsd:string] .
        """;
}
