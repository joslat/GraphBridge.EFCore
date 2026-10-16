using GraphBridge.EntityFrameworkCore.Schema;
using GraphBridge.Tooling.Generation;
using GraphBridge.Tooling.Scaffolding;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace GraphBridge.ToolingTests;

public sealed class ScaffoldCompilerTests
{
    [Fact]
    public void Missing_keys_produce_untracked_DTOs_without_inventing_EF_identity()
    {
        var result = GraphScaffoldCompiler.Compile(Snapshot(), new("Example", "MoviesContext"));
        Assert.True(result.Success);
        Assert.Null(result.Model);
        Assert.Contains(result.Diagnostics, d => d.Code == "ReadOnlyNoReviewedKey");
        Assert.Contains(result.Diagnostics, d => d.Code == "UnknownCardinality");
        var dto = result.Files.Single(f => f.RelativePath.EndsWith(".cs", StringComparison.Ordinal)).Content;
        Assert.Contains("IReadOnlyDictionary<string, object?> Properties", dto);
        Assert.DoesNotContain("DbSet", dto.Replace("not DbSet entities", "", StringComparison.Ordinal));
        Assert.DoesNotContain("HasKey", dto);
        Assert.DoesNotContain("CREATE", dto);
    }

    [Fact]
    public void Reviewed_unique_application_keys_emit_nodes_identified_edge_and_explicit_roles_omission()
    {
        var result = GraphScaffoldCompiler.Compile(Snapshot(), Policy());
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var model = Assert.IsType<GraphGenerationModel>(result.Model);
        Assert.Equal(2, model.Nodes.Count);
        var edge = Assert.Single(model.IdentifiedEdges);
        Assert.Equal("ACTED_IN", edge.RelationshipType);
        Assert.Equal("Person", edge.Source.NodeClrName);
        Assert.Equal("Movie", edge.Target.NodeClrName);
        Assert.Equal("id", edge.KeyProperty);
        Assert.Equal(["Sourceid"], edge.Source.ForeignKeyProperties);
        Assert.Equal(["Targetid"], edge.Target.ForeignKeyProperties);
        Assert.DoesNotContain(edge.Properties, p => p.StoreName == "roles");
        Assert.Contains(result.Diagnostics, d => d.Code == "ExplicitlyIgnoredPayload" && d.Subject == "ACTED_IN.roles");
        Assert.Contains(result.Diagnostics, d => d.Code == "KeyExistenceUnproven");
        Assert.Contains(result.Diagnostics, d => d.Code == "IdentifiedEdgeDecision");
        Assert.Contains(result.Diagnostics, d => d.Code == "ObservedParallelEdges");
        Assert.Contains("roles", result.Files.Single(f => f.RelativePath == "inference-report.md").Content);
    }

    [Fact]
    public void Generated_model_is_compiled_through_actual_EF_before_receiving_binding_fingerprint()
    {
        var proposal = GraphScaffoldCompiler.Compile(Snapshot(), Policy());
        Assert.True(proposal.Success);
        var generated = GraphArtifactGenerator.Generate(proposal.Model!, proposal.SourceFingerprint, proposal.Diagnostics);
        Assert.True(generated.Success, string.Join("\n", generated.Diagnostics));
        Assert.NotNull(generated.Fingerprint);
        Assert.NotEqual(proposal.SourceFingerprint, generated.Fingerprint);
        Assert.Contains(generated.Files, f => f.RelativePath == "graphbridge.binding.json" && f.Content.Contains("ACTED_IN", StringComparison.Ordinal));
        Assert.Contains(generated.Files, f => f.RelativePath == "GraphModelConfiguration.g.cs" && f.Content.Contains("ToGraphIdentifiedRelationship", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false, "KeyReviewRequired")]
    [InlineData(true, "MissingUniqueConstraint")]
    public void Review_and_matching_unique_constraint_are_both_mandatory(bool approve, string code)
    {
        var snapshot = Snapshot(withConstraints: !approve);
        var policy = new GraphScaffoldPolicy("Example", "Context", [new("Person", "Person", ["id"], approve)]);
        var result = GraphScaffoldCompiler.Compile(snapshot, policy);
        Assert.False(result.Success);
        Assert.Null(result.Model);
        Assert.Contains(result.Diagnostics, d => d.Code == code && d.Severity == GraphGenerationSeverity.Error);
    }

    [Fact]
    public void Existence_is_not_claimed_from_full_sample_or_unique_constraint()
    {
        var result = GraphScaffoldCompiler.Compile(Snapshot(), Policy());
        Assert.Equal(3, result.Diagnostics.Count(d => d.Code == "KeyExistenceUnproven"));
        Assert.Contains(result.Diagnostics, d => d.Code == "BoundedObservation" && d.Message.Contains("even below", StringComparison.Ordinal));
        var snapshot = Snapshot();
        var keyed = Replace(snapshot, schema: new(snapshot.Schema.Constraints.Select(c => new GraphObservedConstraint(c.Name,
            c.EntityType == "NODE" ? "NODE_KEY" : "RELATIONSHIP_KEY", c.EntityType, c.LabelsOrTypes, c.Properties)), []));
        Assert.DoesNotContain(GraphScaffoldCompiler.Compile(keyed, Policy()).Diagnostics, d => d.Code == "KeyExistenceUnproven");
    }

    [Fact]
    public void A_sample_missing_a_key_blocks_tracking_even_with_explicit_review()
    {
        var snapshot = Snapshot();
        snapshot = Replace(snapshot, nodes: snapshot.Nodes.Append(new(["Person"], new Dictionary<string, GraphScaffoldValueKind> { ["name"] = GraphScaffoldValueKind.String })));
        var result = GraphScaffoldCompiler.Compile(snapshot, Policy());
        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, d => d.Code == "ObservedMissingKey");
    }

    [Theory]
    [InlineData("elementId()")]
    [InlineData("")]
    public void Internal_identity_expressions_and_empty_keys_are_rejected(string key)
    {
        var result = GraphScaffoldCompiler.Compile(Snapshot(), new("Example", "Context", [new("Person", "Person", [key], true)]));
        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, d => d.Code == "InvalidApplicationKey");
    }

    [Fact]
    public void Sampled_lists_require_explicit_decision_and_are_never_silently_stringified()
    {
        var basePolicy = Policy();
        var result = GraphScaffoldCompiler.Compile(Snapshot(), new(basePolicy.Namespace, basePolicy.ContextName,
            basePolicy.Nodes, [new("ACTED_IN", "ActedIn", "Person", "Movie", "id", true)]));
        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, d => d.Code == "PropertyConversionRequired" && d.Subject == "ACTED_IN.roles");
        var invalidConversion = GraphScaffoldCompiler.Compile(Snapshot(), new(basePolicy.Namespace, basePolicy.ContextName,
            basePolicy.Nodes, [new("ACTED_IN", "ActedIn", "Person", "Movie", "id", true, [new("roles", "Roles", GraphScalarType.String)])]));
        Assert.False(invalidConversion.Success);
        Assert.Contains(invalidConversion.Diagnostics, d => d.Code == "IncompatibleStoredType");
    }

    [Fact]
    public void Mixed_types_are_reported_even_in_DTO_mode_and_block_unsupported_tracked_coercion()
    {
        var snapshot = Snapshot();
        snapshot = Replace(snapshot, nodes: snapshot.Nodes.Append(new(["Person"], Props(("id", GraphScaffoldValueKind.String), ("name", GraphScaffoldValueKind.Integer)))));
        var dto = GraphScaffoldCompiler.Compile(snapshot, new("Example", "Context"));
        Assert.True(dto.Success);
        Assert.Contains(dto.Diagnostics, d => d.Code == "MixedPropertyTypes" && d.Subject == "Person.name");
        var tracked = GraphScaffoldCompiler.Compile(snapshot, Policy());
        Assert.False(tracked.Success);
        Assert.Contains(tracked.Diagnostics, d => d.Code == "PropertyConversionRequired" && d.Subject == "Person.name");
    }

    [Fact]
    public void Multiple_labels_warn_and_overlapping_tracked_mappings_fail()
    {
        var snapshot = Snapshot();
        snapshot = Replace(snapshot, nodes: snapshot.Nodes.Append(new(["Movie", "Person"], Props(("id", GraphScaffoldValueKind.String)))));
        var result = GraphScaffoldCompiler.Compile(snapshot, Policy());
        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, d => d.Code == "MultipleLabels");
        Assert.Contains(result.Diagnostics, d => d.Code == "OverlappingTrackedLabels");
    }

    [Fact]
    public void Endpoint_direction_conflicts_do_not_drop_observed_relationship_shapes()
    {
        var snapshot = Snapshot();
        snapshot = Replace(snapshot, relationships: snapshot.Relationships.Append(new("ACTED_IN", ["Movie"], ["Person"], Props(("id", GraphScaffoldValueKind.String)))));
        var result = GraphScaffoldCompiler.Compile(snapshot, Policy());
        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, d => d.Code == "ConflictingEndpoints");
    }

    [Fact]
    public void Unsupported_graph_types_do_not_prevent_constraint_and_sample_inference()
    {
        var result = GraphScaffoldCompiler.Compile(Snapshot(), Policy());
        Assert.True(result.Success);
        Assert.Contains(result.Diagnostics, d => d.Code == "GraphTypeNotObserved");
    }

    [Fact]
    public void Reordering_inventory_samples_and_policy_does_not_change_bytes_or_fingerprint()
    {
        var snapshot = Snapshot();
        var policy = Policy();
        var expected = GraphScaffoldCompiler.Compile(snapshot, policy);
        var actual = GraphScaffoldCompiler.Compile(new(snapshot.Capabilities, snapshot.Schema, snapshot.Labels.Reverse(),
            snapshot.RelationshipTypes.Reverse(), snapshot.Nodes.Reverse(), snapshot.Relationships.Reverse(), snapshot.SampleLimit),
            new(policy.Namespace, policy.ContextName, policy.Nodes.Reverse(), policy.Relationships.Reverse()));
        Assert.Equal(expected.SourceFingerprint, actual.SourceFingerprint);
        Assert.Equal(expected.Files, actual.Files);
        Assert.Equal(expected.Diagnostics, actual.Diagnostics);
        Assert.Equal(GraphCSharpEmitter.Generate(expected.Model!).Files, GraphCSharpEmitter.Generate(actual.Model!).Files);
        Assert.All(actual.Files, f => Assert.DoesNotContain('\r', f.Content));
    }

    [Fact]
    public void Input_collections_are_copied_and_exposed_readonly()
    {
        var labels = new List<string> { "Person" };
        var properties = new Dictionary<string, GraphScaffoldValueKind> { ["id"] = GraphScaffoldValueKind.String };
        var sample = new GraphScaffoldNodeSample(labels, properties);
        labels.Clear(); properties.Clear();
        Assert.Equal("Person", Assert.Single(sample.Labels));
        Assert.Single(sample.Properties);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)sample.Labels).Clear());
        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, GraphScaffoldValueKind>)sample.Properties).Clear());
    }

    [Fact]
    public void Empty_sample_requires_explicit_property_type_and_never_infers_from_index()
    {
        var snapshot = Snapshot();
        snapshot = Replace(snapshot, nodes: [], schema: new([], [new("index", "ONLINE", "RANGE", "NODE", ["Person"], ["id"])]));
        var result = GraphScaffoldCompiler.Compile(snapshot, new("Example", "Context", [new("Person", "Person", ["id"], true)]));
        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, d => d.Code == "MissingUniqueConstraint");
        Assert.Contains(result.Diagnostics, d => d.Code == "PropertyConversionRequired");
    }

    [Theory]
    [InlineData("ScaffoldNodeDto")]
    [InlineData("ScaffoldRelationshipDto")]
    public void Generated_DTO_names_cannot_collide_with_reviewed_entities(string name)
    {
        var result = GraphScaffoldCompiler.Compile(Snapshot(), new("Example", "Context", [new("Person", name, ["id"], true)]));
        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, d => d.Code == "DtoNameCollision");
    }

    [Fact]
    public void Ignoring_an_identity_property_fails_without_throwing()
    {
        var result = GraphScaffoldCompiler.Compile(Snapshot(), new("Example", "Context",
            [new("Person", "Person", ["id"], true, [new("id", "Id", GraphScalarType.String, Ignore: true)])]));
        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, d => d.Code == "IgnoredKey");
    }

    [Fact]
    public void Duplicate_mappings_and_overrides_are_diagnostics_not_compiler_crashes()
    {
        var mapping = new GraphScaffoldNodeMapping("Person", "Person", ["id"], true,
            [new("id", "Id", GraphScalarType.String), new("id", "Id", GraphScalarType.String)]);
        var result = GraphScaffoldCompiler.Compile(Snapshot(), new("Example", "Context", [mapping, mapping]));
        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, d => d.Code == "DuplicateMapping");
        Assert.Contains(result.Diagnostics, d => d.Code == "DuplicatePropertyOverride");
    }

    [Fact]
    public void Duplicate_CLR_property_names_with_relationship_endpoints_are_diagnostics_not_exceptions()
    {
        var policy = Policy();
        var mapping = new GraphScaffoldNodeMapping("Person", "Person", ["id"], true,
            [new("id", "Id", GraphScalarType.String), new("legacyId", "Id", GraphScalarType.String)]);
        var result = GraphScaffoldCompiler.Compile(Snapshot(), new(policy.Namespace, policy.ContextName,
            [mapping, policy.Nodes.Single(n => n.Label == "Movie")], policy.Relationships));
        Assert.False(result.Success);
        Assert.Null(result.Model);
        Assert.Contains(result.Diagnostics, d => d.Code == "GBGEN003");
    }

    [Fact]
    public void Readonly_DTO_source_compiles_without_provider_dependency_or_warnings()
    {
        var result = GraphScaffoldCompiler.Compile(Snapshot(), new("Example.class", "Context"));
        var source = result.Files.Single(f => f.RelativePath.EndsWith(".cs", StringComparison.Ordinal));
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create("ScaffoldDtoTest",
            [CSharpSyntaxTree.ParseText(source.Content, new CSharpParseOptions(LanguageVersion.CSharp14))], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        using var bytes = new MemoryStream();
        var emitted = compilation.Emit(bytes);
        Assert.True(emitted.Success, string.Join("\n", emitted.Diagnostics));
        Assert.DoesNotContain(emitted.Diagnostics, d => d.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning);
        Assert.DoesNotContain("Microsoft.EntityFrameworkCore", source.Content);
        Assert.DoesNotContain("GraphBridge.EntityFrameworkCore", source.Content);
    }

    private static GraphScaffoldSnapshot Snapshot(bool withConstraints = true) => new(
        GraphSchemaCapabilities.Detect("2026.09.0", "community", ["5", "25"]),
        new(withConstraints ? [new("person_id", "UNIQUENESS", "NODE", ["Person"], ["id"]),
            new("movie_id", "UNIQUENESS", "NODE", ["Movie"], ["id"]),
            new("acted_id", "RELATIONSHIP_UNIQUENESS", "RELATIONSHIP", ["ACTED_IN"], ["id"])] : [], []),
        ["Person", "Movie"], ["ACTED_IN"],
        [new(["Person"], Props(("id", GraphScaffoldValueKind.String), ("name", GraphScaffoldValueKind.String))),
            new(["Movie"], Props(("id", GraphScaffoldValueKind.String), ("title", GraphScaffoldValueKind.String), ("released", GraphScaffoldValueKind.Integer)))],
        [new("ACTED_IN", ["Person"], ["Movie"], Props(("id", GraphScaffoldValueKind.String), ("roles", GraphScaffoldValueKind.StringList)), true)], 100);

    private static GraphScaffoldPolicy Policy() => new("Example.Movies", "MoviesContext",
        [new("Person", "Person", ["id"], true), new("Movie", "Movie", ["id"], true)],
        [new("ACTED_IN", "ActedIn", "Person", "Movie", "id", true, [new("roles", "Roles", GraphScalarType.String, Ignore: true)])]);

    private static Dictionary<string, GraphScaffoldValueKind> Props(params (string Name, GraphScaffoldValueKind Kind)[] values)
        => values.ToDictionary(v => v.Name, v => v.Kind, StringComparer.Ordinal);

    private static GraphScaffoldSnapshot Replace(GraphScaffoldSnapshot snapshot, GraphSchemaObservation? schema = null,
        IEnumerable<GraphScaffoldNodeSample>? nodes = null, IEnumerable<GraphScaffoldRelationshipSample>? relationships = null)
        => new(snapshot.Capabilities, schema ?? snapshot.Schema, snapshot.Labels, snapshot.RelationshipTypes,
            nodes ?? snapshot.Nodes, relationships ?? snapshot.Relationships, snapshot.SampleLimit);
}
