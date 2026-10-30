using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using GraphBridge.EntityFrameworkCore.Migrations;
using GraphBridge.GraphRag;
using GraphBridge.GraphRag.Ontologies;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Neo4j.Driver;

namespace GraphBridge.IntegrationTests;

// GraphRAG wave 4 acceptance (ADR 0052, section 7) on the disposable fixture, with recorded replies instead of a model.
// Each test prefixes the ontology's labels and owns its layout labels and schema prefix.
[Collection(Neo4jIntegrationCollection.Name)]
public sealed class GraphRagExtractionTests(DisposableNeo4jFixture fixture) : IAsyncLifetime
{
    // Written for these tests; the characters are from the public-domain novel, the sentences are not.
    internal const string First = "Alice met the White Rabbit near the river. “I’m late!” said the Rabbit to Alice. The man waved.";
    internal const string Second = "Later, Alice followed the Rabbit into Wonderland. The man laughed.";

    internal const string FirstReply = """
        {"entities": [
          {"kind": "Character", "name": "Alice", "aliases": ["Miss Alice"], "description": "A curious girl.", "mentions": ["Alice"]},
          {"kind": "Character", "name": "White Rabbit", "aliases": ["the Rabbit"], "mentions": ["the White Rabbit", "the Rabbit"]},
          {"kind": "Character", "name": "the man", "mentions": ["The man"]},
          {"kind": "Place", "name": "the river", "mentions": ["the river"]}
        ],
        "relationships": [
          {"type": "RELATED_TO", "from": "Alice", "fromKind": "Character", "to": "White Rabbit", "toKind": "Character", "kind": "friend"}
        ],
        "quotes": [{"type": "SAYS", "speaker": "the Rabbit", "words": "I'm late!", "addressee": "Alice"}]}
        """;

    internal const string SecondReply = """
        {"entities": [
          {"kind": "Character", "name": "Alice", "aliases": ["Alice Liddell"], "mentions": ["Alice"]},
          {"kind": "Character", "name": "White Rabbit", "mentions": ["the Rabbit"]},
          {"kind": "Place", "name": "Wonderland", "mentions": ["Wonderland"]},
          {"kind": "Character", "name": "The Man", "mentions": ["The man"]}
        ],
        "relationships": [
          {"type": "RELATED_TO", "from": "Alice", "fromKind": "Character", "to": "the Rabbit", "toKind": "Character", "kind": "rival"}
        ],
        "quotes": []}
        """;

    private readonly string _suffix = Guid.NewGuid().ToString("N")[..12];
    private readonly GraphRagEmbeddingSet _set = new("fake-hash-8", 8);
    private GraphRagOntology Ontology => _ontology ??= _neo4jShape
        ? GraphRagOntologies.Novel.WithLabelPrefix("X" + _suffix).WithNeo4jGraphRagEntities()
        : GraphRagOntologies.Novel.WithLabelPrefix("X" + _suffix);
    private GraphRagOntology? _ontology;
    private bool _neo4jShape;
    private GraphRagLayout Layout => GraphRagLayout.Default with
    {
        DocumentLabel = "XDocument_" + _suffix, SectionLabel = "XSection_" + _suffix, ChunkLabel = "XChunk_" + _suffix,
        RunLabel = "XRun_" + _suffix, LeaseLabel = "XLease_" + _suffix, SchemaPrefix = "gbx_" + _suffix
    };

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (!Enabled) return;
        var labels = string.Join(" OR ", new[] { Layout.DocumentLabel, Layout.SectionLabel, Layout.ChunkLabel, Layout.RunLabel, Layout.LeaseLabel }
            .Concat(Ontology.Classes.Select(c => c.Label)).Select(l => "n:" + l));
        await Execute($"MATCH (n)-[r]-() WHERE {labels} DELETE r");
        await Execute($"MATCH (n) WHERE {labels} DELETE n");
        foreach (var definition in GraphRagSchema.Definitions(Layout, _set, Ontology))
            await Execute($"DROP {(definition.IsIndex ? "INDEX" : "CONSTRAINT")} {definition.Name} IF EXISTS");
    }

    private static bool Enabled => Environment.GetEnvironmentVariable("GRAPHBRIDGE_INTEGRATION") == "1";

    [Neo4jIntegrationFact]
    public async Task Two_runs_at_once_that_state_one_fact_both_end_up_on_it()
    {
        await ProvisionAsync();
        await using var provider = Services(new Replies());
        var ingestion = provider.GetRequiredService<IGraphRagIngestion>();
        var sources = Sources().ToArray();
        var receipts = await Task.WhenAll(ingestion.IngestAsync(sources.Take(1)), ingestion.IngestAsync(sources.Skip(1)));
        Assert.All(receipts.SelectMany(r => r.Documents), d => Assert.Equal(GraphRagDocumentStatus.Ingested, d.Status));
        await using var db = Context();
        var fact = await db.Set<RelatedTo<Character, Character>>().SingleAsync();
        Assert.Equal(string.Join('\n', receipts.Select(r => r.Documents[0].DocumentId).Order(StringComparer.Ordinal)), fact.DocumentIds);
        Assert.Single(await db.Set<Character>().Where(c => c.Name == "Alice").ToListAsync());
    }

    [Neo4jIntegrationFact]
    public async Task Extraction_writes_typed_things_mentions_facts_and_quotes_that_LINQ_reads_and_a_rerun_writes_nothing_new()
    {
        await ProvisionAsync();
        await using var provider = Services(new Replies());
        // The listener is process-wide, so only this test's trace counts.
        using var test = new Activity("extraction-test").Start();
        var extracts = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == GraphRagTracing.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => { if (activity.TraceId == test.TraceId && activity.OperationName == GraphRagTracing.ExtractActivity) extracts.Enqueue(activity); }
        };
        ActivitySource.AddActivityListener(listener);
        var receipt = await provider.GetRequiredService<IGraphRagIngestion>().IngestAsync(Sources());
        Assert.All(receipt.Documents, d => Assert.Equal(GraphRagDocumentStatus.Ingested, d.Status));
        // Counts are per document: both documents write Alice, the White Rabbit and the fact.
        var extraction = receipt.Extraction!;
        Assert.Equal((2, 8, 9, 2, 1), (extraction.ChatCalls, extraction.Entities, extraction.Mentions, extraction.Facts, extraction.Quotes));
        // Each document's extraction is traced with counts, never text.
        Assert.Equal(2, extracts.Count);
        Assert.All(extracts, a => Assert.Equal((object)1, a.GetTagItem("graphrag.chat_calls")));
        Assert.DoesNotContain(extracts.SelectMany(a => a.TagObjects), t => t.Value is string text && (text.Contains("Alice", StringComparison.Ordinal) || text.Contains("Rabbit", StringComparison.Ordinal)));
        var (first, second) = (receipt.Documents[0].DocumentId, receipt.Documents[1].DocumentId);

        await using var db = Context();
        Assert.Equal(["Alice", "White Rabbit"], await db.Set<Character>().Where(c => !c.IsPlaceholder).OrderBy(c => c.Name).Select(c => c.Name).ToListAsync());
        Assert.Equal(2, await db.Set<Character>().CountAsync(c => c.IsPlaceholder));
        Assert.Equal(["Wonderland", "the river"], (await db.Set<Place>().Select(p => p.Name).ToListAsync()).Order(StringComparer.Ordinal));
        var alice = await db.Set<Character>().SingleAsync(c => c.Name == "Alice");
        var rabbit = await db.Set<Character>().SingleAsync(c => c.Name == "White Rabbit");
        // Aliases unioned across documents; the first document's description kept.
        Assert.Equal(("Alice Liddell\nMiss Alice", "A curious girl."), (alice.Aliases, alice.Description));

        var fact = await db.Set<RelatedTo<Character, Character>>().SingleAsync();
        Assert.Equal((alice.Id, rabbit.Id, "friend"), (fact.FromId, fact.ToId, fact.Kind));
        Assert.Equal(string.Join('\n', new[] { first, second }.Order(StringComparer.Ordinal)), fact.DocumentIds);

        var mentions = await db.Set<GraphRagMention<Character>>().Where(m => m.EntityId == alice.Id).ToListAsync();
        Assert.Equal(2, mentions.Count);
        var source = Encoding.UTF8.GetBytes(First);
        foreach (var mention in await db.Set<GraphRagMention<Character>>().Where(m => m.EntityId == rabbit.Id).ToListAsync())
        {
            var chunk = await db.Set<GraphRagChunk>().SingleAsync(c => c.Id == mention.ChunkId);
            if (chunk.DocumentId == first) Assert.Equal(mention.Quote, Encoding.UTF8.GetString(source, mention.StartByte, mention.EndByte - mention.StartByte));
        }
        var says = await db.Set<Says>().SingleAsync();
        Assert.Equal(("I’m late!", "Alice", rabbit.Id), (says.Words, says.Addressee, says.SpeakerId));
        Assert.Equal(says.Words, Encoding.UTF8.GetString(source, says.StartByte, says.EndByte - says.StartByte));

        var before = await Count(CountAll());
        var again = await provider.GetRequiredService<IGraphRagIngestion>().IngestAsync(Sources());
        Assert.All(again.Documents, d => Assert.Equal(GraphRagDocumentStatus.Unchanged, d.Status));
        Assert.Equal((0, 0), (again.Extraction!.ChatCalls, again.Extraction.Entities));
        Assert.Equal(before, await Count(CountAll()));

        var health = await provider.GetRequiredService<IGraphRagMaintenance>().CheckAsync();
        Assert.DoesNotContain("orphan-entities", health.Findings);
        Assert.DoesNotContain("stale-facts", health.Findings);
        Assert.DoesNotContain("schema-missing", health.Findings);
    }

    [Neo4jIntegrationFact]
    public async Task Removing_a_document_drops_its_mentions_and_quotes_takes_it_off_facts_and_leaves_orphans_to_clean_up()
    {
        await ProvisionAsync();
        await using var provider = Services(new Replies());
        var receipt = await provider.GetRequiredService<IGraphRagIngestion>().IngestAsync(Sources());
        var (first, second) = (receipt.Documents[0].DocumentId, receipt.Documents[1].DocumentId);
        var maintenance = provider.GetRequiredService<IGraphRagMaintenance>();

        var removed = await maintenance.RemoveSourceAsync(first);
        Assert.Equal(1, removed.Documents);
        await using (var db = Context())
        {
            Assert.Equal(second, (await db.Set<RelatedTo<Character, Character>>().SingleAsync()).DocumentIds);
            Assert.Empty(await db.Set<Says>().ToListAsync());
            var alice = await db.Set<Character>().SingleAsync(c => c.Name == "Alice");
            Assert.Single(await db.Set<GraphRagMention<Character>>().Where(m => m.EntityId == alice.Id).ToListAsync());
        }
        // The river and the first document's placeholder lost their only mentions.
        var health = await maintenance.CheckAsync();
        Assert.Equal(2, health.OrphanEntities);
        Assert.Contains("orphan-entities", health.Findings);
        var orphans = await maintenance.RemoveOrphansAsync();
        Assert.Equal(2, orphans.Entities);
        Assert.DoesNotContain("orphan-entities", (await maintenance.CheckAsync()).Findings);

        // A fact that lists a document no longer there, as a document deleted without RemoveSourceAsync would leave it.
        var stale = second + "\n" + "gone";
        await Execute("MATCH ()-[f]->() WHERE f.DocumentIds = $second SET f.DocumentIds = $stale", new { second, stale });
        health = await maintenance.CheckAsync();
        Assert.True(health.StaleFacts > 0);
        Assert.Contains("stale-facts", health.Findings);
        await Execute("MATCH ()-[f]->() WHERE f.DocumentIds = $stale SET f.DocumentIds = $second", new { second, stale });

        await maintenance.RemoveSourceAsync(second);
        Assert.Equal(4, (await maintenance.RemoveOrphansAsync()).Entities);
        await using (var db = Context())
        {
            Assert.Empty(await db.Set<RelatedTo<Character, Character>>().ToListAsync());
            Assert.Equal(0, await db.Set<Character>().CountAsync());
            Assert.Equal(0, await db.Set<Place>().CountAsync());
        }
        Assert.Equal(0, await Count(CountAll()));
    }

    [Neo4jIntegrationFact]
    public async Task Removing_documents_in_one_batch_keeps_a_fact_another_document_states_and_drops_one_only_they_state()
    {
        // GR-66: one transaction removes several documents; facts are judged against all of them at once.
        await ProvisionAsync();
        await using var provider = Services(new Replies());
        var third = GraphRagSources.Text("third", Second + " The Rabbit ran on.");
        var receipt = await provider.GetRequiredService<IGraphRagIngestion>().IngestAsync([.. Sources(), third]);
        Assert.All(receipt.Documents, d => Assert.Equal(GraphRagDocumentStatus.Ingested, d.Status));
        var ids = receipt.Documents.Select(d => d.DocumentId).ToArray();
        var maintenance = provider.GetRequiredService<IGraphRagMaintenance>();
        await using (var db = Context())
            Assert.Equal(string.Join('\n', ids.Order(StringComparer.Ordinal)), (await db.Set<RelatedTo<Character, Character>>().SingleAsync()).DocumentIds);

        var removed = await maintenance.ResetAsync(new GraphRagResetScope(DocumentIds: [ids[0], ids[1]]));
        Assert.Equal(2, removed.Documents);
        await using (var db = Context())
        {
            Assert.Equal(ids[2], (await db.Set<RelatedTo<Character, Character>>().SingleAsync()).DocumentIds);
            Assert.Equal([ids[2]], await db.Set<GraphRagDocument>().Select(d => d.Id).ToListAsync());
            Assert.All(await db.Set<GraphRagChunk>().ToListAsync(), c => Assert.Equal(ids[2], c.DocumentId));
        }

        // Back again: now the fact lists the first and the third, and removing both in one batch drops it.
        await provider.GetRequiredService<IGraphRagIngestion>().IngestAsync(Sources().Take(1));
        removed = await maintenance.ResetAsync(new GraphRagResetScope(DocumentIds: [ids[0], ids[2]]));
        Assert.Equal(2, removed.Documents);
        await using (var db = Context())
        {
            Assert.Empty(await db.Set<RelatedTo<Character, Character>>().ToListAsync());
            Assert.Empty(await db.Set<GraphRagDocument>().ToListAsync());
            Assert.Empty(await db.Set<Says>().ToListAsync());
        }
        await maintenance.RemoveOrphansAsync();
        Assert.Equal(0, await Count(CountAll()));
    }

    [Neo4jIntegrationFact]
    public async Task A_removal_batch_listing_too_many_facts_is_halved_and_one_document_over_the_bound_is_refused()
    {
        // GR-69: the bound on the facts one removal reads is lowered here, so a few facts reach it.
        await ProvisionAsync();
        await using var provider = Services(new Replies());
        var receipt = await provider.GetRequiredService<IGraphRagIngestion>().IngestAsync(Sources());
        var ids = receipt.Documents.Select(d => d.DocumentId).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(2, ids.Length);
        await using (var seed = Context())
        {
            // One more fact for each document alone: each then lists two facts, and the two together three.
            for (var n = 0; n < ids.Length; n++)
            {
                var (from, to) = (new Character { Id = $"gr69-from-{n}", Name = $"From {n}", RunId = "gr69", ExtractorVersion = "test" },
                    new Character { Id = $"gr69-to-{n}", Name = $"To {n}", RunId = "gr69", ExtractorVersion = "test" });
                seed.AddRange(from, to);
                seed.Add(new RelatedTo<Character, Character> { FromId = from.Id, ToId = to.Id, DocumentIds = ids[n], RunId = "gr69", ExtractorVersion = "test" });
            }
            await seed.SaveChangesAsync();
        }
        var maintenance = provider.GetRequiredService<IGraphRagMaintenance>();
        var bound = GraphRagOntologyRuntime.MaxListedFacts;
        try
        {
            GraphRagOntologyRuntime.MaxListedFacts = 1;
            var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => maintenance.ResetAsync(new GraphRagResetScope(DocumentIds: ids)));
            Assert.Contains($"facts list document '{ids[0]}'", refused.Message, StringComparison.Ordinal);
            Assert.Contains("The 0 documents removed before it in this call stay removed.", refused.Message, StringComparison.Ordinal);
            await using (var db = Context())
                Assert.Equal(2, await db.Set<GraphRagDocument>().CountAsync());

            GraphRagOntologyRuntime.MaxListedFacts = 2;
            Assert.Equal(2, (await maintenance.ResetAsync(new GraphRagResetScope(DocumentIds: ids))).Documents);
            await using (var db = Context())
            {
                Assert.Empty(await db.Set<GraphRagDocument>().ToListAsync());
                Assert.Empty(await db.Set<RelatedTo<Character, Character>>().ToListAsync());
            }
        }
        finally { GraphRagOntologyRuntime.MaxListedFacts = bound; }
    }

    [Neo4jIntegrationFact]
    public async Task In_neo4j_graphrags_entity_shape_things_carry_its_labels_and_FROM_CHUNK_and_remove_cleanly()
    {
        // GR-16 (ADR 0052, section 20): the same extraction, stored as neo4j-graphrag stores entities.
        _neo4jShape = true;
        await ProvisionAsync();
        await using var provider = Services(new Replies());
        var receipt = await provider.GetRequiredService<IGraphRagIngestion>().IngestAsync(Sources());
        Assert.All(receipt.Documents, d => Assert.Equal(GraphRagDocumentStatus.Ingested, d.Status));

        var character = Ontology.Classes.Single(c => c.Kind == "Character").Label;
        await using (var session = fixture.OpenSession())
        {
            var cursor = await session.RunAsync($"MATCH (e:`{character}`) WHERE e.name = 'Alice' RETURN labels(e) AS labels, e.Name IS NULL AS noPascal");
            var alice = await cursor.SingleAsync();
            Assert.Equal(new[] { character, "__Entity__", "__KGBuilder__" }.Order(StringComparer.Ordinal), alice["labels"].As<List<string>>().Order(StringComparer.Ordinal));
            Assert.True(alice["noPascal"].As<bool>());
            // Every mention, of every class, is a FROM_CHUNK from the thing to its chunk.
            cursor = await session.RunAsync($"MATCH (e:__Entity__)-[m:FROM_CHUNK]->(c:`{Layout.ChunkLabel}`) RETURN count(m) AS mentions, count(DISTINCT e) AS things");
            var counts = await cursor.SingleAsync();
            Assert.Equal((long)receipt.Extraction!.Mentions, counts["mentions"].As<long>());
        }
        await using (var db = Context())
        {
            var alice = await db.Set<Character>().SingleAsync(c => c.Name == "Alice");
            Assert.Equal(2, (await db.Set<GraphRagMention<Character>>().Where(m => m.EntityId == alice.Id).ToListAsync()).Count);
            Assert.Single(await db.Set<GraphRagMention<Place>>().Where(m => m.Quote == "the river").ToListAsync());
        }

        var maintenance = provider.GetRequiredService<IGraphRagMaintenance>();
        await maintenance.ResetAsync(new GraphRagResetScope());
        await maintenance.RemoveOrphansAsync();
        Assert.Equal(0, await Count(CountAll()));
        Assert.DoesNotContain("orphan-entities", (await maintenance.CheckAsync()).Findings);
    }

    [Neo4jIntegrationFact]
    public async Task A_failed_extraction_writes_nothing_of_the_document_and_the_next_run_retries_it()
    {
        await ProvisionAsync();
        await using (var failing = Services(new Replies { Fail = true }))
        {
            var receipt = await failing.GetRequiredService<IGraphRagIngestion>().IngestAsync(Sources().Take(1));
            var outcome = Assert.Single(receipt.Documents);
            Assert.Equal((GraphRagDocumentStatus.Failed, "extraction-failed"), (outcome.Status, outcome.ErrorCode));
            await using var db = Context();
            Assert.Equal(0, await db.Set<GraphRagDocument>().CountAsync());
            Assert.Equal(0, await db.Set<GraphRagChunk>().CountAsync());
            Assert.Equal(0, await db.Set<Character>().CountAsync());
        }
        await using var provider = Services(new Replies());
        var retried = await provider.GetRequiredService<IGraphRagIngestion>().IngestAsync(Sources().Take(1));
        Assert.Equal(GraphRagDocumentStatus.Ingested, Assert.Single(retried.Documents).Status);
    }

    [Neo4jIntegrationFact]
    public async Task A_cost_preview_counts_the_calls_and_tokens_ingestion_then_makes_writes_nothing_and_sees_what_is_unchanged()
    {
        // GraphRAG v2, wave 7 (GR-47). The chat reports input tokens the way the counter counts them, so the receipt can be compared.
        await ProvisionAsync();
        var counter = new WordCounter();
        await using var provider = Services(new Replies { Counter = counter }, counter);
        var ingestion = provider.GetRequiredService<IGraphRagIngestion>();
        var missing = GraphRagSources.File(Path.Combine(Path.GetTempPath(), "missing-" + _suffix + ".pdf"));
        var preview = await ingestion.PreviewAsync(Sources().Append(missing));
        Assert.Equal([GraphRagPreviewStatus.WouldIngest, GraphRagPreviewStatus.WouldIngest, GraphRagPreviewStatus.WouldFail], preview.Documents.Select(d => d.Status));
        Assert.Equal("unsupported-media-type", preview.Documents[2].ErrorCode);
        Assert.Equal((2, 2, 2, true), (preview.Chunks, preview.EmbeddingCalls, preview.ExtractionCalls, preview.TokensCounted));
        Assert.Equal(counter.Count(First) + counter.Count(Second), preview.EmbeddingTokens);
        Assert.Equal(0, await Count(CountAll()));
        Assert.Equal(0, await Count($"MATCH (n:{Layout.RunLabel}) RETURN count(n) AS value"));

        var receipt = await ingestion.IngestAsync(Sources());
        Assert.Equal((preview.Chunks, preview.EmbeddingCalls, preview.ExtractionCalls), (receipt.ChunksWritten, receipt.EmbeddingCalls, receipt.Extraction!.ChatCalls));
        Assert.Equal(preview.ExtractionInputTokens, receipt.Extraction.InputTokens);

        var again = await ingestion.PreviewAsync(Sources());
        Assert.All(again.Documents, d => Assert.Equal(GraphRagPreviewStatus.Unchanged, d.Status));
        Assert.Equal((0, 0, 0, 0L), (again.Chunks, again.EmbeddingCalls, again.ExtractionCalls, again.ExtractionInputTokens));
    }

    [Neo4jIntegrationFact]
    public async Task A_cancelled_run_leaves_each_document_whole_or_absent_and_the_rerun_completes_it_without_duplicates()
    {
        // GraphRAG v2, wave 7 (GR-63): cancel while the second document is being extracted.
        await ProvisionAsync();
        using var cancel = new CancellationTokenSource();
        await using (var stopping = Services(new Replies { OnSecond = cancel.Cancel }))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stopping.GetRequiredService<IGraphRagIngestion>().IngestAsync(Sources(), cancel.Token));
        await using (var db = Context())
        {
            Assert.Single(await db.Set<GraphRagDocument>().ToListAsync());
            Assert.Equal(0, await db.Set<GraphRagLease>().CountAsync());
        }

        await using var provider = Services(new Replies());
        var resumed = await provider.GetRequiredService<IGraphRagIngestion>().IngestAsync(Sources());
        Assert.Equal([GraphRagDocumentStatus.Unchanged, GraphRagDocumentStatus.Ingested], resumed.Documents.Select(d => d.Status));
        Assert.Equal(1, resumed.Extraction!.ChatCalls);
        await using (var db = Context())
        {
            Assert.Equal(["Alice", "White Rabbit"], await db.Set<Character>().Where(c => !c.IsPlaceholder).OrderBy(c => c.Name).Select(c => c.Name).ToListAsync());
            var alice = await db.Set<Character>().SingleAsync(c => c.Name == "Alice");
            Assert.Equal(2, (await db.Set<GraphRagMention<Character>>().Where(m => m.EntityId == alice.Id).ToListAsync()).Count);
            Assert.Equal(2, (await db.Set<RelatedTo<Character, Character>>().SingleAsync()).DocumentIds!.Split('\n').Length);
            Assert.Single(await db.Set<Says>().ToListAsync());
            Assert.Equal(2, await db.Set<GraphRagDocument>().CountAsync());
        }
    }

    [Neo4jIntegrationFact]
    public async Task Re_ingesting_with_other_settings_replaces_the_extracted_graph_without_duplicates()
    {
        // GraphRAG v2, wave 7: the whole book was ingested again over an earlier extraction when the instructions changed.
        await ProvisionAsync();
        await using (var first = Services(new Replies()))
            Assert.All((await first.GetRequiredService<IGraphRagIngestion>().IngestAsync(Sources())).Documents, d => Assert.Equal(GraphRagDocumentStatus.Ingested, d.Status));
        await using var second = Services(new Replies(), maxUtf8Bytes: 2048);
        var again = await second.GetRequiredService<IGraphRagIngestion>().IngestAsync(Sources());
        Assert.All(again.Documents, d => Assert.True(d.Status == GraphRagDocumentStatus.Ingested, $"{d.SourceUri}: {d.Status} {d.ErrorCode}"));
        await using var db = Context();
        Assert.Equal(["Alice", "White Rabbit"], await db.Set<Character>().Where(c => !c.IsPlaceholder).OrderBy(c => c.Name).Select(c => c.Name).ToListAsync());
        Assert.Single(await db.Set<Says>().ToListAsync());
        Assert.Equal(2, (await db.Set<RelatedTo<Character, Character>>().SingleAsync()).DocumentIds!.Split('\n').Length);
        Assert.Equal(2, await db.Set<GraphRagDocument>().CountAsync());
    }

    [Neo4jIntegrationFact]
    public async Task Re_ingesting_unchanged_text_under_new_settings_replaces_its_chunks_with_the_same_ids()
    {
        // GraphRAG v2, wave 7: new extraction instructions re-ingest every document, and the same text chunks to the same IDs.
        await ProvisionAsync();
        await using var provider = Services(new Replies());
        var ingestion = provider.GetRequiredService<IGraphRagIngestion>();
        Assert.All((await ingestion.IngestAsync(Sources())).Documents, d => Assert.Equal(GraphRagDocumentStatus.Ingested, d.Status));
        await Execute($"MATCH (r:{Layout.RunLabel}) SET r.SettingsFingerprint = 'earlier'");
        var again = await ingestion.IngestAsync(Sources());
        Assert.All(again.Documents, d => Assert.True(d.Status == GraphRagDocumentStatus.Ingested, $"{d.SourceUri}: {d.Status} {d.ErrorCode}"));
        await using var db = Context();
        Assert.Equal(2, await db.Set<GraphRagChunk>().CountAsync());
        Assert.Single(await db.Set<Says>().ToListAsync());
    }

    [Neo4jIntegrationFact]
    public async Task A_duplicate_is_suggested_merged_and_undone_with_the_graph_exactly_as_before()
    {
        // GR-55: the second document names "the White Rabbit", which extraction keeps apart from the first's "White Rabbit".
        await ProvisionAsync();
        await using var provider = Services(new DuplicateReplies());
        var receipt = await provider.GetRequiredService<IGraphRagIngestion>().IngestAsync([GraphRagSources.Text("first", First), GraphRagSources.Text("third", Third)]);
        Assert.All(receipt.Documents, d => Assert.Equal(GraphRagDocumentStatus.Ingested, d.Status));
        var before = await SnapshotAsync();

        await using var db = Context();
        var suggestion = Assert.Single(await GraphRagResolution.SuggestAsync(db));
        Assert.Equal(("White Rabbit", "the White Rabbit", "same-name"), (suggestion.KeepName, suggestion.MergeName, suggestion.Reason));
        var plan = GraphRagResolution.Plan([suggestion]);
        var preview = await plan.PreviewAsync(db);
        Assert.True(preview.CanApply, string.Join("; ", preview.Blockers));
        Assert.Equal((1, 1, 1, 1), (preview.Merges, preview.Mentions, preview.Facts, preview.Quotes));
        Assert.Equal(before, await SnapshotAsync());

        var merged = await plan.ApplyAsync(db);
        Assert.Equal((1, 0, 1, 0, 1), (merged.Mentions, merged.FactsMoved, merged.FactsJoined, merged.FactsDropped, merged.Quotes));
        await using (var after = Context())
        {
            var rabbit = await after.Set<Character>().SingleAsync(c => c.Name == "White Rabbit");
            Assert.Contains("the White Rabbit", GraphRagOntologyRuntimeLines(rabbit.Aliases));
            Assert.Empty(await after.Set<Character>().Where(c => c.Name == "the White Rabbit").ToListAsync());
            Assert.Equal(3, (await after.Set<GraphRagMention<Character>>().Where(m => m.EntityId == rabbit.Id).ToListAsync()).Count);
            Assert.Equal(2, (await after.Set<Says>().Where(q => q.SpeakerId == rabbit.Id).ToListAsync()).Count);
            var fact = await after.Set<RelatedTo<Character, Character>>().SingleAsync(f => f.ToId == rabbit.Id);
            Assert.Equal(2, fact.DocumentIds.Split('\n').Length);
        }
        Assert.Empty(await GraphRagResolution.SuggestAsync(db));

        var undone = await GraphRagMergePlan.UndoAsync(db, Assert.Single(merged.RunIds));
        Assert.Equal((1, 1, 1), (undone.Mentions, undone.Facts, undone.Quotes));
        Assert.Equal(before, await SnapshotAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => GraphRagMergePlan.UndoAsync(db, merged.RunIds[0]));
    }

    [Neo4jIntegrationFact]
    public async Task An_undo_is_refused_once_re_ingesting_wrote_the_merged_thing_again()
    {
        // GR-55: extraction resolves by name, so a re-ingested document brings "the White Rabbit" back; undoing would duplicate it.
        await ProvisionAsync();
        await using var provider = Services(new DuplicateReplies());
        await provider.GetRequiredService<IGraphRagIngestion>().IngestAsync([GraphRagSources.Text("first", First), GraphRagSources.Text("third", Third)]);
        await using var db = Context();
        var merged = await GraphRagResolution.Plan(await GraphRagResolution.SuggestAsync(db)).ApplyAsync(db);

        var again = await provider.GetRequiredService<IGraphRagMaintenance>().ReingestChangedAsync([GraphRagSources.Text("third", Third + " Alice ran.")]);
        Assert.Equal(GraphRagDocumentStatus.Ingested, Assert.Single(again.Documents).Status);
        var suggestion = Assert.Single(await GraphRagResolution.SuggestAsync(db));
        Assert.Equal(("White Rabbit", "the White Rabbit"), (suggestion.KeepName, suggestion.MergeName));
        var before = await SnapshotAsync();
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => GraphRagMergePlan.UndoAsync(db, merged.RunIds[0]));
        Assert.Contains("written again since the merge", refused.Message, StringComparison.Ordinal);
        Assert.Equal(before, await SnapshotAsync());

        // Merging again joins the new copy; that run undoes cleanly.
        var second = await GraphRagResolution.Plan([suggestion]).ApplyAsync(db);
        await GraphRagMergePlan.UndoAsync(db, second.RunIds[0]);
        Assert.Equal(before, await SnapshotAsync());
    }

    [Neo4jIntegrationFact]
    public async Task A_plan_that_fails_at_its_third_merge_reports_the_two_runs_applied_before_it()
    {
        // GR-70: each merge is its own save, so a failure leaves the merges before it written; the exception names their runs.
        await ProvisionAsync();
        await using var provider = Services(new DuplicateReplies());
        await provider.GetRequiredService<IGraphRagIngestion>().IngestAsync([GraphRagSources.Text("first", First), GraphRagSources.Text("third", Third)]);
        await using (var seed = Context())
        {
            // Two more same-name pairs, written directly: things without mentions are suggested like any other.
            foreach (var name in new[] { "Hatter", "the Hatter", "Queen", "the Queen" })
                seed.Add(new Character { Id = "gr70-" + name.Replace(' ', '-'), Name = name, RunId = "gr70", ExtractorVersion = "test" });
            await seed.SaveChangesAsync();
        }
        var before = await SnapshotAsync();
        await using var db = new RagContext(new DbContextOptionsBuilder<RagContext>().UseNeo4j(fixture.Driver, fixture.Database)
            .ReplaceService<IModelCacheKeyFactory, OntologyKey>().AddInterceptors(new FailOnSave(3)).Options, Layout, Ontology);
        var plan = GraphRagResolution.Plan(await GraphRagResolution.SuggestAsync(db));
        Assert.Equal(3, plan.Merges.Count);

        var failed = await Assert.ThrowsAsync<GraphRagMergeException>(() => plan.ApplyAsync(db));
        Assert.Equal(plan.Merges[2], failed.Failed);
        Assert.Equal(2, failed.AppliedRunIds.Count);
        Assert.IsType<InvalidOperationException>(failed.InnerException);
        Assert.Contains($"the 2 merges before it were, as runs {failed.AppliedRunIds[0]}, {failed.AppliedRunIds[1]}", failed.Message, StringComparison.Ordinal);
        // Nothing of the third merge was written: it is the only suggestion left.
        Assert.Equal(plan.Merges[2], Assert.Single(await GraphRagResolution.SuggestAsync(db)));

        foreach (var run in failed.AppliedRunIds.Reverse()) await GraphRagMergePlan.UndoAsync(db, run);
        Assert.Equal(before, await SnapshotAsync());
    }

    [Neo4jIntegrationFact]
    public async Task An_undo_is_refused_while_a_later_write_changed_what_the_merge_wrote_and_last_first_restores_the_graph()
    {
        // GR-69: undoing must not lose what came after the merge, whether a later merge into the same thing or a document that
        // states a fact the merge wrote.
        await ProvisionAsync();
        await using var provider = Services(new DuplicateReplies());
        await provider.GetRequiredService<IGraphRagIngestion>().IngestAsync([GraphRagSources.Text("first", First), GraphRagSources.Text("third", Third)]);
        await using (var seed = Context())
        {
            // A second duplicate of the White Rabbit, written directly, so two merges keep one thing.
            seed.Add(new Character { Id = "gr69-rabbit", Name = "White  Rabbit", RunId = "gr69", ExtractorVersion = "test" });
            await seed.SaveChangesAsync();
        }
        var before = await SnapshotAsync();
        await using var db = Context();
        var plan = GraphRagResolution.Plan(await GraphRagResolution.SuggestAsync(db));
        Assert.Equal(["White  Rabbit", "the White Rabbit"], plan.Merges.Select(m => m.MergeName).Order(StringComparer.Ordinal));
        var merged = await plan.ApplyAsync(db);
        var (first, last) = (merged.RunIds[0], merged.RunIds[1]);

        // The first run's aliases on the White Rabbit were changed by the second merge.
        var early = await Assert.ThrowsAsync<InvalidOperationException>(() => GraphRagMergePlan.UndoAsync(db, first));
        Assert.Contains("what it wrote was changed since", early.Message, StringComparison.Ordinal);

        // A document stating the fact the second merge joined, as ingestion would union it in.
        var rabbitMerge = plan.Merges[1].MergeName == "the White Rabbit" ? last : first;
        string joined;
        await using (var later = Context())
        {
            var rabbit = await later.Set<Character>().SingleAsync(c => c.Name == "White Rabbit");
            var fact = await later.Set<RelatedTo<Character, Character>>().SingleAsync(f => f.ToId == rabbit.Id);
            joined = fact.DocumentIds;
            fact.DocumentIds = joined + "\nlater-document";
            await later.SaveChangesAsync();
        }
        var stated = await SnapshotAsync();
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => GraphRagMergePlan.UndoAsync(db, rabbitMerge));
        Assert.Contains("what it wrote was changed since", refused.Message, StringComparison.Ordinal);
        Assert.Equal(stated, await SnapshotAsync());

        // Once the later write is gone, the runs undo last first, back to the graph before the merges.
        await using (var later = Context())
        {
            var fact = await later.Set<RelatedTo<Character, Character>>().SingleAsync(f => f.DocumentIds == joined + "\nlater-document");
            fact.DocumentIds = joined;
            await later.SaveChangesAsync();
        }
        await GraphRagMergePlan.UndoAsync(db, last);
        await GraphRagMergePlan.UndoAsync(db, first);
        Assert.Equal(before, await SnapshotAsync());
    }

    // Refuses the n-th save before anything of it is written, as a failing database would.
    private sealed class FailOnSave(int failing) : SaveChangesInterceptor
    {
        private int _saves;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
            => ++_saves == failing ? throw new InvalidOperationException($"Save {failing} refused by the test.") : base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private static string[] GraphRagOntologyRuntimeLines(string? text) => string.IsNullOrEmpty(text) ? [] : text.Split('\n');

    // Every node of the ontology's classes and every relationship touching them, with all their properties, in a stable order.
    private async Task<string> SnapshotAsync()
    {
        var labels = string.Join(" OR ", Ontology.Classes.Select(c => "n:" + c.Label));
        await using var session = fixture.OpenSession();
        var nodes = await (await session.RunAsync($"MATCH (n) WHERE {labels} RETURN labels(n) AS labels, properties(n) AS properties")).ToListAsync();
        var edges = await (await session.RunAsync($"MATCH (n)-[r]-(o) WHERE ({labels}) AND elementId(startNode(r)) = elementId(n) RETURN type(r) AS type, properties(r) AS properties, o.Id AS target")).ToListAsync();
        static string Text(IReadOnlyDictionary<string, object> properties) => string.Join(", ", properties.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key}={p.Value}"));
        return string.Join('\n', nodes.Select(r => string.Join(':', r["labels"].As<List<string>>().Order(StringComparer.Ordinal)) + " " + Text(r["properties"].As<IReadOnlyDictionary<string, object>>()))
            .Concat(edges.Select(r => r["type"].As<string>() + " -> " + r["target"].As<string>() + " " + Text(r["properties"].As<IReadOnlyDictionary<string, object>>())))
            .Order(StringComparer.Ordinal));
    }

    internal const string Third = "Later, Alice followed the White Rabbit into Wonderland. “Oh my ears!” cried the White Rabbit.";

    // The first document as in Replies; the third names "the White Rabbit", a thing of its own until it is merged.
    internal sealed class DuplicateReplies : IChatClient
    {
        private const string ThirdReply = """
            {"entities": [
              {"kind": "Character", "name": "Alice", "mentions": ["Alice"]},
              {"kind": "Character", "name": "the White Rabbit", "mentions": ["the White Rabbit"]},
              {"kind": "Place", "name": "Wonderland", "mentions": ["Wonderland"]}
            ],
            "relationships": [
              {"type": "RELATED_TO", "from": "Alice", "fromKind": "Character", "to": "the White Rabbit", "toKind": "Character", "kind": "friend"}
            ],
            "quotes": [{"type": "SAYS", "speaker": "the White Rabbit", "words": "Oh my ears!"}]}
            """;

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                messages.ToList()[1].Text.Contains("near the river", StringComparison.Ordinal) ? FirstReply : ThirdReply)));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private static IEnumerable<GraphRagSource> Sources() => [GraphRagSources.Text("first", First), GraphRagSources.Text("second", Second)];

    private string CountAll()
    {
        var labels = string.Join(" OR ", new[] { Layout.DocumentLabel, Layout.SectionLabel, Layout.ChunkLabel }.Concat(Ontology.Classes.Select(c => c.Label)).Select(l => "n:" + l));
        return $"MATCH (n) WHERE {labels} OPTIONAL MATCH (n)-[r]->() RETURN count(DISTINCT n) + count(r) AS value";
    }

    private async Task ProvisionAsync()
    {
        foreach (var step in GraphRagSchema.CreateSteps(Layout, _set, Ontology))
            await Execute(GraphMigrationStepRenderer.Render(step).Cypher);
        foreach (var index in new[] { _set.VectorIndexNameFor(Layout.SchemaPrefix), GraphRagSchema.FullTextIndexName(Layout), GraphRagSchema.EntityNamesIndexName(Layout) })
            await Execute("CALL db.awaitIndex($index, 60)", new { index });
    }

    private ServiceProvider Services(IChatClient chat, IGraphRagTokenCounter? counter = null, int maxUtf8Bytes = 4096)
    {
        var services = new ServiceCollection();
        services.AddKeyedSingleton(GraphRagServiceKeys.ExtractionChatClient, chat);
        if (counter is not null) services.AddSingleton(counter);
        services.AddDbContext<RagContext>(o => o.UseNeo4j(fixture.Driver, fixture.Database).ReplaceService<IModelCacheKeyFactory, OntologyKey>());
        services.AddSingleton(Layout);
        services.AddSingleton(Ontology);
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(new HashGenerator());
        services.AddGraphRag<RagContext>(_set, o => o.Chunking.MaxUtf8Bytes = maxUtf8Bytes);
        return services.BuildServiceProvider();
    }

    private RagContext Context() => new(new DbContextOptionsBuilder<RagContext>().UseNeo4j(fixture.Driver, fixture.Database)
        .ReplaceService<IModelCacheKeyFactory, OntologyKey>().Options, Layout, Ontology);

    private async Task Execute(string text, object? parameters = null)
    {
        await using var session = fixture.OpenSession();
        await (parameters is null ? await session.RunAsync(text) : await session.RunAsync(text, parameters)).ConsumeAsync();
    }

    private async Task<int> Count(string text)
    {
        await using var session = fixture.OpenSession();
        return (int)(await (await session.RunAsync(text)).SingleAsync())["value"].As<long>();
    }

    public sealed class RagContext(DbContextOptions<RagContext> options, GraphRagLayout layout, GraphRagOntology ontology) : DbContext(options)
    {
        public GraphRagLayout Layout { get; } = layout;
        public GraphRagOntology Ontology { get; } = ontology;
        protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.AddGraphRag(8, o => (o.Layout, o.Ontology) = (Layout, Ontology));
    }

    public sealed class OntologyKey : IModelCacheKeyFactory
    {
        public object Create(DbContext context, bool designTime) => (context.GetType(), ((RagContext)context).Layout, ((RagContext)context).Ontology, designTime);
    }

    // Replies written for these tests by passage, as a model would give them; Fail stands for a client that refuses every call.
    // With a counter, it reports input tokens as the cost preview estimates them: each message, plus the chat format.
    internal sealed class Replies : IChatClient
    {
        public bool Fail { get; init; }
        public IGraphRagTokenCounter? Counter { get; init; }
        public Action? OnSecond { get; init; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            if (Fail) throw new InvalidOperationException("The model refused the request.");
            var list = messages.ToList();
            var passage = list[1].Text;
            var first = passage.Contains("near the river", StringComparison.Ordinal);
            if (!first && OnSecond is { } stop)
            {
                stop();
                cancellationToken.ThrowIfCancellationRequested();
            }
            var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, first ? FirstReply : SecondReply));
            if (Counter is { } counter) response.Usage = new UsageDetails { InputTokenCount = list.Sum(m => counter.Count(m.Text) + 4) + 3 };
            return Task.FromResult(response);
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class WordCounter : IGraphRagTokenCounter
    {
        public int Count(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
    }

    private sealed class HashGenerator : IEmbeddingGenerator<string, Embedding<float>>
    {
        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values, EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(values.Select(text =>
                new Embedding<float>(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text)).Take(8).Select(b => 1f + b).ToArray()))));

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
