using GraphBridge.EntityFrameworkCore.Search;
using GraphBridge.Tooling.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Neo4j.Driver;

namespace GraphBridge.IntegrationTests;

/// <summary>Doctor's search-index warnings against real indexes and real stored values on the disposable fixture.</summary>
[Collection(ToolingIntegrationCollection.Name)]
public sealed class SearchIndexDoctorIntegrationTests(DisposableNeo4jFixture fixture)
{
    [Neo4jIntegrationFact]
    public async Task Real_index_mistakes_are_reported_early_and_unusable_values_match_what_search_leaves_out()
    {
        var run = Guid.NewGuid().ToString("N")[..12];
        var label = "GbDoctorSearch_" + run;
        var (vector, text, empty) = ("gb_doctor_vec_" + run, "gb_doctor_txt_" + run, "gb_doctor_empty_" + run);
        await Execute($"CREATE VECTOR INDEX {vector} FOR (n:{label}) ON (n.embedding) OPTIONS {{indexConfig: {{`vector.dimensions`: 3, `vector.similarity_function`: 'cosine'}}}}");
        await Execute($"CREATE VECTOR INDEX {empty} FOR (n:{label}) ON (n.unusedEmbedding) OPTIONS {{indexConfig: {{`vector.dimensions`: 3, `vector.similarity_function`: 'cosine'}}}}");
        await Execute($"CREATE FULLTEXT INDEX {text} FOR (n:{label}) ON EACH [n.title] OPTIONS {{indexConfig: {{`fulltext.eventually_consistent`: false}}}}");
        try
        {
            foreach (var index in new[] { vector, empty, text }) await Execute("CALL db.awaitIndex($index, 60)", new { index });
            // Three good float vectors and an integer vector, then values Neo4j cannot index: wrong dimensions, a string, a zero vector.
            await Execute($$"""
                UNWIND [[0.9, 0.1, 0.0], [0.8, 0.3, 0.1], [0.1, 0.9, 0.2], [1, 2, 3]] AS embedding CREATE (:{{label}} {title: 'usable vector', embedding: embedding})
                """);
            await Execute($$"""
                UNWIND [[0.5, 0.5], 'not a vector', [0.0, 0.0, 0.0]] AS embedding CREATE (:{{label}} {title: 'unusable vector', embedding: embedding})
                """);
            using var db = new Context(fixture.Driver, fixture.Database);
            var before = await State(label);
            var catalog = new GraphQueryCatalog([],
            [
                GraphSearchIndexExpectation.Vector("taste", vector, label, "embedding", 3, "cosine"),
                GraphSearchIndexExpectation.FullText("titles", text, [label], ["title"]),
                GraphSearchIndexExpectation.Vector("unused", empty, label, "unusedEmbedding", 3),
                GraphSearchIndexExpectation.Vector("missing", "gb_doctor_absent_" + run, label, "embedding", 3),
                GraphSearchIndexExpectation.Vector("other-model", vector, label, "embedding", 1536),
                GraphSearchIndexExpectation.Vector("other-property", vector, label, "embeddings", 3),
                GraphSearchIndexExpectation.Vector("other-similarity", vector, label, "embedding", 3, "euclidean"),
                GraphSearchIndexExpectation.Vector("other-kind", text, label, "title", 3),
                GraphSearchIndexExpectation.FullText("relationship", text, [label], ["title"], GraphSearchEntityKind.Relationship)
            ]);
            var report = await GraphDoctor.InspectAsync(db, catalog, "owned.community.v1");
            GraphSearchIndexEvidence Index(string id) => report.SearchIndexes!.Single(i => i.Id == id);

            var taste = Index("taste");
            Assert.Equal(new[] { "GBD-SEARCH-008" }, taste.Findings);
            Assert.Equal(7, taste.SampledValues);
            Assert.Equal(3, taste.UnusableValues);
            Assert.Empty(Index("titles").Findings);
            Assert.Equal(new[] { "GBD-SEARCH-009" }, Index("unused").Findings);
            Assert.Equal(new[] { "GBD-SEARCH-001" }, Index("missing").Findings);
            Assert.Contains("GBD-SEARCH-006", Index("other-model").Findings);
            Assert.Equal(new[] { "GBD-SEARCH-005" }, Index("other-property").Findings);
            Assert.Null(Index("other-property").SampledValues);
            Assert.Contains("GBD-SEARCH-007", Index("other-similarity").Findings);
            Assert.Equal(new[] { "GBD-SEARCH-004" }, Index("other-kind").Findings);
            Assert.Equal(new[] { "GBD-SEARCH-004" }, Index("relationship").Findings);
            Assert.Equal(GraphEvidenceStatus.Fail, report.Status);

            // The evidence must match the server: search returns exactly the sampled values Doctor counted as usable.
            var found = await db.Database.QueryVectorNodesAsync(vector, new[] { 0.9, 0.2, 0.1 }, node => node["title"].As<string>(), limit: 20);
            Assert.Equal(taste.SampledValues - taste.UnusableValues, found.Results.Count);
            Assert.All(found.Results, r => Assert.Equal("usable vector", r.Value));

            Assert.Equal(before, await State(label)); // Observation only: nothing created, changed or repaired.
            foreach (var output in new[] { report.ToJson(), report.ToText() })
                foreach (var secret in new[] { label, vector, text, empty, "unusedEmbedding", "not a vector" })
                    Assert.DoesNotContain(secret, output);
        }
        finally
        {
            await Execute($"MATCH (n:{label}) DELETE n");
            foreach (var index in new[] { vector, empty, text }) await Execute($"DROP INDEX {index} IF EXISTS");
        }
    }

    private sealed class Item { public string Id { get; set; } = ""; }
    private sealed class Context(IDriver driver, string database) : DbContext(new DbContextOptionsBuilder().UseNeo4j(driver, database).Options)
    {
        protected override void OnModelCreating(ModelBuilder b)
        {
            b.Entity<Item>().ToGraphNode("GbDoctorSearchItem");
            b.Entity<Item>().Property(p => p.Id).HasGraphPropertyName("id");
        }
    }
    private async Task Execute(string query, object? parameters = null)
    {
        await using var session = fixture.OpenSession();
        await (await session.RunAsync(query, parameters)).ConsumeAsync();
    }
    private async Task<string[]> State(string label)
    {
        await using var session = fixture.OpenSession();
        var nodes = await (await session.RunAsync($"MATCH (n:{label}) RETURN n.title + ':' + valueType(n.embedding) AS value ORDER BY value"))
            .ToListAsync(r => r["value"].As<string>());
        var indexes = await (await session.RunAsync("SHOW INDEXES YIELD name, state RETURN name + ':' + state AS value ORDER BY value"))
            .ToListAsync(r => r["value"].As<string>());
        return [.. nodes, .. indexes];
    }
}
