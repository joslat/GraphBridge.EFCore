using System.Diagnostics;

namespace GraphBridge.GraphRag;

/// <summary>
/// The toolkit's tracing (ADR 0052, section 9; GR-39). Subscribe to <see cref="ActivitySourceName"/>. Activities carry IDs,
/// counts, outcomes and timings only: never document text, questions, quotes, prompts or source URIs.
/// </summary>
public static class GraphRagTracing
{
    public const string ActivitySourceName = "GraphBridge.GraphRag";
    public const string IngestActivity = "GraphRag.Ingest";
    public const string DocumentActivity = "GraphRag.Document";
    public const string EmbedActivity = "GraphRag.Embed";
    public const string ExtractActivity = "GraphRag.Extract";
    public const string WriteActivity = "GraphRag.Write";
    public const string RetrieveActivity = "GraphRag.Retrieve";
    public const string AnswerActivity = "GraphRag.Answer";
    public const string RewriteActivity = "GraphRag.Rewrite";
    public const string Text2CypherActivity = "GraphRag.Text2Cypher";
}

/// <summary>What one answer cost: chat calls, tokens when the client reports them, and the time spent retrieving and answering.</summary>
public sealed record GraphRagAnswerReceipt(int ChatCalls, long? InputTokens, long? OutputTokens, TimeSpan Retrieval, TimeSpan Answer);

internal static class GraphRagDiagnostics
{
    private static readonly ActivitySource Source = new(GraphRagTracing.ActivitySourceName, typeof(GraphRagDiagnostics).Assembly.GetName().Version?.ToString());

    public static Activity? Start(string name) => Source.StartActivity(name, ActivityKind.Internal);

    /// <summary>Marks the activity failed with a stable code; the code is the only description, never an exception message.</summary>
    public static void Fail(Activity? activity, string code)
    {
        if (activity is null) return;
        activity.SetTag("graphrag.error_code", code);
        activity.SetStatus(ActivityStatusCode.Error, code);
    }
}
