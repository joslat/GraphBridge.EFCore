using System.Diagnostics;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text.Json;
using GraphBridge.GraphRag;
using GraphBridge.GraphRag.AspNetCore;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Microsoft.AspNetCore.Builder;

/// <summary>
/// The toolkit's HTTP endpoints (ADR 0052, section 12). Authentication, authorization and rate limits are the application's:
/// add them to the returned group, for example <c>.RequireAuthorization()</c> and <c>.RequireRateLimiting("graphrag")</c>.
/// </summary>
public static class GraphRagEndpointRouteBuilderExtensions
{
    private const string LoggerName = "GraphBridge.GraphRag.AspNetCore";
    private const int MaxHistoryTurns = 20;

    /// <summary>
    /// Maps <c>POST {prefix}/ask</c> (server-sent events), <c>POST {prefix}/search</c> and <c>GET {prefix}/documents/{id}</c>.
    /// <typeparamref name="TContext"/> is the context that maps the GraphRAG model with <c>AddGraphRag</c>; its services come
    /// from <c>AddGraphRag&lt;TContext&gt;</c>.
    /// </summary>
    public static RouteGroupBuilder MapGraphRag<TContext>(this IEndpointRouteBuilder endpoints, string prefix = "/graphrag",
        Action<GraphRagEndpointOptions>? configure = null) where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        var options = new GraphRagEndpointOptions();
        configure?.Invoke(options);
        options.Validate();

        var group = endpoints.MapGroup(prefix);
        group.MapPost("/ask", (GraphRagAskRequest? request, IGraphRagAnswers answers, HttpContext http) => Ask(request, answers, http, options));
        group.MapPost("/search", (GraphRagSearchRequest? request, IGraphRagRetriever retriever, HttpContext http, CancellationToken cancellationToken)
            => SearchAsync(request, retriever, options, http, cancellationToken));
        // The context comes from the request's services; a type parameter as a handler parameter stops ASP.NET Core's route analyzer.
        group.MapGet("/documents/{id}", (string id, HttpContext http, CancellationToken cancellationToken)
            => DocumentAsync(id, http.RequestServices.GetRequiredService<TContext>(), options.ScopeFor?.Invoke(http), cancellationToken));
        return group;
    }

    private static IResult Ask(GraphRagAskRequest? request, IGraphRagAnswers answers, HttpContext http, GraphRagEndpointOptions options)
    {
        var question = request?.Question;
        if (string.IsNullOrWhiteSpace(question)) return TypedResults.Problem("The question is empty.", statusCode: StatusCodes.Status400BadRequest);
        if (question.Length > options.MaxQuestionLength)
            return TypedResults.Problem($"The question is longer than {options.MaxQuestionLength} characters.", statusCode: StatusCodes.Status400BadRequest);
        var history = request!.History;
        if (history is { Count: > MaxHistoryTurns })
            return TypedResults.Problem($"The history has more than {MaxHistoryTurns} turns.", statusCode: StatusCodes.Status400BadRequest);
        if (history?.Any(t => t is null || string.IsNullOrWhiteSpace(t.Question) || t.Question.Length > options.MaxQuestionLength
                || t.Answer?.Length > 4 * options.MaxQuestionLength) == true)
            return TypedResults.Problem($"Each turn of the history needs a question of at most {options.MaxQuestionLength} characters, and an answer of at most {4 * options.MaxQuestionLength}.",
                statusCode: StatusCodes.Status400BadRequest);
        var traceId = Activity.Current?.TraceId.ToHexString();
        var logger = http.RequestServices.GetService<ILoggerFactory>()?.CreateLogger(LoggerName);
        var retrieval = RetrievalFor(http, options);
        if (history is { Count: > 0 })
        {
            retrieval ??= Copy(http.RequestServices.GetService<GraphRagOptions>()?.Retrieval) ?? new GraphRagRetrievalOptions();
            retrieval.History = history;
        }
        return TypedResults.ServerSentEvents(Events(answers.AnswerStreamingAsync(question, retrieval, http.RequestAborted), traceId, logger, http.RequestAborted));
    }

    // Each update becomes one event. A failure ends the stream with an error event that names the phase, never the exception's text.
    private static async IAsyncEnumerable<SseItem<string>> Events(IAsyncEnumerable<GraphRagAnswerUpdate> updates, string? traceId, ILogger? logger,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var enumerator = updates.GetAsyncEnumerator(cancellationToken);
        await using var _ = enumerator.ConfigureAwait(false);
        var (retrieved, deltas) = (false, 0);
        while (true)
        {
            bool more;
            Exception? failure = null;
            try { more = await enumerator.MoveNextAsync().ConfigureAwait(false); }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                (more, failure) = (false, exception);
            }
            if (failure is not null)
            {
                var code = retrieved ? "model-failed" : "retrieval-failed";
                logger?.LogError(failure, "GraphRAG ask failed: {Code}.", code);
                yield return Item("error", new GraphRagErrorEvent(code));
                yield break;
            }
            if (!more) yield break;
            var update = enumerator.Current;
            switch (update.Kind)
            {
                case GraphRagAnswerUpdateKind.Retrieved:
                    retrieved = true;
                    var evidence = update.Evidence;
                    yield return Item("retrieved", new GraphRagRetrievedEvent(evidence.Items.Count, evidence.Context.Count, evidence.Utf8Bytes,
                        evidence.OmittedItems, evidence.LimitsReached, traceId, evidence.Scope, evidence.Rewrite));
                    break;
                case GraphRagAnswerUpdateKind.Delta:
                    deltas++;
                    yield return Item("delta", new GraphRagDeltaEvent(update.Delta!));
                    break;
                case GraphRagAnswerUpdateKind.Completed:
                    var answer = update.Answer!;
                    var receipt = answer.Receipt is { } r
                        ? new GraphRagEndpointReceipt(r.ChatCalls, r.InputTokens, r.OutputTokens, r.Retrieval.TotalMilliseconds, r.Answer.TotalMilliseconds)
                        : null;
                    yield return answer.Disposition == GraphRagAnswerDisposition.Answered
                        ? Item("verified", new GraphRagVerifiedEvent(answer.Text!, answer.Citations, receipt))
                        : Item("refused", new GraphRagRefusedEvent(answer.Reason!, deltas > 0, receipt));
                    break;
            }
        }
    }

    private static async Task<IResult> SearchAsync(GraphRagSearchRequest? request, IGraphRagRetriever retriever, GraphRagEndpointOptions options,
        HttpContext http, CancellationToken cancellationToken)
    {
        var query = request?.Query;
        if (string.IsNullOrWhiteSpace(query)) return TypedResults.Problem("The query is empty.", statusCode: StatusCodes.Status400BadRequest);
        if (query.Length > options.MaxQuestionLength)
            return TypedResults.Problem($"The query is longer than {options.MaxQuestionLength} characters.", statusCode: StatusCodes.Status400BadRequest);
        var limit = request!.Limit ?? 5;
        if (limit is < 1 or > 20) return TypedResults.Problem("The limit must be 1 to 20.", statusCode: StatusCodes.Status400BadRequest);
        var retrieval = RetrievalFor(http, options) ?? Copy(http.RequestServices.GetService<GraphRagOptions>()?.Retrieval) ?? new GraphRagRetrievalOptions();
        retrieval.MaxItems = limit;
        var evidence = await retriever.RetrieveAsync(query, retrieval, cancellationToken).ConfigureAwait(false);
        return TypedResults.Ok(new GraphRagSearchResponse(evidence.Items
            .Select(i => new GraphRagSearchPassage(i.DocumentId, i.ChunkId, i.StartByte, i.EndByte, i.Text, i.Score)
            {
                Pages = i.Pages, Grounding = i.Grounding, TextOrigin = i.TextOrigin
            }).ToArray()));
    }

    // Under ScopeFor, a document outside the request's scope is not found, and a document partly inside it lists only the chunks inside.
    private static async Task<IResult> DocumentAsync<TContext>(string id, TContext db, GraphRagScope? scope, CancellationToken cancellationToken) where TContext : DbContext
    {
        Func<int, int, bool>? inScope = null;
        if (scope is not null && (inScope = await GraphRagScopeCheck.DocumentAsync(db, scope, id, cancellationToken).ConfigureAwait(false)) is null)
            return TypedResults.NotFound();
        var document = await db.Set<GraphRagDocument>().AsNoTracking().Where(d => d.Id == id).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (document is null) return TypedResults.NotFound();
        // Spans only: the text is in the chunks, and a client reads what a citation quotes from the citation itself.
        var chunks = await db.Set<GraphRagChunk>().AsNoTracking().Where(c => c.DocumentId == id).OrderBy(c => c.Order)
            .Select(c => new GraphRagDocumentChunk(c.Id, c.Order, c.StartByte, c.EndByte)).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (inScope is not null) chunks = [.. chunks.Where(c => inScope(c.StartByte, c.EndByte))];
        return TypedResults.Ok(new GraphRagDocumentResponse(document.Id, document.SourceUri, document.Title, document.MediaType, document.Published,
            document.Publisher, document.Url, document.LengthBytes, document.ContentHash, document.RunId, chunks)
        {
            PageStarts = document.PageStarts is { Length: > 0 } starts
                ? [.. starts.Split(',').Select(s => int.Parse(s, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture))] : null,
            TextOrigin = document.TextOrigin
        });
    }

    // The endpoint's retrieval options, or the toolkit's defaults when a scope needs options of its own, with this request's scope.
    private static GraphRagRetrievalOptions? RetrievalFor(HttpContext http, GraphRagEndpointOptions options)
    {
        if (options.ScopeFor is null) return Copy(options.Retrieval);
        var retrieval = Copy(options.Retrieval ?? http.RequestServices.GetService<GraphRagOptions>()?.Retrieval) ?? new GraphRagRetrievalOptions();
        retrieval.Scope = options.ScopeFor(http);
        return retrieval;
    }

    private static SseItem<string> Item<T>(string eventType, T data) => new(JsonSerializer.Serialize(data, JsonSerializerOptions.Web), eventType);

    // Each request gets its own copy, so a caller can't change the shared options.
    private static GraphRagRetrievalOptions? Copy(GraphRagRetrievalOptions? options)
    {
        if (options is null) return null;
        var copy = new GraphRagRetrievalOptions
        {
            Routes = options.Routes, CandidatesPerRoute = options.CandidatesPerRoute, MaxItems = options.MaxItems,
            MaxUtf8Bytes = options.MaxUtf8Bytes, NeighbourChunks = options.NeighbourChunks, Scope = options.Scope, ScopeExactChunks = options.ScopeExactChunks
        };
        return options.CypherExpansion is { } cypher ? copy.WithCypherExpansion(cypher, options.CypherExpansionParameters) : copy;
    }
}
