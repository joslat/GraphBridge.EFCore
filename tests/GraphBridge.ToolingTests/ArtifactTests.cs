using System.Reflection;
using GraphBridge.Tooling.Generation;
using GraphBridge.Tooling.Semantics;
using Microsoft.EntityFrameworkCore;
using Neo4j.Driver;

namespace GraphBridge.ToolingTests;

public sealed class ArtifactTests
{
    private static readonly string SourceHash = new('a', 64);

    [Fact]
    public void Compiled_generated_model_matches_independent_manual_finalized_EF_model()
    {
        var generated = Generate();
        using var context = new ManualContext(new DbContextOptionsBuilder()
            .UseNeo4j(DispatchProxy.Create<IDriver, GraphArtifactGenerator.OfflineDriver>()).Options);
        var manual = context.Database.GetGraphSchemaManifest();
        Assert.Equal(manual.CanonicalJson + "\n", File(generated, "graphbridge.binding.json"));
        Assert.Equal(manual.Fingerprint, generated.Fingerprint);
        Assert.Contains("CREATE CONSTRAINT", File(generated, "schema-community.cypher"));
        Assert.Contains("offline declaration", File(generated, "generation-report.md"));
    }

    [Theory]
    [InlineData("namespace", "record", "class", "event")]
    [InlineData("Example", "Context", "9-entity", "9-property")]
    [InlineData("Example", "__arglist", "__makeref", "__reftype")]
    [InlineData("Example", "Context", "__refvalue", "__arglist")]
    public void Normalized_and_keyword_names_compile_without_warnings(string ns, string context, string entity, string property)
    {
        var model = new GraphGenerationModel(ns, context,
            [new(entity, null, "ArtifactNode", [new(property, null, "id", GraphScalarType.String, true,
                GraphScalarConversion.Native)], [property], GraphDeletionPolicy.Restrict)], []);
        var result = GraphArtifactGenerator.Generate(model, SourceHash);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.NotNull(result.Fingerprint);
    }

    [Fact]
    public void Generated_types_do_not_conflict_with_unrelated_types_in_the_host_tooling_assembly()
    {
        // The tool's own assembly is in the host dependency list, but is not a dependency of emitted source.
        // Previously importing all TRUSTED_PLATFORM_ASSEMBLIES caused CS0436 here and when regenerating a loaded app.
        var model = new GraphGenerationModel("GraphBridge.Tooling.Generation", "RegeneratedContext",
            [new("GraphNodeDefinition", null, "ArtifactNode",
                [new("Id", null, "id", GraphScalarType.String, true, GraphScalarConversion.Native)],
                ["Id"], GraphDeletionPolicy.Restrict)], []);
        var result = GraphArtifactGenerator.Generate(model, SourceHash);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.NotNull(result.Fingerprint);
    }

    [Fact]
    public void Organization_source_actually_compiles_and_finalizes_identified_edge_and_date_converter()
    {
        var compiled = OntologyCompiler.Compile(SemanticFixture.Ontology, SemanticFixture.Policy);
        Assert.True(compiled.Success, string.Join("\n", compiled.Diagnostics));
        var result = GraphArtifactGenerator.Generate(compiled.Model!, compiled.SourceFingerprint, compiled.Diagnostics);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("WORKS_FOR", File(result, "graphbridge.binding.json"));
        Assert.Contains("ApplicationId", File(result, "graphbridge.binding.json"));
        Assert.Contains("identified relationship", File(result, "generation-report.md"));
        using var manual = new ManualOrganizationContext(new DbContextOptionsBuilder()
            .UseNeo4j(DispatchProxy.Create<IDriver, GraphArtifactGenerator.OfflineDriver>()).Options);
        Assert.Equal(manual.Database.GetGraphSchemaManifest().CanonicalJson + "\n", File(result, "graphbridge.binding.json"));
        Assert.Equal(manual.Database.GetGraphSchemaManifest().Fingerprint, result.Fingerprint);
    }

    [Fact]
    public void Provenance_changes_report_but_not_finalized_storage_fingerprint()
    {
        var first = Generate();
        var second = GraphArtifactGenerator.Generate(Model(), new string('b', 64));
        Assert.True(second.Success, string.Join("\n", second.Diagnostics));
        Assert.Equal(first.Fingerprint, second.Fingerprint);
        Assert.Equal(File(first, "graphbridge.binding.json"), File(second, "graphbridge.binding.json"));
        Assert.NotEqual(File(first, "generation-report.md"), File(second, "generation-report.md"));
    }

    [Fact]
    public void Repeated_generation_and_diagnostic_order_preserve_all_bytes()
    {
        GraphGenerationDiagnostic[] diagnostics = [new("note", GraphGenerationSeverity.Warning, "same", "same"),
            new("note", GraphGenerationSeverity.Info, "same", "same")];
        var first = GraphArtifactGenerator.Generate(Model(), SourceHash, diagnostics);
        var second = GraphArtifactGenerator.Generate(Model(), SourceHash, diagnostics.Reverse());
        Assert.True(first.Success, string.Join("\n", first.Diagnostics));
        Assert.Equal(first.Files, second.Files);
        Assert.Equal(first.Diagnostics, second.Diagnostics);
    }

    [Fact]
    public void Unrecognized_target_produces_diagnostic_and_no_artifacts()
    {
        var result = GraphArtifactGenerator.Generate(Model(), SourceHash, options: new("unknown", "community"));
        Assert.False(result.Success);
        Assert.Empty(result.Files);
        Assert.Contains(result.Diagnostics, d => d.Code == "GBART002");
    }

    internal static GraphArtifactResult Generate()
    {
        var result = GraphArtifactGenerator.Generate(Model(), SourceHash);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        return result;
    }
    private static GraphGenerationModel Model() => new("Generated.Artifacts", "ArtifactContext",
        [new("Item", null, "ArtifactNode",
            [new("Id", null, "id", GraphScalarType.Guid, true, GraphScalarConversion.GuidString),
             new("Title", null, "title", GraphScalarType.String, false, GraphScalarConversion.Native)],
            ["Id"], GraphDeletionPolicy.Restrict)], []);
    private static string File(GraphArtifactResult result, string path) => Assert.Single(result.Files, f => f.RelativePath == path).Content;

    private sealed class ManualItem { public Guid Key { get; set; } public string? Caption { get; set; } }
    private sealed class ManualPerson { public Guid Id { get; set; } public string? Name { get; set; } }
    private sealed class ManualOrganization { public Guid Id { get; set; } }
    private sealed class ManualEmployment
    {
        public Guid EmploymentId { get; set; }
        public Guid PersonId { get; set; }
        public Guid OrganizationId { get; set; }
        public string? Role { get; set; }
        public DateOnly ValidFrom { get; set; }
        public ManualPerson Person { get; set; } = null!;
        public ManualOrganization Organization { get; set; } = null!;
    }
    private sealed class ManualOrganizationContext(DbContextOptions options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder model)
        {
            var person = model.Entity<ManualPerson>().ToGraphNode("OntologyPerson");
            person.HasKey(x => x.Id);
            person.Property(x => x.Id).HasGraphPropertyName("id").ValueGeneratedNever();
            person.Property(x => x.Name).HasGraphPropertyName("name").IsRequired(false).ValueGeneratedNever();
            var organization = model.Entity<ManualOrganization>().ToGraphNode("OntologyOrganization");
            organization.HasKey(x => x.Id);
            organization.Property(x => x.Id).HasGraphPropertyName("id").ValueGeneratedNever();
            var employment = model.Entity<ManualEmployment>();
            employment.HasKey(x => x.EmploymentId);
            employment.Property(x => x.EmploymentId).HasGraphPropertyName("employmentId").ValueGeneratedNever();
            employment.Property(x => x.PersonId).HasGraphPropertyName("personId").ValueGeneratedNever();
            employment.Property(x => x.OrganizationId).HasGraphPropertyName("organizationId").ValueGeneratedNever();
            employment.Property(x => x.Role).HasGraphPropertyName("role").IsRequired(false).ValueGeneratedNever();
            employment.Property(x => x.ValidFrom).HasGraphPropertyName("validFrom").ValueGeneratedNever()
                .HasConversion(date => date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                    text => DateOnly.ParseExact(text, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
            employment.HasOne(x => x.Person).WithMany().HasForeignKey(x => x.PersonId).OnDelete(DeleteBehavior.Restrict).IsRequired();
            employment.HasOne(x => x.Organization).WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict).IsRequired();
            employment.ToGraphIdentifiedRelationship("WORKS_FOR", "EmploymentId", ["PersonId"], ["OrganizationId"]);
        }
    }
    private sealed class ManualContext(DbContextOptions options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder model)
        {
            var node = model.Entity<ManualItem>().ToGraphNode("ArtifactNode");
            node.HasKey(x => x.Key);
            node.Property(x => x.Key).HasGraphPropertyName("id").ValueGeneratedNever();
            node.Property(x => x.Caption).HasGraphPropertyName("title").IsRequired(false).ValueGeneratedNever();
        }
    }
}
