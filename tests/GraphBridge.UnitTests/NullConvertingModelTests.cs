using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace GraphBridge.UnitTests;

public sealed class NullConvertingModelTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Null_converting_node_or_identified_edge_payload_rejects_during_model_finalization(bool edgePayload)
    {
        // Deliberately exercise EF's experimental null-converting constructor so the provider
        // rejects unsupported semantics instead of silently bypassing the converter for null.
#pragma warning disable EF1001
        var converter = new ValueConverter<string?, string?>(
            value => value ?? "<null>", value => value == "<null>" ? null : value, convertsNulls: true);
#pragma warning restore EF1001
        Assert.True(converter.ConvertsNulls);
        Assert.Equal("<null>", converter.ConvertToProvider(null));
        Assert.Null(converter.ConvertFromProvider("<null>"));
        var driver = ProviderTestDriver.Create();
        using var context = new ProviderModelContext(driver, model =>
        {
            var node = model.Entity<Node>().ToGraphNode("NullConvertingNode");
            if (!edgePayload)
            {
                node.Property(value => value.Note).HasConversion(converter);
                return;
            }

            var edge = model.Entity<Edge>().ToGraphIdentifiedRelationship("NULL_CONVERTING_EDGE", nameof(Edge.Id),
                [nameof(Edge.SourceId)], [nameof(Edge.TargetId)]);
            edge.HasOne<Node>().WithMany().HasForeignKey(value => value.SourceId).OnDelete(DeleteBehavior.Restrict);
            edge.HasOne<Node>().WithMany().HasForeignKey(value => value.TargetId).OnDelete(DeleteBehavior.Restrict);
            edge.Property(value => value.Note).HasConversion(converter);
        });

        var error = Assert.Throws<InvalidOperationException>(() => context.Model);

        Assert.Contains("ConvertsNulls", error.Message);
        Assert.Contains(nameof(Node.Note), error.Message);
        Assert.Empty(((ProviderTestDriver)(object)driver).Calls);
    }

    public sealed class Node
    {
        public Guid Id { get; set; }
        public string? Note { get; set; }
    }

    public sealed class Edge
    {
        public Guid Id { get; set; }
        public Guid SourceId { get; set; }
        public Guid TargetId { get; set; }
        public string? Note { get; set; }
    }
}
