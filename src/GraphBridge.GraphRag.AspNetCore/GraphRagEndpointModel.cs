using System.Text.Json.Serialization;
using GraphBridge.GraphRag;
using Microsoft.AspNetCore.Http;

namespace GraphBridge.GraphRag.AspNetCore;

/// <summary>Options for <c>MapGraphRag</c>.</summary>
public sealed class GraphRagEndpointOptions
{
    /// <summary>1 to 10,000 characters; longer questions and queries get a 400 response before any work. 2,000 by default.</summary>
    public int MaxQuestionLength { get; set; } = 2_000;

    /// <summary>Retrieval for <c>ask</c> and <c>search</c>; <see langword="null"/> uses the defaults given to <c>AddGraphRag</c>.
    /// The toolkit checks them on each request.</summary>
    public GraphRagRetrievalOptions? Retrieval { get; set; }

    /// <summary>
    /// The scope of each request, decided by the application, for example from the signed-in user's tenant. Applies to
    /// <c>ask</c>, <c>search</c> and <c>documents/{id}</c>, which answers 404 for a document outside it and lists only the chunks
    /// inside. Return <see langword="null"/> to search everything; an empty scope finds nothing.
    /// </summary>
    public Func<HttpContext, GraphRagScope?>? ScopeFor { get; set; }

    internal void Validate()
    {
        if (MaxQuestionLength is < 1 or > 10_000) throw new ArgumentOutOfRangeException(nameof(MaxQuestionLength), "MaxQuestionLength must be 1 to 10,000.");
    }
}

/// <summary>
/// The body of <c>POST ask</c>. <see cref="History"/> is the conversation so far, oldest first, which the client keeps: at most
/// 20 turns, each question no longer than a question may be and each answer at most four times that. With history, a follow-up
/// is rewritten into a standalone question first, and the <c>retrieved</c> event says how.
/// </summary>
public sealed record GraphRagAskRequest(string? Question, IReadOnlyList<GraphRagTurn>? History = null);

/// <summary>The body of <c>POST search</c>: <see cref="Limit"/> is 1 to 20 passages, 5 by default.</summary>
public sealed record GraphRagSearchRequest(string? Query, int? Limit = null);

/// <summary>The <c>retrieved</c> event: what the evidence holds, the W3C trace ID when tracing is on, and how a scope was kept.</summary>
public sealed record GraphRagRetrievedEvent(int Items, int ContextItems, int Utf8Bytes, int OmittedItems, IReadOnlyList<GraphRagLimit> LimitsReached, string? TraceId,
    GraphRagScopeTrace? Scope = null, GraphRagRewrite? Rewrite = null);

/// <summary>The <c>delta</c> event: the next piece of the answer as the model writes it. No citation has checked it yet.</summary>
public sealed record GraphRagDeltaEvent(string Text);

/// <summary>The <c>verified</c> event: the answer, which replaces the provisional text, and its checked citations.</summary>
public sealed record GraphRagVerifiedEvent(string Answer, IReadOnlyList<GraphRagCitation> Citations, GraphRagEndpointReceipt? Receipt);

/// <summary>The <c>refused</c> event. When <see cref="Withdraws"/> is true, provisional text was sent and must be removed.</summary>
public sealed record GraphRagRefusedEvent(string Reason, bool Withdraws, GraphRagEndpointReceipt? Receipt);

/// <summary>The <c>error</c> event that ends a failed stream: <c>retrieval-failed</c> or <c>model-failed</c>. Details are in the server's log.</summary>
public sealed record GraphRagErrorEvent(string Code);

/// <summary>Model calls, the tokens the provider reported, and timings in milliseconds.</summary>
public sealed record GraphRagEndpointReceipt(int ChatCalls, long? InputTokens, long? OutputTokens, double RetrievalMilliseconds, double AnswerMilliseconds);

/// <summary>The result of <c>POST search</c>. Passages are evidence to read, not answers.</summary>
public sealed record GraphRagSearchResponse(IReadOnlyList<GraphRagSearchPassage> Passages);

/// <summary>A passage: exactly the document's UTF-8 bytes [<see cref="StartByte"/>, <see cref="EndByte"/>).</summary>
public sealed record GraphRagSearchPassage(string DocumentId, string ChunkId, int StartByte, int EndByte, string Text, double Score)
{
    /// <summary>For a paged document, such as a PDF: the pages the passage spans (ADR 0052, section 14). Null otherwise.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<GraphRagPage>? Pages { get; init; }

    /// <summary>Whether the span is in the source document or in the stored chunk's own text (ADR 0052, section 13); written only for a chunk.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public GraphRagGrounding Grounding { get; init; }

    /// <summary>Where the document's text came from when it isn't the source's own, such as OCR (ADR 0052, section 21). Null otherwise.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TextOrigin { get; init; }
}

/// <summary>The result of <c>GET documents/{id}</c>: the document's metadata and its chunks' spans, in order.</summary>
public sealed record GraphRagDocumentResponse(string Id, string SourceUri, string? Title, string MediaType, string? Published, string? Publisher,
    string? Url, long LengthBytes, string ContentHash, string RunId, IReadOnlyList<GraphRagDocumentChunk> Chunks)
{
    /// <summary>For a paged document, such as a PDF: the byte offset where each page starts, in page order. Null otherwise.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<int>? PageStarts { get; init; }

    /// <summary>Where the document's text came from when it isn't the source's own, such as OCR (ADR 0052, section 21). Null otherwise.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TextOrigin { get; init; }
}

public sealed record GraphRagDocumentChunk(string Id, int Order, int StartByte, int EndByte);
