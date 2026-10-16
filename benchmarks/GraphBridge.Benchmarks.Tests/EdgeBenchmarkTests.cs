using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Neo4j.Driver;

namespace GraphBridge.Benchmarks.Tests;

public sealed class EdgeBenchmarkTests
{
    private const string Run = "abcdef001234567890abcdef12345678";

    [Theory]
    [InlineData(1, 1, 1, 1)]
    [InlineData(100, 10, 100, 100)]
    [InlineData(10000, 10, 100, 100)]
    public void Deterministic_topology_has_exact_physical_IDs_and_parallel_edges_at_scale(int count, int sources, int targets, int pairs)
    {
        var edges = Enumerable.Range(0, count).Select(i => EdgeBenchmarkData.Expected(Run, i, count)).ToArray();
        Assert.Equal(count, edges.Select(e => e.Id).Distinct().Count());
        Assert.Equal(sources, edges.Select(e => e.SourceId).Distinct().Count());
        Assert.Equal(targets, edges.Select(e => e.TargetId).Distinct().Count());
        Assert.Equal(pairs, edges.Select(e => (e.SourceId, e.TargetId)).Distinct().Count());
        Assert.All(edges, edge => Assert.Equal(EdgeBenchmarkData.EdgeKey(Run, edge.Ordinal), edge.Id));
        Assert.Equal(Enumerable.Range(0, count), edges.Select(e => e.Ordinal));
        Assert.DoesNotContain(edges[0].Id, new[] { edges[0].SourceId, edges[0].TargetId });
    }

    [Fact]
    public void Edge_matrix_requires_an_explicit_nonempty_bounded_unique_selection()
    {
        EdgeBenchmarkData.ValidateCounts([1, 100, 10000]);
        EdgeBenchmarkData.ValidateCounts([1]);
        foreach (var invalid in new int[][] { [], [0], [1, 1], [100000], [10] })
            Assert.Throws<ArgumentException>(() => EdgeBenchmarkData.ValidateCounts(invalid));
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(2L)]
    public void Scalar_control_rejects_missing_or_duplicated_physical_identity(long cardinality)
    {
        var row = ScalarRow();
        row["IdentityCount"] = cardinality;
        Assert.Throws<InvalidOperationException>(() => EdgeBenchmarkData.Decode(row, true));
    }

    [Theory]
    [InlineData("Id")]
    [InlineData("SourceId")]
    [InlineData("TargetId")]
    public void Scalar_control_checks_required_canonical_keys_even_without_payload(string key)
    {
        var row = ScalarRow();
        row[key] = row[key]!.ToString()!.ToUpperInvariant();
        Assert.Throws<InvalidOperationException>(() => EdgeBenchmarkData.Decode(row, true));
        row[key] = "malformed";
        Assert.Throws<FormatException>(() => EdgeBenchmarkData.Decode(row, true));
        row.Remove(key);
        Assert.Throws<InvalidOperationException>(() => EdgeBenchmarkData.Decode(row, true));
    }

    [Fact]
    public void Scalar_control_does_not_construct_entity_or_require_unselected_payload()
    {
        var row = ScalarRow();
        var decoded = EdgeBenchmarkData.Decode(row, true);
        Assert.Equal(EdgeBenchmarkData.EdgeKey(Run, 0), decoded.Id);
        Assert.Null(decoded.Entity);
        Assert.Throws<InvalidOperationException>(() => EdgeBenchmarkData.Decode(row, false));
        row["Run"] = Run;
        row["Name"] = "edge-0";
        row["Version"] = 1L;
        var entity = Assert.IsType<EdgeBenchmarkEdge>(EdgeBenchmarkData.Decode(row, false).Entity);
        Assert.Equal(decoded.Id, entity.Id);
        Assert.Equal(decoded.SourceId, entity.SourceId);
        Assert.Equal(decoded.TargetId, entity.TargetId);
        Assert.Equal("edge-0", entity.Name);
        Assert.Equal(1, entity.Version);
    }

    [Theory]
    [InlineData("NODE_PROPERTY_UNIQUENESS", "RELATIONSHIP_PROPERTY_UNIQUENESS")]
    [InlineData("UNIQUENESS", "RELATIONSHIP_UNIQUENESS")]
    [InlineData("NODE_KEY", "RELATIONSHIP_KEY")]
    public void Both_endpoint_constraints_and_the_relationship_ID_constraint_are_required(string nodeType, string edgeType)
    {
        var constraints = Constraints(nodeType, edgeType);
        EdgeBenchmarkSchema.Validate(constraints);
        for (var i = 0; i < constraints.Length; i++)
            Assert.Throws<InvalidOperationException>(() => EdgeBenchmarkSchema.Validate(constraints.Where((_, index) => index != i)));
        var wrongProperty = constraints.ToArray();
        wrongProperty[2] = wrongProperty[2] with { Properties = ["SourceId", "TargetId"] };
        Assert.Throws<InvalidOperationException>(() => EdgeBenchmarkSchema.Validate(wrongProperty));
        var wrongLabel = constraints.ToArray();
        wrongLabel[0] = wrongLabel[0] with { LabelsOrTypes = [EdgeBenchmarkData.TargetLabel] };
        Assert.Throws<InvalidOperationException>(() => EdgeBenchmarkSchema.Validate(wrongLabel));
    }

    [Fact]
    public async Task Direct_control_guard_matches_real_provider_scope_before_filters_and_keeps_scalar_keys()
    {
        // Constructing the driver and translating ToQueryString performs no network operation.
        await using var driver = GraphDatabase.Driver("bolt://127.0.0.1:1", AuthTokens.None);
        await using var context = new EdgeBenchmarkContext(new DbContextOptionsBuilder<EdgeBenchmarkContext>()
            .UseNeo4j(driver, "neo4j").Options);
        var run = Run;
        var provider = context.Edges.Where(e => e.Run == run).OrderBy(e => e.Ordinal).Select(e => e.Ordinal).ToQueryString();
        var direct = EdgeBenchmarkData.ReadQuery(true);
        static string Normalize(string text) => Regex.Replace(text.Replace("`", "", StringComparison.Ordinal)
            .Replace("__gb_identified_edge_count", "IdentityCount", StringComparison.Ordinal), @"\s+", " ").Trim();
        static string Guard(string text)
        {
            var normalized = Normalize(text);
            var start = normalized.IndexOf("CALL (r)", StringComparison.Ordinal);
            var end = normalized.IndexOf("WITH s, r, t, IdentityCount", start, StringComparison.Ordinal);
            return normalized[start..end];
        }
        Assert.Equal(Guard(provider), Guard(direct));
        foreach (var query in new[] { Normalize(provider), Normalize(direct) })
        {
            Assert.Contains("r.Id AS", query);
            Assert.Contains("s.Id AS", query);
            Assert.Contains("t.Id AS", query);
            Assert.DoesNotContain("DISTINCT", query);
            Assert.True(query.IndexOf("WITH candidate LIMIT 2", StringComparison.Ordinal) < query.IndexOf("WHERE coalesce(r.Run", StringComparison.Ordinal));
        }
        Assert.DoesNotContain("r.SourceId", provider);
        Assert.DoesNotContain("r.TargetId", provider);
    }

    private static Dictionary<string, object?> ScalarRow() => new()
    {
        ["Id"] = EdgeBenchmarkData.EdgeKey(Run, 0).ToString("D"),
        ["SourceId"] = EdgeBenchmarkData.SourceKey(Run, 0).ToString("D"),
        ["TargetId"] = EdgeBenchmarkData.TargetKey(Run, 0).ToString("D"),
        ["Ordinal"] = 0L, ["IdentityCount"] = 1L
    };
    private static EdgeBenchmarkConstraint[] Constraints(string nodeType, string edgeType) =>
    [
        new("NODE", nodeType, [EdgeBenchmarkData.SourceLabel], ["Id"]),
        new("NODE", nodeType, [EdgeBenchmarkData.TargetLabel], ["Id"]),
        new("RELATIONSHIP", edgeType, [EdgeBenchmarkData.RelationshipType], ["Id"])
    ];
}
