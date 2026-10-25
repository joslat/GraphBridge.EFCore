using System.Net;
using System.Text;
using System.Text.Json;
using GraphBridge.GraphRag.Ontologies;
using Microsoft.Extensions.AI;

namespace GraphBridge.GraphRag;

/// <summary>Extraction bounds (ADR 0052, section 7). Each has a default and a maximum; anything over a per-chunk limit is pruned with the reason <c>limit</c>.</summary>
public sealed class GraphRagExtractionOptions
{
    /// <summary>1 to 8 chat calls in parallel.</summary>
    public int Lanes { get; set; } = 2;
    /// <summary>1 to 500 things per chunk.</summary>
    public int MaxEntitiesPerChunk { get; set; } = 50;
    /// <summary>0 to 1,000 facts per chunk.</summary>
    public int MaxRelationshipsPerChunk { get; set; } = 100;
    /// <summary>0 to 500 quotes per chunk.</summary>
    public int MaxQuotesPerChunk { get; set; } = 50;
    /// <summary>0 to 100,000: how much of an unreadable reply its quarantine entry keeps.</summary>
    public int MaxQuarantinedCharacters { get; set; } = 2_000;
    /// <summary>Which chat failures are retried, at most twice. Null: an <see cref="HttpRequestException"/> with status 408,
    /// 429 or 5xx, or a timeout.</summary>
    public Func<Exception, bool>? IsTransient { get; set; }

    internal void Validate()
    {
        if (Lanes is < 1 or > 8) throw new ArgumentOutOfRangeException(nameof(Lanes), "Lanes must be 1 to 8.");
        if (MaxEntitiesPerChunk is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(MaxEntitiesPerChunk), "MaxEntitiesPerChunk must be 1 to 500.");
        if (MaxRelationshipsPerChunk is < 0 or > 1_000) throw new ArgumentOutOfRangeException(nameof(MaxRelationshipsPerChunk), "MaxRelationshipsPerChunk must be 0 to 1,000.");
        if (MaxQuotesPerChunk is < 0 or > 500) throw new ArgumentOutOfRangeException(nameof(MaxQuotesPerChunk), "MaxQuotesPerChunk must be 0 to 500.");
        if (MaxQuarantinedCharacters is < 0 or > 100_000)
            throw new ArgumentOutOfRangeException(nameof(MaxQuarantinedCharacters), "MaxQuarantinedCharacters must be 0 to 100,000.");
    }
}

/// <summary>Sees each candidate that passed the checks. Return null to keep it, or a reason to prune it; the receipt records the reason.</summary>
public interface IGraphRagExtractionVerifier
{
    ValueTask<string?> VerifyAsync(GraphRagExtractionCandidate candidate, CancellationToken cancellationToken = default);
}

public enum GraphRagExtractionItem { Entity, Mention, Relationship, Quote }

/// <summary>An extracted item before it is written. <see cref="Label"/> is its kind, relationship type or quote type;
/// <see cref="From"/> and <see cref="To"/> name a fact's ends; <see cref="Text"/> is a quote's located words.</summary>
public sealed record GraphRagExtractionCandidate(string DocumentId, string ChunkId, GraphRagExtractionItem Item, string Label, string? Name,
    string? From, string? To, string? Text);

/// <summary>
/// What extraction did in one ingestion call. Counts are of what was written. <see cref="Pruned"/> and <see cref="Quarantined"/>
/// hold names and reply excerpts from the documents, so treat the receipt like the documents themselves.
/// </summary>
public sealed record GraphRagExtractionSummary(int ChatCalls, long? InputTokens, int Entities, int Mentions, int Facts, int Quotes,
    IReadOnlyList<GraphRagPrunedItem> Pruned, IReadOnlyList<GraphRagQuarantinedReply> Quarantined);

/// <summary>
/// An item the checks dropped. <see cref="Reason"/> is a stable code: <c>unknown-kind</c>, <c>unknown-type</c>, <c>missing-name</c>,
/// <c>quote-not-found</c>, <c>ungrounded</c>, <c>pattern-not-allowed</c>, <c>endpoint-not-found</c>, <c>self-relationship</c>,
/// <c>limit</c>, or <c>refused: </c> followed by the verifier's reason.
/// </summary>
public sealed record GraphRagPrunedItem(string DocumentId, string ChunkId, GraphRagExtractionItem Item, string? Label, string? Name, string Reason);

/// <summary>A reply that wasn't JSON for the schema: <see cref="Error"/> is <c>empty-reply</c>, <c>invalid-json</c> or
/// <c>schema-mismatch</c>, and <see cref="Reply"/> keeps its first characters. Nothing of it is written.</summary>
public sealed record GraphRagQuarantinedReply(string DocumentId, string ChunkId, string Error, string Reply);

internal sealed class GraphRagExtractionException(string errorCode) : Exception(errorCode)
{
    public string ErrorCode { get; } = errorCode;
}

/// <summary>Counts and lists across one ingestion call; documents are extracted one after another.</summary>
internal sealed class GraphRagExtractionTally
{
    private long _tokens;
    private bool _reported;
    public int Calls;
    public int Entities, Mentions, Facts, Quotes;
    public List<GraphRagPrunedItem> Pruned { get; } = [];
    public List<GraphRagQuarantinedReply> Quarantined { get; } = [];
    public long? Tokens => _reported ? Interlocked.Read(ref _tokens) : null;

    public void AddTokens(long tokens)
    {
        Interlocked.Add(ref _tokens, tokens);
        _reported = true;
    }

    public GraphRagExtractionSummary Summary() => new(Calls, Tokens, Entities, Mentions, Facts, Quotes, Pruned.ToArray(), Quarantined.ToArray());
}

/// <summary>One document's resolved graph, keyed so that each thing, mention, fact and quote appears once.</summary>
internal sealed class GraphRagExtractedGraph
{
    public Dictionary<GraphRagOntologyClass, Dictionary<string, GraphRagEntity>> Entities { get; } = [];
    public Dictionary<GraphRagOntologyClass, Dictionary<string, GraphRagMention>> Mentions { get; } = [];
    public Dictionary<GraphRagOntologyRelationship, Dictionary<(string From, string To), GraphRagFact>> Facts { get; } = [];
    public Dictionary<GraphRagOntologyQuote, Dictionary<string, GraphRagQuote>> Quotes { get; } = [];
}

/// <summary>
/// ADR 0052, section 7: asks the chat client for each chunk's things, facts and quotes under the model's JSON schema, keeps
/// only what passes the checks, locates every quoted phrase in the chunk, and resolves things by exact normalised name.
/// </summary>
internal sealed class GraphRagExtractor
{
    internal const string Version = "graphbridge-extractor-1";
    private readonly GraphRagOntology _ontology;
    private readonly IChatClient _chat;
    private readonly GraphRagExtractionOptions _options;
    private readonly IGraphRagExtractionVerifier? _verifier;
    private readonly TimeProvider _clock;
    private readonly ChatOptions _chatOptions;
    private readonly string _instructions;

    public GraphRagExtractor(GraphRagModelSchema schema, IChatClient chat, GraphRagExtractionOptions options, IGraphRagExtractionVerifier? verifier,
        TimeProvider? clock = null)
    {
        (_ontology, _chat, _options, _verifier, _clock) = (schema.Ontology, chat, options, verifier, clock ?? TimeProvider.System);
        using var json = JsonDocument.Parse(schema.ToJsonSchema());
        _chatOptions = new ChatOptions
        {
            ResponseFormat = ChatResponseFormat.ForJsonSchema(json.RootElement.Clone(), "graphrag_extraction", "The things, relationships and quotes in one passage")
        };
        _instructions = _ontology.Instructions + "\n\n" + schema.ToPromptText();
        Fingerprint = GraphRagEmbeddingSet.Hash(schema.Fingerprint + "\u001f" + Version);
    }

    /// <summary>The schema's fingerprint and the extractor version.</summary>
    public string Fingerprint { get; }

    public GraphRagOntologyRuntime Runtime => _ontology.Runtime;

    public async Task<GraphRagExtractedGraph> ExtractAsync(string documentId, string? title, string runId, IReadOnlyList<GraphRagEvidenceItem> chunks,
        GraphRagExtractionTally tally, CancellationToken cancellationToken)
    {
        var replies = new string?[chunks.Count];
        await Parallel.ForEachAsync(Enumerable.Range(0, chunks.Count),
            new ParallelOptions { MaxDegreeOfParallelism = _options.Lanes, CancellationToken = cancellationToken },
            async (index, token) => replies[index] = await AskAsync(title, chunks[index].Text, tally, token).ConfigureAwait(false)).ConfigureAwait(false);

        // Checks and resolution run in chunk order, so the first name, description and payload are the document's first.
        var graph = new GraphRagExtractedGraph();
        var aliases = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        for (var i = 0; i < chunks.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Parse(documentId, chunks[i].ChunkId, replies[i], tally) is { } reply)
                await AddChunkAsync(graph, aliases, documentId, runId, chunks[i], reply, tally, cancellationToken).ConfigureAwait(false);
        }
        foreach (var entity in graph.Entities.Values.SelectMany(e => e.Values))
            entity.Aliases = aliases.TryGetValue(entity.Id, out var set) && set.Count > 0 ? string.Join('\n', set) : null;
        return graph;
    }

    private async Task<string?> AskAsync(string? title, string passage, GraphRagExtractionTally tally, CancellationToken cancellationToken)
    {
        var prompt = (title is null ? "" : "Document: " + title + "\n") + "Passage:\n" + passage;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                Interlocked.Increment(ref tally.Calls);
                var response = await _chat.GetResponseAsync([new ChatMessage(ChatRole.System, _instructions), new ChatMessage(ChatRole.User, prompt)],
                    _chatOptions, cancellationToken).ConfigureAwait(false);
                if (response.Usage?.InputTokenCount is { } tokens) tally.AddTokens(tokens);
                return response.Text;
            }
            catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                if (attempt >= 2 || !(_options.IsTransient ?? IsTransient)(error)) throw new GraphRagExtractionException("extraction-failed");
                await Task.Delay(TimeSpan.FromMilliseconds(200 * (1 << (2 * attempt))), _clock, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static bool IsTransient(Exception error) => error switch
    {
        HttpRequestException { StatusCode: { } status } => status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)status >= 500,
        TimeoutException or TaskCanceledException => true,
        _ => false
    };

    // A reply is the JSON object, tolerating a code fence or a sentence around it; anything else is quarantined.
    private JsonElement? Parse(string documentId, string chunkId, string? reply, GraphRagExtractionTally tally)
    {
        GraphRagQuarantinedReply Quarantine(string error) => new(documentId, chunkId, error,
            reply is null ? "" : reply.Length > _options.MaxQuarantinedCharacters ? reply[.._options.MaxQuarantinedCharacters] : reply);
        if (string.IsNullOrWhiteSpace(reply))
        {
            tally.Quarantined.Add(Quarantine("empty-reply"));
            return null;
        }
        var start = reply.IndexOf('{');
        var end = reply.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            tally.Quarantined.Add(Quarantine("invalid-json"));
            return null;
        }
        try
        {
            using var json = JsonDocument.Parse(reply.AsMemory(start, end - start + 1));
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !IsArray(root, "entities") || !IsArray(root, "relationships")
                || root.TryGetProperty("quotes", out var quotes) && quotes.ValueKind is not (JsonValueKind.Array or JsonValueKind.Null))
            {
                tally.Quarantined.Add(Quarantine("schema-mismatch"));
                return null;
            }
            return root.Clone();
        }
        catch (JsonException)
        {
            tally.Quarantined.Add(Quarantine("invalid-json"));
            return null;
        }

        static bool IsArray(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array;
    }

    private sealed record Kept(GraphRagOntologyClass Class, string Name, string Key, List<GraphRagCitation> Spans, List<string> Aliases,
        string? Description, Dictionary<string, string> Properties)
    {
        public string Id { get; set; } = "";
    }

    private async Task AddChunkAsync(GraphRagExtractedGraph graph, Dictionary<string, SortedSet<string>> aliases, string documentId, string runId,
        GraphRagEvidenceItem chunk, JsonElement reply, GraphRagExtractionTally tally, CancellationToken cancellationToken)
    {
        void Prune(GraphRagExtractionItem item, string? label, string? name, string reason)
            => tally.Pruned.Add(new(documentId, chunk.ChunkId, item, label, name, reason));
        async Task<bool> RefusedAsync(GraphRagExtractionCandidate candidate)
        {
            if (_verifier is null) return false;
            var reason = await _verifier.VerifyAsync(candidate, cancellationToken).ConfigureAwait(false);
            if (reason is null) return false;
            Prune(candidate.Item, candidate.Label, candidate.Name, "refused: " + reason);
            return true;
        }

        // Things: a known kind, a name, at least one phrase found in the chunk.
        var kept = new List<Kept>();
        foreach (var element in Items(reply, "entities"))
        {
            var kind = Text(element, "kind");
            var name = Text(element, "name")?.Trim();
            var type = kind is null ? null : _ontology.FindClass(kind);
            if (type is null)
            {
                Prune(GraphRagExtractionItem.Entity, kind, name, "unknown-kind");
                continue;
            }
            if (string.IsNullOrWhiteSpace(name))
            {
                Prune(GraphRagExtractionItem.Entity, kind, null, "missing-name");
                continue;
            }
            var key = GraphRagOntology.Normalize(name);
            var spans = new List<GraphRagCitation>();
            var phrases = Strings(element, "mentions").ToList();
            if (phrases.Count == 0) phrases.Add(name);
            foreach (var phrase in phrases)
            {
                if (GraphRagCitationVerifier.Locate(chunk, phrase) is { } span) { if (!spans.Any(s => s.StartByte == span.StartByte)) spans.Add(span); }
                else Prune(GraphRagExtractionItem.Mention, kind, phrase, "quote-not-found");
            }
            if (spans.Count == 0)
            {
                Prune(GraphRagExtractionItem.Entity, kind, name, "ungrounded");
                continue;
            }
            if (kept.FirstOrDefault(k => k.Class == type && k.Key == key) is { } same)
            {
                foreach (var span in spans.Where(s => !same.Spans.Any(t => t.StartByte == s.StartByte))) same.Spans.Add(span);
                continue;
            }
            if (kept.Count >= _options.MaxEntitiesPerChunk)
            {
                Prune(GraphRagExtractionItem.Entity, kind, name, "limit");
                continue;
            }
            if (await RefusedAsync(new(documentId, chunk.ChunkId, GraphRagExtractionItem.Entity, type.Kind, name, null, null, spans[0].Quote)).ConfigureAwait(false))
                continue;
            var properties = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var property in type.Properties)
                if (Text(element, GraphRagModelSchema.Camel(property))?.Trim() is { Length: > 0 } value) properties[property] = value;
            var description = Text(element, "description")?.Trim();
            kept.Add(new(type, name, key, spans,
                type.Aliases ? Strings(element, "aliases").Select(a => a.Trim()).Where(a => a.Length > 0 && GraphRagOntology.Normalize(a) != key).ToList() : [],
                string.IsNullOrEmpty(description) ? null : description, properties));
        }

        // Resolution: a placeholder keeps an ID of its own span; any other thing is its kind and normalised name.
        foreach (var thing in kept)
        {
            var placeholder = _ontology.Placeholders.Contains(thing.Key);
            thing.Id = placeholder
                ? GraphRagEmbeddingSet.Hash(string.Join('\u001f', "placeholder", thing.Class.Kind, chunk.ChunkId, thing.Spans.Min(s => s.StartByte)))
                : GraphRagEmbeddingSet.Hash(string.Join('\u001f', thing.Class.Kind, thing.Key));
            var entities = graph.Entities.TryGetValue(thing.Class, out var map) ? map : graph.Entities[thing.Class] = new(StringComparer.Ordinal);
            if (!entities.TryGetValue(thing.Id, out var entity))
            {
                entity = (GraphRagEntity)Activator.CreateInstance(thing.Class.ClrType)!;
                (entity.Id, entity.Name, entity.IsPlaceholder, entity.RunId, entity.ExtractorVersion) = (thing.Id, thing.Name, placeholder, runId, Version);
                entities[thing.Id] = entity;
            }
            entity.Description ??= thing.Description;
            foreach (var (property, value) in thing.Properties)
            {
                var info = thing.Class.ClrType.GetProperty(property)!;
                if (info.GetValue(entity) is null) info.SetValue(entity, value);
            }
            if (thing.Aliases.Count > 0)
            {
                var set = aliases.TryGetValue(thing.Id, out var existing) ? existing : aliases[thing.Id] = new(StringComparer.Ordinal);
                foreach (var alias in thing.Aliases) set.Add(alias);
            }
            var mentions = graph.Mentions.TryGetValue(thing.Class, out var mentionMap) ? mentionMap : graph.Mentions[thing.Class] = new(StringComparer.Ordinal);
            foreach (var span in thing.Spans)
            {
                var mention = (GraphRagMention)Activator.CreateInstance(thing.Class.MentionClrType)!;
                mention.Id = GraphRagEmbeddingSet.Hash(string.Join('\u001f', chunk.ChunkId, span.StartByte, thing.Id));
                (mention.EntityId, mention.ChunkId, mention.StartByte, mention.EndByte, mention.Quote) = (thing.Id, chunk.ChunkId, span.StartByte, span.EndByte, span.Quote);
                (mention.RunId, mention.ExtractorVersion) = (runId, Version);
                mentions.TryAdd(mention.Id, mention);
            }
        }

        // A fact's ends are things kept from this chunk, found by name, then by alias, then by one of their mention phrases
        // in this chunk; or the document itself.
        Kept? Find(string? label, string? name)
        {
            if (label is null || string.IsNullOrWhiteSpace(name)) return null;
            var key = GraphRagOntology.Normalize(name);
            return kept.FirstOrDefault(k => k.Class.Kind == label && k.Key == key)
                ?? kept.FirstOrDefault(k => k.Class.Kind == label && k.Aliases.Any(a => GraphRagOntology.Normalize(a) == key))
                ?? kept.FirstOrDefault(k => k.Class.Kind == label && k.Spans.Any(s => GraphRagOntology.Normalize(s.Quote) == key));
        }
        var facts = 0;
        foreach (var element in Items(reply, "relationships"))
        {
            var type = Text(element, "type");
            var (fromKind, toKind) = (Text(element, "fromKind"), Text(element, "toKind"));
            var (from, to) = (Text(element, "from")?.Trim(), Text(element, "to")?.Trim());
            var label = $"{from} -[{type}]-> {to}";
            if (type is null || _ontology.Relationships.All(r => r.Type != type))
            {
                Prune(GraphRagExtractionItem.Relationship, type, label, "unknown-type");
                continue;
            }
            var pattern = _ontology.Relationships.FirstOrDefault(r => r.Type == type && r.FromKind == fromKind && r.ToKind == toKind);
            if (pattern is null)
            {
                Prune(GraphRagExtractionItem.Relationship, type, label, "pattern-not-allowed");
                continue;
            }
            var fromId = pattern.FromKind == GraphRagOntologyBuilder.DocumentKind ? documentId : Find(fromKind, from)?.Id;
            var toId = pattern.ToKind == GraphRagOntologyBuilder.DocumentKind ? documentId : Find(toKind, to)?.Id;
            if (fromId is null || toId is null)
            {
                Prune(GraphRagExtractionItem.Relationship, type, label, "endpoint-not-found");
                continue;
            }
            if (fromId == toId)
            {
                Prune(GraphRagExtractionItem.Relationship, type, label, "self-relationship");
                continue;
            }
            if (facts >= _options.MaxRelationshipsPerChunk)
            {
                Prune(GraphRagExtractionItem.Relationship, type, label, "limit");
                continue;
            }
            if (await RefusedAsync(new(documentId, chunk.ChunkId, GraphRagExtractionItem.Relationship, type, null, from, to, null)).ConfigureAwait(false))
                continue;
            facts++;
            var map = graph.Facts.TryGetValue(pattern, out var existing) ? existing : graph.Facts[pattern] = [];
            if (map.ContainsKey((fromId, toId))) continue;
            var fact = (GraphRagFact)Activator.CreateInstance(pattern.ClrType)!;
            (fact.FromId, fact.ToId, fact.DocumentIds, fact.RunId, fact.ExtractorVersion) = (fromId, toId, documentId, runId, Version);
            foreach (var property in pattern.Properties)
                if (Text(element, GraphRagModelSchema.Camel(property))?.Trim() is { Length: > 0 } value) pattern.ClrType.GetProperty(property)!.SetValue(fact, value);
            map[(fromId, toId)] = fact;
        }

        // Quotes: a known type, a kept speaker of its class, and words found in the chunk.
        var quotes = 0;
        foreach (var element in Items(reply, "quotes"))
        {
            var type = Text(element, "type");
            var speaker = Text(element, "speaker")?.Trim();
            var words = Text(element, "words");
            var definition = type is null ? null : _ontology.Quotes.FirstOrDefault(q => q.Type == type);
            if (definition is null)
            {
                Prune(GraphRagExtractionItem.Quote, type, speaker, "unknown-type");
                continue;
            }
            var who = Find(definition.SpeakerKind, speaker);
            if (who is null)
            {
                Prune(GraphRagExtractionItem.Quote, type, speaker, "endpoint-not-found");
                continue;
            }
            var span = words is null ? null : GraphRagCitationVerifier.Locate(chunk, words);
            if (span is null)
            {
                Prune(GraphRagExtractionItem.Quote, type, speaker, "quote-not-found");
                continue;
            }
            if (quotes >= _options.MaxQuotesPerChunk)
            {
                Prune(GraphRagExtractionItem.Quote, type, speaker, "limit");
                continue;
            }
            if (await RefusedAsync(new(documentId, chunk.ChunkId, GraphRagExtractionItem.Quote, definition.Type, speaker, null, null, span.Quote)).ConfigureAwait(false))
                continue;
            quotes++;
            var map = graph.Quotes.TryGetValue(definition, out var existing) ? existing : graph.Quotes[definition] = new(StringComparer.Ordinal);
            var quote = (GraphRagQuote)Activator.CreateInstance(definition.ClrType)!;
            quote.Id = GraphRagEmbeddingSet.Hash(string.Join('\u001f', chunk.ChunkId, span.StartByte, who.Id));
            (quote.SpeakerId, quote.ChunkId, quote.StartByte, quote.EndByte, quote.Words) = (who.Id, chunk.ChunkId, span.StartByte, span.EndByte, span.Quote);
            (quote.RunId, quote.ExtractorVersion) = (runId, Version);
            if (definition.Addressee && Text(element, "addressee")?.Trim() is { Length: > 0 } addressee)
                definition.ClrType.GetProperty("Addressee")!.SetValue(quote, addressee);
            map.TryAdd(quote.Id, quote);
        }
    }

    private static IEnumerable<JsonElement> Items(JsonElement reply, string name)
        => reply.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object)
            : [];

    private static string? Text(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static IEnumerable<string> Strings(JsonElement element, string name)
        => element.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).Where(s => !string.IsNullOrWhiteSpace(s))
            : [];
}
