using GraphBridge.GraphRag;

namespace GraphBridge.GraphRag.Tests;

/// <summary>GraphRAG v2, wave 8 (GR-11), offline: what a scope accepts, how it is checked, and how much an over-fetch asks for.</summary>
public sealed class GraphRagScopeTests
{
    [Fact]
    public void Scopes_take_distinct_ids_within_the_limit_and_refuse_empty_ones()
    {
        Assert.Equal("documents (2)", GraphRagScope.Documents("a", "b", "a").ToString());
        Assert.Equal("sections (1)", GraphRagScope.Sections(["s"]).ToString());
        Assert.Equal("documents (0)", GraphRagScope.Documents().ToString());
        Assert.Throws<ArgumentException>(() => GraphRagScope.Documents("a", " "));
        Assert.Throws<ArgumentOutOfRangeException>(() => GraphRagScope.Documents(Enumerable.Range(0, GraphRagScope.MaxDocuments + 1).Select(i => $"d{i}")));
        Assert.Equal(GraphRagScope.MaxDocuments, GraphRagScope.Documents(Enumerable.Range(0, GraphRagScope.MaxDocuments).Select(i => $"d{i}")).Ids.Count);

        Assert.Throws<ArgumentException>(() => GraphRagScope.Matching(new GraphRagDocumentFilter()));
        Assert.Equal("documents by metadata", GraphRagScope.Matching(new GraphRagDocumentFilter { Publisher = "NASA" }).ToString());
        Assert.Throws<ArgumentException>(() => GraphRagScope.FromCypher(" "));
        Assert.Equal("documents by Cypher", GraphRagScope.FromCypher("MATCH (d:Document {Tenant: $t}) RETURN d.Id AS documentId", new Dictionary<string, object?> { ["t"] = "x" }).ToString());
    }

    [Fact]
    public void Options_carry_the_scope_and_bound_the_exact_scoring()
    {
        var scope = GraphRagScope.Documents("a");
        var options = new GraphRagRetrievalOptions { Scope = scope, ScopeExactChunks = 0 };
        var copy = options.WithCypherExpansion("MATCH (c) WHERE c.Id IN $chunkIds RETURN c.Id AS chunkId, c.Text AS text");
        Assert.Same(scope, copy.Scope);
        Assert.Equal(0, copy.ScopeExactChunks);
        Assert.Throws<ArgumentException>(() => options.WithCypherExpansion("RETURN 1", new Dictionary<string, object?> { ["scopeDocumentIds"] = null }));
        new GraphRagRetrievalOptions { ScopeExactChunks = 100_000 }.Validate();
        Assert.Throws<ArgumentOutOfRangeException>(() => new GraphRagRetrievalOptions { ScopeExactChunks = 100_001 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new GraphRagRetrievalOptions { ScopeExactChunks = -1 }.Validate());
    }

    [Theory]
    [InlineData(20, 100, 10_000, 4_000)]     // a scope of 1% asks for 200 times the limit
    [InlineData(20, 50_000, 50_000, 40)]     // the whole graph asks for twice the limit
    [InlineData(20, 1, 1_000_000, 10_000)]   // never more than 10,000
    [InlineData(256, 10_000, 10_000, 512)]
    [InlineData(20, 0, 0, 40)]
    public void An_over_fetch_asks_for_about_twice_the_limit_inside_the_scope(int limit, int scoped, int all, int fetch)
        => Assert.Equal(fetch, GraphRagScopedSearch.Fetch(limit, scoped, all));

    [Fact]
    public void A_chunk_is_inside_when_its_document_is_whole_in_the_scope_or_it_lies_within_a_range()
    {
        var scope = new GraphRagResolvedScope("test", new(StringComparer.Ordinal) { ["whole"] = [], ["part"] = [(100, 200), (300, 400)] }, []);
        Assert.True(scope.Contains("whole", 0, 5_000));
        Assert.True(scope.Contains("part", 100, 200));
        Assert.True(scope.Contains("part", 320, 380));
        Assert.False(scope.Contains("part", 150, 250));   // straddles the end of a range
        Assert.False(scope.Contains("part", 200, 300));   // between ranges
        Assert.False(scope.Contains("other", 0, 10));
        Assert.Equal(["part", "whole"], scope.DocumentIds);
        var parameters = new Dictionary<string, object?>();
        scope.AddParameters(parameters);
        var map = Assert.IsType<Dictionary<string, object>>(parameters["scope"]);
        Assert.Empty((System.Collections.IList)map["whole"]);
        Assert.Equal(2, ((System.Collections.IList)map["part"]).Count);
        Assert.True(new GraphRagResolvedScope("empty", new(StringComparer.Ordinal), []).IsEmpty);
    }
}
