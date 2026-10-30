using System.Runtime.CompilerServices;
using System.Text;
using GraphBridge.GraphRag;
using Microsoft.Extensions.AI;

namespace GraphBridge.GraphRag.Tests;

/// <summary>GraphRAG v2, wave 8 (GR-21), offline: streamed answers send provisional text, then the same checked answer.</summary>
public sealed class GraphRagStreamingTests
{
    private static readonly GraphRagEvidence Evidence = new("What do graphs connect?",
    [
        Item("c1", "Graphs connect facts — not rows.", 0),
        Item("c2", "Edges have a type and a direction.", 100)
    ], [], 70, 0, [], new("2026.09.0", "set", ["index"], "cosine"));

    private const string Citation = """, "citations": [{"evidence": 1, "quote": "Graphs connect facts - not rows."}]}""";

    [Fact]
    public async Task A_streamed_answer_sends_the_evidence_then_decoded_text_then_the_answer_AnswerAsync_gives()
    {
        // Pieces end inside the key, inside an escaped quote, and inside a \u escape.
        string[] pieces = ["```json\n{\"ans", "wer\": \"Facts, \\\"not", "\\\" rows \\u00", "e9t\\u00e9\\n", " \"" + Citation, "\n```"];
        var reply = string.Concat(pieces);
        var updates = await Collect(new GraphRagAnswerer(new FixedRetriever(Evidence), () => new StreamingChat(pieces)).AnswerStreamingAsync("q"));

        Assert.Equal(GraphRagAnswerUpdateKind.Retrieved, updates[0].Kind);
        Assert.Same(Evidence, updates[0].Evidence);
        Assert.Equal(GraphRagAnswerUpdateKind.Completed, updates[^1].Kind);
        var deltas = updates[1..^1];
        Assert.All(deltas, d => Assert.Equal(GraphRagAnswerUpdateKind.Delta, d.Kind));
        Assert.True(deltas.Length >= 3);
        Assert.Equal("Facts, \"not\" rows été\n ", string.Concat(deltas.Select(d => d.Delta)));

        var streamed = updates[^1].Answer!;
        var whole = await new GraphRagAnswerer(new FixedRetriever(Evidence), () => new StreamingChat(reply)).AnswerAsync("q");
        Assert.Equal((GraphRagAnswerDisposition.Answered, "Facts, \"not\" rows été"), (streamed.Disposition, streamed.Text));
        Assert.Equal((whole.Disposition, whole.Text, whole.Reason), (streamed.Disposition, streamed.Text, streamed.Reason));
        Assert.Equal(whole.Citations, streamed.Citations);
        Assert.Equal(1, streamed.Receipt!.ChatCalls);
    }

    [Theory]
    [InlineData("""{"insufficient": "The evidence is about edges."}""", "model-declined", false)]
    [InlineData("""{"answer": "Facts.", "citations": [{"evidence": 1, "quote": "Graphs connect FACTS"}]}""", "citation-not-found", true)]
    [InlineData("""{"answer": "Facts.", "citations": []}""", "no-citations", true)]
    [InlineData("""{"answer": "Facts.", "citations": [ """, "unreadable-response", true)]
    public async Task A_refusal_completes_the_stream_without_text_and_withdraws_any_provisional_text(string reply, string reason, bool sentDeltas)
    {
        var pieces = reply.Chunk(7).Select(c => new string(c)).ToArray();
        var updates = await Collect(new GraphRagAnswerer(new FixedRetriever(Evidence), () => new StreamingChat(pieces)).AnswerStreamingAsync("q"));
        var answer = updates[^1].Answer!;
        Assert.Equal((GraphRagAnswerDisposition.InsufficientEvidence, reason, (string?)null), (answer.Disposition, answer.Reason, answer.Text));
        Assert.Equal(sentDeltas, updates.Any(u => u.Kind == GraphRagAnswerUpdateKind.Delta));
    }

    [Fact]
    public async Task Half_of_a_surrogate_pair_is_held_back_until_its_other_half_arrives()
    {
        string[] pieces = ["{\"answer\": \"Moon \\ud83c", "\\udf15 and \uD83D", "\uDE80 ship" + "\"" + Citation];
        var updates = await Collect(new GraphRagAnswerer(new FixedRetriever(Evidence), () => new StreamingChat(pieces)).AnswerStreamingAsync("q"));
        var deltas = updates.Where(u => u.Kind == GraphRagAnswerUpdateKind.Delta).Select(u => u.Delta!).ToArray();
        Assert.All(deltas, d => Assert.False(char.IsHighSurrogate(d[^1]) || char.IsLowSurrogate(d[0]), $"A delta splits a surrogate pair: {d}"));
        Assert.Equal("Moon 🌕 and 🚀 ship", string.Concat(deltas));
    }

    [Fact]
    public async Task No_evidence_completes_with_a_refusal_without_calling_the_model()
    {
        var chat = new StreamingChat("{}");
        var updates = await Collect(new GraphRagAnswerer(new FixedRetriever(Evidence with { Items = [] }), () => chat).AnswerStreamingAsync("q"));
        Assert.Equal([GraphRagAnswerUpdateKind.Retrieved, GraphRagAnswerUpdateKind.Completed], updates.Select(u => u.Kind));
        Assert.Equal("no-evidence", updates[^1].Answer!.Reason);
        Assert.Equal(0, chat.Calls);
    }

    [Fact]
    public async Task Cancelling_after_the_first_piece_stops_the_model_call()
    {
        var chat = new StreamingChat("{\"answer\": \"Facts") { HangAfterPieces = true };
        using var cancel = new CancellationTokenSource();
        var seen = new List<GraphRagAnswerUpdate>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var update in new GraphRagAnswerer(new FixedRetriever(Evidence), () => chat).AnswerStreamingAsync("q", cancellationToken: cancel.Token))
            {
                seen.Add(update);
                if (update.Kind == GraphRagAnswerUpdateKind.Delta) await cancel.CancelAsync();
            }
        });
        Assert.True(chat.Cancelled);
        Assert.DoesNotContain(seen, u => u.Kind == GraphRagAnswerUpdateKind.Completed);
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("{\"answer", "")]
    [InlineData("{\"answer\": ", "")]
    [InlineData("{\"answer\": \"Fa", "Fa")]
    [InlineData("{\"answer\": \"Fa\\", "Fa")]
    [InlineData("{\"answer\": \"Fa\\u00", "Fa")]
    [InlineData("{\"citations\": [{\"quote\": \"x\", \"answer\": \"nested\"}], \"answer\": \"Top", "Top")]
    [InlineData("{\"note\": \"answer\", \"answer\": \"Real\"}", "Real")]
    [InlineData("{\"insufficient\": \"no\"}", "")]
    [InlineData("{\"answer\": 42}", "")]
    public void The_reader_decodes_only_the_top_level_answer_string_received_so_far(string reply, string decoded)
        => Assert.Equal(decoded, GraphRagAnswerTextReader.Decode(reply));

    private static async Task<GraphRagAnswerUpdate[]> Collect(IAsyncEnumerable<GraphRagAnswerUpdate> updates)
    {
        var list = new List<GraphRagAnswerUpdate>();
        await foreach (var update in updates) list.Add(update);
        return [.. list];
    }

    private static GraphRagEvidenceItem Item(string id, string text, int start)
        => new(id, "doc", text, start, start + Encoding.UTF8.GetByteCount(text), 0.03, []);

    private sealed class FixedRetriever(GraphRagEvidence evidence) : IGraphRagRetriever
    {
        public Task<GraphRagEvidence> RetrieveAsync(string question, GraphRagRetrievalOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(evidence);
    }

    // Streams the reply in the given pieces; GetResponseAsync returns them joined, as a recorded reply would be.
    private sealed class StreamingChat(params string[] pieces) : IChatClient
    {
        public int Calls { get; private set; }
        public bool HangAfterPieces { get; init; }
        public bool Cancelled { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, string.Concat(pieces))));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Calls++;
            foreach (var piece in pieces)
            {
                await Task.Yield();
                yield return new ChatResponseUpdate(ChatRole.Assistant, piece);
            }
            if (!HangAfterPieces) yield break;
            try { await Task.Delay(Timeout.Infinite, cancellationToken); }
            catch (OperationCanceledException) { Cancelled = true; throw; }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
