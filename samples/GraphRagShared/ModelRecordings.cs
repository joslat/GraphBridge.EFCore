using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace GraphBridge.Samples.GraphRagShared;

// Recorded model calls (GraphRAG v2, GR-46): real chat replies and embeddings in a file, keyed by a hash of each request, so a
// run replays offline at zero calls. Linked into the samples and the real-model tests; not a package.

/// <summary>
/// One file of recorded model calls. A chat entry is keyed by a hash of the messages and the reply format; an embedding entry by
/// a hash of the texts and dimensions. Each keeps the model's name, the date, the reply or vectors, and the token counts the
/// provider reported. The file holds no request text beyond a short hint, and no endpoint or key.
/// </summary>
public sealed class ModelRecordings
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private readonly object _gate = new();
    private readonly SortedDictionary<string, JsonObject> _chat = new(StringComparer.Ordinal);
    private readonly SortedDictionary<string, JsonObject> _embeddings = new(StringComparer.Ordinal);

    // With GRAPHBRIDGE_AI_USED_KEYS naming a file, every entry served or added is listed there, so after a full passing run a
    // maintainer can prune the entries no test uses any more.
    private static readonly string? UsedKeys = Environment.GetEnvironmentVariable("GRAPHBRIDGE_AI_USED_KEYS");
    private static readonly object UsedGate = new();

    private ModelRecordings(string path) => Path = path;

    private void Used(string kind, string key)
    {
        if (UsedKeys is null) return;
        lock (UsedGate) File.AppendAllText(UsedKeys, $"{System.IO.Path.GetFileName(Path)}\t{kind}\t{key}\n");
    }

    public string Path { get; }

    /// <summary>The recorded entries; a missing file has none.</summary>
    public static ModelRecordings Load(string path)
    {
        var recordings = new ModelRecordings(path);
        if (!File.Exists(path)) return recordings;
        var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        foreach (var entry in root["chat"]?.AsArray() ?? new JsonArray()) recordings._chat[(string)entry!["request"]!] = entry.AsObject().DeepClone().AsObject();
        foreach (var entry in root["embeddings"]?.AsArray() ?? new JsonArray()) recordings._embeddings[(string)entry!["request"]!] = entry.AsObject().DeepClone().AsObject();
        return recordings;
    }

    public int ChatCount { get { lock (_gate) return _chat.Count; } }
    public int EmbeddingCount { get { lock (_gate) return _embeddings.Count; } }

    /// <summary>The provider the recordings were made on, such as <c>bitdeer</c>, or null when there are none.</summary>
    public string? Provider { get { lock (_gate) return _chat.Values.Concat(_embeddings.Values).Select(e => (string?)e["provider"]).FirstOrDefault(p => p is not null); } }

    /// <summary>The chat model, embedding model and dimensions of the recordings, or null when there are none.</summary>
    public (string ChatModel, string EmbeddingModel, int Dimensions)? Models
    {
        get
        {
            lock (_gate)
            {
                var chat = _chat.Values.Select(e => (string?)e["model"]).FirstOrDefault();
                var embedding = _embeddings.Values.FirstOrDefault();
                if (chat is null || embedding is null) return null;
                return (chat, (string)embedding["model"]!, Vectors(embedding)[0].Length);
            }
        }
    }

    public static string ChatKey(IEnumerable<ChatMessage> messages, ChatOptions? options)
    {
        var request = new JsonObject
        {
            ["messages"] = new JsonArray(messages.Select(MessageNode).ToArray()),
            ["format"] = options?.ResponseFormat switch
            {
                ChatResponseFormatJson { Schema: { } schema } json => new JsonObject { ["name"] = json.SchemaName, ["schema"] = JsonNode.Parse(schema.GetRawText()) },
                ChatResponseFormatJson => "json",
                _ => null
            },
            ["strict"] = options?.AdditionalProperties?.TryGetValue("strict", out var strict) == true && strict is true
        };
        // Only when asked for, so recordings made without it keep their keys.
        if (options?.Reasoning?.Effort is { } effort) request["reasoning"] = effort.ToString();
        if (options?.Instructions is { Length: > 0 } instructions) request["instructions"] = instructions;
        if (options?.Tools is { Count: > 0 } tools)
            request["tools"] = new JsonArray(tools.Select(t => (JsonNode)new JsonObject
            {
                ["name"] = t.Name, ["schema"] = t is AIFunctionDeclaration f ? JsonNode.Parse(f.JsonSchema.GetRawText()) : null
            }).ToArray());
        // Line breaks count as \n wherever they are, in texts, tool results and schemas: a request built on Windows (\r\n, also
        // what indented JSON writes there) and on Linux (\n) has one key.
        return Hash(request.ToJsonString().Replace(@"\r\n", @"\n", StringComparison.Ordinal));
    }

    // A message's role and text, and its tool calls and results only when it has them, so plain requests keep their keys.
    private static JsonNode MessageNode(ChatMessage message)
    {
        var node = new JsonObject { ["role"] = message.Role.Value, ["text"] = message.Text };
        var calls = message.Contents.OfType<FunctionCallContent>().ToArray();
        if (calls.Length > 0) node["calls"] = new JsonArray(calls.Select(CallNode).ToArray());
        var results = message.Contents.OfType<FunctionResultContent>().ToArray();
        if (results.Length > 0)
            node["results"] = new JsonArray(results.Select(r => (JsonNode)new JsonObject { ["id"] = r.CallId, ["result"] = ResultText(r.Result) }).ToArray());
        return node;
    }

    private static JsonNode CallNode(FunctionCallContent call) => new JsonObject
    {
        ["id"] = call.CallId, ["name"] = call.Name, ["arguments"] = JsonSerializer.SerializeToNode(call.Arguments, AIJsonUtilities.DefaultOptions)
    };

    /// <summary>A tool result as the text a model receives.</summary>
    public static string ResultText(object? result) => result switch
    {
        null => "",
        string text => text,
        JsonElement json => json.GetRawText(),
        _ => JsonSerializer.Serialize(result, AIJsonUtilities.DefaultOptions)
    };

    public static string EmbeddingKey(IEnumerable<string> texts, int? dimensions)
        => Hash(new JsonObject { ["dimensions"] = dimensions, ["texts"] = new JsonArray(texts.Select(t => (JsonNode)t).ToArray()) }.ToJsonString());

    public ChatResponse? FindChat(string key)
    {
        lock (_gate)
        {
            if (!_chat.TryGetValue(key, out var entry)) return null;
            Used("chat", key);
            var contents = new List<AIContent>();
            if ((string?)entry["reply"] is { Length: > 0 } reply) contents.Add(new TextContent(reply));
            if (entry["calls"] is JsonArray calls)
                contents.AddRange(calls.Select(c => new FunctionCallContent((string)c!["id"]!, (string)c["name"]!,
                    c["arguments"].Deserialize<Dictionary<string, object?>>(AIJsonUtilities.DefaultOptions))));
            if (contents.Count == 0) contents.Add(new TextContent(""));
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, contents))
            {
                ModelId = (string?)entry["model"],
                FinishReason = (string?)entry["finish"] is { } finish ? new ChatFinishReason(finish) : null,
                Usage = Usage(entry)
            };
        }
    }

    public GeneratedEmbeddings<Embedding<float>>? FindEmbeddings(string key)
    {
        lock (_gate)
        {
            if (!_embeddings.TryGetValue(key, out var entry)) return null;
            Used("embeddings", key);
            return new GeneratedEmbeddings<Embedding<float>>(Vectors(entry).Select(v => new Embedding<float>(v) { ModelId = (string?)entry["model"] }))
            {
                Usage = Usage(entry)
            };
        }
    }

    public void AddChat(string key, string hint, string provider, string model, ChatResponse response)
    {
        var entry = new JsonObject
        {
            ["request"] = key, ["hint"] = Hint(hint), ["provider"] = provider, ["model"] = model, ["recorded"] = Today, ["reply"] = response.Text,
            ["finish"] = response.FinishReason?.Value, ["inputTokens"] = response.Usage?.InputTokenCount, ["outputTokens"] = response.Usage?.OutputTokenCount,
            ["reasoningTokens"] = response.Usage?.ReasoningTokenCount
        };
        var calls = response.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().ToArray();
        if (calls.Length > 0) entry["calls"] = new JsonArray(calls.Select(CallNode).ToArray());
        RefuseSecrets(entry);
        lock (_gate)
        {
            _chat[key] = entry;
            Save();
        }
        Used("chat", key);
    }

    public void AddEmbeddings(string key, string hint, string provider, string model, GeneratedEmbeddings<Embedding<float>> embeddings)
    {
        var entry = new JsonObject
        {
            ["request"] = key, ["hint"] = Hint(hint), ["provider"] = provider, ["model"] = model, ["recorded"] = Today, ["inputTokens"] = embeddings.Usage?.InputTokenCount,
            ["vectors"] = new JsonArray(embeddings.Select(e => (JsonNode)Convert.ToBase64String(MemoryMarshal.AsBytes(e.Vector.Span))).ToArray())
        };
        RefuseSecrets(entry);
        lock (_gate)
        {
            _embeddings[key] = entry;
            Save();
        }
        Used("embeddings", key);
    }

    /// <summary>
    /// Throws if any value of an environment variable whose name says it holds a secret (KEY, TOKEN, SECRET, PASSWORD) of eight
    /// or more characters appears in the text. The value is never printed.
    /// </summary>
    public static void RefuseSecrets(string text)
    {
        foreach (var (name, value) in Secrets())
            if (text.Contains(value, StringComparison.Ordinal))
                throw new InvalidOperationException($"The text contains the value of {name}; it isn't recorded.");
    }

    private static void RefuseSecrets(JsonObject entry) => RefuseSecrets(entry.ToJsonString());

    private static IEnumerable<(string Name, string Value)> Secrets()
    {
        foreach (System.Collections.DictionaryEntry variable in Environment.GetEnvironmentVariables())
        {
            var name = (string)variable.Key;
            if (variable.Value is string { Length: >= 8 } value
                && new[] { "KEY", "TOKEN", "SECRET", "PASSWORD" }.Any(word => name.Contains(word, StringComparison.OrdinalIgnoreCase)))
                yield return (name, value);
        }
    }

    // Written whole after each new entry, through a temporary file, so a stopped run keeps every reply it paid for.
    private void Save()
    {
        var root = new JsonObject
        {
            ["about"] = "Model calls recorded by GraphBridge's real-model tests (GR-46): keyed by a hash of each request, replayed offline at zero calls.",
            ["chat"] = new JsonArray(_chat.Values.Select(e => (JsonNode)e.DeepClone()).ToArray()),
            ["embeddings"] = new JsonArray(_embeddings.Values.Select(e => (JsonNode)e.DeepClone()).ToArray())
        };
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!);
        var temporary = Path + ".tmp";
        File.WriteAllText(temporary, root.ToJsonString(Indented).ReplaceLineEndings("\n") + "\n");
        File.Move(temporary, Path, overwrite: true);
    }

    private static string Today => DateTime.UtcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The text on one line, at most 80 characters.</summary>
    public static string Hint(string text)
    {
        var line = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return line.Length > 80 ? line[..80] + "…" : line;
    }

    private static UsageDetails? Usage(JsonObject entry)
        => (long?)entry["inputTokens"] is null && (long?)entry["outputTokens"] is null
            ? null
            : new UsageDetails
            {
                InputTokenCount = (long?)entry["inputTokens"], OutputTokenCount = (long?)entry["outputTokens"], ReasoningTokenCount = (long?)entry["reasoningTokens"]
            };

    private static float[][] Vectors(JsonObject entry)
        => entry["vectors"]!.AsArray().Select(v => MemoryMarshal.Cast<byte, float>(Convert.FromBase64String((string)v!).AsSpan()).ToArray()).ToArray();

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..32].ToLowerInvariant();
}

/// <summary>A model call a replay has no recording for.</summary>
public sealed class ModelRecordingMissingException(string kind, string key, string hint, string path)
    : InvalidOperationException($"No recorded {kind} reply for \"{ModelRecordings.Hint(hint)}\" (key {key}) in {path}. Record it with a real model: set GRAPHBRIDGE_AI_RECORD=1, AI_INFERENCE_PROVIDER and its key (GRAPHBRIDGE_AI_MAX_CALLS caps the calls, 40 by default), and run the real-model tests.");

/// <summary>More real model calls than the run's budget allows.</summary>
public sealed class ModelCallBudgetExceededException(int budget)
    : InvalidOperationException($"This run may make at most {budget} model calls (GRAPHBRIDGE_AI_MAX_CALLS); stopping before another.");

/// <summary>A cap on real model calls across one run's clients.</summary>
public sealed class ModelCallBudget(int maxCalls)
{
    private int _calls;
    public int MaxCalls { get; } = maxCalls;
    public int Calls => Volatile.Read(ref _calls);

    public void Take()
    {
        if (Interlocked.Increment(ref _calls) > MaxCalls)
        {
            Interlocked.Decrement(ref _calls);
            throw new ModelCallBudgetExceededException(MaxCalls);
        }
    }
}

/// <summary>
/// Answers from the recordings. Without an inner client, a request with no recording throws
/// <see cref="ModelRecordingMissingException"/>, or gets <paramref name="missing"/>'s reply when one is given. With an inner client
/// it records: a request already recorded is replayed, any other is sent to the model within the budget and saved.
/// </summary>
public sealed class RecordedChatClient(ModelRecordings recordings, IChatClient? inner = null, string? model = null, ModelCallBudget? budget = null,
    Func<IEnumerable<ChatMessage>, string>? missing = null, string provider = "unknown") : IChatClient
{
    private int _unmatched;

    /// <summary>Requests answered by <c>missing</c> because nothing was recorded for them.</summary>
    public int Unmatched => Volatile.Read(ref _unmatched);

    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var list = messages.ToList();
        var key = ModelRecordings.ChatKey(list, options);
        if (recordings.FindChat(key) is { } recorded) return recorded;
        var hint = list.FirstOrDefault(m => m.Role == ChatRole.User)?.Text ?? "";
        if (inner is null)
        {
            if (missing is null) throw new ModelRecordingMissingException("chat", key, hint, recordings.Path);
            Interlocked.Increment(ref _unmatched);
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, missing(list)));
        }
        budget?.Take();
        var response = await inner.GetResponseAsync(list, options, cancellationToken).ConfigureAwait(false);
        recordings.AddChat(key, hint, provider, response.ModelId ?? model ?? "unknown", response);
        return response;
    }

    /// <summary>
    /// The same reply as <see cref="GetResponseAsync"/>, in pieces of a few characters, so one recording serves both kinds of
    /// call. When recording, the model is asked once, without streaming.
    /// </summary>
    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default) => ReplyPieces.StreamAsync(GetResponseAsync(messages, options, cancellationToken), cancellationToken);

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}

/// <summary>Streams a whole reply in pieces, as a streaming endpoint would: for clients that have the reply at once.</summary>
public static class ReplyPieces
{
    public const int Size = 24;

    public static async IAsyncEnumerable<ChatResponseUpdate> StreamAsync(Task<ChatResponse> reply, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await reply.ConfigureAwait(false);
        var text = response.Text;
        for (var i = 0; i < text.Length;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var length = Math.Min(Size, text.Length - i);
            // Never split a surrogate pair across pieces.
            if (char.IsHighSurrogate(text[i + length - 1]) && i + length < text.Length) length++;
            yield return new ChatResponseUpdate(ChatRole.Assistant, text.Substring(i, length)) { ModelId = response.ModelId };
            i += length;
        }
        if (response.Usage is { } usage) yield return new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = [new UsageContent(usage)], ModelId = response.ModelId };
    }
}

/// <summary>Embeddings from the recordings, or, with an inner generator, recorded as they're made; see <see cref="RecordedChatClient"/>.</summary>
public sealed class RecordedEmbeddingGenerator(ModelRecordings recordings, IEmbeddingGenerator<string, Embedding<float>>? inner = null, string? model = null,
    ModelCallBudget? budget = null, string provider = "unknown") : IEmbeddingGenerator<string, Embedding<float>>
{
    public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values, EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var texts = values.ToList();
        var key = ModelRecordings.EmbeddingKey(texts, options?.Dimensions);
        if (recordings.FindEmbeddings(key) is { } recorded) return recorded;
        if (inner is null) throw new ModelRecordingMissingException("embedding", key, texts.FirstOrDefault() ?? "", recordings.Path);
        budget?.Take();
        var generated = await inner.GenerateAsync(texts, options, cancellationToken).ConfigureAwait(false);
        recordings.AddEmbeddings(key, texts.FirstOrDefault() ?? "", provider, generated.FirstOrDefault()?.ModelId ?? model ?? "unknown", generated);
        return generated;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}
