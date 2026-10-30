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
    /// <summary>Asks for strict schema adherence, through the <c>strict</c> entry of <see cref="ChatOptions.AdditionalProperties"/>
    /// that Microsoft.Extensions.AI's OpenAI client reads. Clients that don't know it ignore it. On by default.</summary>
    public bool StrictSchema { get; set; } = true;
    /// <summary>Asks once more, with what was wrong, when a reply isn't JSON for the schema; then it's quarantined. On by default.</summary>
    public bool RetryUnreadableReplies { get; set; } = true;

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
/// What extraction did in one ingestion call. Counts are of what was written. <see cref="Chunks"/> is how many passages were
/// sent, <see cref="Repaired"/> how many replies were unreadable at first and usable when asked again, and the token counts
/// are null when the client reports no usage; a reasoning model's output tokens include its <see cref="ReasoningTokens"/>.
/// To change how much a reasoning model thinks, register the extraction client with its own
/// <see cref="ChatOptions.Reasoning"/>, for example through <c>ConfigureOptions</c>.
/// <see cref="Pruned"/> and <see cref="Quarantined"/> hold names and reply excerpts from the documents, so treat the receipt
/// like the documents themselves.
/// </summary>
public sealed record GraphRagExtractionSummary(int ChatCalls, long? InputTokens, int Entities, int Mentions, int Facts, int Quotes,
    IReadOnlyList<GraphRagPrunedItem> Pruned, IReadOnlyList<GraphRagQuarantinedReply> Quarantined, int Chunks = 0, int Repaired = 0,
    long? OutputTokens = null, long? ReasoningTokens = null);

/// <summary>
/// An item the checks dropped. <see cref="Reason"/> is a stable code: <c>unknown-kind</c>, <c>unknown-type</c>, <c>missing-name</c>,
/// <c>quote-not-found</c>, <c>ungrounded</c>, <c>pattern-not-allowed</c>, <c>endpoint-not-found</c>, <c>self-relationship</c>,
/// <c>limit</c>, or <c>refused: </c> followed by the verifier's reason.
/// </summary>
public sealed record GraphRagPrunedItem(string DocumentId, string ChunkId, GraphRagExtractionItem Item, string? Label, string? Name, string Reason);

/// <summary>A reply that wasn't JSON for the schema, asked again when <see cref="GraphRagExtractionOptions.RetryUnreadableReplies"/>
/// is on: <see cref="Error"/> is <c>empty-reply</c>, <c>invalid-json</c>, <c>truncated</c> (cut off at the output limit) or
/// <c>schema-mismatch</c>, and <see cref="Reply"/> keeps the last reply's first characters. Nothing of it is written.</summary>
public sealed record GraphRagQuarantinedReply(string DocumentId, string ChunkId, string Error, string Reply);

internal sealed class GraphRagExtractionException(string errorCode, Exception? inner = null) : Exception(errorCode, inner)
{
    public string ErrorCode { get; } = errorCode;
}

/// <summary>Counts and lists across one ingestion call; documents are extracted one after another.</summary>
internal sealed class GraphRagExtractionTally
{
    private long _tokens, _outputTokens, _reasoningTokens;
    private bool _reported, _outputReported, _reasoningReported;
    public int Calls, Chunks, Repaired;
    public int Entities, Mentions, Facts, Quotes;
    public List<GraphRagPrunedItem> Pruned { get; } = [];
    public List<GraphRagQuarantinedReply> Quarantined { get; } = [];
    public long? Tokens => _reported ? Interlocked.Read(ref _tokens) : null;
    public long? OutputTokens => _outputReported ? Interlocked.Read(ref _outputTokens) : null;
    public long? ReasoningTokens => _reasoningReported ? Interlocked.Read(ref _reasoningTokens) : null;

    public void AddUsage(UsageDetails? usage)
    {
        if (usage?.InputTokenCount is { } input)
        {
            Interlocked.Add(ref _tokens, input);
            _reported = true;
        }
        if (usage?.OutputTokenCount is { } output)
        {
            Interlocked.Add(ref _outputTokens, output);
            _outputReported = true;
        }
        if (usage?.ReasoningTokenCount is { } reasoning)
        {
            Interlocked.Add(ref _reasoningTokens, reasoning);
            _reasoningReported = true;
        }
    }

    public GraphRagExtractionSummary Summary() => new(Calls, Tokens, Entities, Mentions, Facts, Quotes, Pruned.ToArray(), Quarantined.ToArray(),
        Chunks, Repaired, OutputTokens, ReasoningTokens);
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
    private readonly GraphRagRetryOptions _retry;
    private readonly IGraphRagExtractionVerifier? _verifier;
    private readonly TimeProvider _clock;
    private readonly ChatOptions _chatOptions;
    private readonly string _instructions;

    public GraphRagExtractor(GraphRagModelSchema schema, IChatClient chat, GraphRagExtractionOptions options, GraphRagRetryOptions? retry = null,
        IGraphRagExtractionVerifier? verifier = null, TimeProvider? clock = null)
    {
        (_ontology, _chat, _options, _retry, _verifier, _clock) = (schema.Ontology, chat, options, retry ?? new(), verifier, clock ?? TimeProvider.System);
        using var json = JsonDocument.Parse(schema.ToJsonSchema());
        _chatOptions = new ChatOptions
        {
            ResponseFormat = ChatResponseFormat.ForJsonSchema(json.RootElement.Clone(), "graphrag_extraction", "The things, relationships and quotes in one passage")
        };
        if (options.StrictSchema) _chatOptions.AdditionalProperties = new() { ["strict"] = true };
        _instructions = InstructionsFor(schema);
        Fingerprint = FingerprintFor(schema);
    }

    /// <summary>The system message: the ontology's instructions and the schema as text.</summary>
    internal static string InstructionsFor(GraphRagModelSchema schema) => schema.Ontology.Instructions + "\n\n" + schema.ToPromptText();

    internal static string FingerprintFor(GraphRagModelSchema schema) => GraphRagEmbeddingSet.Hash(schema.Fingerprint + "\u001f" + Version);

    /// <summary>The user message for one passage.</summary>
    internal static string PromptFor(string? title, string passage) => (title is null ? "" : "Document: " + title + "\n") + "Passage:\n" + passage;

    /// <summary>The schema's fingerprint and the extractor version.</summary>
    public string Fingerprint { get; }

    public GraphRagOntologyRuntime Runtime => _ontology.Runtime;

    public async Task<GraphRagExtractedGraph> ExtractAsync(string documentId, string? title, string runId, IReadOnlyList<GraphRagEvidenceItem> chunks,
        GraphRagExtractionTally tally, GraphRagRetryTally? retries, CancellationToken cancellationToken)
    {
        retries ??= new();
        var replies = new Reply[chunks.Count];
        Interlocked.Add(ref tally.Chunks, chunks.Count);
        await Parallel.ForEachAsync(Enumerable.Range(0, chunks.Count),
            new ParallelOptions { MaxDegreeOfParallelism = _options.Lanes, CancellationToken = cancellationToken },
            async (index, token) => replies[index] = await ReadAsync(title, chunks[index].Text, tally, retries, token).ConfigureAwait(false)).ConfigureAwait(false);

        // Checks and resolution run in chunk order, so the first name, description and payload are the document's first.
        var graph = new GraphRagExtractedGraph();
        var aliases = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        for (var i = 0; i < chunks.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (replies[i].Json is { } reply)
                await AddChunkAsync(graph, aliases, documentId, runId, chunks[i], reply, tally, cancellationToken).ConfigureAwait(false);
            else
                tally.Quarantined.Add(new(documentId, chunks[i].ChunkId, replies[i].Error!,
                    replies[i].Text.Length > _options.MaxQuarantinedCharacters ? replies[i].Text[.._options.MaxQuarantinedCharacters] : replies[i].Text));
        }
        foreach (var entity in graph.Entities.Values.SelectMany(e => e.Values))
            entity.Aliases = aliases.TryGetValue(entity.Id, out var set) && set.Count > 0 ? string.Join('\n', set) : null;
        return graph;
    }

    /// <summary>A chunk's usable reply, or the error and text of the last unreadable one.</summary>
    private readonly record struct Reply(JsonElement? Json, string? Error, string Text);

    // Asks for one passage's graph. An unreadable reply is asked again once, with what was wrong; the last error is quarantined.
    private async Task<Reply> ReadAsync(string? title, string passage, GraphRagExtractionTally tally, GraphRagRetryTally retries,
        CancellationToken cancellationToken)
    {
        List<ChatMessage> messages = [new(ChatRole.System, _instructions), new(ChatRole.User, PromptFor(title, passage))];
        for (var attempt = 0; ; attempt++)
        {
            var response = await AskAsync(messages, tally, retries, cancellationToken).ConfigureAwait(false);
            var reply = Parse(response.Text, response.FinishReason);
            if (reply.Json is not null)
            {
                if (attempt > 0) Interlocked.Increment(ref tally.Repaired);
                return reply;
            }
            if (attempt > 0 || !_options.RetryUnreadableReplies) return reply;
            messages.Add(new(ChatRole.User, "Your reply to this passage couldn't be used: " + Problem(reply.Error!)
                + ". Reply again with only the JSON object that the schema describes."));
        }

        static string Problem(string error) => error switch
        {
            "empty-reply" => "it was empty",
            "truncated" => "it was cut off at the output limit, so leave out minor things and relationships",
            "schema-mismatch" => "it didn't have the \"entities\" and \"relationships\" arrays",
            _ => "it wasn't valid JSON"
        };
    }

    private Task<ChatResponse> AskAsync(List<ChatMessage> messages, GraphRagExtractionTally tally, GraphRagRetryTally retries,
        CancellationToken cancellationToken)
        => GraphRagRetry.RunAsync(async token =>
        {
            Interlocked.Increment(ref tally.Calls);
            var response = await _chat.GetResponseAsync(messages, _chatOptions, token).ConfigureAwait(false);
            tally.AddUsage(response.Usage);
            return response;
        }, _retry, retries, _clock, "extraction-failed", (code, error) => new GraphRagExtractionException(code, error), cancellationToken);

    // A reply is the JSON object, tolerating a code fence or a sentence around it; anything else is unreadable.
    private static Reply Parse(string? reply, ChatFinishReason? finish)
    {
        reply ??= "";
        var invalid = finish == ChatFinishReason.Length ? "truncated" : "invalid-json";
        if (string.IsNullOrWhiteSpace(reply)) return new(null, finish == ChatFinishReason.Length ? "truncated" : "empty-reply", reply);
        var start = reply.IndexOf('{');
        var end = reply.LastIndexOf('}');
        if (start < 0 || end <= start) return new(null, invalid, reply);
        try
        {
            using var json = JsonDocument.Parse(reply.AsMemory(start, end - start + 1));
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !IsArray(root, "entities") || !IsArray(root, "relationships")
                || root.TryGetProperty("quotes", out var quotes) && quotes.ValueKind is not (JsonValueKind.Array or JsonValueKind.Null))
                return new(null, "schema-mismatch", reply);
            return new(root.Clone(), null, reply);
        }
        catch (JsonException)
        {
            return new(null, invalid, reply);
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
