using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace GraphBridge.GraphRag;

/// <summary>
/// One answer as one JSON document, for audits and bug reports (ADR 0052, section 17): the question, the answer or refusal, its
/// citations, the evidence and context the model saw, the limits reached, provenance and the receipt. The document holds the
/// passages' text, so treat it like the documents. <see cref="Check"/> confirms, without the database, that every citation is
/// the bytes of the evidence it names.
/// </summary>
public static class GraphRagAnswerExport
{
    /// <summary>The format this version writes and reads.</summary>
    public const string Format = "graphbridge-graphrag-answer/1";

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string ToJson(GraphRagAnswer answer, DateTimeOffset? exportedUtc = null)
    {
        ArgumentNullException.ThrowIfNull(answer);
        var document = new JsonObject
        {
            ["format"] = Format,
            ["exportedUtc"] = (exportedUtc ?? DateTimeOffset.UtcNow).ToUniversalTime().ToString("O"),
            ["toolkit"] = typeof(GraphRagAnswer).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            ["answer"] = JsonSerializer.SerializeToNode(answer, Options)
        };
        return document.ToJsonString(Options);
    }

    /// <summary>Reads a document <see cref="ToJson"/> wrote; another format, or an answer without its citations or evidence, is refused.</summary>
    public static GraphRagAnswer FromJson(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        var document = JsonNode.Parse(json) as JsonObject ?? throw new FormatException("An answer export is a JSON object.");
        var format = document["format"]?.GetValue<string>();
        if (format != Format) throw new FormatException($"This is not a '{Format}' answer export (format '{format}').");
        var answer = document["answer"]?.Deserialize<GraphRagAnswer>(Options) ?? throw new FormatException("The export has no answer.");
        if (answer.Citations is null || answer.Evidence?.Items is null) throw new FormatException("The export's answer has no citations or no evidence.");
        return answer;
    }

    /// <summary>
    /// What is wrong with an answer, read from its own evidence; empty when nothing is. Each citation must name an evidence item,
    /// lie inside it and be its bytes, with the pages, grounding and text origin that item gives those bytes; an answer needs a
    /// citation, and a refusal needs a reason and no text. A malformed answer is reported, not thrown.
    /// </summary>
    public static IReadOnlyList<string> Check(GraphRagAnswer answer)
    {
        ArgumentNullException.ThrowIfNull(answer);
        var problems = new List<string>();
        if (answer.Citations is null || answer.Evidence?.Items is null)
        {
            problems.Add("an answer without its citations or evidence");
            return problems;
        }
        if (answer.Disposition == GraphRagAnswerDisposition.Answered && answer.Citations.Count == 0) problems.Add("an answer without citations");
        if (answer.Disposition == GraphRagAnswerDisposition.InsufficientEvidence && (answer.Text is not null || string.IsNullOrEmpty(answer.Reason)))
            problems.Add("a refusal needs a reason and no text");
        for (var i = 0; i < answer.Citations.Count; i++)
        {
            var citation = answer.Citations[i];
            if (citation?.Quote is null || citation.StartByte < 0 || citation.EndByte <= citation.StartByte)
            {
                problems.Add($"citation {i + 1}: no quote, or not a span");
                continue;
            }
            var item = answer.Evidence.Items.FirstOrDefault(e => e?.Text is not null && e.ChunkId == citation.ChunkId && e.DocumentId == citation.DocumentId
                && e.StartByte <= citation.StartByte && citation.EndByte <= e.EndByte);
            if (item is null)
            {
                problems.Add($"citation {i + 1}: no evidence item of chunk '{citation.ChunkId}' holds bytes {citation.StartByte}-{citation.EndByte}");
                continue;
            }
            var bytes = Encoding.UTF8.GetBytes(item.Text);
            // The bytes themselves, so a span that splits a character can't pass as its replacement character.
            if (bytes.Length != item.EndByte - item.StartByte)
                problems.Add($"citation {i + 1}: evidence item '{item.ChunkId}' is {bytes.Length} bytes, not {item.EndByte - item.StartByte}");
            else if (!bytes.AsSpan(citation.StartByte - item.StartByte, citation.EndByte - citation.StartByte).SequenceEqual(Encoding.UTF8.GetBytes(citation.Quote)))
                problems.Add($"citation {i + 1}: the quote is not the evidence's bytes {citation.StartByte}-{citation.EndByte}");
            else if ((citation.Page, citation.EndPage) != (GraphRagPages.At(item.Pages, citation.StartByte), GraphRagPages.At(item.Pages, citation.EndByte - 1)))
                problems.Add($"citation {i + 1}: its pages are not the evidence's pages of bytes {citation.StartByte}-{citation.EndByte}");
            else if (citation.Grounding != item.Grounding || citation.TextOrigin != item.TextOrigin)
                problems.Add($"citation {i + 1}: its grounding or text origin is not its evidence item's");
        }
        return problems;
    }
}
