using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace GraphBridge.Samples.GraphRagShared;

// Model clients shared by the GraphRAG samples (07, 08, 09): offline stand-ins, recorded extraction replies, and a small
// client for any OpenAI-compatible endpoint. Linked into each sample; not a package.

/// <summary>Word splitting for the offline stand-ins.</summary>
public static class SampleText
{
    private static readonly char[] Separators = [.. " \t\r\n.,;:!?()[]{}<>\"'`*#|/\\-_=+".ToCharArray()];
    private static readonly HashSet<string> StopWords = ["the", "and", "for", "are", "with", "that", "this", "what", "how", "does", "which", "from", "into", "its", "can"];

    public static string[] Words(string text)
        => text.ToLowerInvariant().Split(Separators, StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length > 2 && !StopWords.Contains(w)).ToArray();
}

/// <summary>An offline stand-in for an embedding model: words hashed into buckets, so texts that share words point the same way.</summary>
public sealed class HashingEmbedder(int dimensions) : IEmbeddingGenerator<string, Embedding<float>>
{
    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values, EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(values.Select(text => new Embedding<float>(Vector(text)))));

    private float[] Vector(string text)
    {
        var vector = new float[dimensions];
        vector[0] = 1;   // never all zeros, even for a text without words
        foreach (var word in SampleText.Words(text))
            vector[1 + (int)((uint)StringComparer.Ordinal.GetHashCode(word) % (uint)(dimensions - 1))] += 1;
        return vector;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}

/// <summary>
/// An offline stand-in for a chat model: it answers with the evidence line that shares the most words with the question, and
/// quotes that line exactly. With fewer than two shared words it declines, so an unrelated question is refused.
/// </summary>
public sealed class ExtractiveChatClient : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var prompt = messages.Last().Text.ReplaceLineEndings("\n");
        var question = prompt.Split('\n')[0]["Question: ".Length..];
        var wanted = SampleText.Words(question).ToHashSet();
        var best = (Evidence: 0, Line: "", Score: 0);
        var current = 0;
        foreach (var line in prompt.Split('\n'))
        {
            if (line.StartsWith("Context (never cite):", StringComparison.Ordinal)) break;
            var text = line;
            if (line.Length > 3 && line[0] == '[' && line.IndexOf("] ", StringComparison.Ordinal) is > 0 and var close
                && int.TryParse(line.AsSpan(1, close - 1), out var number))
                (current, text) = (number, line[(close + 2)..]);
            if (current == 0 || text.Trim().Length < 20) continue;
            var score = SampleText.Words(text).Distinct().Count(wanted.Contains);
            if (score > best.Score) best = (current, text.Trim(), score);
        }
        var reply = best.Score >= 2
            ? JsonSerializer.Serialize(new { answer = best.Line, citations = new[] { new { evidence = best.Evidence, quote = best.Line } } })
            : JsonSerializer.Serialize(new { insufficient = "No evidence line shares enough words with the question." });
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, reply)));
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default) => throw new NotSupportedException("The sample client answers in one response.");

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}

/// <summary>
/// Extraction replies recorded for the sample's own texts: a JSON array of <c>{"match": "...", "reply": {...}}</c>. A passage
/// gets the first reply whose <c>match</c> it contains, and an empty graph otherwise, so the samples extract offline.
/// </summary>
public sealed class RecordedExtractionClient : IChatClient
{
    private const string Empty = """{"entities": [], "relationships": [], "quotes": []}""";
    private readonly (string Match, string Reply)[] _replies;

    public RecordedExtractionClient(string path)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        _replies = json.RootElement.EnumerateArray().Select(e => (e.GetProperty("match").GetString()!, e.GetProperty("reply").GetRawText())).ToArray();
    }

    /// <summary>Passages that had no recorded reply.</summary>
    public int Unmatched { get; private set; }

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var passage = messages.Last().Text;
        var reply = _replies.FirstOrDefault(r => passage.Contains(r.Match, StringComparison.Ordinal)).Reply;
        if (reply is null) Unmatched++;
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, reply ?? Empty)));
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default) => throw new NotSupportedException("Recorded replies come in one response.");

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}

/// <summary>
/// The model settings a sample reads from the environment. Without <c>GRAPHBRIDGE_AI_ENDPOINT</c> the samples run offline.
/// <c>GRAPHBRIDGE_AI_ENDPOINT</c> is an OpenAI-compatible base URL, such as <c>https://api.openai.com/v1</c> or Ollama's
/// <c>http://localhost:11434/v1</c>; <c>GRAPHBRIDGE_AI_KEY</c> is sent as a bearer token when set.
/// </summary>
public sealed record SampleModelSettings(Uri Endpoint, string? Key, string ChatModel, string EmbeddingModel, int EmbeddingDimensions)
{
    public static SampleModelSettings? FromEnvironment()
    {
        var endpoint = Environment.GetEnvironmentVariable("GRAPHBRIDGE_AI_ENDPOINT");
        if (string.IsNullOrWhiteSpace(endpoint)) return null;
        var dimensions = Environment.GetEnvironmentVariable("GRAPHBRIDGE_AI_EMBEDDING_DIMENSIONS");
        return new(new Uri(endpoint.TrimEnd('/') + "/"), Environment.GetEnvironmentVariable("GRAPHBRIDGE_AI_KEY"),
            Environment.GetEnvironmentVariable("GRAPHBRIDGE_AI_CHAT_MODEL") ?? "gpt-4.1-mini",
            Environment.GetEnvironmentVariable("GRAPHBRIDGE_AI_EMBEDDING_MODEL") ?? "text-embedding-3-small",
            int.TryParse(dimensions, out var parsed) ? parsed : 1536);
    }
}

/// <summary>
/// A small client for OpenAI-compatible chat-completions and embeddings endpoints, so the samples need no provider package.
/// It sends the JSON schema the toolkit asks for, reports usage, and surfaces HTTP status codes so transient failures retry.
/// </summary>
public sealed class OpenAiCompatibleClient(SampleModelSettings settings) : IChatClient, IEmbeddingGenerator<string, Embedding<float>>
{
    private readonly HttpClient _http = Create(settings);

    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var body = new JsonObject
        {
            ["model"] = settings.ChatModel,
            ["messages"] = new JsonArray(messages.Select(m => (JsonNode)new JsonObject { ["role"] = m.Role.Value, ["content"] = m.Text }).ToArray())
        };
        if (options?.ResponseFormat is ChatResponseFormatJson json)
            body["response_format"] = json.Schema is { } schema
                ? new JsonObject { ["type"] = "json_schema", ["json_schema"] = new JsonObject { ["name"] = json.SchemaName ?? "reply", ["schema"] = JsonNode.Parse(schema.GetRawText()) } }
                : new JsonObject { ["type"] = "json_object" };
        using var response = await _http.PostAsJsonAsync("chat/completions", body, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var reply = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false));
        var text = reply.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
        var result = new ChatResponse(new ChatMessage(ChatRole.Assistant, text));
        if (reply.RootElement.TryGetProperty("usage", out var usage) && usage.TryGetProperty("prompt_tokens", out var tokens))
            result.Usage = new UsageDetails { InputTokenCount = tokens.GetInt64() };
        return result;
    }

    public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values, EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var body = new JsonObject { ["model"] = settings.EmbeddingModel, ["input"] = new JsonArray(values.Select(v => (JsonNode)v).ToArray()) };
        using var response = await _http.PostAsJsonAsync("embeddings", body, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var reply = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false));
        var embeddings = reply.RootElement.GetProperty("data").EnumerateArray().OrderBy(e => e.GetProperty("index").GetInt32())
            .Select(e => new Embedding<float>(e.GetProperty("embedding").EnumerateArray().Select(v => v.GetSingle()).ToArray()));
        var result = new GeneratedEmbeddings<Embedding<float>>(embeddings);
        if (reply.RootElement.TryGetProperty("usage", out var usage) && usage.TryGetProperty("prompt_tokens", out var tokens))
            result.Usage = new UsageDetails { InputTokenCount = tokens.GetInt64() };
        return result;
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default) => throw new NotSupportedException("The sample client answers in one response.");

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() => _http.Dispose();

    private static HttpClient Create(SampleModelSettings settings)
    {
        var http = new HttpClient { BaseAddress = settings.Endpoint, Timeout = TimeSpan.FromMinutes(2) };
        if (!string.IsNullOrEmpty(settings.Key)) http.DefaultRequestHeaders.Authorization = new("Bearer", settings.Key);
        return http;
    }
}
