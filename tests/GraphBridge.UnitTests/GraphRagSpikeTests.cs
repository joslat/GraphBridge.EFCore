using Microsoft.EntityFrameworkCore;

namespace GraphBridge.UnitTests;

// GraphRAG wave 0 spike (GR-31), question (a), recorded in ADR 0052. Kept as a guard: if a provider
// change lets mention classes share a relationship type, this fails and ADR 0052's mention mapping is revisited.
public sealed class GraphRagSpikeTests
{
    [Fact]
    public void Two_identified_mention_classes_cannot_share_one_relationship_type_but_one_type_each_works()
    {
        using var shared = Context("MENTIONED_IN", "MENTIONED_IN");
        Assert.Contains("exactly one EF entity owner", Assert.Throws<InvalidOperationException>(() => shared.Model).Message);

        using var separate = Context("MENTIONED_IN_PERSON", "MENTIONED_IN_PLACE");
        Assert.NotNull(separate.Model.FindEntityType(typeof(SpikeMention<SpikePerson>)));
        Assert.NotNull(separate.Model.FindEntityType(typeof(SpikeMention<SpikePlace>)));
    }

    [Fact]
    public void The_lexical_layer_builds_with_one_native_reference_per_node_and_association_edges_for_the_rest()
    {
        using var lexical = new ProviderModelContext(ProviderTestDriver.Create(), b =>
        {
            Lexical(b);
            var inSection = b.Entity<SpikeChunkSection>();
            inSection.HasKey(e => new { e.ChunkId, e.SectionId });
            inSection.HasOne<SpikeLexicalChunk>().WithMany().HasForeignKey(e => e.ChunkId);
            inSection.HasOne<SpikeSection>().WithMany().HasForeignKey(e => e.SectionId);
            inSection.ToGraphRelationship("IN_SECTION", [nameof(SpikeChunkSection.ChunkId)], [nameof(SpikeChunkSection.SectionId)]);
            var next = b.Entity<SpikeNextChunk>();
            next.HasKey(e => new { e.ChunkId, e.NextChunkId });
            next.HasOne<SpikeLexicalChunk>().WithMany().HasForeignKey(e => e.ChunkId);
            next.HasOne<SpikeLexicalChunk>().WithMany().HasForeignKey(e => e.NextChunkId);
            next.ToGraphRelationship("NEXT_CHUNK", [nameof(SpikeNextChunk.ChunkId)], [nameof(SpikeNextChunk.NextChunkId)]);
        });
        Assert.NotNull(lexical.Model.FindEntityType(typeof(SpikeNextChunk)));

        // The design's first table: a second native reference on the chunk is refused.
        using var twoReferences = new ProviderModelContext(ProviderTestDriver.Create(), b =>
        {
            Lexical(b);
            b.Entity<SpikeLexicalChunk>().HasOne<SpikeSection>().WithMany().HasForeignKey(c => c.SectionId)
                .OnDelete(DeleteBehavior.Restrict).ToGraphReference("IN_SECTION");
        });
        Assert.Contains("only one native node-reference foreign key", Assert.Throws<InvalidOperationException>(() => twoReferences.Model).Message);
    }

    private static void Lexical(ModelBuilder b)
    {
        b.Entity<SpikeDocument>().ToGraphNode("SpikeDocument").Property(d => d.Id).ValueGeneratedNever();
        b.Entity<SpikeSection>().ToGraphNode("SpikeSection").Property(s => s.Id).ValueGeneratedNever();
        b.Entity<SpikeSection>().HasOne<SpikeDocument>().WithMany().HasForeignKey(s => s.DocumentId).IsRequired()
            .OnDelete(DeleteBehavior.Restrict).ToGraphReference("SECTION_OF");
        b.Entity<SpikeLexicalChunk>().ToGraphNode("SpikeLexicalChunk").Property(c => c.Id).ValueGeneratedNever();
        b.Entity<SpikeLexicalChunk>().HasOne<SpikeDocument>().WithMany().HasForeignKey(c => c.DocumentId).IsRequired()
            .OnDelete(DeleteBehavior.Restrict).ToGraphReference("IN_DOCUMENT");
    }

    private static ProviderModelContext Context(string personType, string placeType) => new(ProviderTestDriver.Create(), b =>
    {
        b.Entity<SpikeChunk>().ToGraphNode("SpikeChunk").Property(c => c.Id).ValueGeneratedNever();
        b.Entity<SpikePerson>().ToGraphNode("SpikePerson").Property(p => p.Id).ValueGeneratedNever();
        b.Entity<SpikePlace>().ToGraphNode("SpikePlace").Property(p => p.Id).ValueGeneratedNever();
        Mention<SpikePerson>(b, personType);
        Mention<SpikePlace>(b, placeType);
    });

    private static void Mention<T>(ModelBuilder b, string relationshipType) where T : class
    {
        var mention = b.Entity<SpikeMention<T>>();
        mention.Property(m => m.Id).ValueGeneratedNever();
        mention.HasOne<T>().WithMany().HasForeignKey(m => m.EntityId);
        mention.HasOne<SpikeChunk>().WithMany().HasForeignKey(m => m.ChunkId);
        mention.ToGraphIdentifiedRelationship(relationshipType, nameof(SpikeMention<T>.Id),
            [nameof(SpikeMention<T>.EntityId)], [nameof(SpikeMention<T>.ChunkId)]);
    }

    public sealed class SpikeChunk { public Guid Id { get; set; } public string Text { get; set; } = ""; }
    public sealed class SpikeDocument { public string Id { get; set; } = ""; }
    public sealed class SpikeSection { public string Id { get; set; } = ""; public string DocumentId { get; set; } = ""; }
    public sealed class SpikeLexicalChunk { public string Id { get; set; } = ""; public string DocumentId { get; set; } = ""; public string? SectionId { get; set; } public string Text { get; set; } = ""; }
    public sealed class SpikeChunkSection { public string ChunkId { get; set; } = ""; public string SectionId { get; set; } = ""; }
    public sealed class SpikeNextChunk { public string ChunkId { get; set; } = ""; public string NextChunkId { get; set; } = ""; }
    public sealed class SpikePerson { public Guid Id { get; set; } public string Name { get; set; } = ""; }
    public sealed class SpikePlace { public Guid Id { get; set; } public string Name { get; set; } = ""; }

    public sealed class SpikeMention<T> where T : class
    {
        public Guid Id { get; set; }
        public Guid EntityId { get; set; }
        public Guid ChunkId { get; set; }
        public int StartByte { get; set; }
        public int EndByte { get; set; }
        public string Quote { get; set; } = "";
    }
}
