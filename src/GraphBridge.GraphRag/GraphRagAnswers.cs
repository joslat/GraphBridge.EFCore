using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace GraphBridge.GraphRag;

/// <summary>Answers a question from retrieved evidence, with verified citations or an honest refusal.</summary>
public interface IGraphRagAnswers
{
    Task<GraphRagAnswer> AnswerAsync(string question, GraphRagRetrievalOptions? options = null, CancellationToken cancellationToken = default);
}

public enum GraphRagAnswerDisposition { Answered, InsufficientEvidence }

/// <summary>An answer with every citation verified, or <see cref="GraphRagAnswerDisposition.InsufficientEvidence"/> with
/// <see cref="Reason"/>: <c>no-evidence</c>, <c>model-declined</c>, <c>unreadable-response</c>, <c>no-citations</c> or
/// <c>citation-not-found</c>. A refusal carries no answer text.</summary>
public sealed record GraphRagAnswer(GraphRagAnswerDisposition Disposition, string? Text, IReadOnlyList<GraphRagCitation> Citations,
    string? Reason, GraphRagEvidence Evidence, GraphRagAnswerReceipt? Receipt = null);

/// <summary>
/// ADR 0052, section 4: the chat client is asked for an answer and the exact supporting quote for each claim, by evidence
/// number. Each quote is located in that item with <see cref="GraphRagCitationVerifier"/>; if any fails, or none supports
/// the answer, the result is a refusal. Context is shown to the model as context and is never cited.
/// </summary>
internal sealed class GraphRagAnswerer(IGraphRagRetriever retriever, Func<IChatClient?> chat) : IGraphRagAnswers
{
    internal const string Instructions = """
        Answer the question using only the numbered evidence. Reply with JSON only, in one of two forms:
        {"answer": "...", "citations": [{"evidence": 1, "quote": "..."}]}
        {"insufficient": "why the evidence doesn't answer the question"}
        Every claim in the answer needs a citation. A quote is copied exactly from the evidence it names, and may leave words
        out with "..." at most twice. Context passages can help you read the evidence; never cite them.
        """;

    public async Task<GraphRagAnswer> AnswerAsync(string question, GraphRagRetrievalOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(question);
        var client = chat() ?? throw new InvalidOperationException("Answers need an IChatClient in the container.");
        using var activity = GraphRagDiagnostics.Start(GraphRagTracing.AnswerActivity);
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var evidence = await retriever.RetrieveAsync(question, options, cancellationToken).ConfigureAwait(false);
        var retrieval = timer.Elapsed;
        GraphRagAnswer answer;
        var (calls, input, output) = (0, (long?)null, (long?)null);
        if (evidence.Items.Count == 0) answer = Refuse(evidence, "no-evidence");
        else
        {
            var response = await client.GetResponseAsync(
                [new ChatMessage(ChatRole.System, Instructions), new ChatMessage(ChatRole.User, Prompt(evidence))],
                new ChatOptions { ResponseFormat = ChatResponseFormat.Json }, cancellationToken).ConfigureAwait(false);
            (calls, input, output) = (1, response.Usage?.InputTokenCount, response.Usage?.OutputTokenCount);
            answer = Verify(evidence, response.Text);
        }
        answer = answer with { Receipt = new(calls, input, output, retrieval, timer.Elapsed - retrieval) };
        if (activity is not null)
        {
            activity.SetTag("graphrag.disposition", answer.Disposition.ToString());
            activity.SetTag("graphrag.citations", answer.Citations.Count);
            activity.SetTag("graphrag.chat_calls", calls);
            if (input is { } inputTokens) activity.SetTag("graphrag.input_tokens", inputTokens);
            if (output is { } outputTokens) activity.SetTag("graphrag.output_tokens", outputTokens);
            if (answer.Reason is { } reason) activity.SetTag("graphrag.reason", reason);
        }
        return answer;
    }

    internal static string Prompt(GraphRagEvidence evidence)
    {
        var text = new StringBuilder("Question: ").AppendLine(evidence.Question).AppendLine().AppendLine("Evidence:");
        for (var i = 0; i < evidence.Items.Count; i++)
            text.Append('[').Append((i + 1).ToString(CultureInfo.InvariantCulture)).Append("] ").AppendLine(evidence.Items[i].Text.Trim());
        if (evidence.Context.Count > 0)
        {
            text.AppendLine().AppendLine("Context (never cite):");
            foreach (var item in evidence.Context) text.Append("- ").AppendLine(item.Text.Trim());
        }
        return text.ToString();
    }

    internal static GraphRagAnswer Verify(GraphRagEvidence evidence, string? reply)
    {
        if (string.IsNullOrWhiteSpace(reply)) return Refuse(evidence, "unreadable-response");
        // Tolerate a code fence or a sentence around the JSON object; nothing else is interpreted.
        var start = reply.IndexOf('{');
        var end = reply.LastIndexOf('}');
        if (start < 0 || end <= start) return Refuse(evidence, "unreadable-response");
        try
        {
            using var json = JsonDocument.Parse(reply.AsMemory(start, end - start + 1));
            var root = json.RootElement;
            if (root.TryGetProperty("insufficient", out _)) return Refuse(evidence, "model-declined");
            if (!root.TryGetProperty("answer", out var answer) || answer.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(answer.GetString()))
                return Refuse(evidence, "unreadable-response");
            if (!root.TryGetProperty("citations", out var list) || list.ValueKind != JsonValueKind.Array || list.GetArrayLength() == 0)
                return Refuse(evidence, "no-citations");
            var citations = new List<GraphRagCitation>();
            foreach (var entry in list.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("evidence", out var number) || !number.TryGetInt32(out var index)
                    || index < 1 || index > evidence.Items.Count || !entry.TryGetProperty("quote", out var quote) || quote.ValueKind != JsonValueKind.String)
                    return Refuse(evidence, "citation-not-found");
                var located = GraphRagCitationVerifier.Locate(evidence.Items[index - 1], quote.GetString()!);
                if (located is null) return Refuse(evidence, "citation-not-found");
                if (!citations.Contains(located)) citations.Add(located);
            }
            return new(GraphRagAnswerDisposition.Answered, answer.GetString()!.Trim(), citations, null, evidence);
        }
        catch (JsonException) { return Refuse(evidence, "unreadable-response"); }
    }

    private static GraphRagAnswer Refuse(GraphRagEvidence evidence, string reason)
        => new(GraphRagAnswerDisposition.InsufficientEvidence, null, [], reason, evidence);
}
