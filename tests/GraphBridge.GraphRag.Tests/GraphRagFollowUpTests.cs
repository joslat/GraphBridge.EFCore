using System.Diagnostics;
using System.Runtime.CompilerServices;
using GraphBridge.GraphRag;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

namespace GraphBridge.GraphRag.Tests;

/// <summary>GR-57 (ADR 0052, section 18): a follow-up is rewritten into a standalone question, which is retrieved and answered.</summary>
public sealed class GraphRagFollowUpTests
{
    private const string Rewritten = """{"question": "Which organization does Reid Wiseman work for?"}""";
    private const string Answered = """{"answer": "NASA.", "citations": [{"evidence": 1, "quote": "Reid Wiseman of NASA"}]}""";

    private static readonly GraphRagTurn[] History =
    [
        new("Who commands Artemis II?", "Reid Wiseman commands Artemis II."),
    ];

    [Fact]
    public async Task Without_history_the_question_is_retrieved_as_asked_with_one_call()
    {
        var (retriever, chat) = (new RecordingRetriever(), new QueueChat(Answered));
        var answer = await new GraphRagAnswerer(retriever, () => chat).AnswerAsync("Who works for NASA?");
        Assert.Equal(["Who works for NASA?"], retriever.Questions);
        Assert.Null(answer.Evidence.Rewrite);
        Assert.Equal(1, answer.Receipt!.ChatCalls);
    }

    [Fact]
    public async Task A_follow_up_is_rewritten_then_retrieved_and_answered_and_the_evidence_says_how()
    {
        var (retriever, chat) = (new RecordingRetriever(), new QueueChat(Rewritten, Answered));
        var answer = await new GraphRagAnswerer(retriever, () => chat).AnswerAsync("Which organization does he work for?", new() { History = History });

        Assert.Equal(["Which organization does Reid Wiseman work for?"], retriever.Questions);
        Assert.All(retriever.Options, o => Assert.Null(o!.History));
        Assert.Equal("Conversation:\nQ: Who commands Artemis II?\nA: Reid Wiseman commands Artemis II.\n\nLast question: Which organization does he work for?\n",
            chat.Prompts[0].ReplaceLineEndings("\n"));
        Assert.Equal(GraphRagAnswerer.RewriteInstructions, chat.Systems[0]);
        Assert.Equal(ChatResponseFormat.Json, chat.Formats[0]);
        Assert.Contains("Question: Which organization does Reid Wiseman work for?", chat.Prompts[1]);

        Assert.Equal(GraphRagAnswerDisposition.Answered, answer.Disposition);
        Assert.Equal("Which organization does Reid Wiseman work for?", answer.Evidence.Question);
        Assert.Equal(new GraphRagRewrite("Which organization does he work for?", "Which organization does Reid Wiseman work for?", 1, "rewritten"), answer.Evidence.Rewrite);
        // Both calls are on the receipt, with their tokens.
        Assert.Equal((2, (long?)20, (long?)4), (answer.Receipt!.ChatCalls, answer.Receipt.InputTokens, answer.Receipt.OutputTokens));
    }

    [Theory]
    [InlineData("Which organization does Reid Wiseman work for?", """{"question": "Which organization does Reid Wiseman work for?"}""", "unchanged")]
    [InlineData("Which organization does he work for?", "He works for NASA.", "unreadable")]
    [InlineData("Which organization does he work for?", """{"question": "   "}""", "unreadable")]
    [InlineData("Which organization does he work for?", """{"rewritten": "Which organization?"}""", "unreadable")]
    public async Task A_question_that_stands_alone_or_an_unreadable_rewrite_retrieves_the_question_as_asked(string question, string reply, string outcome)
    {
        var (retriever, chat) = (new RecordingRetriever(), new QueueChat(reply, Answered));
        var answer = await new GraphRagAnswerer(retriever, () => chat).AnswerAsync(question, new() { History = History });
        Assert.Equal([question], retriever.Questions);
        Assert.Equal(new GraphRagRewrite(question, question, 1, outcome), answer.Evidence.Rewrite);
    }

    [Fact]
    public async Task The_newest_six_turns_are_used_answers_are_cut_and_the_limit_is_reported()
    {
        var turns = Enumerable.Range(1, 8).Select(i => new GraphRagTurn($"Question {i}?", i == 8 ? new string('x', 1_500) : $"Answer {i}.")).ToArray();
        var (retriever, chat) = (new RecordingRetriever(), new QueueChat(Rewritten, Answered));
        var answer = await new GraphRagAnswerer(retriever, () => chat).AnswerAsync("And then?", new() { History = turns });
        var prompt = chat.Prompts[0];
        Assert.DoesNotContain("Question 2?", prompt);
        Assert.Contains("Q: Question 3?", prompt);
        Assert.Contains("A: " + new string('x', 1_000) + "…", prompt);
        Assert.DoesNotContain(new string('x', 1_001), prompt);
        Assert.Equal(6, answer.Evidence.Rewrite!.Turns);
        Assert.Contains(new GraphRagLimit("HistoryTurns", 6), answer.Evidence.LimitsReached);
    }

    [Fact]
    public async Task A_streamed_follow_up_reports_the_rewrite_when_retrieved()
    {
        var (retriever, chat) = (new RecordingRetriever(), new QueueChat(Rewritten, Answered));
        var updates = new List<GraphRagAnswerUpdate>();
        await foreach (var update in new GraphRagAnswerer(retriever, () => chat).AnswerStreamingAsync("Which organization does he work for?", new() { History = History }))
            updates.Add(update);
        Assert.Equal("rewritten", updates[0].Evidence.Rewrite!.Outcome);
        var answer = updates[^1].Answer!;
        Assert.Equal((GraphRagAnswerDisposition.Answered, 2), (answer.Disposition, answer.Receipt!.ChatCalls));
    }

    [Fact]
    public async Task A_turn_without_its_question_is_refused_and_the_retriever_refuses_history()
    {
        var chat = new QueueChat(Rewritten, Answered);
        await Assert.ThrowsAsync<ArgumentException>(() => new GraphRagAnswerer(new RecordingRetriever(), () => chat)
            .AnswerAsync("And then?", new() { History = [new(" ")] }));
        Assert.Empty(chat.Prompts);

        var retriever = new GraphRagRetriever<DbContext>(null!, new("set", 8), new GraphRagOptions(), null);
        var refused = await Assert.ThrowsAsync<ArgumentException>(() => retriever.RetrieveAsync("And then?", new() { History = History }));
        Assert.Contains("IGraphRagAnswers rewrites a follow-up", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_rewrite_is_traced_with_counts_and_its_outcome_never_the_question()
    {
        using var root = new Activity("follow-up-test").Start();
        var activities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == GraphRagTracing.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => { if (activity.TraceId == root.TraceId) lock (activities) activities.Add(activity); }
        };
        ActivitySource.AddActivityListener(listener);
        await new GraphRagAnswerer(new RecordingRetriever(), () => new QueueChat(Rewritten, Answered)).AnswerAsync("Which organization does he work for?", new() { History = History });

        var rewrite = Assert.Single(activities, a => a.OperationName == GraphRagTracing.RewriteActivity);
        Assert.Equal((1, "rewritten", 12L, 3L), ((int)rewrite.GetTagItem("graphrag.turns")!, (string)rewrite.GetTagItem("graphrag.rewrite")!,
            (long)rewrite.GetTagItem("graphrag.input_tokens")!, (long)rewrite.GetTagItem("graphrag.output_tokens")!));
        Assert.All(activities.SelectMany(a => a.TagObjects), tag => Assert.DoesNotContain("Reid Wiseman", tag.Value?.ToString() ?? "", StringComparison.Ordinal));
    }

    // Returns evidence for the question it's given, as a retriever would.
    private sealed class RecordingRetriever : IGraphRagRetriever
    {
        public List<string> Questions { get; } = [];
        public List<GraphRagRetrievalOptions?> Options { get; } = [];

        public Task<GraphRagEvidence> RetrieveAsync(string question, GraphRagRetrievalOptions? options = null, CancellationToken cancellationToken = default)
        {
            Questions.Add(question);
            Options.Add(options);
            return Task.FromResult(new GraphRagEvidence(question, [new("c1", "d1", "Commander Reid Wiseman of NASA will lead the flight.", 0, 52, 0.03, [])], [], 52, 0, [],
                new("2026.09.0", "set", ["index"], "cosine")));
        }
    }

    // Replies in order: the first call 12 input and 3 output tokens, the next 8 and 1.
    private sealed class QueueChat(params string[] replies) : IChatClient
    {
        public List<string> Prompts { get; } = [];
        public List<string> Systems { get; } = [];
        public List<ChatResponseFormat?> Formats { get; } = [];

        private ChatResponse Next(IEnumerable<ChatMessage> messages, ChatOptions? options)
        {
            var list = messages.ToList();
            Prompts.Add(list[^1].Text);
            Systems.Add(list[0].Text);
            Formats.Add(options?.ResponseFormat);
            var first = Prompts.Count == 1;
            return new(new ChatMessage(ChatRole.Assistant, replies[Prompts.Count - 1]))
            {
                Usage = new() { InputTokenCount = first ? 12 : 8, OutputTokenCount = first ? 3 : 1 }
            };
        }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(Next(messages, options));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            foreach (var update in Next(messages, options).ToChatResponseUpdates()) yield return update;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
