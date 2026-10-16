using GraphBridge.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Neo4j.Driver;

namespace GraphBridge.UnitTests;

public sealed class KeyConversionTests : IDisposable
{
    private readonly IDriver _driver = ProviderTestDriver.Create();
    public void Dispose() => Assert.Empty(((ProviderTestDriver)(object)_driver).Calls);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Lossy_node_or_association_key_conversion_rejects_model_identity_change(bool association)
    {
        var converter = new ValueConverter<string, string>(value => value.ToLowerInvariant(), value => value);
        using var context = Context(converter);
        var property = Key(context, association);
        Assert.Contains("round-trip", Assert.Throws<InvalidOperationException>(() => property.ToStore("MixedCase")).Message);
        Assert.Equal("canonical", property.ToStore("canonical"));
        // Payload values may deliberately normalize; this identity restriction is not imposed on them.
        var payload = context.GetService<GraphBindingCompiler>().GetNode(context.Model.FindEntityType(typeof(Source))!)
            .Properties.Single(p => p.Property.Name == nameof(Source.Note));
        Assert.Equal("mixedcase", payload.ToStore("MixedCase"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Reversible_node_and_inherited_association_key_converter_preserves_identity(bool association)
    {
        var converter = new ValueConverter<string, string>(value => "prefix:" + value, value => value.Substring(7));
        using var context = Context(converter);
        var property = Key(context, association);
        Assert.Equal("prefix:MixedCase", property.ToStore("MixedCase"));
        Assert.Equal("MixedCase", property.FromStore("prefix:MixedCase"));
    }

    private ProviderModelContext Context(ValueConverter<string, string> converter) => new(_driver, model =>
    {
        var source = model.Entity<Source>().ToGraphNode("KeySource");
        source.Property(e => e.Id).HasConversion(converter);
        source.Property(e => e.Note).HasConversion(converter);
        model.Entity<Target>().ToGraphNode("KeyTarget");
        var join = model.Entity<Join>();
        join.HasKey(e => new { e.SourceId, e.TargetId });
        join.HasOne<Source>().WithMany().HasForeignKey(e => e.SourceId);
        join.HasOne<Target>().WithMany().HasForeignKey(e => e.TargetId);
        join.ToGraphRelationship("KEY_ASSOCIATION", [nameof(Join.SourceId)], [nameof(Join.TargetId)]);
    });

    private static GraphPropertyBinding Key(DbContext context, bool association)
    {
        var compiler = context.GetService<GraphBindingCompiler>();
        return association
            ? Assert.Single(compiler.GetAssociation(context.Model.FindEntityType(typeof(Join))!).Source.ForeignKeyProperties)
            : compiler.GetNode(context.Model.FindEntityType(typeof(Source))!).Properties.Single(p => p.Property.Name == nameof(Source.Id));
    }

    public sealed class Source { public string Id { get; set; } = ""; public string Note { get; set; } = ""; }
    public sealed class Target { public Guid Id { get; set; } }
    public sealed class Join { public string SourceId { get; set; } = ""; public Guid TargetId { get; set; } }
}
