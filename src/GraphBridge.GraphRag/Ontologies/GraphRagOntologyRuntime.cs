using GraphBridge.GraphRag.Ontologies;
using Microsoft.EntityFrameworkCore;
using Neo4j.Driver;

namespace GraphBridge.GraphRag;

// ADR 0052, section 7: typed writes and deletes for each ontology class, fact and quote type, through EF. Each class gets
// a closed generic helper, built once per ontology, so the orchestration below works on the ontology's descriptions.
internal sealed class GraphRagOntologyRuntime(GraphRagOntology ontology)
{
    private const int UpsertPage = 10_000;
    /// <summary>Facts listing the documents of one removal, at most. Settable only so a test reaches it with a few facts.</summary>
    internal static int MaxListedFacts { get; set; } = 100_000;

    public GraphRagOntology Ontology { get; } = ontology;

    public IReadOnlyDictionary<GraphRagOntologyClass, IEntityOps> Entities { get; } = ontology.Classes.ToDictionary(c => c,
        c => (IEntityOps)Activator.CreateInstance(typeof(EntityOps<>).MakeGenericType(c.ClrType))!);

    public IReadOnlyDictionary<GraphRagOntologyRelationship, IFactOps> Facts { get; } = ontology.Relationships.ToDictionary(r => r,
        r => (IFactOps)Activator.CreateInstance(typeof(FactOps<>).MakeGenericType(r.ClrType))!);

    public IReadOnlyDictionary<GraphRagOntologyQuote, IQuoteOps> Quotes { get; } = ontology.Quotes.ToDictionary(q => q,
        q => (IQuoteOps)Activator.CreateInstance(typeof(QuoteOps<>).MakeGenericType(q.ClrType))!);

    /// <summary>
    /// Writes one document's graph inside the document's transaction. Things are created if missing; their mentions' preflight
    /// then locks every thing the document mentions, in ID order, so the aliases and facts read next can't be changed by
    /// another run before this one commits.
    /// </summary>
    public async Task<(int Entities, int Mentions, int Facts, int Quotes)> WriteAsync(DbContext db, GraphRagExtractedGraph graph, string documentId,
        CancellationToken cancellationToken)
    {
        foreach (var (type, entities) in graph.Entities)
            await Entities[type].CreateMissingAsync(db, entities.Values, cancellationToken).ConfigureAwait(false);
        foreach (var (type, mentions) in graph.Mentions)
            await Entities[type].UpsertMentionsAsync(db, mentions.Values, cancellationToken).ConfigureAwait(false);

        foreach (var (type, entities) in graph.Entities.Where(e => e.Key.Aliases))
        {
            var stored = await Entities[type].AliasesAsync(db, entities.Keys.ToArray(), cancellationToken).ConfigureAwait(false);
            var changed = new List<GraphRagEntity>();
            foreach (var entity in entities.Values)
            {
                if (!stored.TryGetValue(entity.Id, out var existing)) continue;
                var union = Union(existing, entity.Aliases);
                if (union == existing) continue;
                entity.Aliases = union;
                changed.Add(entity);
            }
            if (changed.Count > 0) await Entities[type].UpdateAliasesAsync(db, changed, cancellationToken).ConfigureAwait(false);
        }

        foreach (var (type, facts) in graph.Facts)
        {
            var ops = Facts[type];
            await ops.CreateMissingAsync(db, facts.Values, cancellationToken).ConfigureAwait(false);
            var stored = await ops.DocumentIdsAsync(db, facts.Keys.ToArray(), cancellationToken).ConfigureAwait(false);
            var changed = new List<GraphRagFact>();
            foreach (var fact in facts.Values)
            {
                if (!stored.TryGetValue((fact.FromId, fact.ToId), out var ids) || Lines(ids).Contains(documentId)) continue;
                fact.DocumentIds = Union(ids, documentId)!;
                changed.Add(fact);
            }
            if (changed.Count > 0) await ops.UpdateDocumentIdsAsync(db, changed, cancellationToken).ConfigureAwait(false);
        }

        foreach (var (type, quotes) in graph.Quotes)
            await Quotes[type].UpsertAsync(db, quotes.Values, cancellationToken).ConfigureAwait(false);
        return (graph.Entities.Sum(e => e.Value.Count), graph.Mentions.Sum(m => m.Value.Count), graph.Facts.Sum(f => f.Value.Count),
            graph.Quotes.Sum(q => q.Value.Count));
    }

    /// <summary>
    /// Removes a document's extraction inside its transaction, before its chunks: the mentions and quotes in its chunks, then
    /// the document from every fact that lists it. Those facts are locked first, then re-read, so a concurrent run adding
    /// its own document can't be lost; facts left listing nothing are deleted.
    /// </summary>
    public async Task<int> RemoveAsync(DbContext db, GraphRagLayout layout, string[] documentIds, IReadOnlyList<string> chunkIds, CancellationToken cancellationToken)
    {
        var removed = 0;
        foreach (var page in chunkIds.Chunk(GraphRagRemoval.PageSize))
        {
            foreach (var ops in Entities.Values) removed += await ops.DeleteMentionsAsync(db, page, cancellationToken).ConfigureAwait(false);
            foreach (var ops in Quotes.Values) removed += await ops.DeleteByChunksAsync(db, page, cancellationToken).ConfigureAwait(false);
        }
        if (Facts.Count == 0) return removed;

        var types = Ontology.Relationships.Select(r => r.Type).Distinct(StringComparer.Ordinal).ToArray();
        // Workaround for GR-06 (string functions in LINQ): the facts that list any of the documents, across every fact type, in one
        // bounded read. A listing that reaches the bound is refused, so no fact keeps a removed document.
        var listing = await db.Database.QueryCypherAsync(
            "MATCH (a)-[f]->(b) WHERE type(f) IN $types AND any(id IN $documentIds WHERE f.DocumentIds CONTAINS id) "
            + "RETURN type(f) AS type, labels(a) AS fromLabels, labels(b) AS toLabels, a.Id AS fromId, b.Id AS toId LIMIT $limit",
            new Dictionary<string, object?> { ["types"] = types, ["documentIds"] = documentIds, ["limit"] = (long)MaxListedFacts + 1 },
            record => (Type: record["type"].As<string>(), From: Kind(record["fromLabels"].As<List<string>>()), To: Kind(record["toLabels"].As<List<string>>()),
                FromId: record["fromId"].As<string>(), ToId: record["toId"].As<string>()),
            maxRows: MaxListedFacts + 1, cancellationToken).ConfigureAwait(false);
        if (listing.Count > MaxListedFacts)
            throw new GraphRagRemoval.FactListingLimitException($"More than {MaxListedFacts:N0} facts list the documents being removed; nothing of them was removed.");
        var removing = documentIds.ToHashSet(StringComparer.Ordinal);
        foreach (var group in listing.GroupBy(row => (row.Type, row.From, row.To)))
        {
            var type = Ontology.Relationships.FirstOrDefault(r => r.Type == group.Key.Type && r.FromKind == group.Key.From && r.ToKind == group.Key.To);
            if (type is null) continue;
            var ops = Facts[type];
            var pairs = group.Select(row => (row.FromId, row.ToId)).Distinct().ToArray();
            // The facts exist, so this upsert creates nothing; its preflight takes the locks.
            await ops.CreateMissingAsync(db, pairs.Select(p => ops.New(p.FromId, p.ToId, documentIds[0])), cancellationToken).ConfigureAwait(false);
            var stored = await ops.DocumentIdsAsync(db, pairs, cancellationToken).ConfigureAwait(false);
            var kept = new List<GraphRagFact>();
            // A fact that lists only removed documents goes; its stored list, exactly, is what the delete matches.
            var gone = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (pair, ids) in stored)
            {
                var rest = Lines(ids).Where(id => !removing.Contains(id)).ToArray();
                if (rest.Length > 0) kept.Add(ops.New(pair.From, pair.To, string.Join('\n', rest)));
                else gone.Add(ids);
            }
            if (gone.Count > 0) removed += await ops.DeleteListingOnlyAsync(db, [.. gone], cancellationToken).ConfigureAwait(false);
            if (kept.Count > 0) await ops.UpdateDocumentIdsAsync(db, kept, cancellationToken).ConfigureAwait(false);
        }
        return removed;

        // Stored labels back to the kinds the relationships are described with.
        string Kind(List<string> labels) => labels.Contains(layout.DocumentLabel) ? GraphRagOntologyBuilder.DocumentKind
            : Ontology.Classes.FirstOrDefault(c => labels.Contains(c.Label))?.Kind ?? "";
    }

    /// <summary>Things no mention points to, per class, at most 10,000 at a time: their facts and quotes first, then the things.</summary>
    public async Task<(int Entities, int Edges)> RemoveOrphansAsync(DbContext db, CancellationToken cancellationToken)
    {
        var (entities, edges) = (0, 0);
        foreach (var (type, ops) in Entities)
        {
            var ids = (await OrphansAsync(db, type, cancellationToken).ConfigureAwait(false)).ToArray();
            if (ids.Length == 0) continue;
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            foreach (var page in ids.Chunk(GraphRagRemoval.PageSize))
            {
                foreach (var (fact, factOps) in Facts.Where(f => f.Key.From == type.ClrType || f.Key.To == type.ClrType))
                    edges += await factOps.DeleteTouchingAsync(db, page, fact.From == type.ClrType, fact.To == type.ClrType, cancellationToken).ConfigureAwait(false);
                foreach (var (_, quoteOps) in Quotes.Where(q => q.Key.Speaker == type.ClrType))
                    edges += await quoteOps.DeleteBySpeakersAsync(db, page, cancellationToken).ConfigureAwait(false);
                entities += await ops.DeleteAsync(db, page, cancellationToken).ConfigureAwait(false);
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        return (entities, edges);
    }

    public async Task<int> CountOrphansAsync(DbContext db, CancellationToken cancellationToken)
    {
        var count = 0;
        foreach (var type in Entities.Keys) count += (await OrphansAsync(db, type, cancellationToken).ConfigureAwait(false)).Count;
        return count;
    }

    /// <summary>Facts that list a document that doesn't exist.</summary>
    public async Task<int> CountStaleFactsAsync(DbContext db, GraphRagLayout layout, CancellationToken cancellationToken)
    {
        if (Facts.Count == 0) return 0;
        var types = Ontology.Relationships.Select(r => r.Type).Distinct(StringComparer.Ordinal).ToArray();
        // Workaround for GR-06 (correlated subqueries in LINQ): facts whose listed documents don't all exist.
        var rows = await db.Database.QueryCypherAsync(
            $"MATCH ()-[f]->() WHERE type(f) IN $types AND any(id IN split(f.DocumentIds, '\\n') WHERE NOT EXISTS {{ MATCH (d:`{layout.DocumentLabel}`) WHERE d.Id = id }}) "
            + "RETURN count(f) AS value",
            new Dictionary<string, object?> { ["types"] = types }, record => (int)record["value"].As<long>(), maxRows: 1, cancellationToken).ConfigureAwait(false);
        return rows.Count == 1 ? rows[0] : 0;
    }

    // Workaround for GR-06 (correlated subqueries in LINQ): things no mention points to.
    private static Task<IReadOnlyList<string>> OrphansAsync(DbContext db, GraphRagOntologyClass type, CancellationToken cancellationToken)
        => db.Database.QueryCypherAsync($"MATCH (e:`{type.Label}`) WHERE NOT EXISTS {{ MATCH (e)-[:`{type.MentionType}`]->() }} RETURN e.Id AS id LIMIT $limit",
            new Dictionary<string, object?> { ["limit"] = 10_000L }, record => record["id"].As<string>(), maxRows: 10_000, cancellationToken);

    internal static string[] Lines(string? text) => string.IsNullOrEmpty(text) ? [] : text.Split('\n');

    /// <summary>The sorted, distinct union of two line lists; null when empty.</summary>
    internal static string? Union(string? left, string? right)
    {
        var set = new SortedSet<string>(Lines(left).Concat(Lines(right)).Where(line => line.Length > 0), StringComparer.Ordinal);
        return set.Count == 0 ? null : string.Join('\n', set);
    }

    internal interface IEntityOps
    {
        Task CreateMissingAsync(DbContext db, IEnumerable<GraphRagEntity> entities, CancellationToken cancellationToken);
        Task UpsertMentionsAsync(DbContext db, IEnumerable<GraphRagMention> mentions, CancellationToken cancellationToken);
        Task<Dictionary<string, string?>> AliasesAsync(DbContext db, string[] ids, CancellationToken cancellationToken);
        Task UpdateAliasesAsync(DbContext db, IEnumerable<GraphRagEntity> entities, CancellationToken cancellationToken);
        Task<int> DeleteMentionsAsync(DbContext db, string[] chunkIds, CancellationToken cancellationToken);
        Task<int> DeleteAsync(DbContext db, string[] ids, CancellationToken cancellationToken);
    }

    internal interface IFactOps
    {
        GraphRagFact New(string fromId, string toId, string documentIds);
        Task CreateMissingAsync(DbContext db, IEnumerable<GraphRagFact> facts, CancellationToken cancellationToken);
        Task<Dictionary<(string From, string To), string>> DocumentIdsAsync(DbContext db, IReadOnlyCollection<(string From, string To)> pairs, CancellationToken cancellationToken);
        Task UpdateDocumentIdsAsync(DbContext db, IEnumerable<GraphRagFact> facts, CancellationToken cancellationToken);
        Task<int> DeleteListingOnlyAsync(DbContext db, string[] documentIdLists, CancellationToken cancellationToken);
        Task<int> DeleteTouchingAsync(DbContext db, string[] ids, bool from, bool to, CancellationToken cancellationToken);
    }

    internal interface IQuoteOps
    {
        Task UpsertAsync(DbContext db, IEnumerable<GraphRagQuote> quotes, CancellationToken cancellationToken);
        Task<int> DeleteByChunksAsync(DbContext db, string[] chunkIds, CancellationToken cancellationToken);
        Task<int> DeleteBySpeakersAsync(DbContext db, string[] ids, CancellationToken cancellationToken);
    }

    // Rows are sorted by the IDs whose nodes the statement locks, so concurrent runs lock shared things in the same order.
    private sealed class EntityOps<TEntity> : IEntityOps where TEntity : GraphRagEntity
    {
        public async Task CreateMissingAsync(DbContext db, IEnumerable<GraphRagEntity> entities, CancellationToken cancellationToken)
        {
            foreach (var page in entities.Cast<TEntity>().OrderBy(e => e.Id, StringComparer.Ordinal).Chunk(UpsertPage))
                await db.Set<TEntity>().ExecuteUpsertAsync(page, e => new { }, cancellationToken).ConfigureAwait(false);
        }

        public async Task UpsertMentionsAsync(DbContext db, IEnumerable<GraphRagMention> mentions, CancellationToken cancellationToken)
        {
            foreach (var page in mentions.Cast<GraphRagMention<TEntity>>().OrderBy(m => m.EntityId, StringComparer.Ordinal)
                         .ThenBy(m => m.Id, StringComparer.Ordinal).Chunk(UpsertPage))
                await db.Set<GraphRagMention<TEntity>>().ExecuteUpsertAsync(page, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        public async Task<Dictionary<string, string?>> AliasesAsync(DbContext db, string[] ids, CancellationToken cancellationToken)
        {
            var result = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var page in ids.Chunk(1_000))
                foreach (var entity in await db.Set<TEntity>().AsNoTracking().Where(e => page.Contains(e.Id)).ToListAsync(cancellationToken).ConfigureAwait(false))
                    result[entity.Id] = entity.Aliases;
            return result;
        }

        public async Task UpdateAliasesAsync(DbContext db, IEnumerable<GraphRagEntity> entities, CancellationToken cancellationToken)
        {
            foreach (var page in entities.Cast<TEntity>().OrderBy(e => e.Id, StringComparer.Ordinal).Chunk(UpsertPage))
                await db.Set<TEntity>().ExecuteUpsertAsync(page, e => e.Aliases, cancellationToken).ConfigureAwait(false);
        }

        public Task<int> DeleteMentionsAsync(DbContext db, string[] chunkIds, CancellationToken cancellationToken)
            => GraphRagRemoval.DeleteInPagesAsync(db.Set<GraphRagMention<TEntity>>().Where(m => chunkIds.Contains(m.ChunkId)).OrderBy(m => m.Id), cancellationToken);

        public Task<int> DeleteAsync(DbContext db, string[] ids, CancellationToken cancellationToken)
            => GraphRagRemoval.DeleteInPagesAsync(db.Set<TEntity>().Where(e => ids.Contains(e.Id)).OrderBy(e => e.Id), cancellationToken);

    }

    private sealed class FactOps<TFact> : IFactOps where TFact : GraphRagFact, new()
    {
        public GraphRagFact New(string fromId, string toId, string documentIds) => new TFact { FromId = fromId, ToId = toId, DocumentIds = documentIds };

        public async Task CreateMissingAsync(DbContext db, IEnumerable<GraphRagFact> facts, CancellationToken cancellationToken)
        {
            foreach (var page in facts.Cast<TFact>().OrderBy(f => f.FromId, StringComparer.Ordinal).ThenBy(f => f.ToId, StringComparer.Ordinal).Chunk(UpsertPage))
                await db.Set<TFact>().ExecuteUpsertAsync(page, f => new { }, cancellationToken).ConfigureAwait(false);
        }

        public async Task<Dictionary<(string From, string To), string>> DocumentIdsAsync(DbContext db, IReadOnlyCollection<(string From, string To)> pairs,
            CancellationToken cancellationToken)
        {
            var wanted = pairs.ToHashSet();
            var result = new Dictionary<(string From, string To), string>();
            foreach (var page in pairs.Select(p => p.From).Distinct(StringComparer.Ordinal).Chunk(1_000))
            {
                var tos = pairs.Where(p => page.Contains(p.From)).Select(p => p.To).Distinct(StringComparer.Ordinal).ToArray();
                var rows = await db.Set<TFact>().AsNoTracking().Where(f => page.Contains(f.FromId) && tos.Contains(f.ToId))
                    .ToListAsync(cancellationToken).ConfigureAwait(false);
                foreach (var row in rows)
                    if (wanted.Contains((row.FromId, row.ToId))) result[(row.FromId, row.ToId)] = row.DocumentIds;
            }
            return result;
        }

        public async Task UpdateDocumentIdsAsync(DbContext db, IEnumerable<GraphRagFact> facts, CancellationToken cancellationToken)
        {
            foreach (var page in facts.Cast<TFact>().OrderBy(f => f.FromId, StringComparer.Ordinal).ThenBy(f => f.ToId, StringComparer.Ordinal).Chunk(UpsertPage))
                await db.Set<TFact>().ExecuteUpsertAsync(page, f => f.DocumentIds, cancellationToken).ConfigureAwait(false);
        }

        public async Task<int> DeleteListingOnlyAsync(DbContext db, string[] documentIdLists, CancellationToken cancellationToken)
        {
            var deleted = 0;
            foreach (var page in documentIdLists.Chunk(1_000))
                deleted += await GraphRagRemoval.DeleteInPagesAsync(db.Set<TFact>().Where(f => page.Contains(f.DocumentIds)).OrderBy(f => f.FromId).ThenBy(f => f.ToId),
                    cancellationToken).ConfigureAwait(false);
            return deleted;
        }

        public Task<int> DeleteTouchingAsync(DbContext db, string[] ids, bool from, bool to, CancellationToken cancellationToken)
            => GraphRagRemoval.DeleteInPagesAsync((from && to ? db.Set<TFact>().Where(f => ids.Contains(f.FromId) || ids.Contains(f.ToId))
                : from ? db.Set<TFact>().Where(f => ids.Contains(f.FromId)) : db.Set<TFact>().Where(f => ids.Contains(f.ToId)))
                .OrderBy(f => f.FromId).ThenBy(f => f.ToId), cancellationToken);

    }

    private sealed class QuoteOps<TQuote> : IQuoteOps where TQuote : GraphRagQuote
    {
        public async Task UpsertAsync(DbContext db, IEnumerable<GraphRagQuote> quotes, CancellationToken cancellationToken)
        {
            foreach (var page in quotes.Cast<TQuote>().OrderBy(q => q.SpeakerId, StringComparer.Ordinal).ThenBy(q => q.Id, StringComparer.Ordinal).Chunk(UpsertPage))
                await db.Set<TQuote>().ExecuteUpsertAsync(page, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        public Task<int> DeleteByChunksAsync(DbContext db, string[] chunkIds, CancellationToken cancellationToken)
            => GraphRagRemoval.DeleteInPagesAsync(db.Set<TQuote>().Where(q => chunkIds.Contains(q.ChunkId)).OrderBy(q => q.Id), cancellationToken);

        public Task<int> DeleteBySpeakersAsync(DbContext db, string[] ids, CancellationToken cancellationToken)
            => GraphRagRemoval.DeleteInPagesAsync(db.Set<TQuote>().Where(q => ids.Contains(q.SpeakerId)).OrderBy(q => q.Id), cancellationToken);

    }
}
