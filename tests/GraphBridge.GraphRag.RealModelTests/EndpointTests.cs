using System.Net;
using System.Net.Http.Json;
using System.Net.ServerSentEvents;
using System.Text;
using System.Text.Json;
using GraphBridge.EntityFrameworkCore.Migrations;
using GraphBridge.GraphRag.AspNetCore;
using GraphBridge.IntegrationTests;
using GraphBridge.Samples.GraphRagShared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GraphBridge.GraphRag.RealModelTests;

// GraphRAG v2, wave 8 (GR-21): MapGraphRag over a real graph, served on a loopback port, with the offline stand-ins for the
// models. It lives here, in the GraphRAG live suite, so the provider's integration suite doesn't take ASP.NET Core's shared
// framework, which would stop it carrying the libraries sample 01's console app needs.
[Collection(Neo4jIntegrationCollection.Name)]
public sealed class EndpointTests(DisposableNeo4jFixture fixture) : IAsyncLifetime
{
    private const string Markdown = """
        # Graphs — an overview 🌍

        Graphs connect facts. Ünïcödé text keeps its bytes, and a node holds properties that describe it.

        ## Indexes

        A vector index finds similar passages, and a full-text index finds words.
        """;

    internal static readonly string Suffix = Guid.NewGuid().ToString("N")[..12];
    internal static readonly GraphRagEmbeddingSet Embeddings = new("fake-hash-8", 8);
    internal static readonly GraphRagLayout Layout = GraphRagLayout.Default with
    {
        DocumentLabel = "EDocument_" + Suffix, SectionLabel = "ESection_" + Suffix, ChunkLabel = "EChunk_" + Suffix,
        RunLabel = "ERun_" + Suffix, LeaseLabel = "ELease_" + Suffix, SchemaPrefix = "gbe_" + Suffix
    };

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (!Enabled) return;
        var labels = new[] { Layout.DocumentLabel, Layout.SectionLabel, Layout.ChunkLabel, Layout.RunLabel, Layout.LeaseLabel }.Select(l => "n:" + l);
        await Execute($"MATCH (n)-[r]-() WHERE {string.Join(" OR ", labels)} DELETE r");
        await Execute($"MATCH (n) WHERE {string.Join(" OR ", labels)} DELETE n");
        foreach (var definition in GraphRagSchema.Definitions(Layout, Embeddings))
            await Execute($"DROP {(definition.IsIndex ? "INDEX" : "CONSTRAINT")} {definition.Name} IF EXISTS");
    }

    private static bool Enabled => Environment.GetEnvironmentVariable("GRAPHBRIDGE_INTEGRATION") == "1";

    [Neo4jIntegrationFact]
    public async Task The_endpoints_stream_a_checked_answer_from_stored_documents_and_return_a_document_with_its_chunk_spans()
    {
        foreach (var step in GraphRagSchema.CreateSteps(Layout, Embeddings)) await Execute(GraphMigrationStepRenderer.Render(step).Cypher);
        foreach (var definition in GraphRagSchema.Definitions(Layout, Embeddings).Where(d => d.IsIndex))
            await Execute("CALL db.awaitIndex($index, 60)", new { index = definition.Name });
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IChatClient>(new ExtractiveChatClient());
        builder.Services.AddDbContext<EndpointContext>(o => o.UseNeo4j(fixture.Driver, fixture.Database));
        builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(new HashingEmbedder(Embeddings.Dimensions));
        builder.Services.AddGraphRag<EndpointContext>(Embeddings, o => o.Chunking.MaxUtf8Bytes = 96);
        await using var app = builder.Build();
        app.MapGraphRag<EndpointContext>("/graphrag");
        await app.StartAsync();
        var receipt = await app.Services.GetRequiredService<IGraphRagIngestion>().IngestAsync([GraphRagSources.Text("guide", Markdown, "text/markdown")]);
        var document = Assert.Single(receipt.Documents);
        using var http = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        var stored = Encoding.UTF8.GetBytes(Markdown.ReplaceLineEndings("\n"));

        using (var response = await http.PostAsJsonAsync("/graphrag/ask", new { question = "Which index finds similar passages?" }))
        {
            var events = new List<SseItem<string>>();
            await using (var stream = await response.Content.ReadAsStreamAsync())
                await foreach (var item in SseParser.Create(stream).EnumerateAsync()) events.Add(item);
            Assert.Equal("retrieved", events[0].EventType);
            Assert.Contains(events, e => e.EventType == "delta");
            Assert.Equal("verified", events[^1].EventType);
            var verified = JsonSerializer.Deserialize<GraphRagVerifiedEvent>(events[^1].Data, JsonSerializerOptions.Web)!;
            var citation = Assert.Single(verified.Citations);
            Assert.Equal(document.DocumentId, citation.DocumentId);
            Assert.Equal(citation.Quote, Encoding.UTF8.GetString(stored, citation.StartByte, citation.EndByte - citation.StartByte));
            Assert.Contains("vector index finds similar passages", citation.Quote, StringComparison.Ordinal);
        }

        var read = (await http.GetFromJsonAsync<GraphRagDocumentResponse>($"/graphrag/documents/{document.DocumentId}", JsonSerializerOptions.Web))!;
        Assert.Equal((document.DocumentId, "text/markdown", (long)stored.Length), (read.Id, read.MediaType, read.LengthBytes));
        Assert.Equal(Enumerable.Range(0, read.Chunks.Count), read.Chunks.Select(c => c.Order));
        Assert.Equal(0, read.Chunks[0].StartByte);
        Assert.Equal(stored.Length, read.Chunks[^1].EndByte);
        Assert.All(read.Chunks.Zip(read.Chunks.Skip(1)), pair => Assert.Equal(pair.First.EndByte, pair.Second.StartByte));
        using (var missing = await http.GetAsync("/graphrag/documents/no-such-document"))
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    private async Task Execute(string text, object? parameters = null)
    {
        await using var session = fixture.OpenSession();
        await (parameters is null ? await session.RunAsync(text) : await session.RunAsync(text, parameters)).ConsumeAsync();
    }

    public sealed class EndpointContext(DbContextOptions<EndpointContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.AddGraphRag(Embeddings.Dimensions, o => o.Layout = Layout);
    }
}
