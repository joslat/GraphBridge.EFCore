using System.Net;
using System.Text.Json;
using GraphBridge.GraphRag.Ontologies;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

namespace GraphBridge.GraphRag.Tests;

/// <summary>
/// GraphRAG v2, wave 7 (GR-63 and GR-48), offline with fault-injecting fakes: retry hints, backoff, the rate-limit code and the
/// receipt's retry counts; bounded parallel extraction; a second ask for an unreadable reply; strict schema mode.
/// </summary>
public sealed class GraphRagResilienceTests
{
    private const string Passage = "Alice met the White Rabbit near the river.";
    private const string Reply = """{"entities": [{"kind": "Character", "name": "Alice", "aliases": [], "description": null, "mentions": ["Alice"]}], "relationships": [], "quotes": []}""";

    [Fact]
    public async Task A_retry_hint_is_honoured_counted_and_reported_as_a_rate_limit()
    {
        var limited = new HttpRequestException("slow down", null, HttpStatusCode.TooManyRequests);
        limited.Data[GraphRagRetryOptions.RetryAfterKey] = TimeSpan.FromMilliseconds(120);
        var generator = new FakeGenerator(4) { Failures = new Queue<Exception>([limited]) };
        var retries = new GraphRagRetryTally();
        var started = DateTime.UtcNow;
        Assert.Single(await GraphRagEmbeddingTests.Ingestion(generator).EmbedAsync(["a"], new(), retries, default));
        Assert.True(DateTime.UtcNow - started >= TimeSpan.FromMilliseconds(110));
        Assert.Equal(new GraphRagRetrySummary(1, 1, TimeSpan.FromMilliseconds(120)), retries.Summary());

        // A hint found on an inner exception counts too.
        var wrapped = new InvalidOperationException("client", limited);
        Assert.True(GraphRagRetry.IsTransient(limited));
        var options = new GraphRagRetryOptions { IsTransient = _ => true, BaseDelay = TimeSpan.FromMilliseconds(1) };
        var tally = new GraphRagRetryTally();
        var calls = 0;
        await GraphRagRetry.RunAsync(_ => ++calls == 1 ? throw wrapped : Task.FromResult(1), options, tally, TimeProvider.System, "x", (c, _) => new Exception(c), default);
        Assert.Equal(TimeSpan.FromMilliseconds(120), tally.Summary().Waited);
    }

    [Fact]
    public async Task A_failure_that_is_not_retried_keeps_its_cause()
    {
        // The code alone wouldn't say why, such as a 401 from a wrong key.
        var denied = new HttpRequestException("denied", null, HttpStatusCode.Unauthorized);
        var failure = await Assert.ThrowsAsync<GraphRagEmbeddingException>(() => GraphRagRetry.RunAsync<int>(_ => throw denied, new GraphRagRetryOptions(),
            new GraphRagRetryTally(), TimeProvider.System, "embedding-failed", (code, error) => new GraphRagEmbeddingException(code, error), default));
        Assert.Equal(("embedding-failed", (Exception)denied), (failure.Message, failure.InnerException!));
    }

    [Fact]
    public async Task A_hint_longer_than_the_longest_wait_fails_at_once_with_the_rate_limit_code()
    {
        var limited = new HttpRequestException("quota", null, HttpStatusCode.TooManyRequests);
        limited.Data[GraphRagRetryOptions.RetryAfterKey] = TimeSpan.FromHours(1);
        var generator = new FakeGenerator(4) { Failures = new Queue<Exception>([limited]) };
        var error = await Assert.ThrowsAsync<GraphRagEmbeddingException>(() => GraphRagEmbeddingTests.Ingestion(generator).EmbedAsync(["a"], new(), new(), default));
        Assert.Equal(("rate-limited", 1), (error.ErrorCode, generator.Calls));
    }

    [Fact]
    public void Backoff_doubles_from_the_base_with_at_most_a_quarter_jitter_and_stops_at_the_longest_wait()
    {
        var options = new GraphRagRetryOptions { BaseDelay = TimeSpan.FromMilliseconds(100), MaxDelay = TimeSpan.FromMilliseconds(500) };
        for (var i = 0; i < 50; i++)
        {
            Assert.InRange(GraphRagRetry.Backoff(options, 0).TotalMilliseconds, 100, 125);
            Assert.InRange(GraphRagRetry.Backoff(options, 1).TotalMilliseconds, 200, 250);
            Assert.InRange(GraphRagRetry.Backoff(options, 2).TotalMilliseconds, 400, 500);
            Assert.Equal(500, GraphRagRetry.Backoff(options, 9).TotalMilliseconds);
        }
        Assert.True(GraphRagRetry.IsTransient(new HttpRequestException(HttpRequestError.ConnectionError, "reset")));
        Assert.False(GraphRagRetry.IsTransient(new HttpRequestException("bad", null, HttpStatusCode.BadRequest)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GraphRagRetryOptions { MaxRetries = 11 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new GraphRagRetryOptions { MaxDelay = TimeSpan.FromHours(1) }.Validate());
    }

    [Fact]
    public async Task Extraction_retries_transient_failures_within_its_lanes_and_reports_the_waits()
    {
        var chat = new FaultyChat(_ => Reply) { Failures = new Queue<Exception>([new HttpRequestException("busy", null, HttpStatusCode.ServiceUnavailable), new TimeoutException()]) };
        var passages = Enumerable.Range(0, 6).Select(i => $"{Passage} ({i})").ToArray();
        var (graph, tally, retries) = await ExtractAsync(passages, chat, new GraphRagExtractionOptions { Lanes = 3 });
        Assert.Single(graph.Entities.Single().Value);
        Assert.Equal((8, 6), (tally.Calls, tally.Chunks));
        Assert.Equal((2, 0), (retries.Summary().Retries, retries.Summary().RateLimited));
        Assert.InRange(chat.MaxConcurrent, 1, 3);

        // Output tokens are summed when the client reports them; a reasoning model's include its reasoning.
        Assert.Equal((60L, 30L), (tally.Tokens, tally.OutputTokens));
    }

    [Fact]
    public async Task An_unreadable_reply_is_asked_again_with_what_was_wrong_and_a_cut_off_one_is_called_truncated()
    {
        var replies = new Queue<(string, ChatFinishReason)>([("{\"entities\": [", ChatFinishReason.Length), (Reply, ChatFinishReason.Stop)]);
        var chat = new FaultyChat(_ => "") { Replies = replies };
        var (graph, tally, _) = await ExtractAsync([Passage], chat);
        Assert.Single(graph.Entities);
        Assert.Equal((2, 1, 0), (tally.Calls, tally.Repaired, tally.Quarantined.Count));
        Assert.Equal([2, 3], chat.MessageCounts);
        Assert.StartsWith("Your reply to this passage couldn't be used: it was cut off at the output limit", chat.LastNote);

        // Cut off twice: quarantined as truncated.
        chat = new FaultyChat(_ => "") { Replies = new Queue<(string, ChatFinishReason)>([("{\"entities\": [", ChatFinishReason.Length), ("{\"ent", ChatFinishReason.Length)]) };
        (graph, tally, _) = await ExtractAsync([Passage], chat);
        Assert.Empty(graph.Entities);
        Assert.Equal(("truncated", 0), (Assert.Single(tally.Quarantined).Error, tally.Repaired));
    }

    [Fact]
    public async Task Strict_schema_mode_is_asked_for_by_default_and_the_schema_meets_its_rules()
    {
        var chat = new FaultyChat(_ => Reply);
        await ExtractAsync([Passage], chat);
        Assert.Equal(true, chat.Options!.AdditionalProperties!["strict"]);
        chat = new FaultyChat(_ => Reply);
        await ExtractAsync([Passage], chat, new GraphRagExtractionOptions { StrictSchema = false });
        Assert.Null(chat.Options!.AdditionalProperties);

        // Every object is closed and requires all of its fields; optional ones are nullable.
        foreach (var ontology in new[] { GraphRagOntologies.Generic, GraphRagOntologies.Novel, GraphRagOntologies.Article })
        {
            using var db = new GraphRagOntologyTests.OntologyContext(ontology);
            using var json = JsonDocument.Parse(GraphRagModelSchema.From(db.Model).ToJsonSchema());
            foreach (var item in Objects(json.RootElement))
            {
                Assert.False(item.GetProperty("additionalProperties").GetBoolean());
                Assert.Equal(item.GetProperty("properties").EnumerateObject().Select(p => p.Name), item.GetProperty("required").EnumerateArray().Select(r => r.GetString()));
            }
            var entity = json.RootElement.GetProperty("properties").GetProperty("entities").GetProperty("items").GetProperty("properties");
            Assert.Equal(["string", "null"], entity.GetProperty("description").GetProperty("type").EnumerateArray().Select(t => t.GetString()));
        }

        static IEnumerable<JsonElement> Objects(JsonElement schema)
        {
            if (schema.ValueKind != JsonValueKind.Object) yield break;
            if (schema.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == "object") yield return schema;
            foreach (var property in schema.EnumerateObject())
                foreach (var nested in property.Value.ValueKind == JsonValueKind.Object ? Objects(property.Value) : [])
                    yield return nested;
        }
    }

    [Fact]
    public async Task Cancelling_stops_extraction_without_a_retry()
    {
        using var cancel = new CancellationTokenSource();
        var chat = new FaultyChat(_ => { cancel.Cancel(); return Reply; });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ExtractAsync([Passage, Passage + " Again."], chat, cancellationToken: cancel.Token));
        Assert.InRange(chat.Calls, 1, 2);
    }

    private static async Task<(GraphRagExtractedGraph Graph, GraphRagExtractionTally Tally, GraphRagRetryTally Retries)> ExtractAsync(string[] passages,
        FaultyChat chat, GraphRagExtractionOptions? options = null, CancellationToken cancellationToken = default)
    {
        using var db = new GraphRagOntologyTests.OntologyContext(GraphRagOntologies.Novel);
        var extractor = new GraphRagExtractor(GraphRagModelSchema.From(db.Model), chat, options ?? new GraphRagExtractionOptions(),
            new GraphRagRetryOptions { BaseDelay = TimeSpan.FromMilliseconds(1) });
        var items = passages.Select((p, i) => new GraphRagEvidenceItem($"chunk-{i}", "doc-1", p, 0, System.Text.Encoding.UTF8.GetByteCount(p), 0, [])).ToArray();
        var tally = new GraphRagExtractionTally();
        var retries = new GraphRagRetryTally();
        var graph = await extractor.ExtractAsync("doc-1", null, "run-1", items, tally, retries, cancellationToken);
        return (graph, tally, retries);
    }

    /// <summary>A chat client that fails, cuts off or answers as told, reports usage, and records how it was asked.</summary>
    private sealed class FaultyChat(Func<string, string> reply) : IChatClient
    {
        private int _active;
        public int Calls;
        public int MaxConcurrent;
        public Queue<Exception> Failures { get; init; } = new();
        public Queue<(string Text, ChatFinishReason Finish)> Replies { get; init; } = new();
        public List<int> MessageCounts { get; } = [];
        public string LastNote { get; private set; } = "";
        public ChatOptions? Options { get; private set; }

        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            var active = Interlocked.Increment(ref _active);
            lock (MessageCounts) MaxConcurrent = Math.Max(MaxConcurrent, active);
            try
            {
                await Task.Delay(10, cancellationToken);
                var list = messages.ToList();
                lock (MessageCounts)
                {
                    if (Failures.Count > 0) throw Failures.Dequeue();
                    MessageCounts.Add(list.Count);
                    Options = options;
                    if (list.Count > 2) LastNote = list[^1].Text;
                }
                var (text, finish) = Replies.Count > 0 ? Replies.Dequeue() : (reply(list[1].Text), ChatFinishReason.Stop);
                return new ChatResponse(new ChatMessage(ChatRole.Assistant, text))
                {
                    FinishReason = finish, Usage = new UsageDetails { InputTokenCount = 10, OutputTokenCount = 5 }
                };
            }
            finally { Interlocked.Decrement(ref _active); }
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
