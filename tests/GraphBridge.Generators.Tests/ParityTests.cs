using System.Reflection;
using GraphBridge.Generators.Consumer;
using Microsoft.EntityFrameworkCore;

namespace GraphBridge.Generators.Tests;

public sealed class ParityTests
{
    [Fact]
    public void Real_analyzer_consumer_matches_expected_and_independent_manual_finalized_models_without_transport()
    {
        using var generated = new ConsumerContext(GeneratorHarness.Options());
        using var manual = new ManualContext(GeneratorHarness.Options());
        var actual = generated.Database.GetGraphSchemaManifest();
        var handwritten = manual.Database.GetGraphSchemaManifest();
        OrganizationMapping.Verify(generated.Model);
        OrganizationMapping.Verify(manual.Model);
        Assert.Equal(OrganizationMapping.ExpectedCanonicalJson, actual.CanonicalJson);
        Assert.Equal(OrganizationMapping.Fingerprint, actual.Fingerprint);
        Assert.Equal(handwritten.CanonicalJson, actual.CanonicalJson);
        Assert.Equal(handwritten.Fingerprint, actual.Fingerprint);
        Assert.Equal(new[] { "tenant", "id" }, Assert.Single(actual.Nodes, n => n.Label == "GeneratorPerson").KeyProperties);
        var edge = Assert.Single(actual.Relationships);
        Assert.Equal(new[] { "tenant", "id" }, edge.Source.KeyProperties);
        Assert.DoesNotContain(edge.Properties, p => p.Name == "personId" || p.Name == "personTenant" || p.Name == "companyId");
        Assert.Contains(actual.Caveats, c => c.Contains("opaque conversion", StringComparison.Ordinal));
        Assert.Contains(actual.Caveats, c => c.Contains("endpoint slots", StringComparison.Ordinal));
        Assert.Equal("IncrementingInt64", Assert.Single(edge.Properties, p => p.Name == "version").Concurrency.ToString());
        Assert.Equal("OriginalValue", Assert.Single(edge.Properties, p => p.Name == "role").Concurrency.ToString());
        Assert.DoesNotContain(typeof(ConsumerContext).Assembly.GetReferencedAssemblies(),
            a => a.Name!.StartsWith("Microsoft.CodeAnalysis", StringComparison.Ordinal) || a.Name == "GraphBridge.Generators" || a.Name == "GraphBridge.Tooling");
    }

    [Theory]
    [InlineData("label")]
    [InlineData("required")]
    [InlineData("extra")]
    [InlineData("key-order")]
    public void Later_runtime_customization_is_detected_against_generated_expected_fingerprint(string change)
    {
        using var context = new ChangedContext(GeneratorHarness.Options(), change);
        var error = Assert.Throws<InvalidOperationException>(() => OrganizationMapping.Verify(context.Model));
        Assert.Contains("differs from the finalized EF storage model", error.Message);
        Assert.Contains(OrganizationMapping.Fingerprint, error.Message);
    }

    [Fact]
    public void Omitted_registration_is_detected()
    {
        using var context = new MissingContext(GeneratorHarness.Options());
        Assert.Throws<InvalidOperationException>(() => OrganizationMapping.Verify(context.Model));
    }

    [Fact]
    public void Driver_generated_simple_mapping_compiles_and_matches_actual_and_renamed_manual_storage()
    {
        var source = GeneratorHarness.Minimal + """

            public sealed class TestContext(Microsoft.EntityFrameworkCore.DbContextOptions options) : Microsoft.EntityFrameworkCore.DbContext(options)
            {
                protected override void OnModelCreating(Microsoft.EntityFrameworkCore.ModelBuilder model) => Mapping.Apply(model);
            }
            """;
        var run = GeneratorHarness.Run(source);
        GeneratorHarness.AssertClean(run.Output, run.Diagnostics);
        GeneratorHarness.WithAssembly(run.Output, assembly =>
        {
            using var context = (DbContext)Activator.CreateInstance(assembly.GetType("Fixture.TestContext")!, GeneratorHarness.Options())!;
            using var manual = new SimpleManualContext(GeneratorHarness.Options());
            var mapping = assembly.GetType("Fixture.Mapping")!;
            mapping.GetMethod("Verify")!.Invoke(null, [context.Model]);
            var actual = context.Database.GetGraphSchemaManifest();
            Assert.Equal(actual.CanonicalJson, mapping.GetField("ExpectedCanonicalJson")!.GetRawConstantValue());
            Assert.Equal(actual.Fingerprint, mapping.GetField("Fingerprint")!.GetRawConstantValue());
            Assert.Equal(manual.Database.GetGraphSchemaManifest().CanonicalJson, actual.CanonicalJson);
            Assert.Contains("GeneratorItem", Constants(mapping.GetNestedType("Identifiers")!).ToArray());
        });
    }

    private static IEnumerable<string> Constants(Type type)
        => type.GetFields(BindingFlags.Public | BindingFlags.Static).Select(f => (string)f.GetRawConstantValue()!)
            .Concat(type.GetNestedTypes().SelectMany(Constants));

    private sealed class ChangedContext(DbContextOptions options, string change) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder model)
        {
            OrganizationMapping.Apply(model);
            if (change == "label") model.Entity<Person>().ToGraphNode("DifferentPerson");
            if (change == "required") model.Entity<Person>().Property(p => p.Name).IsRequired();
            if (change == "extra") model.Entity<Person>().Property<string>("Extra").HasGraphPropertyName("extra");
            if (change == "key-order")
            {
                // Use a standalone changed node: avoid asking EF to reorder a principal key already referenced by FKs.
                model.Ignore<Employment>(); model.Ignore<Company>();
                model.Entity<Person>().HasKey(p => new { p.Id, p.Tenant });
            }
        }
    }
    private sealed class MissingContext(DbContextOptions options) : DbContext(options) { }
    private sealed class SimpleManualContext(DbContextOptions options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder model)
        {
            var entity = model.Entity<ManualItem>().ToGraphNode("GeneratorItem");
            entity.HasKey(p => p.Key);
            entity.Property(p => p.Key).HasGraphPropertyName("id").ValueGeneratedNever();
            entity.Property(p => p.Caption).HasGraphPropertyName("title").IsRequired(false).ValueGeneratedNever();
        }
    }
    private sealed class ManualItem { public Guid Key { get; set; } public string? Caption { get; set; } }
    private sealed class ManualPerson { public string Tenant { get; set; } = ""; public Guid Key { get; set; } public string? Name { get; set; } public long Version { get; set; } }
    private sealed class ManualCompany { public long Key { get; set; } public bool Enabled { get; set; } }
    private sealed class ManualEmployment
    {
        public Guid Key { get; set; }
        public string PersonTenant { get; set; } = "";
        public Guid PersonId { get; set; }
        public long CompanyId { get; set; }
        public string? Role { get; set; }
        public DateOnly Since { get; set; }
        public DateOnly? Until { get; set; }
        public int? Rank { get; set; }
        public double Weight { get; set; }
        public long Version { get; set; }
        public ManualPerson Person { get; set; } = null!;
        public ManualCompany Company { get; set; } = null!;
    }
    private sealed class ManualContext(DbContextOptions options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder model)
        {
            var person = model.Entity<ManualPerson>().ToGraphNode("GeneratorPerson");
            person.HasKey(p => new { p.Tenant, p.Key });
            person.Property(p => p.Tenant).HasGraphPropertyName("tenant").IsRequired().ValueGeneratedNever();
            person.Property(p => p.Key).HasGraphPropertyName("id").ValueGeneratedNever();
            person.Property(p => p.Name).HasGraphPropertyName("name").IsRequired(false).ValueGeneratedNever();
            person.Property(p => p.Version).HasGraphPropertyName("version").IsRowVersion();
            var company = model.Entity<ManualCompany>().ToGraphNode("GeneratorCompany");
            company.HasKey(p => p.Key);
            company.Property(p => p.Key).HasGraphPropertyName("id").ValueGeneratedNever();
            company.Property(p => p.Enabled).HasGraphPropertyName("enabled").ValueGeneratedNever();
            var edge = model.Entity<ManualEmployment>();
            edge.HasKey(p => p.Key);
            edge.Property(p => p.Key).HasGraphPropertyName("id").ValueGeneratedNever();
            edge.Property(p => p.PersonTenant).HasGraphPropertyName("personTenant").IsRequired().ValueGeneratedNever();
            edge.Property(p => p.PersonId).HasGraphPropertyName("personId").ValueGeneratedNever();
            edge.Property(p => p.CompanyId).HasGraphPropertyName("companyId").ValueGeneratedNever();
            edge.Property(p => p.Role).HasGraphPropertyName("role").IsRequired(false).ValueGeneratedNever().IsConcurrencyToken();
            edge.Property(p => p.Since).HasGraphPropertyName("since").ValueGeneratedNever()
                .HasConversion(date => date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                    value => DateOnly.ParseExact(value, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
            edge.Property(p => p.Until).HasGraphPropertyName("until").ValueGeneratedNever()
                .HasConversion(new Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DateOnly, string>(
                    date => date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                    value => DateOnly.ParseExact(value, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)));
            edge.Property(p => p.Rank).HasGraphPropertyName("rank").ValueGeneratedNever();
            edge.Property(p => p.Weight).HasGraphPropertyName("weight").ValueGeneratedNever();
            edge.Property(p => p.Version).HasGraphPropertyName("version").IsRowVersion();
            edge.HasOne(p => p.Person).WithMany().HasForeignKey(p => new { p.PersonTenant, p.PersonId }).OnDelete(DeleteBehavior.Restrict).IsRequired();
            edge.HasOne(p => p.Company).WithMany().HasForeignKey(p => p.CompanyId).OnDelete(DeleteBehavior.Restrict).IsRequired();
            edge.ToGraphIdentifiedRelationship("GENERATOR_WORKS_FOR", nameof(ManualEmployment.Key),
                [nameof(ManualEmployment.PersonTenant), nameof(ManualEmployment.PersonId)], [nameof(ManualEmployment.CompanyId)]);
        }
    }
}
