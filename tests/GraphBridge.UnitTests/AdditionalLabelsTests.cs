using System.Text.Json;
using GraphBridge.EntityFrameworkCore.Cypher;
using GraphBridge.EntityFrameworkCore.Metadata;
using GraphBridge.EntityFrameworkCore.Schema;
using GraphBridge.EntityFrameworkCore.Update;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using static GraphBridge.UnitTests.UpsertTests;

namespace GraphBridge.UnitTests;

// ADR 0055 (GR-40): additional labels are written on create and upsert, and every match of the type requires them.
public sealed class AdditionalLabelsTests
{
    [Fact]
    public void A_binding_keeps_its_own_label_and_lists_every_label_in_order()
    {
        using var context = new ProviderModelContext(ProviderTestDriver.Create(), b => b.Entity<Labelled>().ToGraphNode("Person").HasAdditionalGraphLabels("__KGBuilder__", "__Entity__"));
        var binding = context.GetService<GraphBindingCompiler>().GetNode(context.Model.FindEntityType(typeof(Labelled))!);
        Assert.Equal("Person", binding.Label);
        Assert.Equal(["__Entity__", "__KGBuilder__"], binding.AdditionalLabels);
        Assert.Equal("Person:__Entity__:__KGBuilder__", binding.Labels);
        Assert.Equal("`Person`:`__Entity__`:`__KGBuilder__`", CypherQueryRenderer.Labels(binding.Labels));
    }

    [Fact]
    public void Reads_creates_and_keyed_writes_require_every_label()
    {
        using var context = new ProviderModelContext(ProviderTestDriver.Create(), b => b.Entity<Labelled>().ToGraphNode("Person").HasAdditionalGraphLabels("__Entity__"));
        Assert.Contains("MATCH (n:`Person`:`__Entity__`)", context.Set<Labelled>().Where(p => p.Name == "Ada").ToQueryString());
        Assert.StartsWith("CREATE (n:`Person`:`__Entity__`)", CypherMutationRenderer.Create("Person:__Entity__", new Dictionary<string, object?> { ["Id"] = 1L }).Text);
        var keys = new Dictionary<string, object?> { ["Id"] = 1L };
        Assert.StartsWith("MATCH (n:`Person`:`__Entity__` {", CypherMutationRenderer.Delete("Person:__Entity__", keys, new Dictionary<string, object?>()).Text);
        // A label list is validated label by label.
        Assert.Throws<InvalidOperationException>(() => CypherQueryRenderer.Labels("Person:bad label"));
        Assert.Throws<InvalidOperationException>(() => CypherQueryRenderer.Labels("Person:"));
    }

    [Fact]
    public async Task An_upsert_merges_on_the_types_own_label_and_then_sets_the_others()
    {
        var script = new Script();
        await using var db = new LabelledUpsertContext(new DbContextOptionsBuilder<LabelledUpsertContext>().UseNeo4j(script.Driver, "neo4j").Options);
        await db.Set<UpsertPerson>().ExecuteUpsertAsync([new UpsertPerson { Id = Guid.NewGuid(), Name = "Ada" }]);
        Assert.Equal("UNWIND $__gb_upsert_rows AS row\nMERGE (n:`UpsertPerson` {`person_key`: row.key.`person_key`})\n"
            + "ON CREATE SET n += row.create\nON MATCH SET n += row.update\nSET n:`__Entity__`:`__KGBuilder__`\nRETURN count(n) AS affected", script.Commands[1]);
    }

    [Theory]
    [InlineData("bad label")]
    [InlineData("")]
    [InlineData("1st")]
    public void An_invalid_label_is_refused_when_configured(string label)
        => Assert.ThrowsAny<ArgumentException>(() => new ProviderModelContext(ProviderTestDriver.Create(),
            b => b.Entity<Labelled>().ToGraphNode("Person").HasAdditionalGraphLabels(label)).Model);

    [Fact]
    public void Repeated_or_missing_labels_are_refused_when_configured()
    {
        Assert.Throws<ArgumentException>(() => new ProviderModelContext(ProviderTestDriver.Create(),
            b => b.Entity<Labelled>().ToGraphNode("Person").HasAdditionalGraphLabels("__Entity__", "__Entity__")).Model);
        Assert.Throws<ArgumentException>(() => new ProviderModelContext(ProviderTestDriver.Create(),
            b => b.Entity<Labelled>().ToGraphNode("Person").HasAdditionalGraphLabels()).Model);
    }

    [Fact]
    public void A_label_that_names_a_type_or_repeats_the_types_own_is_refused_when_the_model_is_built()
    {
        ExpectInvalid(b =>
        {
            b.Entity<Labelled>().ToGraphNode("Person").HasAdditionalGraphLabels("Organization");
            b.Entity<Other>().ToGraphNode("Organization");
        }, "additional label 'Organization' is the label of an entity type");
        ExpectInvalid(b => b.Entity<Labelled>().ToGraphNode("Person").HasAdditionalGraphLabels("Person"), "additional label 'Person' is the label of an entity type");
        // Set directly, past the builder's checks.
        ExpectInvalid(b => b.Entity<Labelled>().ToGraphNode("Person").HasAnnotation(GraphAnnotationNames.AdditionalNodeLabels, "__Entity__:__Entity__"),
            "additional labels must be distinct");
        // GR-69: a value of another type, or an empty string, is refused rather than read as no labels.
        ExpectInvalid(b => b.Entity<Labelled>().ToGraphNode("Person").HasAnnotation(GraphAnnotationNames.AdditionalNodeLabels, new[] { "__Entity__" }),
            "additional labels must be one nonempty string");
        ExpectInvalid(b => b.Entity<Labelled>().ToGraphNode("Person").HasAnnotation(GraphAnnotationNames.AdditionalNodeLabels, ""),
            "additional labels must be one nonempty string");
        // An invalid label is refused as an invalid type label is, with ArgumentException.
        using var invalid = new ProviderModelContext(ProviderTestDriver.Create(),
            b => b.Entity<Labelled>().ToGraphNode("Person").HasAnnotation(GraphAnnotationNames.AdditionalNodeLabels, "__Entity__:bad label"));
        Assert.Contains("Graph identifiers must match", Assert.Throws<ArgumentException>(() => invalid.Model).Message);
    }

    [Fact]
    public void The_migration_journals_label_prefix_and_labels_on_relationships_are_refused()
    {
        // A type carrying a journal label would read and constrain the journal's records.
        ExpectInvalid(b => b.Entity<Labelled>().ToGraphNode("Person").HasAdditionalGraphLabels("__GraphBridgeMigration"),
            "additional label '__GraphBridgeMigration' uses the prefix '__GraphBridge'");
        ExpectInvalid(b => b.Entity<Labelled>().ToGraphNode("__GraphBridgeAudit"), "node label '__GraphBridgeAudit' uses the prefix '__GraphBridge'");
        ExpectInvalid(b =>
        {
            b.Entity<Labelled>().ToGraphNode("Person");
            b.Entity<Other>().ToGraphNode("Organization");
            b.Entity<Membership>().HasKey(m => new { m.PersonId, m.OrganizationId });
            b.Entity<Membership>().HasOne<Labelled>().WithMany().HasForeignKey(m => m.PersonId);
            b.Entity<Membership>().HasOne<Other>().WithMany().HasForeignKey(m => m.OrganizationId);
            b.Entity<Membership>().ToGraphRelationship("MEMBER_OF", [nameof(Membership.PersonId)], [nameof(Membership.OrganizationId)])
                .HasAnnotation(GraphAnnotationNames.AdditionalNodeLabels, "__Entity__");
        }, "additional labels belong on node entity types, not on relationships");
    }

    [Fact]
    public void Several_types_share_an_additional_label()
    {
        using var context = new ProviderModelContext(ProviderTestDriver.Create(), b =>
        {
            b.Entity<Labelled>().ToGraphNode("Person").HasAdditionalGraphLabels("__Entity__");
            b.Entity<Other>().ToGraphNode("Organization").HasAdditionalGraphLabels("__Entity__");
        });
        Assert.Contains("MATCH (n:`Organization`:`__Entity__`)", context.Set<Other>().ToQueryString());
    }

    [Fact]
    public void The_manifest_records_additional_labels_only_where_a_type_has_them()
    {
        using var context = new ProviderModelContext(ProviderTestDriver.Create(), b =>
        {
            b.Entity<Labelled>().ToGraphNode("Person").HasAdditionalGraphLabels("__KGBuilder__", "__Entity__");
            b.Entity<Other>().ToGraphNode("Organization");
        });
        var manifest = GraphSchemaManifest.Create(context.Model);
        using var json = JsonDocument.Parse(manifest.CanonicalJson);
        var nodes = json.RootElement.GetProperty("nodes").EnumerateArray().ToDictionary(n => n.GetProperty("label").GetString()!);
        Assert.Equal(["__Entity__", "__KGBuilder__"], nodes["Person"].GetProperty("additionalLabels").EnumerateArray().Select(l => l.GetString()));
        Assert.False(nodes["Organization"].TryGetProperty("additionalLabels", out _));
        Assert.Equal(["__Entity__", "__KGBuilder__"], manifest.Nodes.Single(n => n.Label == "Person").AdditionalLabels);
    }

    private static void ExpectInvalid(Action<ModelBuilder> configure, string expected)
    {
        using var context = new ProviderModelContext(ProviderTestDriver.Create(), configure);
        Assert.Contains(expected, Assert.Throws<InvalidOperationException>(() => context.Model).Message);
    }

    public sealed class Labelled
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
    }

    public sealed class Other
    {
        public int Id { get; set; }
    }

    public sealed class Membership
    {
        public int PersonId { get; set; }
        public int OrganizationId { get; set; }
    }

    public sealed class LabelledUpsertContext(DbContextOptions<LabelledUpsertContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder model)
        {
            var person = model.Entity<UpsertPerson>().ToGraphNode("UpsertPerson").HasAdditionalGraphLabels("__KGBuilder__", "__Entity__");
            person.Property(p => p.Id).HasGraphPropertyName("person_key").ValueGeneratedNever();
            person.Property(p => p.Mood).HasConversion<string>();
        }
    }
}
