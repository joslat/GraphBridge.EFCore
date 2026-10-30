using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace GraphBridge.GraphRag;

/// <summary>Answers a question from retrieved evidence, with verified citations or an honest refusal.</summary>
public interface IGraphRagAnswers
{
    Task<GraphRagAnswer> AnswerAsync(string question, GraphRagRetrievalOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// The same answer, streamed (ADR 0052, section 12): <see cref="GraphRagAnswerUpdateKind.Retrieved"/> once, then provisional
    /// <see cref="GraphRagAnswerUpdateKind.Delta"/> text as the model writes it, then <see cref="GraphRagAnswerUpdateKind.Completed"/>
    /// with the checked answer or a refusal. Cancelling stops the model call.
    /// </summary>
    IAsyncEnumerable<GraphRagAnswerUpdate> AnswerStreamingAsync(string question, GraphRagRetrievalOptions? options = null, CancellationToken cancellationToken = default);
}

public enum GraphRagAnswerDisposition { Answered, InsufficientEvidence }

/// <summary>An answer with every citation verified, or <see cref="GraphRagAnswerDisposition.InsufficientEvidence"/> with
/// <see cref="Reason"/>: <c>no-evidence</c>, <c>model-declined</c>, <c>unreadable-response</c>, <c>no-citations</c> or
/// <c>citation-not-found</c>. A refusal carries no answer text.</summary>
public sealed record GraphRagAnswer(GraphRagAnswerDisposition Disposition, string? Text, IReadOnlyList<GraphRagCitation> Citations,
    string? Reason, GraphRagEvidence Evidence, GraphRagAnswerReceipt? Receipt = null);

public enum GraphRagAnswerUpdateKind { Retrieved, Delta, Completed }

/// <summary>
/// One step of a streamed answer. <see cref="Delta"/> is the next piece of the model's answer text, which no citation has checked
/// yet: a <see cref="GraphRagAnswerUpdateKind.Completed"/> refusal withdraws it, and a completed answer replaces it with
/// <see cref="GraphRagAnswer.Text"/>. <see cref="Answer"/> is set on the completed update only.
/// </summary>
public sealed record GraphRagAnswerUpdate(GraphRagAnswerUpdateKind Kind, GraphRagEvidence Evidence, string? Delta = null, GraphRagAnswer? Answer = null);

/// <summary>
/// ADR 0052, section 4: the chat client is asked for an answer and the exact supporting quote for each claim, by evidence
/// number. Each quote is located in that item with <see cref="GraphRagCitationVerifier"/>; if any fails, or none supports
/// the answer, the result is a refusal. Context is shown to the model as context and is never cited. Streaming (section 12)
/// sends the same request and verifies the same reply.
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
        var timer = Stopwatch.StartNew();
        var (evidence, calls, input, output) = await RetrieveAsync(client, question, options, cancellationToken).ConfigureAwait(false);
        var retrieval = timer.Elapsed;
        GraphRagAnswer answer;
        if (evidence.Items.Count == 0) answer = Refuse(evidence, "no-evidence");
        else
        {
            var response = await client.GetResponseAsync(Messages(evidence), ReplyOptions(), cancellationToken).ConfigureAwait(false);
            (calls, input, output) = (calls + 1, Add(input, response.Usage?.InputTokenCount), Add(output, response.Usage?.OutputTokenCount));
            answer = Verify(evidence, response.Text);
        }
        answer = answer with { Receipt = new(calls, input, output, retrieval, timer.Elapsed - retrieval) };
        Tag(activity, answer, calls, input, output);
        return answer;
    }

    public async IAsyncEnumerable<GraphRagAnswerUpdate> AnswerStreamingAsync(string question, GraphRagRetrievalOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(question);
        var client = chat() ?? throw new InvalidOperationException("Answers need an IChatClient in the container.");
        using var activity = GraphRagDiagnostics.Start(GraphRagTracing.AnswerActivity);
        var timer = Stopwatch.StartNew();
        var (evidence, calls, input, output) = await RetrieveAsync(client, question, options, cancellationToken).ConfigureAwait(false);
        var retrieval = timer.Elapsed;
        yield return new(GraphRagAnswerUpdateKind.Retrieved, evidence);

        GraphRagAnswer answer;
        var deltas = 0;
        if (evidence.Items.Count == 0) answer = Refuse(evidence, "no-evidence");
        else
        {
            calls++;
            var reply = new StringBuilder();
            var reader = new GraphRagAnswerTextReader();
            var updates = new List<ChatResponseUpdate>();
            await foreach (var update in client.GetStreamingResponseAsync(Messages(evidence), ReplyOptions(), cancellationToken).ConfigureAwait(false))
            {
                updates.Add(update);
                if (update.Text is not { Length: > 0 } text) continue;
                reply.Append(text);
                if (reader.Next(reply) is { Length: > 0 } piece)
                {
                    deltas++;
                    yield return new(GraphRagAnswerUpdateKind.Delta, evidence, piece);
                }
            }
            var usage = updates.ToChatResponse().Usage;
            (input, output) = (Add(input, usage?.InputTokenCount), Add(output, usage?.OutputTokenCount));
            answer = Verify(evidence, reply.ToString());
        }
        answer = answer with { Receipt = new(calls, input, output, retrieval, timer.Elapsed - retrieval) };
        Tag(activity, answer, calls, input, output);
        activity?.SetTag("graphrag.deltas", deltas);
        yield return new(GraphRagAnswerUpdateKind.Completed, evidence, Answer: answer);
    }

    internal const string RewriteInstructions = """
        Rewrite the last question so that it can be understood without the conversation before it: replace words such as "he",
        "it" or "that mission" with what they refer to. Keep its meaning and its language, and add nothing else. If it already
        stands alone, return it unchanged. Reply with JSON only: {"question": "..."}
        """;

    internal const int MaxHistoryTurns = 6;
    internal const int MaxTurnAnswerLength = 1_000;
    private const int MaxRewriteLength = 2_000;

    // ADR 0052, section 18: with history, the question is rewritten into one that stands alone, then retrieved; without, nothing
    // changes. A reply that can't be read leaves the asked question, and the evidence says so.
    private async Task<(GraphRagEvidence Evidence, int Calls, long? Input, long? Output)> RetrieveAsync(IChatClient client, string question,
        GraphRagRetrievalOptions? options, CancellationToken cancellationToken)
    {
        if (options?.History is not { Count: > 0 } history)
            return (await retriever.RetrieveAsync(question, options, cancellationToken).ConfigureAwait(false), 0, null, null);
        if (history.Any(t => t is null || string.IsNullOrWhiteSpace(t.Question))) throw new ArgumentException("Every turn of the history needs its question.", nameof(options));
        var turns = history.Skip(Math.Max(0, history.Count - MaxHistoryTurns)).ToArray();
        ChatResponse response;
        string? standalone;
        using (var activity = GraphRagDiagnostics.Start(GraphRagTracing.RewriteActivity))
        {
            response = await client.GetResponseAsync([new(ChatRole.System, RewriteInstructions), new(ChatRole.User, RewritePrompt(turns, question))],
                ReplyOptions(), cancellationToken).ConfigureAwait(false);
            standalone = ReadRewrite(response.Text);
            activity?.SetTag("graphrag.turns", turns.Length);
            activity?.SetTag("graphrag.rewrite", Outcome(question, standalone));
            if (response.Usage?.InputTokenCount is { } inputTokens) activity?.SetTag("graphrag.input_tokens", inputTokens);
            if (response.Usage?.OutputTokenCount is { } outputTokens) activity?.SetTag("graphrag.output_tokens", outputTokens);
        }
        var single = options.Clone();
        single.History = null;
        var evidence = await retriever.RetrieveAsync(standalone ?? question, single, cancellationToken).ConfigureAwait(false);
        evidence = evidence with { Rewrite = new(question, standalone ?? question, turns.Length, Outcome(question, standalone)) };
        if (turns.Length < history.Count) evidence = evidence with { LimitsReached = [.. evidence.LimitsReached, new("HistoryTurns", MaxHistoryTurns)] };
        return (evidence, 1, response.Usage?.InputTokenCount, response.Usage?.OutputTokenCount);
    }

    // A rewrite that differs from the question only in its spacing left it unchanged.
    private static string Outcome(string question, string? standalone) => standalone is null ? "unreadable" : standalone == OneLine(question) ? "unchanged" : "rewritten";

    internal static string RewritePrompt(IReadOnlyList<GraphRagTurn> turns, string question)
    {
        var text = new StringBuilder("Conversation:").Append('\n');
        foreach (var turn in turns)
        {
            text.Append("Q: ").Append(OneLine(turn.Question)).Append('\n');
            if (turn.Answer is { Length: > 0 } answer)
                text.Append("A: ").Append(OneLine(answer.Length > MaxTurnAnswerLength ? Cut(answer) + "…" : answer)).Append('\n');
        }
        return text.Append('\n').Append("Last question: ").Append(OneLine(question)).Append('\n').ToString();
    }

    // The rewritten question, on one line, or null when the reply isn't the JSON asked for or is empty or too long.
    internal static string? ReadRewrite(string? reply)
    {
        if (string.IsNullOrWhiteSpace(reply)) return null;
        var start = reply.IndexOf('{');
        var end = reply.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        try
        {
            using var json = JsonDocument.Parse(reply.AsMemory(start, end - start + 1));
            if (!json.RootElement.TryGetProperty("question", out var question) || question.ValueKind != JsonValueKind.String) return null;
            var text = OneLine(question.GetString()!);
            return text.Length is 0 or > MaxRewriteLength ? null : text;
        }
        catch (JsonException) { return null; }
    }

    private static string OneLine(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    // An earlier answer's first MaxTurnAnswerLength characters, never half of a surrogate pair.
    private static string Cut(string answer) => answer[..(char.IsHighSurrogate(answer[MaxTurnAnswerLength - 1]) ? MaxTurnAnswerLength - 1 : MaxTurnAnswerLength)];

    private static long? Add(long? left, long? right) => left is null && right is null ? null : (left ?? 0) + (right ?? 0);

    private static ChatMessage[] Messages(GraphRagEvidence evidence)
        => [new ChatMessage(ChatRole.System, Instructions), new ChatMessage(ChatRole.User, Prompt(evidence))];

    private static ChatOptions ReplyOptions() => new() { ResponseFormat = ChatResponseFormat.Json };

    private static void Tag(Activity? activity, GraphRagAnswer answer, int calls, long? input, long? output)
    {
        if (activity is null) return;
        activity.SetTag("graphrag.disposition", answer.Disposition.ToString());
        activity.SetTag("graphrag.citations", answer.Citations.Count);
        activity.SetTag("graphrag.chat_calls", calls);
        if (input is { } inputTokens) activity.SetTag("graphrag.input_tokens", inputTokens);
        if (output is { } outputTokens) activity.SetTag("graphrag.output_tokens", outputTokens);
        if (answer.Reason is { } reason) activity.SetTag("graphrag.reason", reason);
    }

    internal static string Prompt(GraphRagEvidence evidence)
    {
        var text = new StringBuilder("Question: ").Append(evidence.Question).Append('\n').Append('\n').Append("Evidence:").Append('\n');
        for (var i = 0; i < evidence.Items.Count; i++)
            text.Append('[').Append((i + 1).ToString(CultureInfo.InvariantCulture)).Append("] ").Append(evidence.Items[i].Text.Trim()).Append('\n');
        if (evidence.Context.Count > 0)
        {
            text.Append('\n').Append("Context (never cite):").Append('\n');
            foreach (var item in evidence.Context) text.Append("- ").Append(item.Text.Trim()).Append('\n');
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

/// <summary>
/// Reads the top-level <c>answer</c> string of a JSON reply while it streams. Each call decodes the reply so far and returns
/// what is new. It stops before an escape sequence or a surrogate pair that hasn't fully arrived, so every piece is text the
/// finished reply also decodes to. Like <see cref="GraphRagAnswerer.Verify"/>, it reads from the first <c>{</c>.
/// </summary>
internal sealed class GraphRagAnswerTextReader
{
    private int _emitted;

    public string Next(StringBuilder reply)
    {
        var decoded = Decode(reply.ToString());
        if (decoded.Length <= _emitted) return "";
        var piece = decoded[_emitted..];
        _emitted = decoded.Length;
        return piece;
    }

    internal static string Decode(string reply)
    {
        var start = reply.IndexOf('{');
        if (start < 0) return "";
        var depth = 0;
        var i = start;
        while (i < reply.Length)
        {
            var c = reply[i];
            if (c == '"')
            {
                var (key, next, closed) = ReadString(reply, i + 1);
                if (!closed) return "";
                i = next;
                if (depth != 1) continue;
                // A key at the top level: skip to its value.
                while (i < reply.Length && char.IsWhiteSpace(reply[i])) i++;
                if (i >= reply.Length || reply[i] != ':') continue;
                i++;
                while (i < reply.Length && char.IsWhiteSpace(reply[i])) i++;
                if (i >= reply.Length) return "";
                if (key == "answer") return reply[i] == '"' ? ReadString(reply, i + 1).Text : "";
                continue;
            }
            if (c is '{' or '[') depth++;
            else if (c is '}' or ']') depth--;
            i++;
        }
        return "";
    }

    // Decodes a JSON string from just after its opening quote. Text is what decoded completely; Closed says the quote ended it.
    private static (string Text, int Next, bool Closed) ReadString(string s, int i)
    {
        var text = new StringBuilder();
        while (i < s.Length)
        {
            var c = s[i];
            if (c == '"') return (text.ToString(), i + 1, true);
            if (c != '\\') { text.Append(c); i++; continue; }
            if (i + 1 >= s.Length) break;
            var e = s[i + 1];
            if (e == 'u')
            {
                if (i + 6 > s.Length || !int.TryParse(s.AsSpan(i + 2, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code)) break;
                text.Append((char)code);
                i += 6;
                continue;
            }
            text.Append(e switch { 'n' => '\n', 't' => '\t', 'r' => '\r', 'b' => '\b', 'f' => '\f', _ => e });
            i += 2;
        }
        // Not closed yet: hold back half of a surrogate pair until its other half arrives.
        if (text.Length > 0 && char.IsHighSurrogate(text[^1])) text.Length--;
        return (text.ToString(), i, false);
    }
}
