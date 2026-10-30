using System.Text;
using GraphBridge.GraphRag.Ontologies;
using Microsoft.Extensions.AI;

namespace GraphBridge.GraphRag.Tests;

/// <summary>Wave 4 (GR-34): checks and pruning with reasons, grounding, resolution, placeholders, quarantine, the verifier and limits.</summary>
public sealed class GraphRagExtractionTests
{
    // Written for these tests; the characters are from the public-domain novel, the sentences are not.
    private const string First = "Alice met the White Rabbit near the river. “I’m late!” said the Rabbit to Alice. The man waved.";
    private const string Second = "Later, Alice followed the Rabbit into Wonderland. The man laughed.";

    private const string FirstReply = """
        {"entities": [
          {"kind": "Character", "name": "Alice", "aliases": ["Miss Alice", "alice"], "description": "A curious girl.", "mentions": ["Alice", "Alice"]},
          {"kind": "Character", "name": "White Rabbit", "aliases": ["the Rabbit"], "mentions": ["the White Rabbit", "the Rabbit"]},
          {"kind": "Character", "name": "the man", "mentions": ["The man"]},
          {"kind": "Place", "name": "the river", "mentions": ["the river"]},
          {"kind": "Dragon", "name": "Smaug", "mentions": ["Smaug"]},
          {"kind": "Character", "name": " ", "mentions": []},
          {"kind": "Character", "name": "Queen", "mentions": ["the Queen"]}
        ],
        "relationships": [
          {"type": "RELATED_TO", "from": "Alice", "fromKind": "Character", "to": "White Rabbit", "toKind": "Character", "kind": "friend", "description": "Alice meets the Rabbit."},
          {"type": "RELATED_TO", "from": "Alice", "fromKind": "Character", "to": "the Rabbit", "toKind": "Character", "kind": "rival"},
          {"type": "KIN_OF", "from": "Alice", "fromKind": "Character", "to": "the river", "toKind": "Place"},
          {"type": "FLIES_TO", "from": "Alice", "fromKind": "Character", "to": "the river", "toKind": "Place"},
          {"type": "RELATED_TO", "from": "Alice", "fromKind": "Character", "to": "Dinah", "toKind": "Character"},
          {"type": "RELATED_TO", "from": "Alice", "fromKind": "Character", "to": "alice", "toKind": "Character"}
        ],
        "quotes": [
          {"type": "SAYS", "speaker": "the Rabbit", "words": "I'm late!", "addressee": "Alice"},
          {"type": "SAYS", "speaker": "Alice", "words": "Off with his head!"}
        ]}
        """;

    private const string SecondReply = """
        Here is the graph:
        ```json
        {"entities": [
          {"kind": "Character", "name": "alice", "mentions": ["Alice"]},
          {"kind": "Character", "name": "White Rabbit", "mentions": ["the Rabbit"]},
          {"kind": "Place", "name": "Wonderland", "mentions": ["Wonderland"]},
          {"kind": "Character", "name": "The Man", "mentions": ["The man"]}
        ], "relationships": [], "quotes": []}
        ```
        """;

    [Fact]
    public async Task Things_merge_by_exact_name_placeholders_stay_apart_and_every_span_is_the_stored_text()
    {
        var (graph, tally, document) = await ExtractAsync(GraphRagOntologies.Novel, [First, Second], [FirstReply, SecondReply]);
        var characters = Entities<Character>(graph);
        Assert.Equal(["Alice", "The Man", "White Rabbit", "the man"], characters.Select(c => c.Name).Order(StringComparer.Ordinal));
        var alice = characters.Single(c => c.Name == "Alice");
        Assert.Equal(("Miss Alice", "A curious girl.", false), (alice.Aliases, alice.Description, alice.IsPlaceholder));
        Assert.Equal(GraphRagEmbeddingSet.Hash("Character\u001falice"), alice.Id);
        Assert.Equal("the Rabbit", characters.Single(c => c.Name == "White Rabbit").Aliases);
        // Placeholders: one per span, never merged, flagged.
        var placeholders = characters.Where(c => c.IsPlaceholder).ToArray();
        Assert.Equal(2, placeholders.Length);
        Assert.NotEqual(placeholders[0].Id, placeholders[1].Id);
        Assert.Equal(["Wonderland", "the river"], Entities<Place>(graph).Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.All(graph.Entities.Values.SelectMany(e => e.Values), e => Assert.Equal(("run-1", GraphRagExtractor.Version), (e.RunId, e.ExtractorVersion)));

        var mentions = graph.Mentions.Single(m => m.Key.Label == "Character").Value.Values.ToArray();
        Assert.Equal(2, mentions.Count(m => m.EntityId == alice.Id));
        Assert.Equal(3, mentions.Count(m => m.EntityId == characters.Single(c => c.Name == "White Rabbit").Id));
        foreach (var mention in graph.Mentions.Values.SelectMany(m => m.Values))
            Assert.Equal(mention.Quote, Encoding.UTF8.GetString(document, mention.StartByte, mention.EndByte - mention.StartByte));

        var fact = Assert.Single(Assert.Single(graph.Facts).Value.Values);
        Assert.IsType<RelatedTo<Character, Character>>(fact);
        Assert.Equal((alice.Id, characters.Single(c => c.Name == "White Rabbit").Id, "doc-1", "friend", "Alice meets the Rabbit."),
            (fact.FromId, fact.ToId, fact.DocumentIds, ((RelatedTo<Character, Character>)fact).Kind, ((RelatedTo<Character, Character>)fact).Description));

        var says = (Says)Assert.Single(Assert.Single(graph.Quotes).Value.Values);
        Assert.Equal(("I’m late!", "Alice"), (says.Words, says.Addressee));
        Assert.Equal(says.Words, Encoding.UTF8.GetString(document, says.StartByte, says.EndByte - says.StartByte));
        Assert.Equal(characters.Single(c => c.Name == "White Rabbit").Id, says.SpeakerId);

        Assert.Equal(2, tally.Calls);
        Assert.Equal(
            [("Entity", "unknown-kind"), ("Entity", "missing-name"), ("Mention", "quote-not-found"), ("Entity", "ungrounded"),
             ("Relationship", "pattern-not-allowed"), ("Relationship", "unknown-type"), ("Relationship", "endpoint-not-found"),
             ("Relationship", "self-relationship"), ("Quote", "quote-not-found")],
            tally.Pruned.Select(p => (p.Item.ToString(), p.Reason)));
        Assert.All(tally.Pruned, p => Assert.Equal(("doc-1", "chunk-0"), (p.DocumentId, p.ChunkId)));
        Assert.Empty(tally.Quarantined);
    }

    [Fact]
    public async Task Unreadable_replies_are_asked_again_once_then_quarantined_truncated_and_write_nothing()
    {
        var options = new GraphRagExtractionOptions { MaxQuarantinedCharacters = 8 };
        var (graph, tally, _) = await ExtractAsync(GraphRagOntologies.Novel, [First, Second, "A third passage."], ["", "not json at all", """{"entities": {}, "relationships": []}"""], options);
        Assert.Empty(graph.Entities);
        Assert.Equal([("chunk-0", "empty-reply", ""), ("chunk-1", "invalid-json", "not json"), ("chunk-2", "schema-mismatch", "{\"entiti")],
            tally.Quarantined.Select(q => (q.ChunkId, q.Error, q.Reply)));
        Assert.Equal((6, 3, 0), (tally.Calls, tally.Chunks, tally.Repaired));

        // Without the second ask, one call per passage.
        (_, tally, _) = await ExtractAsync(GraphRagOntologies.Novel, [First], ["not json at all"], new GraphRagExtractionOptions { RetryUnreadableReplies = false });
        Assert.Equal((1, "invalid-json"), (tally.Calls, Assert.Single(tally.Quarantined).Error));
    }

    [Fact]
    public async Task The_verifier_and_the_limits_prune_with_reasons()
    {
        var verifier = new RefusePlaces();
        var (graph, tally, _) = await ExtractAsync(GraphRagOntologies.Novel, [First], [FirstReply], verifier: verifier);
        Assert.Equal(["Alice", "White Rabbit", "the man"], Entities<Character>(graph).Select(c => c.Name).Order(StringComparer.Ordinal));
        Assert.Empty(Entities<Place>(graph));
        Assert.Contains(tally.Pruned, p => p is { Item: GraphRagExtractionItem.Entity, Name: "the river", Reason: "refused: no places" });
        Assert.Contains(verifier.Seen, c => c is { Item: GraphRagExtractionItem.Relationship, Label: "RELATED_TO", From: "Alice", To: "White Rabbit" });
        Assert.Contains(verifier.Seen, c => c is { Item: GraphRagExtractionItem.Quote, Label: "SAYS", Name: "the Rabbit", Text: "I’m late!" });

        // Over the limits: the fourth thing and every quote.
        var limits = new GraphRagExtractionOptions { MaxEntitiesPerChunk = 3, MaxQuotesPerChunk = 0, MaxRelationshipsPerChunk = 0 };
        (graph, tally, _) = await ExtractAsync(GraphRagOntologies.Novel, [First], [FirstReply], limits);
        Assert.Empty(Entities<Place>(graph));
        Assert.Empty(graph.Facts);
        Assert.Empty(graph.Quotes);
        Assert.Contains(tally.Pruned, p => p is { Item: GraphRagExtractionItem.Entity, Name: "the river", Reason: "limit" });
        Assert.Contains(tally.Pruned, p => p is { Item: GraphRagExtractionItem.Relationship, Reason: "limit" });
        Assert.Contains(tally.Pruned, p => p is { Item: GraphRagExtractionItem.Quote, Reason: "limit" });
    }

    [Fact]
    public async Task Article_facts_can_end_at_the_document_and_quotes_are_grounded()
    {
        const string text = "Dr. Mira Okafor wrote this report on river data. “The data is open,” Okafor said.";
        const string reply = """
            {"entities": [
              {"kind": "Person", "name": "Mira Okafor", "aliases": ["Okafor", "Dr. Mira Okafor"], "mentions": ["Dr. Mira Okafor", "Okafor"]},
              {"kind": "Topic", "name": "river data", "aliases": ["streams"], "mentions": ["river data"]}
            ],
            "relationships": [
              {"type": "AUTHORED", "from": "Mira Okafor", "fromKind": "Person", "to": "this report", "toKind": "Document"},
              {"type": "ABOUT", "from": "this report", "fromKind": "Document", "to": "river data", "toKind": "Topic"},
              {"type": "ABOUT", "from": "Mira Okafor", "fromKind": "Person", "to": "river data", "toKind": "Topic"}
            ],
            "quotes": [{"type": "QUOTED", "speaker": "Okafor", "words": "The data is open,"}]}
            """;
        var (graph, tally, _) = await ExtractAsync(GraphRagOntologies.Article, [text], [reply]);
        var person = Assert.Single(Entities<Person>(graph));
        Assert.Equal("Dr. Mira Okafor\nOkafor", person.Aliases);
        Assert.Null(Assert.Single(Entities<Topic>(graph)).Aliases);
        var authored = graph.Facts.Single(f => f.Key.Type == "AUTHORED").Value.Values.Single();
        Assert.Equal((person.Id, "doc-1"), (authored.FromId, authored.ToId));
        var about = graph.Facts.Single(f => f.Key.Type == "ABOUT").Value.Values.Single();
        Assert.Equal("doc-1", about.FromId);
        Assert.Contains(tally.Pruned, p => p is { Item: GraphRagExtractionItem.Relationship, Reason: "pattern-not-allowed" });
        var quoted = Assert.Single(graph.Quotes.Single().Value.Values);
        Assert.Equal(("The data is open,", person.Id), (quoted.Words, quoted.SpeakerId));
    }

    [Fact]
    public async Task Transient_chat_failures_are_retried_and_others_fail_the_document()
    {
        var flaky = new Chat(_ => FirstReply) { FailFirst = 1, Failure = new HttpRequestException("busy", null, System.Net.HttpStatusCode.ServiceUnavailable) };
        var (graph, tally, _) = await ExtractAsync(GraphRagOntologies.Novel, [First], chat: flaky);
        Assert.NotEmpty(graph.Entities);
        Assert.Equal(2, tally.Calls);

        var broken = new Chat(_ => FirstReply) { FailFirst = 1, Failure = new InvalidOperationException("bad request") };
        var error = await Assert.ThrowsAsync<GraphRagExtractionException>(() => ExtractAsync(GraphRagOntologies.Novel, [First], chat: broken));
        Assert.Equal("extraction-failed", error.ErrorCode);
    }

    [Fact]
    public async Task The_prompt_carries_the_instructions_schema_text_title_and_passage_and_asks_for_the_json_schema()
    {
        var chat = new Chat(_ => FirstReply);
        await ExtractAsync(GraphRagOntologies.Novel, [First], chat: chat);
        Assert.StartsWith(GraphRagOntologies.Novel.Instructions, chat.System);
        Assert.Contains("- SAYS: spoken by a Character.", chat.System);
        Assert.Equal("Document: Alice\nPassage:\n" + First, chat.User);
        var format = Assert.IsType<ChatResponseFormatJson>(chat.Options!.ResponseFormat);
        Assert.Equal("graphrag_extraction", format.SchemaName);
        Assert.NotNull(format.Schema);
    }

    private static async Task<(GraphRagExtractedGraph Graph, GraphRagExtractionTally Tally, byte[] Document)> ExtractAsync(GraphRagOntology ontology,
        string[] passages, string[]? replies = null, GraphRagExtractionOptions? options = null, IGraphRagExtractionVerifier? verifier = null, Chat? chat = null)
    {
        using var db = new GraphRagOntologyTests.OntologyContext(ontology);
        var schema = GraphRagModelSchema.From(db.Model);
        // The document is the passages joined by a line break, so each chunk's bytes are its own span of the document.
        var document = Encoding.UTF8.GetBytes(string.Join("\n", passages));
        var items = new List<GraphRagEvidenceItem>();
        var start = 0;
        for (var i = 0; i < passages.Length; i++)
        {
            var length = Encoding.UTF8.GetByteCount(passages[i]);
            items.Add(new GraphRagEvidenceItem($"chunk-{i}", "doc-1", passages[i], start, start + length, 0, []));
            start += length + 1;
        }
        chat ??= new Chat(prompt => replies![items.FindIndex(item => prompt.EndsWith(item.Text, StringComparison.Ordinal))]);
        // The chat sees the passage's message: a second ask adds a note after it.
        var extractor = new GraphRagExtractor(schema, chat, options ?? new GraphRagExtractionOptions(), new GraphRagRetryOptions { BaseDelay = TimeSpan.FromMilliseconds(1) },
            verifier);
        var tally = new GraphRagExtractionTally();
        var graph = await extractor.ExtractAsync("doc-1", "Alice", "run-1", items, tally, null, CancellationToken.None);
        return (graph, tally, document);
    }

    private static T[] Entities<T>(GraphRagExtractedGraph graph) where T : GraphRagEntity
        => graph.Entities.Where(e => e.Key.ClrType == typeof(T)).SelectMany(e => e.Value.Values).Cast<T>().ToArray();

    private sealed class RefusePlaces : IGraphRagExtractionVerifier
    {
        public List<GraphRagExtractionCandidate> Seen { get; } = [];

        public ValueTask<string?> VerifyAsync(GraphRagExtractionCandidate candidate, CancellationToken cancellationToken = default)
        {
            lock (Seen) Seen.Add(candidate);
            return ValueTask.FromResult(candidate is { Item: GraphRagExtractionItem.Entity, Label: "Place" } ? "no places" : null);
        }
    }

    internal sealed class Chat(Func<string, string> reply) : IChatClient
    {
        private int _calls;
        public int FailFirst { get; init; }
        public Exception? Failure { get; init; }
        public string System { get; private set; } = "";
        public string User { get; private set; } = "";
        public ChatOptions? Options { get; private set; }
        /// <summary>How many messages each call carried: two for a first ask, three for a second.</summary>
        public List<int> Asked { get; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _calls) <= FailFirst) throw Failure!;
            var list = messages.ToList();
            (System, User, Options) = (list[0].Text, list[1].Text, options);
            lock (Asked) Asked.Add(list.Count);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, reply(list[1].Text))));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
