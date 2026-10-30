using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GraphBridge.EntityFrameworkCore.Infrastructure;
using GraphBridge.GraphRag.Ontologies;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Neo4j.Driver;

namespace GraphBridge.GraphRag;

/// <summary>Bounds for Text2Cypher (ADR 0052, section 15).</summary>
public sealed class GraphRagText2CypherOptions
{
    /// <summary>1 to 1,000, 50 by default: the most rows read; more are reported in <c>LimitsReached</c>.</summary>
    public int MaxRows { get; set; } = 50;
    /// <summary>1 second to 5 minutes, 10 seconds by default: the server's timeout for the query's read transaction.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);
    /// <summary>100 to 100,000 characters, 4,000 by default: a longer query is refused as <c>not-allowed</c>.</summary>
    public int MaxQueryLength { get; set; } = 4000;

    internal void Validate()
    {
        if (MaxRows is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(MaxRows), "MaxRows must be 1 to 1,000.");
        if (Timeout < TimeSpan.FromSeconds(1) || Timeout > TimeSpan.FromMinutes(5)) throw new ArgumentOutOfRangeException(nameof(Timeout), "Timeout must be 1 second to 5 minutes.");
        if (MaxQueryLength is < 100 or > 100_000) throw new ArgumentOutOfRangeException(nameof(MaxQueryLength), "MaxQueryLength must be 100 to 100,000.");
    }
}

/// <summary>
/// Questions about the graph, answered by a read-only query a model writes (ADR 0052, section 15). Experimental and off by
/// default: register it with <see cref="GraphRagText2CypherServiceCollectionExtensions.AddGraphRagText2Cypher{TContext}"/>.
/// </summary>
public interface IGraphRagGraphQuestions
{
    Task<GraphRagGraphAnswer> AskAsync(string question, CancellationToken cancellationToken = default);
}

public enum GraphRagGraphAnswerDisposition
{
    Answered,
    Refused
}

/// <summary>
/// A graph answer: the query a model wrote and the rows it returned, never a text citation. A refusal has a reason:
/// <c>model-declined</c>, <c>unreadable-response</c>, <c>not-allowed</c>, <c>invalid-query</c>, <c>not-read-only</c> or
/// <c>query-failed</c>. Rows hold strings, numbers, booleans, lists and maps; a node or relationship is its properties without
/// embeddings. A list longer than 1,000 items or a string longer than 10,000 characters is cut, and reported in
/// <see cref="LimitsReached"/> as <c>GraphQueryListItems</c> or <c>GraphQueryText</c>.
/// </summary>
public sealed record GraphRagGraphAnswer(GraphRagGraphAnswerDisposition Disposition, string? Reason, string? Cypher, IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows, IReadOnlyList<GraphRagLimit> LimitsReached)
{
    public GraphRagGraphReceipt? Receipt { get; init; }
}

/// <summary>Model calls, the tokens the provider reported, and the time taken.</summary>
public sealed record GraphRagGraphReceipt(int ChatCalls, long? InputTokens, long? OutputTokens, TimeSpan Elapsed);

public static class GraphRagText2CypherServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IGraphRagGraphQuestions"/> over <typeparamref name="TContext"/>'s GraphRAG model, with the
    /// <see cref="IChatClient"/> in the container. Experimental and off by default: nothing exists until this is called.
    /// </summary>
    public static IServiceCollection AddGraphRagText2Cypher<TContext>(this IServiceCollection services, Action<GraphRagText2CypherOptions>? configure = null)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = new GraphRagText2CypherOptions();
        configure?.Invoke(options);
        options.Validate();
        services.TryAddTransient<IGraphRagGraphQuestions>(provider => new GraphRagText2Cypher<TContext>(provider.GetRequiredService<IServiceScopeFactory>(),
            options, () => provider.GetService<IChatClient>()));
        return services;
    }
}

internal sealed partial class GraphRagText2Cypher<TContext>(IServiceScopeFactory scopes, GraphRagText2CypherOptions options, Func<IChatClient?> chat)
    : IGraphRagGraphQuestions where TContext : DbContext
{
    internal const string Instructions = """
        Write one read-only Cypher query for Neo4j (Cypher 25) that answers the question from the graph described below. Use only
        the labels, relationship types and properties it lists; match names exactly as they would be stored. Return short, named
        columns and at most {0} rows. Reply with JSON only, in one of two forms:
        {{"cypher": "..."}}
        {{"insufficient": "why the graph can't answer the question"}}
        """;

    public async Task<GraphRagGraphAnswer> AskAsync(string question, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(question);
        var client = chat() ?? throw new InvalidOperationException("Text2Cypher needs an IChatClient in the container.");
        using var activity = GraphRagDiagnostics.Start(GraphRagTracing.Text2CypherActivity);
        var timer = Stopwatch.StartNew();
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();
        var layout = GraphRagRemoval.LayoutOf(db);
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, string.Format(CultureInfo.InvariantCulture, Instructions, options.MaxRows)),
            new(ChatRole.User, $"Question: {question.Trim()}\n\nGraph:\n{Schema(layout, GraphRagRemoval.OntologyOf(db))}")
        };
        var response = await client.GetResponseAsync(messages, new ChatOptions { ResponseFormat = ChatResponseFormat.Json }, cancellationToken).ConfigureAwait(false);
        GraphRagGraphReceipt Receipt() => new(1, response.Usage?.InputTokenCount, response.Usage?.OutputTokenCount, timer.Elapsed);

        var answer = await RunAsync(db, layout, response.Text, cancellationToken).ConfigureAwait(false) with { Receipt = Receipt() };
        activity?.SetTag("graphrag.disposition", answer.Disposition.ToString());
        activity?.SetTag("graphrag.reason", answer.Reason);
        activity?.SetTag("graphrag.rows", answer.Rows.Count);
        activity?.SetTag("graphrag.chat_calls", 1);
        return answer;
    }

    private async Task<GraphRagGraphAnswer> RunAsync(TContext db, GraphRagLayout layout, string? reply, CancellationToken cancellationToken)
    {
        var (cypher, refusal) = Parse(reply);
        if (refusal is not null) return Refuse(refusal, null);
        if (!Allowed(cypher!, options.MaxQueryLength)) return Refuse("not-allowed", cypher);

        var extension = db.GetService<IDbContextOptions>().FindExtension<Neo4jDbContextOptionsExtension>()
            ?? throw new InvalidOperationException("Text2Cypher needs a context configured with UseNeo4j.");
        await using var session = extension.Driver.AsyncSession(o => o.WithDatabase(extension.DatabaseName).WithDefaultAccessMode(AccessMode.Read));
        // The server decides whether the query writes, and what its plan reaches: EXPLAIN plans it without running it. Explicit
        // transactions, so the driver never retries one past the timeout.
        IResultSummary plan;
        try
        {
            await using var explain = await session.BeginTransactionAsync(config => config.WithTimeout(options.Timeout)).ConfigureAwait(false);
            plan = await (await explain.RunAsync("EXPLAIN " + cypher).ConfigureAwait(false)).ConsumeAsync().ConfigureAwait(false);
        }
        catch (ClientException) { return Refuse("invalid-query", cypher); }
        if (plan.QueryType != QueryType.ReadOnly) return Refuse("not-read-only", cypher);
        if (!plan.HasPlan || !PlanAllowed(plan.Plan)) return Refuse("not-allowed", cypher);

        try
        {
            // A read: the transaction is rolled back as it is disposed.
            await using var transaction = await session.BeginTransactionAsync(config => config.WithTimeout(options.Timeout)).ConfigureAwait(false);
            var cursor = await transaction.RunAsync(cypher).ConfigureAwait(false);
            var columns = await cursor.KeysAsync().ConfigureAwait(false);
            var values = new RowValues(layout.ChunkEmbeddingProperty);
            var rows = new List<IReadOnlyDictionary<string, object?>>();
            var more = false;
            while (await cursor.FetchAsync().ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (rows.Count == options.MaxRows) { more = true; break; }
                rows.Add(columns.ToDictionary(k => k, k => values.Value(cursor.Current[k]), StringComparer.Ordinal));
            }
            await cursor.ConsumeAsync().ConfigureAwait(false);
            GraphRagLimit[] limits = [.. more ? [new GraphRagLimit("GraphQueryRows", options.MaxRows)] : Array.Empty<GraphRagLimit>(), .. values.Limits];
            return new(GraphRagGraphAnswerDisposition.Answered, null, cypher, columns, rows, limits);
        }
        // An outage isn't the query's fault: it propagates, rather than reading as a refusal of the model's query.
        catch (Neo4jException error) when (error is not (ServiceUnavailableException or SessionExpiredException)) { return Refuse("query-failed", cypher); }
    }

    private static GraphRagGraphAnswer Refuse(string reason, string? cypher) => new(GraphRagGraphAnswerDisposition.Refused, reason, cypher, [], [], []);

    // The model's JSON: a query, or why the graph can't answer. A code fence or a sentence around the object is tolerated.
    internal static (string? Cypher, string? Refusal) Parse(string? reply)
    {
        if (string.IsNullOrWhiteSpace(reply)) return (null, "unreadable-response");
        var (start, end) = (reply.IndexOf('{'), reply.LastIndexOf('}'));
        if (start < 0 || end <= start) return (null, "unreadable-response");
        try
        {
            using var json = JsonDocument.Parse(reply.AsMemory(start, end - start + 1));
            if (json.RootElement.TryGetProperty("insufficient", out _)) return (null, "model-declined");
            return json.RootElement.TryGetProperty("cypher", out var cypher) && cypher.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(cypher.GetString())
                ? (cypher.GetString()!.Trim().TrimEnd(';').Trim(), null) : (null, "unreadable-response");
        }
        catch (JsonException) { return (null, "unreadable-response"); }
    }

    // The text gate, on the query with its comments and string literals blanked and its quoted names unquoted, so that none of
    // them hides a keyword or trips one: one statement that starts with a reading clause, no procedure call, file, other database
    // or batched transaction, and functions only from Neo4j's own namespaces. The server's EXPLAIN plan is checked after it.
    internal static bool Allowed(string cypher, int maxLength)
    {
        if (cypher.Length > maxLength || Code(cypher) is not { } code) return false;
        return !code.Contains(';', StringComparison.Ordinal) && First().IsMatch(code) && !Forbidden().IsMatch(code)
            && Functions().Matches(code).All(m => BuiltInNamespaces.Contains(Regex.Replace(m.Groups["ns"].Value, @"\s", "")));
    }

    // Namespaces of Cypher 25's own functions; any other namespace, such as a plugin's, is refused.
    private static readonly HashSet<string> BuiltInNamespaces = new(StringComparer.OrdinalIgnoreCase)
    {
        "date", "datetime", "localdatetime", "localtime", "time", "duration", "point", "vector.similarity", "coll"
    };

    // The query's code: a comment becomes a space, a string literal loses its content, and a quoted name loses its quotes and
    // anything but letters, digits and dots. Null when a literal, a quoted name or a comment isn't closed.
    internal static string? Code(string cypher)
    {
        var code = new StringBuilder(cypher.Length);
        for (var i = 0; i < cypher.Length; i++)
        {
            var c = cypher[i];
            if (c is '\'' or '"')
            {
                var end = i + 1;
                while (end < cypher.Length && cypher[end] != c) end += cypher[end] == '\\' ? 2 : 1;
                if (end >= cypher.Length) return null;
                code.Append(c).Append(c);
                i = end;
            }
            else if (c == '`')
            {
                var end = i + 1;
                while (end < cypher.Length && (cypher[end] != '`' || end + 1 < cypher.Length && cypher[end + 1] == '`')) end += cypher[end] == '`' ? 2 : 1;
                if (end >= cypher.Length) return null;
                foreach (var n in cypher.AsSpan(i + 1, end - i - 1)) code.Append(char.IsAsciiLetterOrDigit(n) || n == '.' ? n : '_');
                i = end;
            }
            else if (c == '/' && i + 1 < cypher.Length && cypher[i + 1] == '/')
            {
                while (i + 1 < cypher.Length && cypher[i + 1] != '\n') i++;
                code.Append(' ');
            }
            else if (c == '/' && i + 1 < cypher.Length && cypher[i + 1] == '*')
            {
                var end = cypher.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0) return null;
                code.Append(' ');
                i = end + 1;
            }
            else code.Append(c);
        }
        return code.ToString();
    }

    [GeneratedRegex(@"^\s*(?:CYPHER\s+\d+\s+)?(?:MATCH|OPTIONAL\s+MATCH|WITH|UNWIND|RETURN|CALL\s*[{(])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex First();

    [GeneratedRegex(@"(?<![\w.$])(?:LOAD\s+CSV\b|CALL\b(?!\s*[{(])|USE\s+\S)|\bIN\s+TRANSACTIONS\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Forbidden();

    // A namespaced function call, such as point.distance(...) or apoc.cypher.runFirstColumnSingle(...).
    [GeneratedRegex(@"(?<![\w.$])(?<ns>[A-Za-z_]\w*(?:\s*\.\s*[A-Za-z_]\w*)*?)\s*\.\s*[A-Za-z_]\w*\s*\(", RegexOptions.CultureInvariant)]
    private static partial Regex Functions();

    // A plan that reaches outside the graph, through a procedure, a file or a batched transaction, is refused even when the text
    // gate let its query through.
    internal static bool PlanAllowed(IPlan plan)
    {
        var name = plan.OperatorType?.Split('@')[0] ?? "";
        return !name.Contains("ProcedureCall", StringComparison.OrdinalIgnoreCase) && !name.Equals("LoadCSV", StringComparison.OrdinalIgnoreCase)
            && !name.StartsWith("Transaction", StringComparison.OrdinalIgnoreCase) && (plan.Children ?? []).All(PlanAllowed);
    }

    /// <summary>The graph as the model sees it: labels, properties, and relationship types with their ends.</summary>
    internal static string Schema(GraphRagLayout layout, GraphRagOntology? ontology)
    {
        var text = new StringBuilder();
        text.Append("Nodes (label: properties):").Append('\n');
        text.Append($"- {layout.DocumentLabel}: a source document. Id, Title, Published, Publisher, Url, SourceUri").Append('\n');
        text.Append($"- {layout.ChunkLabel}: a passage of a document. {layout.ChunkIdProperty}, DocumentId, {layout.ChunkTextProperty}, {layout.ChunkOrderProperty}").Append('\n');
        var labels = new Dictionary<string, string>(StringComparer.Ordinal) { [GraphRagOntologyBuilder.DocumentKind] = layout.DocumentLabel };
        foreach (var type in ontology?.Classes ?? [])
        {
            labels[type.Kind] = type.Label;
            var extra = type.Properties.Count > 0 ? ", " + string.Join(", ", type.Properties) : "";
            text.Append($"- {type.Label}: {type.Kind}, {type.Description} Id, {ontology!.NameProperty}, Aliases (one per line), Description, IsPlaceholder{extra}").Append('\n');
        }
        text.Append("Relationships:").Append('\n');
        text.Append($"- ({layout.ChunkLabel})-[:{layout.ChunkDocumentType}]->({layout.DocumentLabel})").Append('\n');
        foreach (var type in ontology?.Classes ?? [])
            text.Append($"- ({type.Label})-[:{type.MentionType} {{Quote}}]->({layout.ChunkLabel}): the {type.Kind} is mentioned in the passage.").Append('\n');
        foreach (var fact in ontology?.Relationships ?? [])
        {
            var extra = fact.Properties.Count > 0 ? ", " + string.Join(", ", fact.Properties) : "";
            text.Append($"- ({labels.GetValueOrDefault(fact.FromKind, fact.FromKind)})-[:{fact.Type} {{DocumentIds{extra}}}]->({labels.GetValueOrDefault(fact.ToKind, fact.ToKind)}): {fact.Description}").Append('\n');
        }
        foreach (var quote in ontology?.Quotes ?? [])
            text.Append($"- ({labels.GetValueOrDefault(quote.SpeakerKind, quote.SpeakerKind)})-[:{quote.Type} {{Words{(quote.Addressee ? ", Addressee" : "")}}}]->({layout.ChunkLabel}): {quote.Description}").Append('\n');
        text.Append("DocumentIds holds the Ids of the documents that state a fact, one per line.").Append('\n');
        return text.ToString();
    }

    internal static object? Value(object? value, string embeddingProperty) => new RowValues(embeddingProperty).Value(value);

    /// <summary>
    /// Rows as plain values, bounded. A node or relationship is its properties, without the chunk's embedding property. An
    /// embedding is left out anywhere: a list of more than 32 numbers with a fractional one, and in properties any numeric list
    /// that long, such as a staged embedding. Lists keep 1,000 items and strings 10,000 characters; what's cut is reported.
    /// </summary>
    internal sealed class RowValues(string embeddingProperty)
    {
        internal const int MaxListItems = 1000;
        internal const int MaxTextLength = 10_000;
        private bool _listsCut, _textCut;

        public IEnumerable<GraphRagLimit> Limits
            => (_listsCut ? [new GraphRagLimit("GraphQueryListItems", MaxListItems)] : Array.Empty<GraphRagLimit>())
                .Concat(_textCut ? [new GraphRagLimit("GraphQueryText", MaxTextLength)] : []);

        public object? Value(object? value) => value switch
        {
            null or bool or long or double => value,
            string text => Text(text),
            INode node => Properties(node.Properties),
            IRelationship relationship => Properties(relationship.Properties),
            IPath path => List(path.Nodes),
            IReadOnlyDictionary<string, object> map => Properties(map),
            IDictionary<string, object> map => Properties(map.ToDictionary()),
            System.Collections.IList list => Embedding(list, integers: false) ? null : List(list.Cast<object?>()),
            IFormattable formattable => Text(formattable.ToString(null, CultureInfo.InvariantCulture)),
            _ => Text(value.ToString() ?? "")
        };

        private string Text(string text)
        {
            if (text.Length <= MaxTextLength) return text;
            _textCut = true;
            return text[..(char.IsHighSurrogate(text[MaxTextLength - 1]) ? MaxTextLength - 1 : MaxTextLength)];
        }

        private object?[] List(IEnumerable<object?> items)
        {
            var kept = items.Take(MaxListItems + 1).ToList();
            if (kept.Count > MaxListItems) { _listsCut = true; kept.RemoveAt(MaxListItems); }
            return [.. kept.Select(Value)];
        }

        private static bool Embedding(System.Collections.IList list, bool integers)
            => list.Count > 32 && list.Cast<object?>().All(v => v is double or long) && (integers || list.Cast<object?>().Any(v => v is double));

        private Dictionary<string, object?> Properties(IReadOnlyDictionary<string, object> properties)
            => properties.Where(p => p.Key != embeddingProperty && !(p.Value is System.Collections.IList list && Embedding(list, integers: true)))
                .OrderBy(p => p.Key, StringComparer.Ordinal).ToDictionary(p => p.Key, p => Value(p.Value), StringComparer.Ordinal);
    }
}
