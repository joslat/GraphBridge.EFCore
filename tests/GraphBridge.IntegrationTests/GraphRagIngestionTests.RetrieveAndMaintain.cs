using System.Text;
using System.Text.Json;
using GraphBridge.GraphRag;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Neo4j.Driver;

namespace GraphBridge.IntegrationTests;

// GraphRAG waves 2 and 3 acceptance on the disposable fixture: retrieval with a trace, verified answers, maintenance.
public sealed partial class GraphRagIngestionTests
{
    [Neo4jIntegrationFact]
    public async Task Hybrid_retrieval_traces_each_item_and_a_scripted_answer_cites_the_stored_bytes()
    {
        await ProvisionAsync();
        var chat = new QuotingChat();
        await using var provider = Services(new HashGenerator(), chat);
        var receipt = await provider.GetRequiredService<IGraphRagIngestion>().IngestAsync([GraphRagSources.Text("guide", Markdown, "text/markdown")]);
        var source = Encoding.UTF8.GetBytes(Markdown.ReplaceLineEndings("\n"));

        var evidence = await provider.GetRequiredService<IGraphRagRetriever>().RetrieveAsync("Edges link nodes");
        Assert.NotEmpty(evidence.Items);
        Assert.All(evidence.Items, item =>
        {
            Assert.NotEmpty(item.Routes);
            Assert.Equal(item.Routes.Sum(r => r.RankScore), item.Score, 12);
            Assert.Equal(Encoding.UTF8.GetString(source, item.StartByte, item.EndByte - item.StartByte), item.Text);
        });
        Assert.Contains(evidence.Items[0].Routes, r => r.Route == GraphRagRoutes.FullText);
        Assert.Equal([_set.VectorIndexNameFor(Layout.SchemaPrefix), GraphRagSchema.FullTextIndexName(Layout)], evidence.Provenance.Indexes);
        Assert.Equal(_set.Id, evidence.Provenance.EmbeddingSetId);
        Assert.False(string.IsNullOrEmpty(evidence.Provenance.ServerVersion));
        Assert.All(evidence.Context, c => Assert.StartsWith("neighbour of ", c.Reason));
        Assert.True(evidence.Utf8Bytes <= 16_384);

        var answer = await provider.GetRequiredService<IGraphRagAnswers>().AnswerAsync("Edges link nodes");
        Assert.Equal(GraphRagAnswerDisposition.Answered, answer.Disposition);
        var citation = Assert.Single(answer.Citations);
        Assert.Equal(Encoding.UTF8.GetString(source, citation.StartByte, citation.EndByte - citation.StartByte), citation.Quote);
        Assert.Equal(receipt.Documents[0].DocumentId, citation.DocumentId);

        chat.Alter = true;
        var altered = await provider.GetRequiredService<IGraphRagAnswers>().AnswerAsync("Edges link nodes");
        Assert.Equal(("citation-not-found", (string?)null), (altered.Reason, altered.Text));
    }

    [Neo4jIntegrationFact]
    public async Task A_cypher_expansion_adds_labelled_context_and_cannot_write()
    {
        await ProvisionAsync();
        await using var provider = Services(new HashGenerator());
        await provider.GetRequiredService<IGraphRagIngestion>().IngestAsync([GraphRagSources.Text("guide", Markdown, "text/markdown")]);
        var retriever = provider.GetRequiredService<IGraphRagRetriever>();
        var expanded = await retriever.RetrieveAsync("Edges link nodes", GraphRagRetrievers.HybridCypher(
            $"MATCH (c:{Layout.ChunkLabel})-[:IN_SECTION]->(s:{Layout.SectionLabel}) WHERE c.Id IN $chunkIds RETURN c.Id AS chunkId, s.Title AS text"));
        Assert.Contains(expanded.Context, c => c.Reason == "cypher expansion" && c.Text == "Edges");
        await Assert.ThrowsAnyAsync<Neo4jException>(() => retriever.RetrieveAsync("Edges link nodes",
            GraphRagRetrievers.Vector().WithCypherExpansion("CREATE (:GraphRagExpansionWrite) RETURN 'x' AS chunkId, 'y' AS text")));
        Assert.Equal(0, await Count("MATCH (n:GraphRagExpansionWrite) RETURN count(n) AS value"));
    }

    [Neo4jIntegrationFact]
    public async Task Maintenance_reingests_only_edits_removes_sources_cleanly_and_refuses_a_foreign_graph()
    {
        await ProvisionAsync();
        await using var provider = Services(new HashGenerator());
        var ingestion = provider.GetRequiredService<IGraphRagIngestion>();
        var maintenance = provider.GetRequiredService<IGraphRagMaintenance>();
        var first = await ingestion.IngestAsync([GraphRagSources.Text("one", "# One\n\nFirst text.", "text/markdown"), GraphRagSources.Text("two", Markdown, "text/markdown")]);
        var two = first.Documents[1].DocumentId;
        await using var db = Context();
        var twoChunks = await db.Set<GraphRagChunk>().Where(c => c.DocumentId == two).Select(c => c.Id).OrderBy(id => id).ToListAsync();

        var edited = await maintenance.ReingestChangedAsync([GraphRagSources.Text("one", "# One\n\nFirst text, edited.", "text/markdown"), GraphRagSources.Text("two", Markdown, "text/markdown")]);
        Assert.Equal([GraphRagDocumentStatus.Ingested, GraphRagDocumentStatus.Unchanged], edited.Documents.Select(d => d.Status));
        Assert.Equal(twoChunks, await db.Set<GraphRagChunk>().Where(c => c.DocumentId == two).Select(c => c.Id).OrderBy(id => id).ToListAsync());

        var removed = await maintenance.RemoveSourceAsync(two);
        Assert.Equal((1, 4, twoChunks.Count), (removed.Documents, removed.Sections, removed.Chunks));
        Assert.Equal(0, await db.Set<GraphRagChunk>().CountAsync(c => c.DocumentId == two));
        Assert.Equal(0, await db.Set<GraphRagSection>().CountAsync(s => s.DocumentId == two));
        var health = await maintenance.CheckAsync();
        Assert.DoesNotContain("orphans", health.Findings);
        Assert.Equal(1, health.Documents);

        // A node the toolkit didn't write, under its labels: removal refuses and changes nothing.
        await Execute($"CREATE (:{Layout.DocumentLabel} {{Id: 'foreign', SourceUri: 'x', MediaType: 'text/plain', ContentHash: 'h', LengthBytes: 0}})");
        await Assert.ThrowsAsync<InvalidOperationException>(() => maintenance.ResetAsync(new GraphRagResetScope()));
        Assert.Equal(2, await db.Set<GraphRagDocument>().CountAsync());
        await Execute($"MATCH (d:{Layout.DocumentLabel} {{Id: 'foreign'}}) DELETE d");
        var reset = await maintenance.ResetAsync(new GraphRagResetScope(RunIds: [edited.RunId]));
        Assert.Equal(1, reset.Documents);
        Assert.Equal(0, await db.Set<GraphRagChunk>().CountAsync());
    }

    [Neo4jIntegrationFact]
    public async Task The_health_report_flags_a_missing_embedding_a_mixed_set_and_orphans_it_can_remove()
    {
        await ProvisionAsync();
        await using var provider = Services(new HashGenerator());
        var maintenance = provider.GetRequiredService<IGraphRagMaintenance>();
        var receipt = await provider.GetRequiredService<IGraphRagIngestion>().IngestAsync([GraphRagSources.Text("guide", Markdown, "text/markdown")]);
        Assert.Empty((await maintenance.CheckAsync()).Findings);

        await using var db = Context();
        var ids = await db.Set<GraphRagChunk>().OrderBy(c => c.Order).Select(c => c.Id).Take(2).ToListAsync();
        await Execute($"MATCH (c:{Layout.ChunkLabel} {{Id: $id}}) REMOVE c.Embedding", new { id = ids[0] });
        await Execute($"MATCH (c:{Layout.ChunkLabel} {{Id: $id}}) SET c.EmbeddingSetId = 'another-set'", new { id = ids[1] });
        await Execute($"CREATE (:{Layout.ChunkLabel} {{Id: 'orphan', DocumentId: 'gone', Order: 0, Text: 'x', StartByte: 0, EndByte: 1, RunId: $run, "
            + "Embedding: [1.0, 1.0, 1.0, 1.0, 1.0, 1.0, 1.0, 1.0], EmbeddingSetId: $set})", new { run = receipt.RunId, set = _set.Id });
        var health = await maintenance.CheckAsync();
        Assert.Equal(["missing-embedding", "mixed-embedding-sets", "orphans"], health.Findings);
        Assert.Equal((1, 1, 1), (health.ChunksWithoutEmbedding, health.ChunksFromOtherEmbeddingSets, health.OrphanChunks));
        Assert.Equal(7, health.IndexStates.Count);
        Assert.All(health.IndexStates.Values, state => Assert.Equal("ONLINE", state));

        Assert.Equal(1, (await maintenance.RemoveOrphansAsync()).Chunks);
        Assert.DoesNotContain("orphans", (await maintenance.CheckAsync()).Findings);
    }

    // A scripted chat client: it cites the first full line of evidence 1, exactly, or with two letters added.
    private sealed class QuotingChat : IChatClient
    {
        public bool Alter { get; set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var lines = messages.Last().Text.ReplaceLineEndings("\n").Split('\n');
            var index = Array.FindIndex(lines, line => line.StartsWith("[1] ", StringComparison.Ordinal));
            var quote = new[] { lines[index][4..] }
                .Concat(lines.Skip(index + 1).TakeWhile(line => !line.StartsWith("[2] ", StringComparison.Ordinal) && !line.StartsWith("Context (never cite):", StringComparison.Ordinal)))
                .Select(line => line.Trim()).First(line => line.Length >= 8);
            if (Alter) quote += " zz";
            var reply = JsonSerializer.Serialize(new { answer = "From the evidence.", citations = new[] { new { evidence = 1, quote } } });
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, reply)));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
