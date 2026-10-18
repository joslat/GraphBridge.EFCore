using GraphBridge.EntityFrameworkCore.Search;
using GraphBridge.Tooling.Diagnostics;
using Neo4j.Driver;
using Context = GraphBridge.ToolingTests.ConnectedDoctorTests.Context;
using Protocol = GraphBridge.ToolingTests.ConnectedDoctorTests.Protocol;

namespace GraphBridge.ToolingTests;

/// <summary>Doctor's early warnings for declared search indexes, against a scripted server. Names never reach the report.</summary>
public sealed class SearchIndexDoctorTests
{
    private const string VectorIndex = "private_vector_index";
    private const string TextIndex = "private_text_index";

    [Fact]
    public async Task Matching_online_indexes_with_usable_values_pass_and_retain_no_names()
    {
        var protocol = Script(Vector(), FullText(), present: 10);
        using var db = new Context(protocol.Driver);
        var report = await GraphDoctor.InspectAsync(db, Catalog(), "test.read.v1");
        Assert.Equal(GraphEvidenceStatus.Pass, report.Status);
        Assert.Contains(report.Diagnostics, d => d.Code == "GBD-SEARCH-000");
        var vector = report.SearchIndexes!.Single(i => i.Id == "movie-taste");
        Assert.Equal(GraphEvidenceStatus.Pass, vector.Status);
        Assert.Equal(10, vector.SampledValues);
        Assert.Equal(0, vector.UnusableValues);
        Assert.Empty(vector.Findings);
        Assert.Equal(GraphSearchKind.FullText, report.SearchIndexes!.Single(i => i.Id == "movie-text").Kind);
        foreach (var text in new[] { report.ToJson(), report.ToText(), GraphQueryLab.RenderHtml(report) })
        {
            Assert.Contains("movie-taste", text);
            foreach (var secret in new[] { VectorIndex, TextIndex, "PrivateMovie", "privateEmbedding", "privateTitle" })
                Assert.DoesNotContain(secret, text);
        }
        // Schema inspection and the search checks each own one read transaction, rolled back and never committed.
        protocol.AssertCleanup(transactions: 2);
        Assert.Equal(2, protocol.Queries.Count(q => q.StartsWith("MATCH", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("missing", "GBD-SEARCH-001", GraphEvidenceStatus.Fail)]
    [InlineData("populating", "GBD-SEARCH-002", GraphEvidenceStatus.Blocked)]
    [InlineData("failed", "GBD-SEARCH-003", GraphEvidenceStatus.Fail)]
    [InlineData("range", "GBD-SEARCH-004", GraphEvidenceStatus.Fail)]
    [InlineData("relationship", "GBD-SEARCH-004", GraphEvidenceStatus.Fail)]
    [InlineData("other-property", "GBD-SEARCH-005", GraphEvidenceStatus.Fail)]
    [InlineData("dimensions", "GBD-SEARCH-006", GraphEvidenceStatus.Fail)]
    [InlineData("euclidean", "GBD-SEARCH-007", GraphEvidenceStatus.Pass)]
    public async Task Each_index_mistake_has_its_own_code_and_status(string mistake, string code, GraphEvidenceStatus status)
    {
        var vector = mistake switch
        {
            "missing" => null,
            "populating" => Vector(state: "POPULATING"),
            "failed" => Vector(state: "FAILED"),
            "range" => Vector(type: "RANGE"),
            "relationship" => Vector(entityType: "RELATIONSHIP"),
            "other-property" => Vector(property: "otherEmbedding"),
            "dimensions" => Vector(dimensions: 1536),
            _ => Vector(similarity: "EUCLIDEAN")
        };
        var protocol = Script(vector, FullText(), present: 10);
        using var db = new Context(protocol.Driver);
        var report = await GraphDoctor.InspectAsync(db, Catalog(), "test.read.v1");
        var evidence = report.SearchIndexes!.Single(i => i.Id == "movie-taste");
        Assert.Equal(new[] { code }, evidence.Findings);
        Assert.Equal(status, evidence.Status);
        Assert.Contains(report.Diagnostics, d => d.Code == code);
        Assert.Equal(status == GraphEvidenceStatus.Pass ? GraphEvidenceStatus.Pass : status, report.Status);
        // Sampling a wrong-kind, wrong-property or missing index would be meaningless.
        var sampled = mistake is not ("missing" or "range" or "relationship" or "other-property");
        Assert.Equal(sampled, evidence.SampledValues is not null);
        protocol.AssertCleanup(transactions: 2);
    }

    [Theory]
    [InlineData(0, 0, 0, 0, 0, "GBD-SEARCH-009", null)]
    [InlineData(10, 8, 8, 0, 0, "GBD-SEARCH-008", 2)]  // two strings or other non-numeric values
    [InlineData(10, 10, 7, 0, 0, "GBD-SEARCH-008", 3)] // three lists with other dimensions
    [InlineData(10, 10, 10, 1, 0, "GBD-SEARCH-008", 1)] // one zero vector on a cosine index
    [InlineData(10, 6, 6, 0, 4, null, 0)]              // native VECTOR values are not judged
    public async Task Sampled_values_the_index_silently_skips_are_counted(int present, int numeric, int sized, int zero, int vectors,
        string? code, int? unusable)
    {
        var protocol = Script(Vector(), FullText(), present, numeric, sized, zero, vectors);
        using var db = new Context(protocol.Driver);
        var report = await GraphDoctor.InspectAsync(db, Catalog(), "test.read.v1");
        var evidence = report.SearchIndexes!.Single(i => i.Id == "movie-taste");
        Assert.Equal(code is null ? Array.Empty<string>() : new[] { code }, evidence.Findings);
        Assert.Equal(present, evidence.SampledValues);
        if (unusable is not null) Assert.Equal(unusable, evidence.UnusableValues);
        Assert.Equal(GraphEvidenceStatus.Pass, evidence.Status); // Warnings, not failures: searches still run.
        var sample = protocol.Queries.First(q => q.StartsWith("MATCH (e:`PrivateMovie`)", StringComparison.Ordinal));
        Assert.Contains("LIMIT $scan", sample);
        Assert.Contains("LIMIT $sample", sample);
    }

    [Fact]
    public async Task Eventually_consistent_full_text_old_servers_and_metadata_only_declarations()
    {
        var eventual = Script(Vector(), FullText(eventual: true), present: 10);
        using (var db = new Context(eventual.Driver))
        {
            var report = await GraphDoctor.InspectAsync(db, Catalog(), "test.read.v1");
            Assert.Equal(new[] { "GBD-SEARCH-011" }, report.SearchIndexes!.Single(i => i.Id == "movie-text").Findings);
            Assert.Equal(GraphEvidenceStatus.Pass, report.Status);
        }

        var old = Script(Vector(), FullText(), present: 10, version: "2026.03.1");
        using (var db = new Context(old.Driver))
        {
            var report = await GraphDoctor.InspectAsync(db, Catalog(), "test.read.v1");
            Assert.Equal(new[] { "GBD-SEARCH-010" }, report.SearchIndexes!.Single(i => i.Id == "movie-text").Findings);
            Assert.Empty(report.SearchIndexes!.Single(i => i.Id == "movie-taste").Findings);
            Assert.Equal(GraphEvidenceStatus.Unsupported, report.Status);
        }

        var metadataOnly = Script(Vector(), FullText(), present: 10);
        using (var db = new Context(metadataOnly.Driver))
        {
            var catalog = new GraphQueryCatalog([], [GraphSearchIndexExpectation.Vector("movie-taste", VectorIndex, "PrivateMovie", "privateEmbedding", 4, "cosine", sampleSize: 0)]);
            var report = await GraphDoctor.InspectAsync(db, catalog, "test.read.v1");
            Assert.Null(Assert.Single(report.SearchIndexes!).SampledValues);
            Assert.DoesNotContain(metadataOnly.Queries, q => q.StartsWith("MATCH", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task Offline_lists_declarations_unobserved_and_failures_never_pass()
    {
        var offline = Script(Vector(), FullText(), present: 10);
        using (var db = new Context(offline.Driver))
        {
            var report = GraphDoctor.InspectOffline(db, Catalog());
            Assert.All(report.SearchIndexes!, i => Assert.Equal(GraphEvidenceStatus.NotRun, i.Status));
            Assert.DoesNotContain(report.Diagnostics, d => d.Code.StartsWith("GBD-SEARCH", StringComparison.Ordinal));
            Assert.Empty(offline.Calls);
        }

        var denied = Script(Vector(), FullText(), present: 10,
            native: () => throw new ClientException("Neo.ClientError.Security.Forbidden", "credential-sensitive-message"));
        using (var db = new Context(denied.Driver))
        {
            var report = await GraphDoctor.InspectAsync(db, Catalog(), "test.read.v1");
            Assert.Equal(GraphEvidenceStatus.Blocked, report.Status);
            Assert.Contains(report.Diagnostics, d => d.Code == "GBD-CONN-002");
            Assert.All(report.SearchIndexes!, i => Assert.Equal(GraphEvidenceStatus.NotRun, i.Status));
            Assert.DoesNotContain("credential-sensitive-message", report.ToJson());
            denied.AssertCleanup(transactions: 2);
        }
    }

    [Fact]
    public void Declarations_are_validated_before_any_database_work()
    {
        Assert.Throws<ArgumentException>(() => GraphSearchIndexExpectation.Vector("Bad Id", "i", "L", "p", 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => GraphSearchIndexExpectation.Vector("a", "i", "L", "p", 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => GraphSearchIndexExpectation.Vector("a", "i", "L", "p", 4097));
        Assert.Throws<ArgumentException>(() => GraphSearchIndexExpectation.Vector("a", "i", "L", "p", 4, "dot"));
        Assert.Throws<ArgumentException>(() => GraphSearchIndexExpectation.Vector("a", "i", "L`) DETACH DELETE (x", "p", 4));
        Assert.Throws<ArgumentException>(() => GraphSearchIndexExpectation.FullText("a", "i", [], ["p"]));
        Assert.Throws<ArgumentOutOfRangeException>(() => GraphSearchIndexExpectation.FullText("a", "i", ["L"], ["p"], sampleSize: 10_001));
        var one = GraphSearchIndexExpectation.Vector("a", "i", "L", "p", 4);
        Assert.Throws<ArgumentException>(() => new GraphQueryCatalog([], [one, one]));
        Assert.Equal("cosine", GraphSearchIndexExpectation.Vector("a", "i", "L", "p", 4, "COSINE").Similarity);
    }

    private static GraphQueryCatalog Catalog() => new([],
    [
        GraphSearchIndexExpectation.Vector("movie-taste", VectorIndex, "PrivateMovie", "privateEmbedding", 4, "cosine"),
        GraphSearchIndexExpectation.FullText("movie-text", TextIndex, ["PrivateMovie"], ["privateTitle"])
    ]);

    private static Protocol Script(Dictionary<string, object>? vector, Dictionary<string, object> fullText, int present,
        int? numeric = null, int? sized = null, int zero = 0, int vectors = 0, string version = "2026.09.0", Func<IResultCursor>? native = null)
    {
        var protocol = new Protocol
        {
            Version = version,
            Native = (query, _) => native?.Invoke() ?? Protocol.Rows([query.Contains("valueType", StringComparison.Ordinal)
                ? new() { ["present"] = (long)present, ["numeric"] = (long)(numeric ?? present), ["sized"] = (long)(sized ?? numeric ?? present),
                          ["zero"] = (long)zero, ["vectors"] = (long)vectors }
                : new() { ["present"] = (long)present }])
        };
        if (vector is not null) protocol.SearchIndexes[VectorIndex] = vector;
        protocol.SearchIndexes[TextIndex] = fullText;
        return protocol;
    }

    private static Dictionary<string, object> Vector(string state = "ONLINE", string type = "VECTOR", string entityType = "NODE",
        string property = "privateEmbedding", long dimensions = 4, string similarity = "COSINE")
        => Index(VectorIndex, state, type, entityType, property,
            new() { ["vector.dimensions"] = dimensions, ["vector.similarity_function"] = similarity });

    private static Dictionary<string, object> FullText(bool eventual = false)
        => Index(TextIndex, "ONLINE", "FULLTEXT", "NODE", "privateTitle",
            new() { ["fulltext.analyzer"] = "standard-no-stop-words", ["fulltext.eventually_consistent"] = eventual });

    private static Dictionary<string, object> Index(string name, string state, string type, string entityType, string property,
        Dictionary<string, object> config) => new()
    {
        ["name"] = name, ["state"] = state, ["type"] = type, ["entityType"] = entityType,
        ["labelsOrTypes"] = new List<object> { "PrivateMovie" }, ["properties"] = new List<object> { property },
        ["options"] = new Dictionary<string, object> { ["indexProvider"] = "vector-3.0", ["indexConfig"] = config }
    };
}
