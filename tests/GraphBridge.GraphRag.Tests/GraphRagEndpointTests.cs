using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using GraphBridge.GraphRag;
using GraphBridge.GraphRag.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Neo4j.Driver;

namespace GraphBridge.GraphRag.Tests;

/// <summary>
/// GraphRAG v2, wave 8 (GR-21), offline: the endpoints run in process on a loopback port, with scripted answers and the
/// toolkit's own answerer over fixed evidence. The documents endpoint reads the database, so its tests are live.
/// </summary>
public sealed class GraphRagEndpointTests
{
    private static readonly GraphRagEvidence Evidence = new("What do graphs connect?",
        [new GraphRagEvidenceItem("c1", "doc-1", "Graphs connect facts — not rows.", 10, 10 + Encoding.UTF8.GetByteCount("Graphs connect facts — not rows."), 0.03, [])],
        [], 36, 2, [new GraphRagLimit("MaxItems", 1)], new("2026.09.0", "set", ["index"], "cosine"));

    [Fact]
    public async Task Ask_streams_the_evidence_then_provisional_text_then_the_checked_answer()
    {
        var chat = new PiecesChat("{\"answer\": \"Facts, ", "not rows.\", \"citations\": [{\"evidence\": 1, \"quote\": \"Graphs connect facts - not rows.\"}]}");
        await using var server = await Server.StartAsync(new GraphRagAnswerer(new FixedRetriever(Evidence), () => chat));
        using var response = await server.Client.PostAsJsonAsync("/graphrag/ask", new { question = "What do graphs connect?" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);

        var events = await ReadEvents(response);
        Assert.Equal(["retrieved", "delta", "delta", "verified"], events.Select(e => e.EventType));
        var retrieved = Data<GraphRagRetrievedEvent>(events[0]);
        Assert.Equal((1, 36, 2, "MaxItems"), (retrieved.Items, retrieved.Utf8Bytes, retrieved.OmittedItems, retrieved.LimitsReached.Single().Name));
        Assert.Equal("Facts, not rows.", string.Concat(events.Where(e => e.EventType == "delta").Select(e => Data<GraphRagDeltaEvent>(e).Text)));
        var verified = Data<GraphRagVerifiedEvent>(events[^1]);
        Assert.Equal("Facts, not rows.", verified.Answer);
        Assert.Equal(new GraphRagCitation("doc-1", "c1", 10, Evidence.Items[0].EndByte, "Graphs connect facts — not rows."), verified.Citations.Single());
        Assert.Equal(1, verified.Receipt!.ChatCalls);
        // The wire format is camel case, as web clients expect.
        Assert.Contains("\"startByte\":10", events[^1].Data, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_refusal_says_whether_it_withdraws_provisional_text(bool provisional)
    {
        var chat = provisional
            ? new PiecesChat("{\"answer\": \"Made up.\", \"citations\": [{\"evidence\": 1, \"quote\": \"not in the text\"}]}")
            : new PiecesChat("{\"insufficient\": \"no\"}");
        await using var server = await Server.StartAsync(new GraphRagAnswerer(new FixedRetriever(Evidence), () => chat));
        using var response = await server.Client.PostAsJsonAsync("/graphrag/ask", new { question = "q" });
        var events = await ReadEvents(response);
        Assert.Equal(provisional, events.Any(e => e.EventType == "delta"));
        var refused = Data<GraphRagRefusedEvent>(events[^1]);
        Assert.Equal(("refused", provisional ? "citation-not-found" : "model-declined", provisional), (events[^1].EventType, refused.Reason, refused.Withdraws));
        Assert.DoesNotContain(events, e => e.EventType == "verified");
    }

    [Fact]
    public async Task Closing_the_request_stops_the_model_call()
    {
        var chat = new PiecesChat("{\"answer\": \"Facts") { Hang = true };
        await using var server = await Server.StartAsync(new GraphRagAnswerer(new FixedRetriever(Evidence), () => chat));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/graphrag/ask") { Content = JsonContent.Create(new { question = "q" }) };
        using (var response = await server.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead))
        {
            await using var stream = await response.Content.ReadAsStreamAsync();
            await foreach (var item in SseParser.Create(stream).EnumerateAsync())
                if (item.EventType == "delta") break;
        }
        // Disposing the response closes the connection; the server's request token cancels the model call.
        Assert.True(await chat.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(20)));
    }

    [Theory]
    [InlineData(false, "retrieval-failed")]
    [InlineData(true, "model-failed")]
    public async Task A_failure_ends_the_stream_with_a_code_and_never_the_exception_text(bool afterRetrieval, string code)
    {
        IGraphRagAnswers answers = afterRetrieval
            ? new GraphRagAnswerer(new FixedRetriever(Evidence), () => new PiecesChat("{\"answer\": \"x") { Fail = true })
            : new GraphRagAnswerer(new FailingRetriever(), () => new PiecesChat("{}"));
        await using var server = await Server.StartAsync(answers);
        using var response = await server.Client.PostAsJsonAsync("/graphrag/ask", new { question = "q" });
        var events = await ReadEvents(response);
        Assert.Equal("error", events[^1].EventType);
        Assert.Equal(code, Data<GraphRagErrorEvent>(events[^1]).Code);
        Assert.DoesNotContain(events, e => e.Data.Contains("secret detail", StringComparison.Ordinal));
        Assert.Equal(afterRetrieval, events.Any(e => e.EventType == "retrieved"));
    }

    [Fact]
    public async Task The_retrieved_event_carries_the_trace_id_when_tracing_is_on()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(listener);
        await using var server = await Server.StartAsync(new GraphRagAnswerer(new FixedRetriever(Evidence), () => new PiecesChat("{\"insufficient\": \"no\"}")));
        using var response = await server.Client.PostAsJsonAsync("/graphrag/ask", new { question = "q" });
        var retrieved = Data<GraphRagRetrievedEvent>((await ReadEvents(response))[0]);
        Assert.Matches("^[0-9a-f]{32}$", retrieved.TraceId);
    }

    [Theory]
    [InlineData("/graphrag/ask", """{"question": ""}""")]
    [InlineData("/graphrag/ask", """{"question": "   "}""")]
    [InlineData("/graphrag/ask", """{"question": "eleven char"}""")]
    [InlineData("/graphrag/ask", """{}""")]
    [InlineData("/graphrag/ask", """{"question": "q", "history": [{"question": " "}]}""")]
    [InlineData("/graphrag/ask", """{"question": "q", "history": [{"question": "eleven char"}]}""")]
    [InlineData("/graphrag/ask", """{"question": "q", "history": [{"question": "q?", "answer": "an answer of more than forty characters, four times ten"}]}""")]
    [InlineData("/graphrag/ask", """{"question": "q", "history": [{"question": "q?"}, {"question": "q?"}, {"question": "q?"}, {"question": "q?"}, {"question": "q?"}, {"question": "q?"}, {"question": "q?"}, {"question": "q?"}, {"question": "q?"}, {"question": "q?"}, {"question": "q?"}, {"question": "q?"}, {"question": "q?"}, {"question": "q?"}, {"question": "q?"}, {"question": "q?"}, {"question": "q?"}, {"question": "q?"}, {"question": "q?"}, {"question": "q?"}, {"question": "q?"}]}""")]
    [InlineData("/graphrag/search", """{"query": ""}""")]
    [InlineData("/graphrag/search", """{"query": "graphs", "limit": 0}""")]
    [InlineData("/graphrag/search", """{"query": "graphs", "limit": 21}""")]
    public async Task Bad_requests_get_a_400_before_any_work(string path, string body)
    {
        var chat = new PiecesChat("{}");
        var retriever = new FixedRetriever(Evidence);
        await using var server = await Server.StartAsync(new GraphRagAnswerer(retriever, () => chat), o => o.MaxQuestionLength = 10, retriever);
        using var response = await server.Client.PostAsync(path, new StringContent(body, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal((0, 0), (chat.Calls, retriever.Calls));
    }

    [Fact]
    public async Task A_follow_up_with_its_history_is_rewritten_and_the_retrieved_event_says_how()
    {
        // GR-57: the client keeps the conversation; the server rewrites the follow-up before retrieving it.
        var chat = new PiecesChat("{\"answer\": \"Facts.\", \"citations\": [{\"evidence\": 1, \"quote\": \"Graphs connect facts\"}]}")
        {
            Rewrite = """{"question": "What do graphs connect, rather than rows?"}"""
        };
        var retriever = new FixedRetriever(Evidence);
        await using var server = await Server.StartAsync(new GraphRagAnswerer(retriever, () => chat), retriever: retriever);
        using var response = await server.Client.PostAsJsonAsync("/graphrag/ask",
            new { question = "And what do they connect?", history = new[] { new { question = "Do graphs store rows?", answer = "No." } } });
        var events = await ReadEvents(response);
        Assert.Equal(["retrieved", "delta", "verified"], events.Select(e => e.EventType));
        Assert.Equal("What do graphs connect, rather than rows?", retriever.LastQuestion);
        var rewrite = Data<GraphRagRetrievedEvent>(events[0]).Rewrite!;
        Assert.Equal(("And what do they connect?", "What do graphs connect, rather than rows?", 1, "rewritten"), (rewrite.Asked, rewrite.Standalone, rewrite.Turns, rewrite.Outcome));
        Assert.Equal(2, Data<GraphRagVerifiedEvent>(events[^1]).Receipt!.ChatCalls);
    }

    [Fact]
    public async Task Search_returns_passages_with_exact_spans_within_the_limit()
    {
        var retriever = new FixedRetriever(Evidence);
        await using var server = await Server.StartAsync(new GraphRagAnswerer(retriever, () => null), retriever: retriever);
        using var response = await server.Client.PostAsJsonAsync("/graphrag/search", new { query = "graphs", limit = 3 });
        var result = (await response.Content.ReadFromJsonAsync<GraphRagSearchResponse>(JsonSerializerOptions.Web))!;
        var passage = Assert.Single(result.Passages);
        Assert.Equal(("doc-1", "c1", 10, Evidence.Items[0].EndByte, "Graphs connect facts — not rows."),
            (passage.DocumentId, passage.ChunkId, passage.StartByte, passage.EndByte, passage.Text));
        Assert.Equal(3, retriever.LastMaxItems);
    }

    [Fact]
    public async Task Each_request_gets_the_scope_the_application_decides()
    {
        var retriever = new FixedRetriever(Evidence);
        var tenant = GraphRagScope.Documents("doc-1");
        await using var server = await Server.StartAsync(new GraphRagAnswerer(retriever, () => new PiecesChat("{\"insufficient\": \"no\"}")),
            o => o.ScopeFor = http => http.Request.Headers["X-Tenant"] == "a" ? tenant : GraphRagScope.Documents(), retriever);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/graphrag/search") { Content = JsonContent.Create(new { query = "graphs" }) };
        request.Headers.Add("X-Tenant", "a");
        using (var response = await server.Client.SendAsync(request)) Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Same(tenant, retriever.LastScope);

        using (var response = await server.Client.PostAsJsonAsync("/graphrag/ask", new { question = "q" })) await ReadEvents(response);
        Assert.Equal("documents (0)", retriever.LastScope?.ToString());
    }

    [Fact]
    public async Task A_document_outside_the_request_scope_is_not_found_and_the_database_is_not_read()
    {
        // The context's driver points at nothing: a document read would fail, so a 404 shows the scope refused it first.
        await using var server = await Server.StartAsync(new GraphRagAnswerer(new FixedRetriever(Evidence), () => null),
            o => o.ScopeFor = _ => GraphRagScope.Documents("doc-1"));
        using var response = await server.Client.GetAsync("/graphrag/documents/doc-2");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static async Task<SseItem<string>[]> ReadEvents(HttpResponseMessage response)
    {
        await using var stream = await response.Content.ReadAsStreamAsync();
        var items = new List<SseItem<string>>();
        await foreach (var item in SseParser.Create(stream).EnumerateAsync()) items.Add(item);
        return [.. items];
    }

    private static T Data<T>(SseItem<string> item) => JsonSerializer.Deserialize<T>(item.Data, JsonSerializerOptions.Web)!;

    private sealed class Server : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private Server(WebApplication app, HttpClient client) => (_app, Client) = (app, client);
        public HttpClient Client { get; }

        public static async Task<Server> StartAsync(IGraphRagAnswers answers, Action<GraphRagEndpointOptions>? configure = null, IGraphRagRetriever? retriever = null)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            builder.Services.AddSingleton(answers).AddSingleton(retriever ?? new FixedRetriever(Evidence))
                .AddDbContext<EndpointContext>(o => o.UseNeo4j(GraphDatabase.Driver("neo4j://localhost:7687", AuthTokens.None), "neo4j"));
            var app = builder.Build();
            app.MapGraphRag<EndpointContext>("/graphrag", configure);
            await app.StartAsync();
            return new(app, new HttpClient { BaseAddress = new Uri(app.Urls.Single()) });
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.DisposeAsync();
        }
    }

    public sealed class EndpointContext(DbContextOptions<EndpointContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.AddGraphRag(8);
    }

    private sealed class FixedRetriever(GraphRagEvidence evidence) : IGraphRagRetriever
    {
        public int Calls;
        public int LastMaxItems;
        public GraphRagScope? LastScope;
        public string? LastQuestion;

        public Task<GraphRagEvidence> RetrieveAsync(string question, GraphRagRetrievalOptions? options = null, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            LastMaxItems = options?.MaxItems ?? 0;
            LastScope = options?.Scope;
            LastQuestion = question;
            return Task.FromResult(evidence);
        }
    }

    private sealed class FailingRetriever : IGraphRagRetriever
    {
        public Task<GraphRagEvidence> RetrieveAsync(string question, GraphRagRetrievalOptions? options = null, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("secret detail from the database");
    }

    // Streams its pieces; then hangs until cancelled, or fails, when asked to.
    private sealed class PiecesChat(params string[] pieces) : IChatClient
    {
        public int Calls;
        public bool Hang { get; init; }
        public bool Fail { get; init; }
        public TaskCompletionSource<bool> Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>The reply to a follow-up's rewrite, the one call that isn't streamed.</summary>
        public string? Rewrite { get; init; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Rewrite is { } rewrite ? Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, rewrite))) : throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            foreach (var piece in pieces)
            {
                await Task.Yield();
                yield return new ChatResponseUpdate(ChatRole.Assistant, piece);
            }
            if (Fail) throw new HttpRequestException("secret detail from the model provider");
            if (!Hang) yield break;
            try { await Task.Delay(Timeout.Infinite, cancellationToken); }
            finally { Cancelled.TrySetResult(cancellationToken.IsCancellationRequested); }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
