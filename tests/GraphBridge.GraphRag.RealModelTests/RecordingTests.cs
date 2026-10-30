using System.Text.Json;
using System.Text.RegularExpressions;
using GraphBridge.Samples.GraphRagShared;
using Microsoft.Extensions.AI;

namespace GraphBridge.GraphRag.RealModelTests;

/// <summary>GraphRAG v2, wave 7 (GR-46), offline: what the recordings hold, and how replaying and recording behave.</summary>
public sealed partial class RecordingTests
{
    public static TheoryData<string> Files => new(Directory.GetFiles(ModelProfile.Repository("samples"), "*-recordings.json", SearchOption.AllDirectories)
        .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)));

    [Theory]
    [MemberData(nameof(Files))]
    public void Committed_recordings_name_their_model_and_date_and_hold_no_keys_or_addresses(string path)
    {
        var text = File.ReadAllText(path);
        ModelRecordings.RefuseSecrets(text);
        Assert.DoesNotMatch(SecretShapes(), text);
        using var json = JsonDocument.Parse(text);
        foreach (var entry in json.RootElement.GetProperty("chat").EnumerateArray().Concat(json.RootElement.GetProperty("embeddings").EnumerateArray()))
        {
            Assert.Matches("^[0-9a-f]{32}$", entry.GetProperty("request").GetString());
            Assert.False(string.IsNullOrWhiteSpace(entry.GetProperty("model").GetString()));
            Assert.Matches(@"^\d{4}-\d{2}-\d{2}$", entry.GetProperty("recorded").GetString());
        }
        var recordings = ModelRecordings.Load(path);
        Assert.NotNull(recordings.Models);
    }

    [Fact]
    public async Task A_replay_fails_on_a_request_it_has_no_recording_for_and_a_recorder_records_within_its_budget()
    {
        var path = Path.Combine(Path.GetTempPath(), "graphbridge-recordings-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var model = new CountingChat();
            var budget = new ModelCallBudget(2);
            var recorder = new RecordedChatClient(ModelRecordings.Load(path), model, "test-model", budget);
            var options = new ChatOptions { ResponseFormat = ChatResponseFormat.Json };
            var first = await recorder.GetResponseAsync([new ChatMessage(ChatRole.User, "one")], options);
            await recorder.GetResponseAsync([new ChatMessage(ChatRole.User, "one")], options);
            await recorder.GetResponseAsync([new ChatMessage(ChatRole.User, "two")], options);
            Assert.Equal((2, 2), (model.Calls, budget.Calls));
            await Assert.ThrowsAsync<ModelCallBudgetExceededException>(() => recorder.GetResponseAsync([new ChatMessage(ChatRole.User, "three")], options));

            // The file replays what was recorded, with its usage, and refuses anything else.
            var replay = new RecordedChatClient(ModelRecordings.Load(path));
            var replayed = await replay.GetResponseAsync([new ChatMessage(ChatRole.User, "one")], options);
            Assert.Equal((first.Text, "test-model", 7L, 3L), (replayed.Text, replayed.ModelId, replayed.Usage!.InputTokenCount, replayed.Usage.OutputTokenCount));
            await Assert.ThrowsAsync<ModelRecordingMissingException>(() => replay.GetResponseAsync([new ChatMessage(ChatRole.User, "one")]));
            await Assert.ThrowsAsync<ModelRecordingMissingException>(() => replay.GetResponseAsync([new ChatMessage(ChatRole.User, "three")], options));

            // Embeddings round-trip exactly.
            var generator = new RecordedEmbeddingGenerator(ModelRecordings.Load(path), new FixedEmbeddings(), "test-embedder");
            var made = await generator.GenerateAsync(["a", "b"]);
            var again = await new RecordedEmbeddingGenerator(ModelRecordings.Load(path)).GenerateAsync(["a", "b"]);
            Assert.Equal(made.Select(e => e.Vector.ToArray()), again.Select(e => e.Vector.ToArray()));
            Assert.Equal(("test-model", "test-embedder", 3), ModelRecordings.Load(path).Models);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_secret_from_the_environment_is_never_recorded_and_never_shown()
    {
        var name = "GRAPHBRIDGE_TEST_API_KEY_" + Guid.NewGuid().ToString("N");
        var value = "not-a-real-key-" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(name, value);
        try
        {
            var error = Assert.Throws<InvalidOperationException>(() => ModelRecordings.RefuseSecrets("the reply mentions " + value));
            Assert.Contains(name, error.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(value, error.Message, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    // OpenAI-style keys, bearer tokens and e-mail addresses.
    [GeneratedRegex(@"sk-[A-Za-z0-9_-]{16,}|Bearer\s+[A-Za-z0-9._-]{8,}|[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+\.[A-Za-z]{2,}")]
    private static partial Regex SecretShapes();

    [Fact]
    public async Task A_streamed_call_replays_the_recorded_reply_in_pieces_under_the_same_reasoning_default()
    {
        var path = Path.Combine(Path.GetTempPath(), "graphbridge-recordings-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            // GR-21: the streaming endpoint asks with GetStreamingResponseAsync; a recording made by GetResponseAsync must serve it.
            var settings = new SampleModelSettings("bitdeer", null, null, "test-model", "test-embedder", 3) { ReasoningEffort = ReasoningEffort.Low };
            var options = new ChatOptions { ResponseFormat = ChatResponseFormat.Json };
            var reply = new string('a', ReplyPieces.Size - 1) + "\U0001F315 and the rest of a reply that takes a few pieces";
            var recorder = settings.WithReasoning(new RecordedChatClient(ModelRecordings.Load(path), new CountingChat { Reply = reply }, "test-model"));
            var recorded = await recorder.GetResponseAsync([new ChatMessage(ChatRole.User, "one")], options);

            var updates = new List<ChatResponseUpdate>();
            await foreach (var update in settings.WithReasoning(new RecordedChatClient(ModelRecordings.Load(path))).GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "one")], options))
                updates.Add(update);
            var pieces = updates.Select(u => u.Text).Where(t => t.Length > 0).ToArray();
            Assert.True(pieces.Length > 2);
            Assert.Equal(recorded.Text, string.Concat(pieces));
            Assert.All(pieces, p => Assert.False(char.IsHighSurrogate(p[^1]), "A piece ends inside a surrogate pair."));
            Assert.Equal(3L, updates.ToChatResponse().Usage!.OutputTokenCount);

            // Without the reasoning default it's a different request, which the file doesn't hold.
            await Assert.ThrowsAsync<ModelRecordingMissingException>(async () =>
            {
                await foreach (var _ in new RecordedChatClient(ModelRecordings.Load(path)).GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "one")], options)) { }
            });
        }
        finally
        {
            File.Delete(path);
        }
    }

    private sealed class CountingChat : IChatClient
    {
        public int Calls;
        public string? Reply { get; init; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, Reply ?? "reply to " + messages.Last().Text))
            {
                Usage = new UsageDetails { InputTokenCount = 7, OutputTokenCount = 3 }
            });
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class FixedEmbeddings : IEmbeddingGenerator<string, Embedding<float>>
    {
        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values, EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(values.Select(v => new Embedding<float>(new[] { v.Length, 0.1f, -2.5e-8f }))));

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
