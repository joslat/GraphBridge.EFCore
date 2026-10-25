using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace GraphBridge.GraphRag.Evaluation;

// Deterministic stand-ins so tests run without a model, a key or, with the in-memory retriever, a database.

/// <summary>Words hashed into buckets: texts that share words point the same way.</summary>
public sealed class GraphRagHashingEmbedder(int dimensions) : IEmbeddingGenerator<string, Embedding<float>>
{
    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values, EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(values.Select(text =>
        {
            var vector = new float[dimensions];
            vector[0] = 1;
            foreach (var word in GraphRagWords.Of(text)) vector[1 + (int)((uint)StringComparer.Ordinal.GetHashCode(word) % (uint)(dimensions - 1))] += 1;
            return new Embedding<float>(vector);
        })));

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}

/// <summary>
/// Answers with the evidence line that shares the most words with the question, quoting it exactly; with fewer than two
/// shared words it declines. The answer is only as good as word overlap, which is the point: it is deterministic.
/// </summary>
public sealed class GraphRagExtractiveChatClient : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var prompt = messages.Last().Text.ReplaceLineEndings("\n");
        var wanted = GraphRagWords.Of(prompt.Split('\n')[0]["Question: ".Length..]).ToHashSet();
        var best = (Evidence: 0, Line: "", Score: 0);
        var current = 0;
        foreach (var line in prompt.Split('\n'))
        {
            if (line.StartsWith("Context (never cite):", StringComparison.Ordinal)) break;
            var text = line;
            if (line.Length > 3 && line[0] == '[' && line.IndexOf("] ", StringComparison.Ordinal) is > 0 and var close && int.TryParse(line.AsSpan(1, close - 1), out var number))
                (current, text) = (number, line[(close + 2)..]);
            if (current == 0 || text.Trim().Length < 20) continue;
            var score = GraphRagWords.Of(text).Distinct().Count(wanted.Contains);
            if (score > best.Score) best = (current, text.Trim(), score);
        }
        var reply = best.Score >= 2
            ? JsonSerializer.Serialize(new { answer = best.Line, citations = new[] { new { evidence = best.Evidence, quote = best.Line } } })
            : JsonSerializer.Serialize(new { insufficient = "No evidence line shares enough words with the question." });
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, reply)));
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}

/// <summary>Replies with whatever the function returns for the messages it is given.</summary>
public sealed class GraphRagScriptedChatClient(Func<IReadOnlyList<ChatMessage>, string> reply) : IChatClient
{
    public int Calls { get; private set; }

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        Calls++;
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, reply(messages.ToList()))));
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}

/// <summary>
/// Retrieval over texts held in memory, for offline tests of answers and of the harness itself: each text is chunked with the
/// toolkit's chunker, and chunks are ranked by the words they share with the question. Positions are exact.
/// </summary>
public sealed class GraphRagInMemoryRetriever : IGraphRagRetriever
{
    private readonly List<GraphRagEvidenceItem> _chunks = [];

    public GraphRagInMemoryRetriever(IEnumerable<(string DocumentId, string Text)> documents, GraphRagChunkerOptions? chunking = null)
    {
        ArgumentNullException.ThrowIfNull(documents);
        var chunker = GraphRagChunkers.Create(chunking);
        foreach (var (documentId, text) in documents)
        {
            var bytes = Encoding.UTF8.GetBytes(text.ReplaceLineEndings("\n"));
            var plan = chunker.Chunk(new GraphRagLoadedDocument("memory:" + documentId, "text/markdown", null, bytes));
            foreach (var chunk in plan.Chunks)
                _chunks.Add(new GraphRagEvidenceItem($"{documentId}#{chunk.Order}", documentId, Encoding.UTF8.GetString(bytes, chunk.StartByte, chunk.EndByte - chunk.StartByte),
                    chunk.StartByte, chunk.EndByte, 0, []));
        }
    }

    public Task<GraphRagEvidence> RetrieveAsync(string question, GraphRagRetrievalOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(question);
        var wanted = GraphRagWords.Of(question).ToHashSet();
        var max = options?.MaxItems ?? 8;
        var ranked = _chunks.Select(c => (Chunk: c, Score: GraphRagWords.Of(c.Text).Distinct().Count(wanted.Contains))).Where(r => r.Score > 0)
            .OrderByDescending(r => r.Score).ThenBy(r => r.Chunk.ChunkId, StringComparer.Ordinal).Take(max)
            .Select(r => r.Chunk with { Score = r.Score }).ToArray();
        return Task.FromResult(new GraphRagEvidence(question, ranked, [], ranked.Sum(r => Encoding.UTF8.GetByteCount(r.Text)), 0, [],
            new GraphRagProvenance("in-memory", "words", [], null)));
    }
}

internal static class GraphRagWords
{
    private static readonly char[] Separators = [.. " \t\r\n.,;:!?()[]{}<>\"'`*#|/\\-_=+“”‘’".ToCharArray()];
    private static readonly HashSet<string> StopWords = ["the", "and", "for", "are", "with", "that", "this", "what", "how", "does", "which", "from", "into", "its", "can", "did", "were", "was"];

    public static string[] Of(string text)
        => text.ToLowerInvariant().Split(Separators, StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length > 2 && !StopWords.Contains(w)).ToArray();
}
