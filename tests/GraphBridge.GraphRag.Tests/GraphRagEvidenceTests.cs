using System.Text;
using GraphBridge.GraphRag;

namespace GraphBridge.GraphRag.Tests;

/// <summary>Wave 2 (GR-04, GR-35, GR-05) pure parts: rank fusion keyed by identity, the evidence budget and quote location.</summary>
public sealed class GraphRagEvidenceTests
{
    [Fact]
    public void Fusion_adds_reciprocal_ranks_by_chunk_identity_and_breaks_ties_by_ID()
    {
        var fused = GraphRagFusion.Fuse(
        [
            (GraphRagRoutes.Vector, [new("b", 0.9), new("a", 0.8), new("c", 0.7)]),
            (GraphRagRoutes.FullText, [new("a", 12.5), new("d", 9.0), new("a", 1.0)])
        ]);
        Assert.Equal(["a", "b", "d", "c"], fused.Select(f => f.ChunkId));
        var a = fused[0];
        Assert.Equal(1.0 / 62 + 1.0 / 61, a.Score, 12);
        Assert.Equal([(GraphRagRoutes.Vector, 2, 0.8), (GraphRagRoutes.FullText, 1, 12.5)], a.Routes.Select(r => (r.Route, r.Rank, r.NativeScore)));
        Assert.Equal(1.0 / 61, fused[1].Score, 12);
        // Equal fused scores: the ordinal chunk ID decides, so the order never depends on input order.
        Assert.Equal(1.0 / 62, fused[2].Score, 12);
        Assert.Equal(1.0 / 63, fused[3].Score, 12);
        var ties = GraphRagFusion.Fuse([(GraphRagRoutes.Vector, [new("z", 1)]), (GraphRagRoutes.FullText, [new("y", 1)])]);
        Assert.Equal(["y", "z"], ties.Select(t => t.ChunkId));
        // Identity, never text: two different chunks with the same text stay apart.
        Assert.Equal(2, GraphRagFusion.Fuse([(GraphRagRoutes.Vector, [new("same-text-1", 1), new("same-text-2", 1)])]).Count);
    }

    [Fact]
    public void The_budget_stops_at_items_or_bytes_and_a_cut_item_keeps_an_exact_span()
    {
        var ranked = new[] { Chunk("c1", "first chunk", 0), Chunk("c2", "ü second chunk", 100), Chunk("c3", "third", 200) }
            .Select((c, i) => (c, 1.0 / (61 + i), (IReadOnlyList<GraphRagRouteRank>)[])).ToArray();
        var (items, bytes, omitted, limits) = GraphRagFusion.Budget(ranked, maxItems: 2, maxUtf8Bytes: 1_000);
        Assert.Equal(["c1", "c2"], items.Select(i => i.ChunkId));
        Assert.Equal((1, "MaxItems"), (omitted, Assert.Single(limits).Name));
        Assert.Equal(Encoding.UTF8.GetByteCount("first chunk") + Encoding.UTF8.GetByteCount("ü second chunk"), bytes);

        // 11 bytes for c1, then 2 bytes left: "ü" is 2 bytes, so the cut keeps exactly it.
        (items, bytes, omitted, limits) = GraphRagFusion.Budget(ranked, maxItems: 8, maxUtf8Bytes: 13);
        Assert.Equal(("ü", 100, 102), (items[1].Text, items[1].StartByte, items[1].EndByte));
        Assert.Equal((13, 1, "MaxUtf8Bytes"), (bytes, omitted, Assert.Single(limits).Name));
        // One byte left would split "ü": the cut moves back to a boundary and the item is left out.
        (items, _, omitted, _) = GraphRagFusion.Budget(ranked, maxItems: 8, maxUtf8Bytes: 12);
        Assert.Equal(["c1"], items.Select(i => i.ChunkId));
        Assert.Equal(2, omitted);
        Assert.Empty(GraphRagFusion.Budget(ranked, 8, 1_000).Limits);
    }

    [Fact]
    public void A_quote_is_found_with_normalised_whitespace_quotes_and_dashes_and_cites_the_stored_bytes()
    {
        var item = Item("Prefix ü text. The graph — “connects”   facts,\nnot rows. Suffix.", startByte: 1_000);
        var citation = GraphRagCitationVerifier.Locate(item, "The graph - \"connects\" facts, not rows.");
        Assert.NotNull(citation);
        Assert.Equal("The graph — “connects”   facts,\nnot rows.", citation.Quote);
        var offset = Encoding.UTF8.GetByteCount("Prefix ü text. ");
        Assert.Equal(1_000 + offset, citation.StartByte);
        Assert.Equal(citation.StartByte + Encoding.UTF8.GetByteCount(citation.Quote), citation.EndByte);
        Assert.Equal(("doc", "chunk"), (citation.DocumentId, citation.ChunkId));
        Assert.Equal("The graph — “connects”", GraphRagCitationVerifier.Locate(item, "The graph – ‘connects’".Replace('‘', '"').Replace('’', '"'))?.Quote);
    }

    [Fact]
    public void Emphasis_marks_are_ignored_and_the_citation_keeps_them_as_stored()
    {
        // GraphRAG v2, wave 7: Project Gutenberg marks italics with underscores, and a model copying the words drops them.
        var item = Item("when the Rabbit actually _took a watch out of its waistcoat-pocket_, and looked at it, and **then** hurried on");
        Assert.Equal("took a watch out of its waistcoat-pocket", GraphRagCitationVerifier.Locate(item, "took a watch out of its waistcoat-pocket")?.Quote);
        Assert.Equal("actually _took a watch", GraphRagCitationVerifier.Locate(item, "actually took a watch")?.Quote);
        Assert.Equal("looked at it, and **then** hurried on", GraphRagCitationVerifier.Locate(item, "looked at it, and then hurried on")?.Quote);
        Assert.Equal("took a watch", GraphRagCitationVerifier.Locate(item, "_took a watch")?.Quote);   // a leading mark isn't part of the span
        Assert.Null(GraphRagCitationVerifier.Locate(item, "__"));
        Assert.Null(GraphRagCitationVerifier.Locate(item, "took the watch"));
    }

    [Fact]
    public void Elisions_keep_order_and_anything_else_altered_is_rejected()
    {
        var item = Item("Alpha beta gamma. Delta epsilon zeta. Eta theta.");
        Assert.Equal("Alpha beta gamma. Delta epsilon zeta. Eta", GraphRagCitationVerifier.Locate(item, "Alpha beta ... epsilon ... Eta")?.Quote);
        Assert.Equal("beta gamma", GraphRagCitationVerifier.Locate(item, "...beta gamma…")?.Quote);
        Assert.Null(GraphRagCitationVerifier.Locate(item, "Alpha ... beta ... gamma ... Delta"));   // three elisions
        Assert.Null(GraphRagCitationVerifier.Locate(item, "epsilon ... Alpha"));                 // out of order
        Assert.Null(GraphRagCitationVerifier.Locate(item, "alpha beta gamma"));                  // case differs
        Assert.Null(GraphRagCitationVerifier.Locate(item, "Alpha beta gama"));                   // a typo
        Assert.Null(GraphRagCitationVerifier.Locate(item, "Alpha ... ... beta"));                // an empty segment
        Assert.Null(GraphRagCitationVerifier.Locate(item, "   "));
        Assert.Null(GraphRagCitationVerifier.Locate(item, "Iota"));                              // not in this chunk
    }

    [Fact]
    public void Retrieval_options_validate_bounds_and_presets_name_their_routes()
    {
        Assert.Equal(GraphRagRoutes.Vector, GraphRagRetrievers.Vector().Routes);
        Assert.Equal(GraphRagRoutes.Vector | GraphRagRoutes.FullText, GraphRagRetrievers.Hybrid().Routes);
        var expanded = GraphRagRetrievers.HybridCypher("MATCH (c) WHERE c.Id IN $chunkIds RETURN c.Id AS chunkId, c.Text AS text");
        Assert.NotNull(expanded.CypherExpansion);
        Assert.Throws<ArgumentException>(() => new GraphRagRetrievalOptions().WithCypherExpansion("RETURN 1", new Dictionary<string, object?> { ["chunkIds"] = 1 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GraphRagRetrievalOptions { Routes = 0 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new GraphRagRetrievalOptions { MaxItems = 129 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new GraphRagRetrievalOptions { MaxUtf8Bytes = 63 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new GraphRagRetrievalOptions { NeighbourChunks = 5 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new GraphRagRetrievalOptions { CandidatesPerRoute = 0 }.Validate());
        new GraphRagRetrievalOptions().Validate();
    }

    private static GraphRagChunkText Chunk(string id, string text, int start)
        => new(id, "doc", text, start, start + Encoding.UTF8.GetByteCount(text));

    private static GraphRagEvidenceItem Item(string text, int startByte = 0)
        => new("chunk", "doc", text, startByte, startByte + Encoding.UTF8.GetByteCount(text), 1, []);
}
