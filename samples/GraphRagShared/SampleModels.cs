using System.Globalization;
using System.Runtime.CompilerServices;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using GraphBridge.GraphRag;
using Microsoft.Extensions.AI;

namespace GraphBridge.Samples.GraphRagShared;

// Model clients shared by the GraphRAG samples (07, 08, 09): offline stand-ins, extraction replies written for sample 09, and a
// small client for any OpenAI-compatible endpoint. Recorded real-model calls are in ModelRecordings.cs. Linked into each
// sample; not a package.

/// <summary>Word splitting for the offline stand-ins.</summary>
public static class SampleText
{
    private static readonly char[] Separators = [.. " \t\r\n.,;:!?()[]{}<>\"'`*#|/\\-_=+".ToCharArray()];
    private static readonly HashSet<string> StopWords = ["the", "and", "for", "are", "with", "that", "this", "what", "how", "does", "which", "from", "into", "its", "can"];

    public static string[] Words(string text)
        => text.ToLowerInvariant().Split(Separators, StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length > 2 && !StopWords.Contains(w)).ToArray();
}

/// <summary>
/// An offline stand-in for an embedding model: words hashed into buckets, so texts that share words point the same way. The
/// hash is FNV-1a over each word's UTF-8 bytes, so a vector is the same in every process, and the interop suite's Python port
/// (tests/interop/python) computes the same one.
/// </summary>
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
            vector[1 + (int)(Fnv1a(word) % (uint)(dimensions - 1))] += 1;
        return vector;
    }

    // string.GetHashCode differs between processes; this doesn't.
    internal static uint Fnv1a(string word)
    {
        var hash = 2166136261u;
        foreach (var b in System.Text.Encoding.UTF8.GetBytes(word)) hash = (hash ^ b) * 16777619u;
        return hash;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}

/// <summary>
/// An offline stand-in for an agent's model: it asks <c>answer_from_documents</c> the user's question, then repeats what the
/// tool returned. A real model decides for itself which tool to call; this one shows the tool path without a model.
/// </summary>
public sealed class AnswerToolStandIn : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var list = messages.ToList();
        var result = list.SelectMany(m => m.Contents).OfType<FunctionResultContent>().LastOrDefault();
        var question = list.Last(m => m.Role == ChatRole.User).Text;
        return Task.FromResult(result is null
            ? new ChatResponse(new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent("call-1", GraphRagTools.AnswerTool, new Dictionary<string, object?> { ["question"] = question })]))
            : new ChatResponse(new ChatMessage(ChatRole.Assistant, "From the documents: " + ModelRecordings.ResultText(result.Result))));
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default) => ReplyPieces.StreamAsync(GetResponseAsync(messages, options, cancellationToken), cancellationToken);

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
        CancellationToken cancellationToken = default) => ReplyPieces.StreamAsync(GetResponseAsync(messages, options, cancellationToken), cancellationToken);

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}

/// <summary>
/// Extraction replies written by hand for a sample's own texts, not recorded from a model: a JSON array of
/// <c>{"match": "...", "reply": {...}}</c>. A passage gets the first reply whose <c>match</c> it contains, and an empty graph
/// otherwise, so the sample extracts offline.
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
/// The models a sample uses, chosen with the environment contract that AgentEval and AgentMemory share:
/// <list type="bullet">
/// <item><c>AI_INFERENCE_PROVIDER</c> names the provider: <c>bitdeer</c>, <c>openai</c>, <c>openai-compatible</c> or
/// <c>ollama</c>. Bitdeer needs only <c>BITDEER_API_KEY</c>; <c>BITDEER_ENDPOINT</c>, <c>BITDEER_MODEL</c> and
/// <c>BITDEER_EMBEDDING_MODEL</c> default to <c>https://api-inference.bitdeer.ai/v1</c>, <c>zai-org/GLM-5.3-Flash</c> and
/// <c>BAAI/bge-m3</c>. A named provider with a variable missing fails, naming the variable and never a value. Unlike those
/// tools, the samples never pick a provider from keys that happen to be set: without <c>AI_INFERENCE_PROVIDER</c> they stay
/// offline, so a test run never spends money by accident.</item>
/// <item><c>GRAPHBRIDGE_AI_MODE=replay</c> replays the sample's recorded calls offline, at zero calls; a request that wasn't
/// recorded fails.</item>
/// <item>Neither: null, and the sample runs offline with stand-ins.</item>
/// </list>
/// <c>AI_EMBEDDING_DIMENSIONS</c> gives the embedding width for a model the samples don't know (they know bge-m3, 1,024, and
/// OpenAI's text-embedding-3-small and ada-002, 1,536). <c>GRAPHBRIDGE_AI_REASONING_EFFORT</c> (<c>low</c>, <c>medium</c> or
/// <c>high</c>) asks a reasoning model for that effort on every chat call that doesn't set its own.
/// </summary>
public sealed record SampleModelSettings(string Provider, Uri? Endpoint, string? Key, string ChatModel, string EmbeddingModel, int EmbeddingDimensions,
    string? ReplayFile = null)
{
    public ReasoningEffort? ReasoningEffort { get; init; } = Environment.GetEnvironmentVariable("GRAPHBRIDGE_AI_REASONING_EFFORT")?.ToLowerInvariant() switch
    {
        "low" => Microsoft.Extensions.AI.ReasoningEffort.Low,
        "medium" => Microsoft.Extensions.AI.ReasoningEffort.Medium,
        "high" => Microsoft.Extensions.AI.ReasoningEffort.High,
        _ => null
    };

    /// <summary>The model and where it ran, such as <c>zai-org/GLM-5.3-Flash@bitdeer</c>: the same model on two hosts isn't the same measurement.</summary>
    public string ModelIdentity => ChatModel + "@" + Provider;

    public static SampleModelSettings? FromEnvironment(string recordingsPath) => FromEnvironment(recordingsPath, Environment.GetEnvironmentVariable);

    public static SampleModelSettings? FromEnvironment(string recordingsPath, Func<string, string?> environment)
    {
        string? Read(string name) => environment(name) is { } value && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;
        var provider = Read("AI_INFERENCE_PROVIDER")?.ToLowerInvariant();
        if (provider is null)
        {
            if (Read("GRAPHBRIDGE_AI_MODE") != "replay") return null;
            var recordings = ModelRecordings.Load(recordingsPath);
            var (chat, embedding, recorded) = recordings.Models
                ?? throw new InvalidOperationException($"GRAPHBRIDGE_AI_MODE is replay, but {recordingsPath} has no recordings.");
            return new(recordings.Provider ?? "recorded", null, null, chat, embedding, recorded, recordingsPath);
        }
        // Said before any call is made: a provider named for every tool on the machine would otherwise turn a replay into paid calls.
        if (Read("GRAPHBRIDGE_AI_MODE") == "replay")
            Console.Error.WriteLine($"GRAPHBRIDGE_AI_MODE=replay is ignored: AI_INFERENCE_PROVIDER={provider} is set, so the calls go to the model. Remove AI_INFERENCE_PROVIDER to replay.");
        var (endpointVariable, keyVariable, modelVariable, embeddingVariable, defaultEndpoint, defaultModel, defaultEmbedding, keyOptional) = provider switch
        {
            "bitdeer" => ("BITDEER_ENDPOINT", "BITDEER_API_KEY", "BITDEER_MODEL", "BITDEER_EMBEDDING_MODEL",
                "https://api-inference.bitdeer.ai/v1", "zai-org/GLM-5.3-Flash", "BAAI/bge-m3", false),
            "openai" => ("OPENAI_BASE_URL", "OPENAI_API_KEY", "OPENAI_MODEL", "OPENAI_EMBEDDING_MODEL",
                "https://api.openai.com/v1", "gpt-4o-mini", "text-embedding-3-small", false),
            "openai-compatible" => ("OPENAI_COMPATIBLE_ENDPOINT", "OPENAI_COMPATIBLE_API_KEY", "OPENAI_COMPATIBLE_MODEL", "OPENAI_COMPATIBLE_EMBEDDING_MODEL",
                (string?)null, (string?)null, (string?)null, true),
            "ollama" => ("OLLAMA_ENDPOINT", "OLLAMA_API_KEY", "OLLAMA_MODEL", "OLLAMA_EMBEDDING_MODEL", "http://127.0.0.1:11434/v1", null, "bge-m3", true),
            _ => throw new InvalidOperationException("AI_INFERENCE_PROVIDER is not one the samples support: bitdeer, openai, openai-compatible or ollama. "
                + "Azure OpenAI and Foundry serve an OpenAI-compatible API at /openai/v1: name it with openai-compatible.")
        };
        var (endpoint, key, model, embeddingModel) = (Read(endpointVariable) ?? defaultEndpoint, Read(keyVariable), Read(modelVariable) ?? defaultModel,
            Read(embeddingVariable) ?? defaultEmbedding);
        int? dimensions = Read("AI_EMBEDDING_DIMENSIONS") is { } width
            ? int.TryParse(width, System.Globalization.NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed > 0 ? parsed
                : throw new InvalidOperationException("AI_EMBEDDING_DIMENSIONS is not a positive whole number.")
            : embeddingModel switch
            {
                "BAAI/bge-m3" or "bge-m3" => 1024,
                "text-embedding-3-small" or "text-embedding-ada-002" => 1536,
                _ => null
            };
        var missing = new[]
        {
            endpoint is null ? endpointVariable : null, key is null && !keyOptional ? keyVariable : null, model is null ? modelVariable : null,
            embeddingModel is null ? embeddingVariable : null, dimensions is null ? "AI_EMBEDDING_DIMENSIONS" : null
        }.OfType<string>().ToArray();
        if (missing.Length > 0)
            throw new InvalidOperationException($"AI_INFERENCE_PROVIDER={provider}, but it is missing: {string.Join(", ", missing)}.");
        if (!Uri.TryCreate(endpoint!.TrimEnd('/') + "/", UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http")
            || uri.Scheme == "http" && !uri.IsLoopback || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query))
            throw new InvalidOperationException($"{endpointVariable} must be an https URL, or http to this machine, without user information or a query.");
        return new(provider, uri, key ?? "no-key-needed", model!, embeddingModel!, dimensions!.Value);
    }

    /// <summary>The settings without the key, which a record's generated <c>ToString</c> would print.</summary>
    public override string ToString() => $"SampleModelSettings {{ Provider = {Provider}, Endpoint = {SafeEndpoint(Endpoint)}, Key = {(Key is null ? "null" : "[redacted]")}, "
        + $"ChatModel = {ChatModel}, EmbeddingModel = {EmbeddingModel}, EmbeddingDimensions = {EmbeddingDimensions}, ReplayFile = {ReplayFile} }}";

    /// <summary>An endpoint's scheme, host and port only: never user information, path, query or fragment.</summary>
    public static string SafeEndpoint(Uri? endpoint) => endpoint is null ? "(none)"
        : $"{endpoint.Scheme}://{endpoint.Host}{(endpoint.IsDefaultPort ? "" : ":" + endpoint.Port.ToString(CultureInfo.InvariantCulture))}";

    /// <summary>The chat client and embedding generator: the recordings when replaying, otherwise the endpoint's.</summary>
    public (IChatClient Chat, IEmbeddingGenerator<string, Embedding<float>> Embeddings) CreateClients()
    {
        if (ReplayFile is not null)
        {
            var recordings = ModelRecordings.Load(ReplayFile);
            return (WithReasoning(new RecordedChatClient(recordings)), new RecordedEmbeddingGenerator(recordings));
        }
        var client = new OpenAiCompatibleClient(this);
        return (WithReasoning(client), client);
    }

    /// <summary>The client, asking for <see cref="ReasoningEffort"/> on calls that don't set their own.</summary>
    public IChatClient WithReasoning(IChatClient client) => ReasoningEffort is { } effort ? new ReasoningDefault(client, effort) : client;

    // Streamed calls get the same default, so they match the same recordings.
    private sealed class ReasoningDefault(IChatClient inner, ReasoningEffort effort) : DelegatingChatClient(inner)
    {
        public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => base.GetResponseAsync(messages, WithEffort(options), cancellationToken);

        public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => base.GetStreamingResponseAsync(messages, WithEffort(options), cancellationToken);

        private ChatOptions? WithEffort(ChatOptions? options)
        {
            if (options?.Reasoning is not null) return options;
            options = options?.Clone() ?? new ChatOptions();
            options.Reasoning = new ReasoningOptions { Effort = effort };
            return options;
        }
    }

    /// <summary>Where the settings come from, without the key.</summary>
    public string Describe() => ReplayFile is not null
        ? $"replaying {ModelIdentity} and {EmbeddingModel} from {Path.GetFileName(ReplayFile)}"
        : $"{ModelIdentity} and {EmbeddingModel} at {SafeEndpoint(Endpoint)}";
}

/// <summary>
/// A small client for OpenAI-compatible chat-completions and embeddings endpoints, so the samples need no provider package.
/// It sends the JSON schema the toolkit asks for, strict when asked, and reports the model, finish reason and token usage. A
/// failed call throws <see cref="HttpRequestException"/> with its status code and any retry hint the server sent, so the
/// toolkit retries transient failures and honours rate limits.
/// </summary>
public sealed class OpenAiCompatibleClient(SampleModelSettings settings) : IChatClient, IEmbeddingGenerator<string, Embedding<float>>
{
    private readonly HttpClient _http = Create(settings);

    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        using var reply = await PostAsync("chat/completions", ChatBody(messages, options), cancellationToken).ConfigureAwait(false);
        var choice = reply.RootElement.GetProperty("choices")[0];
        var message = choice.GetProperty("message");
        var contents = new List<AIContent>();
        if (message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String && content.GetString() is { Length: > 0 } text)
            contents.Add(new TextContent(text));
        if (message.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
            foreach (var call in toolCalls.EnumerateArray())
            {
                var function = call.GetProperty("function");
                var arguments = function.TryGetProperty("arguments", out var raw) && raw.ValueKind == JsonValueKind.String && raw.GetString() is { Length: > 0 } json
                    ? JsonSerializer.Deserialize<Dictionary<string, object?>>(json, AIJsonUtilities.DefaultOptions)
                    : new Dictionary<string, object?>();
                contents.Add(new FunctionCallContent(call.GetProperty("id").GetString()!, function.GetProperty("name").GetString()!, arguments));
            }
        if (contents.Count == 0) contents.Add(new TextContent(""));
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, contents))
        {
            ModelId = reply.RootElement.TryGetProperty("model", out var model) ? model.GetString() : settings.ChatModel,
            FinishReason = choice.TryGetProperty("finish_reason", out var finish) && finish.ValueKind == JsonValueKind.String ? new ChatFinishReason(finish.GetString()!) : null,
            Usage = Usage(reply.RootElement)
        };
    }

    /// <summary>
    /// Streams the reply's text as the endpoint sends it (<c>stream: true</c>), then the token usage. Reasoning text, which some
    /// models stream separately, is not part of the reply.
    /// </summary>
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var body = ChatBody(messages, options);
        body["stream"] = true;
        body["stream_options"] = new JsonObject { ["include_usage"] = true };
        using var request = new HttpRequestMessage(HttpMethod.Post, "chat/completions") { Content = JsonContent.Create(body) };
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        await ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false));
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var data = line[5..].Trim();
            if (data == "[DONE]") yield break;
            using var chunk = JsonDocument.Parse(data);
            var root = chunk.RootElement;
            var model = root.TryGetProperty("model", out var name) ? name.GetString() : settings.ChatModel;
            if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0
                && choices[0].TryGetProperty("delta", out var delta) && delta.TryGetProperty("content", out var content)
                && content.ValueKind == JsonValueKind.String && content.GetString() is { Length: > 0 } text)
                yield return new ChatResponseUpdate(ChatRole.Assistant, text) { ModelId = model };
            if (Usage(root) is { } usage) yield return new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = [new UsageContent(usage)], ModelId = model };
        }
    }

    private JsonObject ChatBody(IEnumerable<ChatMessage> messages, ChatOptions? options)
    {
        var body = new JsonObject
        {
            ["model"] = settings.ChatModel,
            // Per-request instructions, as an agent's context provider gives them, go first as a system message.
            ["messages"] = new JsonArray((options?.Instructions is { Length: > 0 } instructions
                    ? [new ChatMessage(ChatRole.System, instructions), .. messages] : messages).SelectMany(MessageNodes).ToArray())
        };
        if (options?.Tools?.OfType<AIFunctionDeclaration>().ToArray() is { Length: > 0 } functions)
            body["tools"] = new JsonArray(functions.Select(f => (JsonNode)new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = f.Name, ["description"] = f.Description, ["parameters"] = JsonNode.Parse(f.JsonSchema.GetRawText()) }
            }).ToArray());
        if (options?.ResponseFormat is ChatResponseFormatJson json)
        {
            var strict = options.AdditionalProperties?.TryGetValue("strict", out var value) == true && value is true;
            body["response_format"] = json.Schema is { } schema
                ? new JsonObject
                {
                    ["type"] = "json_schema",
                    ["json_schema"] = new JsonObject { ["name"] = json.SchemaName ?? "reply", ["schema"] = JsonNode.Parse(schema.GetRawText()), ["strict"] = strict }
                }
                : new JsonObject { ["type"] = "json_object" };
        }
        if (options?.Reasoning?.Effort is { } effort)
            body["reasoning_effort"] = effort switch { ReasoningEffort.ExtraHigh => "high", _ => effort.ToString().ToLowerInvariant() };
        return body;
    }

    public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values, EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var body = new JsonObject { ["model"] = settings.EmbeddingModel, ["input"] = new JsonArray(values.Select(v => (JsonNode)v).ToArray()) };
        using var reply = await PostAsync("embeddings", body, cancellationToken).ConfigureAwait(false);
        var model = reply.RootElement.TryGetProperty("model", out var name) ? name.GetString() : settings.EmbeddingModel;
        var embeddings = reply.RootElement.GetProperty("data").EnumerateArray().OrderBy(e => e.GetProperty("index").GetInt32())
            .Select(e => new Embedding<float>(e.GetProperty("embedding").EnumerateArray().Select(v => v.GetSingle()).ToArray()) { ModelId = model });
        return new GeneratedEmbeddings<Embedding<float>>(embeddings) { Usage = Usage(reply.RootElement) };
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() => _http.Dispose();

    private async Task<JsonDocument> PostAsync(string path, JsonObject body, CancellationToken cancellationToken)
    {
        using var response = await _http.PostAsJsonAsync(path, body, cancellationToken).ConfigureAwait(false);
        await ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    // OpenAI's chat format: each tool result is a message of its own, and an assistant's tool calls go in tool_calls.
    private static IEnumerable<JsonNode> MessageNodes(ChatMessage message)
    {
        var results = message.Contents.OfType<FunctionResultContent>().ToArray();
        if (results.Length > 0)
        {
            foreach (var result in results)
                yield return new JsonObject { ["role"] = "tool", ["tool_call_id"] = result.CallId, ["content"] = ModelRecordings.ResultText(result.Result) };
            yield break;
        }
        var node = new JsonObject { ["role"] = message.Role.Value, ["content"] = message.Text };
        var calls = message.Contents.OfType<FunctionCallContent>().ToArray();
        if (calls.Length > 0)
        {
            if (message.Text.Length == 0) node["content"] = null;
            node["tool_calls"] = new JsonArray(calls.Select(c => (JsonNode)new JsonObject
            {
                ["id"] = c.CallId, ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = c.Name, ["arguments"] = JsonSerializer.Serialize(c.Arguments, AIJsonUtilities.DefaultOptions) }
            }).ToArray());
        }
        yield return node;
    }

    private static async Task ThrowIfFailedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        var detail = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var error = new HttpRequestException($"The model endpoint answered {(int)response.StatusCode}: {(detail.Length > 300 ? detail[..300] : detail)}",
            null, response.StatusCode);
        if (RetryAfter(response) is { } wait) error.Data[GraphRagRetryOptions.RetryAfterKey] = wait;
        throw error;
    }

    // Retry-After in seconds or as a date, or OpenAI's retry-after-ms.
    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("retry-after-ms", out var values)
            && double.TryParse(values.First(), NumberStyles.Float, CultureInfo.InvariantCulture, out var milliseconds))
            return TimeSpan.FromMilliseconds(milliseconds);
        return response.Headers.RetryAfter switch
        {
            { Delta: { } delta } => delta,
            { Date: { } date } => date - DateTimeOffset.UtcNow is var wait && wait > TimeSpan.Zero ? wait : TimeSpan.Zero,
            _ => null
        };
    }

    private static UsageDetails? Usage(JsonElement reply)
    {
        if (!reply.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object) return null;
        static long? Read(JsonElement usage, string name) => usage.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt64() : null;
        var reasoning = usage.TryGetProperty("completion_tokens_details", out var details) && details.ValueKind == JsonValueKind.Object ? Read(details, "reasoning_tokens") : null;
        return new UsageDetails
        {
            InputTokenCount = Read(usage, "prompt_tokens"), OutputTokenCount = Read(usage, "completion_tokens"), TotalTokenCount = Read(usage, "total_tokens"),
            ReasoningTokenCount = reasoning
        };
    }

    private static HttpClient Create(SampleModelSettings settings)
    {
        var http = new HttpClient { BaseAddress = settings.Endpoint, Timeout = TimeSpan.FromMinutes(3) };
        // Some endpoints sit behind a firewall that refuses requests without a user agent.
        http.DefaultRequestHeaders.UserAgent.ParseAdd("GraphBridge-Samples/0.1");
        if (settings.Key is { Length: > 0 } key && key != "no-key-needed") http.DefaultRequestHeaders.Authorization = new("Bearer", key);
        return http;
    }
}

/// <summary>
/// The samples' shipped data, named by its address in the public repository: a document keeps its ID, and a recorded model
/// call replays, wherever the repository is cloned. A file or folder outside the build output's <c>data</c> folder keeps its
/// path as its identity.
/// </summary>
public static class SampleData
{
    public const string Repository = "https://github.com/joslat/GraphBridge.EFCore/blob/main/";

    public static IReadOnlyList<GraphRagSource> Folder(string folder, string sample, string searchPattern = "*.md")
        => (Shipped(folder, sample) is { } address ? GraphRagSources.Folder(folder, new Uri(address + "/"), searchPattern)
            : GraphRagSources.Folder(folder, searchPattern)).ToArray();

    public static GraphRagSource File(string path, string sample)
        => Shipped(path, sample) is { } address ? GraphRagSources.File(path, new Uri(address)) : GraphRagSources.File(path);

    // The repository address of a path inside the build output's data folder, which mirrors the sample's data folder.
    private static string? Shipped(string path, string sample)
    {
        var relative = Path.GetRelativePath(Path.Combine(AppContext.BaseDirectory, "data"), Path.GetFullPath(path));
        if (relative == "." || relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative)) return null;
        return $"{Repository}samples/{sample}/data/" + string.Join('/', relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Select(Uri.EscapeDataString));
    }
}
