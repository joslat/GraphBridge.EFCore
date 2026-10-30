using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace GraphRagStarter;

// Offline stand-ins for the models, so the starter runs without a key. Replace them with real clients in Starter.AddStarter.

/// <summary>An embedding stand-in: each word hashed into a bucket, so texts that share words point the same way.</summary>
public sealed class HashingEmbedder(int dimensions) : IEmbeddingGenerator<string, Embedding<float>>
{
    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values, EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(values.Select(text => new Embedding<float>(Vector(text)))));

    public float[] Vector(string text)
    {
        var vector = new float[dimensions];
        vector[0] = 1;   // never all zeros, even for a text without words
        foreach (var word in Words.Of(text))
        {
            var hash = 2166136261u;   // FNV-1a, the same in every process
            foreach (var b in Encoding.UTF8.GetBytes(word)) hash = (hash ^ b) * 16777619u;
            vector[1 + (int)(hash % (uint)(dimensions - 1))] += 1;
        }
        return vector;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}

/// <summary>
/// A chat stand-in: it answers with the evidence line that shares the most words with the question, quoting it, and
/// declines when no line shares two. The toolkit checks the quote against the stored text, as it would a model's.
/// </summary>
public sealed class ExtractiveChatClient : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var prompt = messages.Last().Text.ReplaceLineEndings("\n");
        var wanted = Words.Of(prompt.Split('\n')[0]).ToHashSet();
        var best = (Evidence: 0, Line: "", Score: 0);
        var current = 0;
        foreach (var line in prompt.Split('\n'))
        {
            var text = line;
            if (line.Length > 3 && line[0] == '[' && line.IndexOf("] ", StringComparison.Ordinal) is > 0 and var close
                && int.TryParse(line.AsSpan(1, close - 1), out var number))
                (current, text) = (number, line[(close + 2)..]);
            if (current == 0 || text.Trim().Length < 20) continue;
            var score = Words.Of(text).Distinct().Count(wanted.Contains);
            if (score > best.Score) best = (current, text.Trim(), score);
        }
        var reply = best.Score >= 2
            ? JsonSerializer.Serialize(new { answer = best.Line, citations = new[] { new { evidence = best.Evidence, quote = best.Line } } })
            : JsonSerializer.Serialize(new { insufficient = "No evidence line shares enough words with the question." });
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, reply)));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken);
        yield return new ChatResponseUpdate(ChatRole.Assistant, response.Text);
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}

internal static class Words
{
    private static readonly char[] Separators = [.. " \t\r\n.,;:!?()[]{}<>\"'`*#|/\\-_=+".ToCharArray()];
    private static readonly HashSet<string> StopWords = ["the", "and", "for", "are", "with", "that", "this", "what", "how", "does", "which", "from", "into", "its", "can", "question"];

    public static string[] Of(string text)
        => text.ToLowerInvariant().Split(Separators, StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length > 2 && !StopWords.Contains(w)).ToArray();
}
