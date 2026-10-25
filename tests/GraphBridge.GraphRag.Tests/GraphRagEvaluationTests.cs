using System.Diagnostics;
using GraphBridge.GraphRag.Evaluation;
using Microsoft.Extensions.AI;

namespace GraphBridge.GraphRag.Tests;

/// <summary>Wave 6 (GR-23, GR-39), offline: the evaluation harness on the public corpus, its failures, answer receipts and activities.</summary>
public sealed class GraphRagEvaluationTests
{
    [Fact]
    public async Task The_public_corpus_passes_citation_validity_abstention_and_coverage_offline()
    {
        var (corpus, cases) = GraphRagEvaluator.AliceCorpus();
        Assert.Equal(5, cases.Count);
        var answers = new GraphRagAnswerer(new GraphRagInMemoryRetriever([("alice", await File.ReadAllTextAsync(corpus))]), () => new GraphRagExtractiveChatClient());
        var report = await GraphRagEvaluator.RunAsync(answers, cases);
        Assert.True(report.Passed, string.Join("; ", report.Failures.Select(f => $"{f.Case}: {f.Metric.Explanation}")));
        Assert.Equal((1.0, 1.0, 1.0), (report.Score("graphrag_citation_validity"), report.Score("graphrag_abstention"), report.Score("graphrag_retrieval_coverage")));
        Assert.Equal(["no-evidence", "no-evidence"], report.Cases.Where(c => !c.Case.Answerable).Select(c => c.Answer.Reason));
    }

    [Fact]
    public async Task A_tampered_citation_and_an_answered_unanswerable_question_fail_their_metrics()
    {
        var item = new GraphRagEvidenceItem("c1", "d1", "Alice was beginning to get very tired.", 100, 138, 1, []);
        var evidence = new GraphRagEvidence("q", [item], [], 38, 0, [], new GraphRagProvenance("test", "set", [], null));
        var tampered = new GraphRagAnswer(GraphRagAnswerDisposition.Answered, "Alice was tired.", [new GraphRagCitation("d1", "c1", 100, 105, "Alicia")], null, evidence);
        var honest = new GraphRagAnswer(GraphRagAnswerDisposition.Answered, "Alice was tired.", [new GraphRagCitation("d1", "c1", 100, 105, "Alice")], null, evidence);
        Assert.False(GraphRagCitationValidityMetric.Valid(tampered.Citations[0], evidence));
        Assert.True(GraphRagCitationValidityMetric.Valid(honest.Citations[0], evidence));

        var report = await GraphRagEvaluator.RunAsync(new FixedAnswers(tampered), [new GraphRagEvaluationCase("trick", "What is the capital of Mongolia?", false, [])]);
        Assert.False(report.Passed);
        Assert.Equal(["graphrag_citation_validity", "graphrag_abstention"], report.Failures.Select(f => f.Metric.MetricName));
        // An unanswerable case measures no coverage.
        Assert.False(report.Cases[0].Metrics.Single(m => m.MetricName == "graphrag_retrieval_coverage").Measured);
    }

    [Fact]
    public async Task An_answer_carries_a_receipt_and_an_activity_with_counts_but_no_text()
    {
        var activities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == GraphRagTracing.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activities.Add
        };
        ActivitySource.AddActivityListener(listener);
        var chat = new GraphRagScriptedChatClient(_ => """{"answer": "Alice was tired.", "citations": [{"evidence": 1, "quote": "Alice was beginning to get very tired."}]}""");
        var usage = new UsageChat(chat);
        var answers = new GraphRagAnswerer(new GraphRagInMemoryRetriever([("alice", "Alice was beginning to get very tired.")]), () => usage);
        var answer = await answers.AnswerAsync("Was Alice tired?");
        Assert.Equal(GraphRagAnswerDisposition.Answered, answer.Disposition);
        Assert.Equal((1, 120L, 15L), (answer.Receipt!.ChatCalls, answer.Receipt.InputTokens, answer.Receipt.OutputTokens));
        Assert.True(answer.Receipt.Retrieval >= TimeSpan.Zero && answer.Receipt.Answer >= TimeSpan.Zero);

        var traced = activities.Single(a => a.OperationName == GraphRagTracing.AnswerActivity);
        Assert.Equal(("Answered", 1, 120L), (traced.GetTagItem("graphrag.disposition"), traced.GetTagItem("graphrag.citations"), traced.GetTagItem("graphrag.input_tokens")));
        Assert.DoesNotContain(traced.TagObjects, t => t.Value is string text && (text.Contains("Alice", StringComparison.Ordinal) || text.Contains("tired", StringComparison.Ordinal)));

        var refused = await new GraphRagAnswerer(new GraphRagInMemoryRetriever([]), () => usage).AnswerAsync("Anything?");
        Assert.Equal((0, "no-evidence"), (refused.Receipt!.ChatCalls, refused.Reason));
    }

    private sealed class FixedAnswers(GraphRagAnswer answer) : IGraphRagAnswers
    {
        public Task<GraphRagAnswer> AnswerAsync(string question, GraphRagRetrievalOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(answer);
    }

    // Reports token usage, as a real client would.
    private sealed class UsageChat(IChatClient inner) : IChatClient
    {
        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var response = await inner.GetResponseAsync(messages, options, cancellationToken);
            response.Usage = new UsageDetails { InputTokenCount = 120, OutputTokenCount = 15 };
            return response;
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
