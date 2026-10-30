using System.Text.Json;
using GraphBridge.EntityFrameworkCore.Migrations;
using GraphBridge.GraphRag;
using GraphBridge.GraphRag.Evaluation;
using GraphBridge.Samples.GraphRagShared;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Neo4j.Driver;
using static GraphBridge.IntegrationTests.GraphRagServeTests;

namespace GraphBridge.IntegrationTests;

// GraphRAG GR-15 (ADR 0052, section 15) on the disposable fixture, with a scripted model: each gate refuses what it should,
// with the server's EXPLAIN and a read transaction, and the row cap is reported.
[Collection(Neo4jIntegrationCollection.Name)]
public sealed class GraphRagText2CypherTests(DisposableNeo4jFixture fixture) : IAsyncLifetime
{
    private const int Dimensions = 8;
    private readonly string _suffix = Guid.NewGuid().ToString("N")[..12];
    private readonly GraphRagEmbeddingSet _set = new("graphbridge-sample-hashing", Dimensions);
    private GraphRagLayout Layout => GraphRagLayout.Default with
    {
        DocumentLabel = "TDocument_" + _suffix, SectionLabel = "TSection_" + _suffix, ChunkLabel = "TChunk_" + _suffix,
        RunLabel = "TRun_" + _suffix, LeaseLabel = "TLease_" + _suffix, SchemaPrefix = "gbt_" + _suffix
    };

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (Environment.GetEnvironmentVariable("GRAPHBRIDGE_INTEGRATION") != "1") return;
        var labels = new[] { Layout.DocumentLabel, Layout.SectionLabel, Layout.ChunkLabel, Layout.RunLabel, Layout.LeaseLabel }.Select(l => "n:" + l);
        await Execute($"MATCH (n)-[r]-() WHERE {string.Join(" OR ", labels)} DELETE r");
        await Execute($"MATCH (n) WHERE {string.Join(" OR ", labels)} DELETE n");
        foreach (var definition in GraphRagSchema.Definitions(Layout, _set))
            await Execute($"DROP {(definition.IsIndex ? "INDEX" : "CONSTRAINT")} `{definition.Name}` IF EXISTS");
    }

    [Neo4jIntegrationFact]
    public async Task A_read_only_query_answers_with_its_rows_and_every_gate_refuses_what_it_should()
    {
        foreach (var step in GraphRagSchema.CreateSteps(Layout, _set)) await Execute(GraphMigrationStepRenderer.Render(step).Cypher);
        await Execute("CALL db.awaitIndexes(60)");
        var (document, chunk) = (Layout.DocumentLabel, Layout.ChunkLabel);
        // The scripted model writes, for the first word of each question, this reply.
        var replies = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["count"] = Reply($"MATCH (d:{document}) RETURN count(d) AS documents"),
            ["passages"] = Reply($"MATCH (c:{chunk}) RETURN c ORDER BY c.Order LIMIT 1"),
            ["many"] = Reply("UNWIND range(1, 100) AS i RETURN i"),
            // A write that starts by reading passes the text gate, and the server's EXPLAIN refuses it; one that starts writing doesn't get that far.
            ["write"] = Reply($"MATCH (c:{chunk}) WITH c LIMIT 1 CREATE (d:{document} {{Id: 'forged'}}) RETURN d"),
            ["create"] = Reply($"CREATE (d:{document} {{Id: 'forged'}}) RETURN d"),
            ["file"] = Reply("LOAD CSV FROM 'https://example.org/data.csv' AS row RETURN row"),
            ["procedure"] = Reply("CALL db.labels() YIELD label RETURN label"),
            // GR-69: a comment hides no keyword, and a keyword inside a string literal refuses nothing.
            ["hidden"] = Reply("CALL/**/db.labels() YIELD label RETURN label"),
            ["fetch"] = Reply("LOAD/**/CSV FROM 'https://example.org/data.csv' AS row RETURN row"),
            ["literal"] = Reply($"MATCH (c:{chunk}) WHERE NOT c.Text CONTAINS 'the use of; call sign' RETURN count(c) AS passages"),
            ["broken"] = Reply("MATCH (d RETURN d"),
            ["capital"] = """{"insufficient": "The graph holds no capitals."}"""
        };
        var chat = new GraphRagScriptedChatClient(messages =>
        {
            var question = messages[^1].Text.Split('\n')[0]["Question: ".Length..];
            return replies[question.Split(' ')[0]];
        });
        await using var provider = Services(chat);
        var receipt = await provider.GetRequiredService<IGraphRagIngestion>().IngestAsync(
        [
            GraphRagSources.Text("one", "The rocket engine burns liquid hydrogen."),
            GraphRagSources.Text("two", "The crew of four trained for years.")
        ]);
        Assert.All(receipt.Documents, d => Assert.Equal(GraphRagDocumentStatus.Ingested, d.Status));
        var graph = provider.GetRequiredService<IGraphRagGraphQuestions>();

        var counted = await graph.AskAsync("count the documents");
        Assert.Equal(GraphRagGraphAnswerDisposition.Answered, counted.Disposition);
        Assert.Equal(["documents"], counted.Columns);
        Assert.Equal(2L, Assert.Single(counted.Rows)["documents"]);
        Assert.Equal(1, counted.Receipt!.ChatCalls);

        // A node comes back as its properties, without the embedding.
        var passage = Assert.IsType<Dictionary<string, object?>>(Assert.Single((await graph.AskAsync("passages first")).Rows)["c"]);
        Assert.Contains("Text", passage.Keys);
        Assert.DoesNotContain("Embedding", passage.Keys);

        var many = await graph.AskAsync("many numbers");
        Assert.Equal((GraphRagGraphAnswerDisposition.Answered, 50), (many.Disposition, many.Rows.Count));
        Assert.Equal(new GraphRagLimit("GraphQueryRows", 50), Assert.Single(many.LimitsReached));

        Assert.Equal(2L, Assert.Single((await graph.AskAsync("literal words")).Rows)["passages"]);

        foreach (var (question, reason) in new[] { ("write a document", "not-read-only"), ("create a document", "not-allowed"), ("file contents", "not-allowed"),
                     ("procedure call", "not-allowed"), ("hidden procedure", "not-allowed"), ("fetch a file", "not-allowed"),
                     ("broken query", "invalid-query"), ("capital of Mongolia", "model-declined") })
        {
            var refused = await graph.AskAsync(question);
            Assert.Equal((GraphRagGraphAnswerDisposition.Refused, reason), (refused.Disposition, refused.Reason));
            Assert.Empty(refused.Rows);
        }
        // Nothing was written: the forged document doesn't exist.
        await using var session = fixture.OpenSession();
        Assert.Equal(0, (await (await session.RunAsync($"MATCH (d:{document} {{Id: 'forged'}}) RETURN count(d) AS n")).SingleAsync())["n"].As<int>());

        // The plan check refuses what the server plans as a procedure or a file read, whatever the text gate let through.
        foreach (var (cypher, allowed) in new[]
                 {
                     ("CALL db.labels() YIELD label RETURN label", false), ("LOAD CSV FROM 'https://example.org/data.csv' AS row RETURN row", false),
                     ($"MATCH (c:{chunk}) RETURN count(c) AS passages", true)
                 })
        {
            var plan = await (await session.RunAsync("EXPLAIN " + cypher)).ConsumeAsync();
            Assert.True(plan.HasPlan);
            Assert.Equal(allowed, GraphRagText2Cypher<ServeContext>.PlanAllowed(plan.Plan));
        }
    }

    private static string Reply(string cypher) => JsonSerializer.Serialize(new { cypher });

    private ServiceProvider Services(IChatClient chat)
    {
        var services = new ServiceCollection();
        services.AddSingleton(chat);
        services.AddSingleton(new ServeModel(Layout, Dimensions, null));
        services.AddDbContext<ServeContext>(o => o.UseNeo4j(fixture.Driver, fixture.Database).ReplaceService<IModelCacheKeyFactory, ServeKey>());
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(new HashingEmbedder(Dimensions));
        services.AddGraphRag<ServeContext>(_set).AddGraphRagText2Cypher<ServeContext>();
        return services.BuildServiceProvider();
    }

    private async Task Execute(string cypher)
    {
        await using var session = fixture.OpenSession();
        await (await session.RunAsync(cypher)).ConsumeAsync();
    }
}
