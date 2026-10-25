using System.Text;
using System.Text.Json;
using AgentEval.Core;

namespace GraphBridge.GraphRag.Evaluation;

// ADR 0052, section 9 (GR-23): a test-only harness with deterministic checks, built on AgentEval's metric contract. No model
// judges anything here: citations are compared byte for byte, refusals against labels, coverage against required phrases.

/// <summary>One labelled question: whether the corpus answers it, and phrases the retrieved evidence must contain if it does.</summary>
public sealed record GraphRagEvaluationCase(string Id, string Question, bool Answerable, IReadOnlyList<string> RequiredEvidence);

/// <summary>The keys under which the harness puts the answer and the case on AgentEval's <see cref="EvaluationContext"/>.</summary>
public static class GraphRagEvaluationKeys
{
    public const string Answer = "graphrag.answer";
    public const string Case = "graphrag.case";
}

/// <summary>Every citation's quote equals the evidence text at its byte span. A refusal, which cites nothing, passes.</summary>
public sealed class GraphRagCitationValidityMetric : IRAGMetric
{
    public string Name => "graphrag_citation_validity";
    public string Description => "Each citation's quote equals the stored evidence text at its byte span.";
    public MetricCategory Categories => MetricCategory.RAG | MetricCategory.Faithfulness | MetricCategory.CodeBased;
    public decimal? EstimatedCostPerEvaluation => 0m;
    public bool RequiresContext => false;
    public bool RequiresGroundTruth => false;

    public Task<MetricResult> EvaluateAsync(EvaluationContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.GetProperty<GraphRagAnswer>(GraphRagEvaluationKeys.Answer) is not { } answer)
            return Task.FromResult(MetricResult.NotMeasured(Name, "No GraphRAG answer on the context.", new Dictionary<string, object>()));
        if (answer.Citations.Count == 0)
            return Task.FromResult(MetricResult.Pass(Name, 1, "No citations to check.", new Dictionary<string, object>()));
        var invalid = answer.Citations.Where(c => !Valid(c, answer.Evidence)).Select(c => $"{c.ChunkId}:{c.StartByte}-{c.EndByte}").ToArray();
        var score = (double)(answer.Citations.Count - invalid.Length) / answer.Citations.Count;
        var details = new Dictionary<string, object> { ["citations"] = answer.Citations.Count, ["invalid"] = invalid };
        return Task.FromResult(invalid.Length == 0
            ? MetricResult.Pass(Name, score, $"All {answer.Citations.Count} citations match the stored bytes.", details)
            : MetricResult.Fail(Name, $"{invalid.Length} of {answer.Citations.Count} citations don't match the stored bytes.", score, details));
    }

    /// <summary>The quote equals the bytes of the cited evidence item at the citation's span.</summary>
    public static bool Valid(GraphRagCitation citation, GraphRagEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(citation);
        ArgumentNullException.ThrowIfNull(evidence);
        foreach (var item in evidence.Items.Where(i => i.ChunkId == citation.ChunkId && i.DocumentId == citation.DocumentId))
        {
            var bytes = Encoding.UTF8.GetBytes(item.Text);
            var (start, end) = (citation.StartByte - item.StartByte, citation.EndByte - item.StartByte);
            if (start >= 0 && end <= bytes.Length && start < end && Encoding.UTF8.GetString(bytes, start, end - start) == citation.Quote) return true;
        }
        return false;
    }
}

/// <summary>An unanswerable question is refused, and an answerable one is answered.</summary>
public sealed class GraphRagAbstentionMetric : IRAGMetric
{
    public string Name => "graphrag_abstention";
    public string Description => "Refuses what the corpus doesn't answer, and answers what it does.";
    public MetricCategory Categories => MetricCategory.RAG | MetricCategory.Relevance | MetricCategory.CodeBased | MetricCategory.RequiresGroundTruth;
    public decimal? EstimatedCostPerEvaluation => 0m;
    public bool RequiresContext => false;
    public bool RequiresGroundTruth => true;

    public Task<MetricResult> EvaluateAsync(EvaluationContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.GetProperty<GraphRagAnswer>(GraphRagEvaluationKeys.Answer) is not { } answer
            || context.GetProperty<GraphRagEvaluationCase>(GraphRagEvaluationKeys.Case) is not { } label)
            return Task.FromResult(MetricResult.NotMeasured(Name, "No GraphRAG answer and case on the context.", new Dictionary<string, object>()));
        var answered = answer.Disposition == GraphRagAnswerDisposition.Answered;
        var details = new Dictionary<string, object> { ["answerable"] = label.Answerable, ["answered"] = answered, ["reason"] = answer.Reason ?? "" };
        if (answered == label.Answerable)
            return Task.FromResult(MetricResult.Pass(Name, 1, answered ? "Answered an answerable question." : "Refused an unanswerable question.", details));
        return Task.FromResult(MetricResult.Fail(Name, answered ? "Answered a question the corpus doesn't answer." : $"Refused an answerable question ({answer.Reason}).", 0, details));
    }
}

/// <summary>The share of a case's required evidence phrases that the retrieved items contain; passes at or above the threshold.</summary>
public sealed class GraphRagRetrievalCoverageMetric(double threshold = 1.0) : IRAGMetric
{
    public string Name => "graphrag_retrieval_coverage";
    public string Description => "Retrieved evidence contains the case's required phrases.";
    public MetricCategory Categories => MetricCategory.RAG | MetricCategory.Relevance | MetricCategory.CodeBased | MetricCategory.RequiresGroundTruth;
    public decimal? EstimatedCostPerEvaluation => 0m;
    public bool RequiresContext => true;
    public bool RequiresGroundTruth => true;

    public Task<MetricResult> EvaluateAsync(EvaluationContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.GetProperty<GraphRagAnswer>(GraphRagEvaluationKeys.Answer) is not { } answer
            || context.GetProperty<GraphRagEvaluationCase>(GraphRagEvaluationKeys.Case) is not { } label)
            return Task.FromResult(MetricResult.NotMeasured(Name, "No GraphRAG answer and case on the context.", new Dictionary<string, object>()));
        if (!label.Answerable || label.RequiredEvidence.Count == 0)
            return Task.FromResult(MetricResult.NotMeasured(Name, "The case requires no evidence.", new Dictionary<string, object>()));
        var evidence = answer.Evidence.Items.Select(i => Collapse(i.Text)).ToArray();
        var missing = label.RequiredEvidence.Where(phrase => !evidence.Any(text => text.Contains(Collapse(phrase), StringComparison.OrdinalIgnoreCase))).ToArray();
        var score = (double)(label.RequiredEvidence.Count - missing.Length) / label.RequiredEvidence.Count;
        var details = new Dictionary<string, object> { ["required"] = label.RequiredEvidence.Count, ["missing"] = missing };
        return Task.FromResult(score >= threshold
            ? MetricResult.Pass(Name, score, $"Retrieved {label.RequiredEvidence.Count - missing.Length} of {label.RequiredEvidence.Count} required phrases.", details)
            : MetricResult.Fail(Name, $"Missing {missing.Length} of {label.RequiredEvidence.Count} required phrases.", score, details));
    }

    private static string Collapse(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}

/// <summary>One case's answer and its metric results.</summary>
public sealed record GraphRagCaseResult(GraphRagEvaluationCase Case, GraphRagAnswer Answer, IReadOnlyList<MetricResult> Metrics);

/// <summary>Every case's results; <see cref="Score"/> averages a metric over the cases that measured it.</summary>
public sealed record GraphRagEvaluationReport(IReadOnlyList<GraphRagCaseResult> Cases)
{
    public bool Passed => Cases.All(c => c.Metrics.Where(m => m.Measured).All(m => m.Passed));

    public double? Score(string metric)
    {
        var measured = Cases.SelectMany(c => c.Metrics).Where(m => m.MetricName == metric && m.Measured).ToArray();
        return measured.Length == 0 ? null : measured.Average(m => m.Score);
    }

    public IEnumerable<(string Case, MetricResult Metric)> Failures
        => Cases.SelectMany(c => c.Metrics.Where(m => m.Measured && !m.Passed).Select(m => (c.Case.Id, m)));
}

/// <summary>Runs labelled cases through <see cref="IGraphRagAnswers"/> and scores each with AgentEval metrics.</summary>
public static class GraphRagEvaluator
{
    /// <summary>Citation validity, abstention and full retrieval coverage.</summary>
    public static IReadOnlyList<IMetric> DefaultMetrics() =>
        [new GraphRagCitationValidityMetric(), new GraphRagAbstentionMetric(), new GraphRagRetrievalCoverageMetric()];

    public static async Task<GraphRagEvaluationReport> RunAsync(IGraphRagAnswers answers, IEnumerable<GraphRagEvaluationCase> cases,
        IEnumerable<IMetric>? metrics = null, GraphRagRetrievalOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(answers);
        ArgumentNullException.ThrowIfNull(cases);
        var checks = (metrics ?? DefaultMetrics()).ToArray();
        var results = new List<GraphRagCaseResult>();
        foreach (var label in cases)
        {
            var answer = await answers.AnswerAsync(label.Question, options, cancellationToken).ConfigureAwait(false);
            var context = new EvaluationContext
            {
                Input = label.Question, Output = answer.Text ?? "",
                Context = string.Join("\n\n", answer.Evidence.Items.Select(i => i.Text)), GroundTruth = string.Join("\n", label.RequiredEvidence)
            };
            context.SetProperty(GraphRagEvaluationKeys.Answer, answer);
            context.SetProperty(GraphRagEvaluationKeys.Case, label);
            var scored = new List<MetricResult>();
            foreach (var metric in checks) scored.Add(await metric.EvaluateAsync(context, cancellationToken).ConfigureAwait(false));
            results.Add(new(label, answer, scored));
        }
        return new(results);
    }

    /// <summary>Reads cases from JSON: <c>{"cases": [{"id", "question", "answerable", "requiredEvidence"}]}</c>, or a bare array.</summary>
    public static IReadOnlyList<GraphRagEvaluationCase> LoadCases(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        var list = json.RootElement.ValueKind == JsonValueKind.Array ? json.RootElement : json.RootElement.GetProperty("cases");
        return list.EnumerateArray().Select(e => new GraphRagEvaluationCase(e.GetProperty("id").GetString()!, e.GetProperty("question").GetString()!,
            e.GetProperty("answerable").GetBoolean(),
            e.TryGetProperty("requiredEvidence", out var required) ? required.EnumerateArray().Select(r => r.GetString()!).ToArray() : [])).ToArray();
    }

    /// <summary>The public corpus shipped with the harness: sample 08's excerpt and its labelled cases.</summary>
    public static (string CorpusPath, IReadOnlyList<GraphRagEvaluationCase> Cases) AliceCorpus()
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "corpus");
        return (Path.Combine(folder, "alice-chapter-1-opening.txt"), LoadCases(Path.Combine(folder, "alice-cases.json")));
    }
}
