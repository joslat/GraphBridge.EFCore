using System.Collections.Concurrent;
using System.ComponentModel;
using GraphBridge.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Neo4j.Driver;

namespace GraphBridge.GraphRag;

/// <summary>Settings for <see cref="GraphRagTools"/>.</summary>
public sealed class GraphRagToolOptions
{
    /// <summary>The retrieval behind both document tools; null uses the toolkit's defaults.</summary>
    public GraphRagRetrievalOptions? Retrieval { get; set; }
    /// <summary>Records every call with the evidence it returned; null records nothing.</summary>
    public IGraphRagToolTrail? Trail { get; set; }
}

/// <summary>Receives one record per tool call: what was asked and which evidence came back, never document text.</summary>
public interface IGraphRagToolTrail
{
    void Record(GraphRagToolCall call);
}

/// <summary>One tool call: the tool, its arguments, the evidence it returned and when.</summary>
public sealed record GraphRagToolCall(string Tool, IReadOnlyDictionary<string, string?> Arguments, IReadOnlyList<GraphRagToolEvidence> Evidence,
    DateTimeOffset AtUtc);

/// <summary>A piece of returned evidence: a span of a document's chunk, a thing, or a fact between two things.</summary>
public sealed record GraphRagToolEvidence(string? DocumentId, string? ChunkId, int? StartByte, int? EndByte, string? ThingId);

/// <summary>An in-memory trail, safe for concurrent calls.</summary>
public sealed class GraphRagToolTrail : IGraphRagToolTrail
{
    private readonly ConcurrentQueue<GraphRagToolCall> _calls = new();

    public IReadOnlyList<GraphRagToolCall> Calls => _calls.ToArray();

    public void Record(GraphRagToolCall call)
    {
        ArgumentNullException.ThrowIfNull(call);
        _calls.Enqueue(call);
    }
}

/// <summary>What <c>answer_from_documents</c> returns: a checked answer with its citations, or a refusal and its reason.</summary>
public sealed record GraphRagToolAnswer(bool Answered, string? Answer, IReadOnlyList<GraphRagToolCitation> Citations, string? Reason);

public sealed record GraphRagToolCitation(string DocumentId, string ChunkId, int StartByte, int EndByte, string Quote);

/// <summary>What <c>search_documents</c> returns: passages to read and quote, each with its exact span.</summary>
public sealed record GraphRagToolPassages(IReadOnlyList<GraphRagToolPassage> Passages, string Note);

public sealed record GraphRagToolPassage(string Evidence, string DocumentId, string ChunkId, int StartByte, int EndByte, string Text);

/// <summary>A thing <c>find_things</c> found by name.</summary>
public sealed record GraphRagToolThing(string Id, string Kind, string Name, IReadOnlyList<string> Aliases, string? Description);

/// <summary>A fact <c>related_facts</c> found: its type, its two ends and the documents that state it.</summary>
public sealed record GraphRagToolFact(string Type, string FromId, string? FromName, string ToId, string? ToName, IReadOnlyList<string> DocumentIds);

/// <summary>
/// Agent tools (ADR 0052, section 8; GR-13) as <see cref="AIFunction"/>s, for any chat client or agent that calls functions.
/// Every result carries IDs and byte spans, so an answer can be traced to stored text.
/// </summary>
public static class GraphRagTools
{
    public const string AnswerTool = "answer_from_documents";
    public const string SearchTool = "search_documents";
    public const string FindThingsTool = "find_things";
    public const string RelatedFactsTool = "related_facts";

    /// <summary>
    /// The document tools, and with an ontology mapped on <typeparamref name="TContext"/>, the tools over extracted things.
    /// <paramref name="services"/> resolves <see cref="IGraphRagAnswers"/>, <see cref="IGraphRagRetriever"/> and scopes of the context.
    /// </summary>
    public static IReadOnlyList<AIFunction> Create<TContext>(IServiceProvider services, GraphRagToolOptions? options = null) where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        options ??= new GraphRagToolOptions();
        options.Retrieval?.Validate();
        var scopes = services.GetRequiredService<IServiceScopeFactory>();
        var tools = new List<AIFunction>
        {
            AIFunctionFactory.Create(
                ([Description("The question to answer from the documents.")] string question, CancellationToken cancellationToken)
                    => AnswerAsync(services, options, question, cancellationToken),
                AnswerTool, "Answers a question from the stored documents. Every citation is checked against the stored text; when the documents don't answer it, the result is a refusal with a reason."),
            AIFunctionFactory.Create(
                ([Description("What to look for.")] string query, [Description("How many passages, 1 to 20.")] int limit, CancellationToken cancellationToken)
                    => SearchAsync(services, options, query, limit, cancellationToken),
                SearchTool, "Finds passages of the stored documents, each with its document, chunk and byte span. Passages are evidence to read, not answers.")
        };
        using (var scope = scopes.CreateScope())
            if (GraphRagRemoval.OntologyOf(scope.ServiceProvider.GetRequiredService<TContext>()) is not null)
            {
                tools.Add(AIFunctionFactory.Create(
                    ([Description("A name or part of one.")] string name, [Description("A kind to restrict to, such as Person; empty for any.")] string? kind, CancellationToken cancellationToken)
                        => FindThingsAsync<TContext>(scopes, options, name, kind, cancellationToken),
                    FindThingsTool, "Finds extracted things (people, places, organisations and the other kinds of the ontology) by name. Returns up to 10."));
                tools.Add(AIFunctionFactory.Create(
                    ([Description("The ID of a thing, from find_things.")] string thingId, CancellationToken cancellationToken)
                        => RelatedFactsAsync<TContext>(scopes, options, thingId, cancellationToken),
                    RelatedFactsTool, "Lists up to 50 facts that involve a thing, with the documents that state each one. Facts are context; cite passages, not facts."));
            }
        return tools.AsReadOnly();
    }

    private static async Task<GraphRagToolAnswer> AnswerAsync(IServiceProvider services, GraphRagToolOptions options, string question, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(question);
        var answer = await services.GetRequiredService<IGraphRagAnswers>().AnswerAsync(question, options.Retrieval?.Clone(), cancellationToken).ConfigureAwait(false);
        var citations = answer.Citations.Select(c => new GraphRagToolCitation(c.DocumentId, c.ChunkId, c.StartByte, c.EndByte, c.Quote)).ToArray();
        Record(options, AnswerTool, new() { ["question"] = question }, citations.Select(c => new GraphRagToolEvidence(c.DocumentId, c.ChunkId, c.StartByte, c.EndByte, null)));
        return new(answer.Disposition == GraphRagAnswerDisposition.Answered, answer.Text, citations, answer.Reason);
    }

    private static async Task<GraphRagToolPassages> SearchAsync(IServiceProvider services, GraphRagToolOptions options, string query, int limit,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        var retrieval = options.Retrieval?.Clone() ?? new GraphRagRetrievalOptions();
        retrieval.MaxItems = Math.Clamp(limit, 1, 20);
        var evidence = await services.GetRequiredService<IGraphRagRetriever>().RetrieveAsync(query, retrieval, cancellationToken).ConfigureAwait(false);
        var passages = evidence.Items.Select((item, i) => new GraphRagToolPassage($"E{i + 1}", item.DocumentId, item.ChunkId, item.StartByte, item.EndByte, item.Text)).ToArray();
        Record(options, SearchTool, new() { ["query"] = query, ["limit"] = retrieval.MaxItems.ToString(System.Globalization.CultureInfo.InvariantCulture) },
            passages.Select(p => new GraphRagToolEvidence(p.DocumentId, p.ChunkId, p.StartByte, p.EndByte, null)));
        return new(passages, "Passages are evidence. To answer with checked citations, use " + AnswerTool + ".");
    }

    private static async Task<IReadOnlyList<GraphRagToolThing>> FindThingsAsync<TContext>(IServiceScopeFactory scopes, GraphRagToolOptions options, string name,
        string? kind, CancellationToken cancellationToken) where TContext : DbContext
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();
        var ontology = GraphRagRemoval.OntologyOf(db)!;
        var terms = GraphRagRetriever<TContext>.LuceneTerms(name);
        var things = new List<GraphRagToolThing>();
        if (terms.Length > 0)
        {
            var labels = ontology.Classes.ToDictionary(c => c.Label, c => c.Kind, StringComparer.Ordinal);
            var hits = await db.Database.QueryFullTextNodesAsync(GraphRagSchema.EntityNamesIndexName(GraphRagRemoval.LayoutOf(db)), terms,
                node => new GraphRagToolThing(node["Id"].As<string>(), node.Labels.Select(l => labels.GetValueOrDefault(l)).FirstOrDefault(k => k is not null) ?? "",
                    node["Name"].As<string>(), node.Properties.TryGetValue("Aliases", out var aliases) && aliases is string text ? text.Split('\n') : [],
                    node.Properties.TryGetValue("Description", out var description) ? description as string : null),
                limit: 30, cancellationToken).ConfigureAwait(false);
            things.AddRange(hits.Results.Select(r => r.Value).Where(t => string.IsNullOrWhiteSpace(kind) || t.Kind == kind).Take(10));
        }
        Record(options, FindThingsTool, new() { ["name"] = name, ["kind"] = kind }, things.Select(t => new GraphRagToolEvidence(null, null, null, null, t.Id)));
        return things;
    }

    private static async Task<IReadOnlyList<GraphRagToolFact>> RelatedFactsAsync<TContext>(IServiceScopeFactory scopes, GraphRagToolOptions options, string thingId,
        CancellationToken cancellationToken) where TContext : DbContext
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(thingId);
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();
        var ontology = GraphRagRemoval.OntologyOf(db)!;
        var layout = GraphRagRemoval.LayoutOf(db);
        // Workaround for GR-06 (search and traversal across relationship types): one bounded read of the thing under any ontology
        // label and the facts of every ontology type that touch it.
        var labels = string.Join(" OR ", ontology.Classes.Select(c => $"t:`{c.Label}`"));
        var facts = await db.Database.QueryCypherAsync(
            $"MATCH (t) WHERE t.Id = $id AND ({labels}) MATCH (t)-[f]-(o) WHERE type(f) IN $types "
            + "RETURN type(f) AS type, startNode(f) = t AS outgoing, t.Id AS thingId, t.Name AS thingName, o.Id AS otherId, "
            + $"CASE WHEN o:`{layout.DocumentLabel}` THEN o.Title ELSE o.Name END AS otherName, f.DocumentIds AS documents LIMIT 50",
            new Dictionary<string, object?> { ["id"] = thingId, ["types"] = ontology.Relationships.Select(r => r.Type).Distinct(StringComparer.Ordinal).ToArray() },
            record =>
            {
                var outgoing = record["outgoing"].As<bool>();
                var (thing, thingName) = (record["thingId"].As<string>(), record["thingName"].As<string?>());
                var (other, otherName) = (record["otherId"].As<string>(), record["otherName"].As<string?>());
                var documents = GraphRagOntologyRuntime.Lines(record["documents"].As<string?>());
                return outgoing
                    ? new GraphRagToolFact(record["type"].As<string>(), thing, thingName, other, otherName, documents)
                    : new GraphRagToolFact(record["type"].As<string>(), other, otherName, thing, thingName, documents);
            }, maxRows: 50, cancellationToken).ConfigureAwait(false);
        Record(options, RelatedFactsTool, new() { ["thingId"] = thingId },
            facts.SelectMany(f => f.DocumentIds.Select(d => new GraphRagToolEvidence(d, null, null, null, f.FromId == thingId ? f.ToId : f.FromId))));
        return facts;
    }

    private static void Record(GraphRagToolOptions options, string tool, Dictionary<string, string?> arguments, IEnumerable<GraphRagToolEvidence> evidence)
        => options.Trail?.Record(new(tool, arguments, evidence.ToArray(), DateTimeOffset.UtcNow));
}
