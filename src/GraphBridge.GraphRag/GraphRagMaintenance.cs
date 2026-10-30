using GraphBridge.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Neo4j.Driver;

namespace GraphBridge.GraphRag;

/// <summary>Keeps the lexical graph healthy (ADR 0052, section 4): explicit, bounded deletes that refuse what the toolkit didn't write.</summary>
public interface IGraphRagMaintenance
{
    /// <summary>Ingests the sources again; documents whose content and settings are unchanged are skipped.</summary>
    Task<GraphRagIngestionReceipt> ReingestChangedAsync(IEnumerable<GraphRagSource> sources, CancellationToken cancellationToken = default);
    /// <summary>Removes one document with its sections, chunks and links.</summary>
    Task<GraphRagRemovalReport> RemoveSourceAsync(string documentId, CancellationToken cancellationToken = default);
    /// <summary>Removes the documents the scope selects: by writing run, by ID, or every toolkit document when both are null.</summary>
    Task<GraphRagRemovalReport> ResetAsync(GraphRagResetScope scope, CancellationToken cancellationToken = default);
    /// <summary>Removes sections and chunks whose document is gone, expired leases and, with an ontology, things no mention points to.
    /// One call handles the orphans of at most 10,000 documents: call it again until it removes nothing.</summary>
    Task<GraphRagRemovalReport> RemoveOrphansAsync(CancellationToken cancellationToken = default);
    /// <summary>Reports coverage, orphans, index state, embedding sets and readback, without changing anything.</summary>
    Task<GraphRagHealthReport> CheckAsync(CancellationToken cancellationToken = default);
}

/// <summary>Which documents a reset removes. Both null: every document the toolkit wrote.</summary>
public sealed record GraphRagResetScope(IReadOnlyList<string>? RunIds = null, IReadOnlyList<string>? DocumentIds = null);

/// <summary>What a removal deleted. <see cref="Edges"/> counts structural links, mentions, quotes and facts; <see cref="Entities"/> extracted things.</summary>
public sealed record GraphRagRemovalReport(int Documents, int Sections, int Chunks, int Edges, int Leases, int Entities = 0);

/// <summary>
/// The lexical graph's health. <see cref="Findings"/> holds stable codes: <c>schema-missing</c>, <c>index-not-online</c>,
/// <c>missing-embedding</c>, <c>mixed-embedding-sets</c>, <c>orphans</c>, <c>readback-mismatch</c>, <c>expired-leases</c> and, with an
/// ontology, <c>orphan-entities</c> (things no mention points to) and <c>stale-facts</c> (facts listing a document that is gone);
/// none means healthy.
/// </summary>
public sealed record GraphRagHealthReport(int Documents, int Sections, int Chunks, int ChunksWithoutEmbedding, int ChunksFromOtherEmbeddingSets,
    int OrphanSections, int OrphanChunks, int ReadbackMismatches, int ExpiredLeases, IReadOnlyDictionary<string, string> IndexStates,
    IReadOnlyList<string> Findings, int OrphanEntities = 0, int StaleFacts = 0);

/// <summary>Deletes a document's content: edges first, then nodes, by key and in bounded pages, never with a DETACH DELETE.</summary>
internal static class GraphRagRemoval
{
    internal const int PageSize = 1_000;
    /// <summary>Documents removed in one transaction: at most this many.</summary>
    internal const int BatchDocuments = 100;
    /// <summary>And at most this many bytes of source text, unless one document alone is larger.</summary>
    internal const long BatchBytes = 8 << 20;

    /// <summary>Documents in order, cut into batches of at most <see cref="BatchDocuments"/> and <see cref="BatchBytes"/>.</summary>
    internal static IEnumerable<string[]> Batches(IEnumerable<(string Id, long Bytes)> documents)
    {
        var (batch, bytes) = (new List<string>(), 0L);
        foreach (var (id, length) in documents)
        {
            if (batch.Count > 0 && (batch.Count == BatchDocuments || bytes + length > BatchBytes))
            {
                yield return [.. batch];
                (batch, bytes) = ([], 0L);
            }
            batch.Add(id);
            bytes += length;
        }
        if (batch.Count > 0) yield return [.. batch];
    }

    /// <summary>More facts list a batch's documents than one removal reads (<see cref="GraphRagOntologyRuntime.MaxListedFacts"/>).</summary>
    internal sealed class FactListingLimitException(string message) : InvalidOperationException(message);

    /// <summary>
    /// Removes each batch in its own transaction, the documents themselves too when <paramref name="documents"/> is set. A batch
    /// whose documents list more facts than one removal reads is halved and tried again; a single document that does is refused,
    /// and the batches before it stay removed.
    /// </summary>
    internal static async Task<(int Documents, int Sections, int Chunks, int Edges)> RemoveInBatchesAsync(DbContext db, IEnumerable<string[]> batches,
        bool documents, CancellationToken cancellationToken)
    {
        var pending = new Stack<string[]>(batches.Reverse());
        var (removedDocuments, sections, chunks, edges) = (0, 0, 0, 0);
        while (pending.TryPop(out var batch))
        {
            try
            {
                // One transaction per batch (ADR 0052, section 19): each document in it is removed whole, or the batch not at all.
                await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                var removed = await RemoveContentAsync(db, batch, cancellationToken).ConfigureAwait(false);
                var deleted = documents ? await db.Set<GraphRagDocument>().Where(d => batch.Contains(d.Id)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false) : 0;
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                (removedDocuments, sections, chunks, edges) = (removedDocuments + deleted, sections + removed.Sections, chunks + removed.Chunks, edges + removed.Edges);
            }
            catch (FactListingLimitException) when (batch.Length > 1)
            {
                pending.Push(batch[(batch.Length / 2)..]);
                pending.Push(batch[..(batch.Length / 2)]);
            }
            catch (FactListingLimitException error)
            {
                throw new InvalidOperationException($"More than {GraphRagOntologyRuntime.MaxListedFacts:N0} facts list document '{batch[0]}', more than one removal "
                    + $"reads; nothing of it was removed. The {removedDocuments} documents removed before it in this call stay removed.", error);
            }
        }
        return (removedDocuments, sections, chunks, edges);
    }

    internal static async Task<(int Sections, int Chunks, int Edges)> RemoveContentAsync(DbContext db, string[] documentIds, CancellationToken cancellationToken)
    {
        var chunkIds = await db.Set<GraphRagChunk>().Where(c => documentIds.Contains(c.DocumentId)).Select(c => c.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        var sectionIds = await db.Set<GraphRagSection>().Where(s => documentIds.Contains(s.DocumentId)).Select(s => s.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        var edges = 0;
        // Extraction first: mentions and quotes point at the chunks, and facts list the documents.
        if (db.Model.FindAnnotation(GraphRagModelBuilderExtensions.OntologyAnnotation)?.Value is GraphRagOntology ontology)
            edges += await ontology.Runtime.RemoveAsync(db, LayoutOf(db), documentIds, chunkIds, cancellationToken).ConfigureAwait(false);
        foreach (var page in chunkIds.Chunk(PageSize))
        {
            edges += await db.Set<GraphRagNextChunk>().Where(e => page.Contains(e.ChunkId) || page.Contains(e.NextChunkId)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            edges += await db.Set<GraphRagChunkSection>().Where(e => page.Contains(e.ChunkId)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        }
        foreach (var page in sectionIds.Chunk(PageSize))
            edges += await db.Set<GraphRagSubsection>().Where(e => page.Contains(e.SectionId) || page.Contains(e.ParentSectionId)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        edges += await DeleteInPagesAsync(db.Set<GraphRagChunkDocument>().Where(e => documentIds.Contains(e.DocumentId)).OrderBy(e => e.ChunkId), cancellationToken).ConfigureAwait(false);
        edges += await DeleteInPagesAsync(db.Set<GraphRagSectionDocument>().Where(e => documentIds.Contains(e.DocumentId)).OrderBy(e => e.SectionId), cancellationToken).ConfigureAwait(false);
        var chunks = await DeleteInPagesAsync(db.Set<GraphRagChunk>().Where(c => documentIds.Contains(c.DocumentId)).OrderBy(c => c.Id), cancellationToken).ConfigureAwait(false);
        var sections = await DeleteInPagesAsync(db.Set<GraphRagSection>().Where(s => documentIds.Contains(s.DocumentId)).OrderBy(s => s.Id), cancellationToken).ConfigureAwait(false);
        return (sections, chunks, edges);
    }

    internal static GraphRagLayout LayoutOf(DbContext db)
        => db.Model.FindAnnotation(GraphRagModelBuilderExtensions.LayoutAnnotation)?.Value as GraphRagLayout
            ?? throw new InvalidOperationException($"{db.GetType().Name} doesn't map the GraphRAG model; call modelBuilder.AddGraphRag(...) in OnModelCreating.");

    internal static GraphRagOntology? OntologyOf(DbContext db) => db.Model.FindAnnotation(GraphRagModelBuilderExtensions.OntologyAnnotation)?.Value as GraphRagOntology;

    internal static async Task<int> DeleteInPagesAsync<T>(IOrderedQueryable<T> query, CancellationToken cancellationToken)
    {
        var total = 0;
        int deleted;
        do
        {
            deleted = await query.Take(PageSize).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            total += deleted;
        } while (deleted == PageSize);
        return total;
    }
}

internal sealed class GraphRagMaintenance<TContext>(IServiceScopeFactory scopes, GraphRagEmbeddingSet embeddingSet, IGraphRagIngestion ingestion,
    TimeProvider? clock = null) : IGraphRagMaintenance where TContext : DbContext
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public Task<GraphRagIngestionReceipt> ReingestChangedAsync(IEnumerable<GraphRagSource> sources, CancellationToken cancellationToken = default)
        => ingestion.IngestAsync(sources, cancellationToken);

    public Task<GraphRagRemovalReport> RemoveSourceAsync(string documentId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);
        return ResetAsync(new GraphRagResetScope(DocumentIds: [documentId]), cancellationToken);
    }

    public async Task<GraphRagRemovalReport> ResetAsync(GraphRagResetScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        await using var serviceScope = scopes.CreateAsyncScope();
        var db = serviceScope.ServiceProvider.GetRequiredService<TContext>();
        await RefuseForeignAsync(db, cancellationToken).ConfigureAwait(false);
        var runIds = scope.RunIds?.ToArray();
        var documentIds = scope.DocumentIds?.ToArray();
        var query = db.Set<GraphRagDocument>().AsNoTracking();
        if (runIds is not null) query = query.Where(d => runIds.Contains(d.RunId));
        if (documentIds is not null) query = query.Where(d => documentIds.Contains(d.Id));
        var selected = await query.OrderBy(d => d.Id).Select(d => new { d.Id, d.LengthBytes }).ToListAsync(cancellationToken).ConfigureAwait(false);

        var (documents, sections, chunks, edges) = await GraphRagRemoval.RemoveInBatchesAsync(db, GraphRagRemoval.Batches(selected.Select(d => (d.Id, d.LengthBytes))),
            documents: true, cancellationToken).ConfigureAwait(false);
        return new(documents, sections, chunks, edges, 0);
    }

    public async Task<GraphRagRemovalReport> RemoveOrphansAsync(CancellationToken cancellationToken = default)
    {
        await using var serviceScope = scopes.CreateAsyncScope();
        var db = serviceScope.ServiceProvider.GetRequiredService<TContext>();
        await RefuseForeignAsync(db, cancellationToken).ConfigureAwait(false);
        var layout = Layout(db);
        var orphaned = await db.Database.QueryCypherAsync(
            $"MATCH (n) WHERE (n:`{layout.ChunkLabel}` OR n:`{layout.SectionLabel}`) AND NOT EXISTS {{ MATCH (d:`{layout.DocumentLabel}`) WHERE d.Id = n.DocumentId }} "
            + "RETURN DISTINCT n.DocumentId AS documentId LIMIT $limit",
            new Dictionary<string, object?> { ["limit"] = 10_000L }, record => record["documentId"].As<string>(), maxRows: 10_000, cancellationToken)
            .ConfigureAwait(false);
        var (_, sections, chunks, edges) = await GraphRagRemoval.RemoveInBatchesAsync(db, GraphRagRemoval.Batches(orphaned.Select(id => (id, 0L))),
            documents: false, cancellationToken).ConfigureAwait(false);
        var now = _clock.GetUtcNow();
        var leases = await GraphRagRemoval.DeleteInPagesAsync(db.Set<GraphRagLease>().Where(l => l.ExpiresUtc < now).OrderBy(l => l.Key), cancellationToken).ConfigureAwait(false);
        var entities = 0;
        if (GraphRagRemoval.OntologyOf(db) is { } ontology)
        {
            var (removed, links) = await ontology.Runtime.RemoveOrphansAsync(db, cancellationToken).ConfigureAwait(false);
            (entities, edges) = (removed, edges + links);
        }
        return new(0, sections, chunks, edges, leases, entities);
    }

    public async Task<GraphRagHealthReport> CheckAsync(CancellationToken cancellationToken = default)
    {
        await using var serviceScope = scopes.CreateAsyncScope();
        var db = serviceScope.ServiceProvider.GetRequiredService<TContext>();
        var layout = Layout(db);
        var findings = new List<string>();
        var states = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var ontology = GraphRagRemoval.OntologyOf(db);
        var definitions = GraphRagSchema.Definitions(layout, embeddingSet, ontology);
        var names = definitions.Select(d => d.Name).ToArray();
        var observed = await db.Database.QueryCypherAsync("SHOW INDEXES YIELD name, state WHERE name IN $names RETURN name, state",
            new Dictionary<string, object?> { ["names"] = names }, record => (record["name"].As<string>(), record["state"].As<string>()),
            maxRows: names.Length, cancellationToken).ConfigureAwait(false);
        foreach (var (name, state) in observed) states[name] = state;
        // A uniqueness constraint shows as its backing index, under the constraint's name.
        if (definitions.Any(d => !states.ContainsKey(d.Name))) findings.Add("schema-missing");
        if (states.Values.Any(state => state != "ONLINE")) findings.Add("index-not-online");

        var documents = await db.Set<GraphRagDocument>().CountAsync(cancellationToken).ConfigureAwait(false);
        var sections = await db.Set<GraphRagSection>().CountAsync(cancellationToken).ConfigureAwait(false);
        var chunks = await db.Set<GraphRagChunk>().CountAsync(cancellationToken).ConfigureAwait(false);
        var missing = await db.Set<GraphRagChunk>().CountAsync(c => c.Embedding == null, cancellationToken).ConfigureAwait(false);
        var setId = embeddingSet.Id;
        var otherSets = await db.Set<GraphRagChunk>().CountAsync(c => c.Embedding != null && c.EmbeddingSetId != setId, cancellationToken).ConfigureAwait(false);
        if (missing > 0) findings.Add("missing-embedding");
        if (otherSets > 0) findings.Add("mixed-embedding-sets");

        var orphans = await db.Database.QueryCypherAsync(
            $"MATCH (n) WHERE (n:`{layout.ChunkLabel}` OR n:`{layout.SectionLabel}`) AND NOT EXISTS {{ MATCH (d:`{layout.DocumentLabel}`) WHERE d.Id = n.DocumentId }} "
            + $"RETURN sum(CASE WHEN n:`{layout.SectionLabel}` THEN 1 ELSE 0 END) AS sections, sum(CASE WHEN n:`{layout.ChunkLabel}` THEN 1 ELSE 0 END) AS chunks",
            new Dictionary<string, object?>(), record => ((int)record["sections"].As<long>(), (int)record["chunks"].As<long>()), maxRows: 1, cancellationToken)
            .ConfigureAwait(false);
        var (orphanSections, orphanChunks) = orphans.Count == 1 ? orphans[0] : (0, 0);
        if (orphanSections + orphanChunks > 0) findings.Add("orphans");

        // Readback: a chunk's text must be exactly as long, in UTF-8, as its span.
        var mismatches = await db.Database.QueryCypherAsync(
            $"MATCH (c:`{layout.ChunkLabel}`) WITH c, c.`{layout.ChunkTextProperty}` AS text "
            + "WHERE text IS NULL OR c.EndByte - c.StartByte < size(text) OR c.EndByte - c.StartByte > 4 * size(text) RETURN count(c) AS value",
            new Dictionary<string, object?>(), record => (int)record["value"].As<long>(), maxRows: 1, cancellationToken).ConfigureAwait(false);
        var readback = mismatches.Count == 1 ? mismatches[0] : 0;
        if (readback > 0) findings.Add("readback-mismatch");

        var now = _clock.GetUtcNow();
        var expired = await db.Set<GraphRagLease>().CountAsync(l => l.ExpiresUtc < now, cancellationToken).ConfigureAwait(false);
        if (expired > 0) findings.Add("expired-leases");
        var (orphanEntities, staleFacts) = (0, 0);
        if (ontology is not null)
        {
            orphanEntities = await ontology.Runtime.CountOrphansAsync(db, cancellationToken).ConfigureAwait(false);
            staleFacts = await ontology.Runtime.CountStaleFactsAsync(db, layout, cancellationToken).ConfigureAwait(false);
            if (orphanEntities > 0) findings.Add("orphan-entities");
            if (staleFacts > 0) findings.Add("stale-facts");
        }
        return new(documents, sections, chunks, missing, otherSets, orphanSections, orphanChunks, readback, expired, states, findings, orphanEntities, staleFacts);
    }

    // Reset and removal refuse a graph where the toolkit's labels hold nodes it didn't write: no run ID, or an unknown run.
    private static async Task RefuseForeignAsync(TContext db, CancellationToken cancellationToken)
    {
        var layout = Layout(db);
        var foreign = await db.Database.QueryCypherAsync(
            $"MATCH (n) WHERE n:`{layout.DocumentLabel}` OR n:`{layout.SectionLabel}` OR n:`{layout.ChunkLabel}` "
            + $"OPTIONAL MATCH (r:`{layout.RunLabel}`) WHERE r.Id = n.RunId WITH n, r WHERE n.RunId IS NULL OR r IS NULL RETURN count(n) AS value",
            new Dictionary<string, object?>(), record => record["value"].As<long>(), maxRows: 1, cancellationToken).ConfigureAwait(false);
        if (foreign.Count == 1 && foreign[0] > 0)
            throw new InvalidOperationException($"{foreign[0]} nodes under the GraphRAG labels have no toolkit run ID; nothing was removed. Use a layout with labels of their own.");
    }

    private static GraphRagLayout Layout(TContext db)
        => db.Model.FindAnnotation(Microsoft.EntityFrameworkCore.GraphRagModelBuilderExtensions.LayoutAnnotation)?.Value as GraphRagLayout
            ?? throw new InvalidOperationException($"{typeof(TContext).Name} doesn't map the GraphRAG model; call modelBuilder.AddGraphRag(...) in OnModelCreating.");
}
