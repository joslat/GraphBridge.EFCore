using System.Text;
using GraphBridge.EntityFrameworkCore.Infrastructure;
using GraphBridge.EntityFrameworkCore.Search;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Neo4j.Driver;

namespace GraphBridge.GraphRag;

/// <summary>
/// Retrieval (ADR 0052, section 4): each route searches its index through GraphBridge's typed search, ranks are fused by
/// chunk identity, the budget takes items in rank order, and neighbours and Cypher expansions become labelled context.
/// A failed or cancelled route fails the whole retrieval. Chunk text is read by projection; embeddings are never read.
/// With a scope (section 12), the routes run through <see cref="GraphRagScopedSearch"/>, and every item and neighbour is
/// checked against the scope again before it becomes evidence.
/// </summary>
internal sealed class GraphRagRetriever<TContext>(IServiceScopeFactory scopes, GraphRagEmbeddingSet embeddingSet, GraphRagOptions options,
    IEmbeddingGenerator<string, Embedding<float>>? generator) : IGraphRagRetriever where TContext : DbContext
{
    private const int MaxExpansionRows = 256;

    public async Task<GraphRagEvidence> RetrieveAsync(string question, GraphRagRetrievalOptions? retrieval = null, CancellationToken cancellationToken = default)
    {
        using var activity = GraphRagDiagnostics.Start(GraphRagTracing.RetrieveActivity);
        var evidence = await RetrieveCoreAsync(question, retrieval, cancellationToken).ConfigureAwait(false);
        if (activity is not null)
        {
            activity.SetTag("graphrag.routes", (retrieval ?? options.Retrieval).Routes.ToString());
            activity.SetTag("graphrag.items", evidence.Items.Count);
            activity.SetTag("graphrag.context_items", evidence.Context.Count);
            activity.SetTag("graphrag.omitted_items", evidence.OmittedItems);
            activity.SetTag("graphrag.utf8_bytes", evidence.Utf8Bytes);
            activity.SetTag("graphrag.embedding_set", evidence.Provenance.EmbeddingSetId);
            if (evidence.Scope is { } scope)
            {
                activity.SetTag("graphrag.scope", scope.Scope);
                activity.SetTag("graphrag.scope_chunks", scope.Chunks);
                activity.SetTag("graphrag.scope_strategies", string.Join(',', scope.Routes.Select(r => $"{r.Route}:{r.Strategy}")));
            }
        }
        return evidence;
    }

    private async Task<GraphRagEvidence> RetrieveCoreAsync(string question, GraphRagRetrievalOptions? retrieval, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(question);
        retrieval ??= options.Retrieval;
        if (retrieval.History is { Count: > 0 })
            throw new ArgumentException("Retrieval takes one standalone question; IGraphRagAnswers rewrites a follow-up with its history first.", nameof(retrieval));
        retrieval.Validate();
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();
        var layout = db.Model.FindAnnotation(Microsoft.EntityFrameworkCore.GraphRagModelBuilderExtensions.LayoutAnnotation)?.Value as GraphRagLayout
            ?? throw new InvalidOperationException($"{typeof(TContext).Name} doesn't map the GraphRAG model; call modelBuilder.AddGraphRag(...) in OnModelCreating.");

        var lists = new List<(GraphRagRoutes, IReadOnlyList<GraphRagCandidate>)>();
        var indexes = new List<string>();
        string? server = null;
        string? similarity = null;
        GraphRagResolvedScope? resolved = null;
        GraphRagScopeTrace? trace = null;
        var scopeLimits = new List<GraphRagLimit>();
        if (retrieval.Scope is { } requested)
        {
            var extension = Extension(db, "Scoped retrieval");
            var search = new GraphRagScopedSearch(extension.Driver, extension.DatabaseName, layout);
            resolved = await search.ResolveAsync(requested, cancellationToken).ConfigureAwait(false);
            scopeLimits.AddRange(resolved.Limits);
            var (documents, scoped, all) = resolved.IsEmpty ? (0, 0, 0) : await search.CountAsync(resolved, cancellationToken).ConfigureAwait(false);
            var routes = new List<GraphRagScopeRoute>();
            var k = retrieval.CandidatesPerRoute;
            if (scoped > 0 && retrieval.Routes.HasFlag(GraphRagRoutes.Vector))
            {
                var vector = await QuestionVectorAsync(question, cancellationToken).ConfigureAwait(false);
                IReadOnlyList<GraphRagCandidate> hits;
                if (scoped <= retrieval.ScopeExactChunks)
                {
                    hits = await search.ExactVectorAsync(resolved, vector, embeddingSet.Similarity, k, cancellationToken).ConfigureAwait(false);
                    routes.Add(new(GraphRagRoutes.Vector, "exact", scoped, hits.Count));
                }
                else
                {
                    var index = layout.VectorIndexFor(embeddingSet);
                    var fetch = GraphRagScopedSearch.Fetch(k, scoped, all);
                    hits = await search.OverFetchVectorAsync(resolved, index, vector, fetch, k, cancellationToken).ConfigureAwait(false);
                    routes.Add(new(GraphRagRoutes.Vector, "over-fetch", fetch, hits.Count));
                    if (hits.Count < k && fetch < all) scopeLimits.Add(new("VectorScopeOverFetch", fetch));
                    indexes.Add(index);
                }
                lists.Add((GraphRagRoutes.Vector, hits));
                similarity = embeddingSet.Similarity.ToString().ToUpperInvariant();
            }
            if (scoped > 0 && retrieval.Routes.HasFlag(GraphRagRoutes.FullText) && LuceneTerms(question) is { Length: > 0 } scopedTerms)
            {
                var index = GraphRagSchema.FullTextIndexName(layout);
                var fetch = GraphRagScopedSearch.Fetch(k, scoped, all);
                var hits = await search.OverFetchFullTextAsync(resolved, index, scopedTerms, fetch, k, cancellationToken).ConfigureAwait(false);
                routes.Add(new(GraphRagRoutes.FullText, "over-fetch", fetch, hits.Count));
                if (hits.Count < k && fetch < all) scopeLimits.Add(new("FullTextScopeOverFetch", fetch));
                lists.Add((GraphRagRoutes.FullText, hits));
                indexes.Add(index);
            }
            trace = new(resolved.Description, documents, scoped, routes);
            server = search.ServerVersion;
        }
        else
        {
            if (retrieval.Routes.HasFlag(GraphRagRoutes.Vector))
            {
                var index = layout.VectorIndexFor(embeddingSet);
                var hits = await db.Database.QueryVectorNodesAsync(index, await QuestionVectorAsync(question, cancellationToken).ConfigureAwait(false),
                    node => node[layout.ChunkIdProperty].As<string>(), retrieval.CandidatesPerRoute, cancellationToken).ConfigureAwait(false);
                lists.Add((GraphRagRoutes.Vector, hits.Results.Select(r => new GraphRagCandidate(r.Value, r.Score)).ToArray()));
                indexes.Add(index);
                (server, similarity) = (hits.Provenance.ServerVersion, hits.Provenance.SimilarityFunction);
            }
            if (retrieval.Routes.HasFlag(GraphRagRoutes.FullText) && LuceneTerms(question) is { Length: > 0 } terms)
            {
                var index = GraphRagSchema.FullTextIndexName(layout);
                var hits = await db.Database.QueryFullTextNodesAsync(index, terms, node => node[layout.ChunkIdProperty].As<string>(),
                    retrieval.CandidatesPerRoute, cancellationToken).ConfigureAwait(false);
                lists.Add((GraphRagRoutes.FullText, hits.Results.Select(r => new GraphRagCandidate(r.Value, r.Score)).ToArray()));
                indexes.Add(index);
                server ??= hits.Provenance.ServerVersion;
            }
        }

        var fused = GraphRagFusion.Fuse(lists);
        var ids = fused.Select(f => f.ChunkId).ToArray();
        var chunks = (await db.Set<GraphRagChunk>().AsNoTracking().Where(c => ids.Contains(c.Id))
                .Select(c => new { c.Id, c.DocumentId, c.Text, c.StartByte, c.EndByte, c.Order, c.Grounding })
                .ToListAsync(cancellationToken).ConfigureAwait(false))
            .ToDictionary(c => c.Id, StringComparer.Ordinal);
        // A chunk removed between the search and this read is simply not evidence; under a scope, neither is one outside it.
        var ranked = fused.Where(f => chunks.TryGetValue(f.ChunkId, out var c) && (resolved is null || resolved.Contains(c.DocumentId, c.StartByte, c.EndByte)))
            .Select(f => (new GraphRagChunkText(f.ChunkId, chunks[f.ChunkId].DocumentId, chunks[f.ChunkId].Text, chunks[f.ChunkId].StartByte, chunks[f.ChunkId].EndByte,
                chunks[f.ChunkId].Grounding == "chunk" ? GraphRagGrounding.Chunk : GraphRagGrounding.Source), f.Score, f.Routes))
            .ToArray();
        var (budgeted, bytes, omitted, limits) = GraphRagFusion.Budget(ranked, retrieval.MaxItems, retrieval.MaxUtf8Bytes);
        var items = await WithDocumentAsync(db, budgeted, cancellationToken).ConfigureAwait(false);

        var context = new List<GraphRagContextItem>();
        if (retrieval.NeighbourChunks > 0)
            context.AddRange(await NeighboursAsync(db, items.Select(i => (i.ChunkId, chunks[i.ChunkId].DocumentId, chunks[i.ChunkId].Order)).ToArray(),
                retrieval.NeighbourChunks, resolved, cancellationToken).ConfigureAwait(false));
        var expansionLimits = new List<GraphRagLimit>();
        if (retrieval.CypherExpansion is { } cypher)
            context.AddRange(await ExpandAsync(db, cypher, retrieval.CypherExpansionParameters, items.Select(i => i.ChunkId).ToArray(), resolved?.DocumentIds,
                expansionLimits, cancellationToken).ConfigureAwait(false));

        return new(question, items, context, bytes, omitted, [.. limits, .. expansionLimits, .. scopeLimits],
            new GraphRagProvenance(server ?? "", embeddingSet.Id, indexes, similarity)) { Scope = trace };
    }

    // What each item's document adds: for a paged document (ADR 0052, section 14), the pages the item spans; for derived text
    // (section 21), where the text came from.
    private static async Task<IReadOnlyList<GraphRagEvidenceItem>> WithDocumentAsync(TContext db, IReadOnlyList<GraphRagEvidenceItem> items,
        CancellationToken cancellationToken)
    {
        var documentIds = items.Select(i => i.DocumentId).Distinct(StringComparer.Ordinal).ToArray();
        if (documentIds.Length == 0) return items;
        var documents = (await db.Set<GraphRagDocument>().AsNoTracking().Where(d => documentIds.Contains(d.Id) && (d.PageStarts != null || d.TextOrigin != null))
                .Select(d => new { d.Id, d.PageStarts, d.TextOrigin }).ToListAsync(cancellationToken).ConfigureAwait(false))
            .ToDictionary(d => d.Id, d => (Pages: d.PageStarts is { } starts ? GraphRagPages.Parse(starts) : null, d.TextOrigin), StringComparer.Ordinal);
        if (documents.Count == 0) return items;
        return [.. items.Select(i => documents.TryGetValue(i.DocumentId, out var document)
            ? i with
            {
                Pages = document.Pages is { } starts && i.Grounding == GraphRagGrounding.Source ? GraphRagPages.Spanning(starts, i.StartByte, i.EndByte) : i.Pages,
                TextOrigin = document.TextOrigin
            }
            : i)];
    }

    // The question's embedding, checked against the embedding set and normalised when the set is.
    private async Task<double[]> QuestionVectorAsync(string question, CancellationToken cancellationToken)
    {
        if (generator is null) throw new InvalidOperationException("The vector route needs an IEmbeddingGenerator<string, Embedding<float>> in the container.");
        var embedded = await generator.GenerateAsync([question], cancellationToken: cancellationToken).ConfigureAwait(false);
        var vector = embedded.Count == 1 ? embedded[0].Vector : throw new InvalidOperationException("The embedding generator returned no vector for the question.");
        if (vector.Length != embeddingSet.Dimensions)
            throw new InvalidOperationException($"The question's embedding has {vector.Length} dimensions; the embedding set has {embeddingSet.Dimensions}.");
        return Normalised(vector.Span);
    }

    private static Neo4jDbContextOptionsExtension Extension(TContext db, string purpose)
        => db.GetService<IDbContextOptions>().FindExtension<Neo4jDbContextOptionsExtension>()
            ?? throw new InvalidOperationException($"{purpose} needs a context configured with UseNeo4j.");

    // Chunks next to each item in the same document, by order, which is the NEXT_CHUNK chain. Bounded: at most 2n per item.
    // Under a scope, a neighbour outside it is left out.
    private static async Task<IReadOnlyList<GraphRagContextItem>> NeighboursAsync(TContext db,
        IReadOnlyList<(string ChunkId, string DocumentId, int Order)> items, int distance, GraphRagResolvedScope? scope, CancellationToken cancellationToken)
    {
        var cited = items.Select(i => i.ChunkId).ToHashSet(StringComparer.Ordinal);
        var result = new List<GraphRagContextItem>();
        foreach (var document in items.GroupBy(i => i.DocumentId, StringComparer.Ordinal))
        {
            var orders = document.SelectMany(i => Enumerable.Range(i.Order - distance, 2 * distance + 1)).Where(o => o >= 0).Distinct().ToArray();
            var documentId = document.Key;
            var nearby = await db.Set<GraphRagChunk>().AsNoTracking().Where(c => c.DocumentId == documentId && orders.Contains(c.Order))
                .OrderBy(c => c.Order).Select(c => new { c.Id, c.Text, c.Order, c.StartByte, c.EndByte }).ToListAsync(cancellationToken).ConfigureAwait(false);
            foreach (var chunk in nearby.Where(c => !cited.Contains(c.Id) && (scope is null || scope.Contains(documentId, c.StartByte, c.EndByte))))
            {
                var of = document.OrderBy(i => Math.Abs(i.Order - chunk.Order)).ThenBy(i => i.ChunkId, StringComparer.Ordinal).First();
                result.Add(new(chunk.Id, chunk.Text, $"neighbour of {of.ChunkId}"));
            }
        }
        return result;
    }

    // The application's Cypher, in a read transaction, with $chunkIds and $scopeDocumentIds bound; at most 256 rows, and a limit reached is reported.
    private static async Task<IReadOnlyList<GraphRagContextItem>> ExpandAsync(TContext db, string cypher, IReadOnlyDictionary<string, object?>? parameters,
        string[] chunkIds, string[]? scopeDocumentIds, List<GraphRagLimit> limits, CancellationToken cancellationToken)
    {
        var extension = Extension(db, "Cypher expansion");
        var values = new Dictionary<string, object?>(parameters ?? new Dictionary<string, object?>(), StringComparer.Ordinal)
        {
            ["chunkIds"] = chunkIds, ["scopeDocumentIds"] = scopeDocumentIds
        };
        await using var session = extension.Driver.AsyncSession(o => o.WithDatabase(extension.DatabaseName).WithDefaultAccessMode(AccessMode.Read));
        // The driver may retry a read transaction, so the limit is taken from the attempt that returned.
        var (rows, truncated) = await session.ExecuteReadAsync(async transaction =>
        {
            var cursor = await transaction.RunAsync(cypher, values).ConfigureAwait(false);
            var read = new List<GraphRagContextItem>();
            var more = false;
            while (await cursor.FetchAsync().ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (read.Count == MaxExpansionRows) { more = true; break; }
                var record = cursor.Current;
                if (!record.Keys.Contains("chunkId") || !record.Keys.Contains("text"))
                    throw new InvalidOperationException("A Cypher expansion must return the columns chunkId and text.");
                read.Add(new(record["chunkId"].As<string>(), record["text"].As<string>(), "cypher expansion"));
            }
            await cursor.ConsumeAsync().ConfigureAwait(false);
            return (read, more);
        }).ConfigureAwait(false);
        if (truncated) limits.Add(new("CypherExpansionRows", MaxExpansionRows));
        return rows;
    }

    private double[] Normalised(ReadOnlySpan<float> vector)
    {
        var result = new double[vector.Length];
        double norm = 0;
        for (var i = 0; i < vector.Length; i++) { result[i] = vector[i]; norm += result[i] * result[i]; }
        if (embeddingSet.Normalize && norm > 0)
            for (var i = 0; i < result.Length; i++) result[i] /= Math.Sqrt(norm);
        return result;
    }

    // The question's words as plain Lucene terms: syntax characters are escaped, so a question can't be a query.
    internal static string LuceneTerms(string question)
    {
        var text = new StringBuilder(question.Length);
        foreach (var c in question)
        {
            if (char.IsWhiteSpace(c)) { if (text.Length > 0 && text[^1] != ' ') text.Append(' '); continue; }
            if (c is '+' or '-' or '&' or '|' or '!' or '(' or ')' or '{' or '}' or '[' or ']' or '^' or '"' or '~' or '*' or '?' or ':' or '\\' or '/')
                text.Append('\\');
            text.Append(c);
        }
        var terms = text.ToString().Trim();
        // Bare AND, OR and NOT are operators even when escaped characters aren't; lower-case keeps them as words.
        return string.Join(' ', terms.Split(' ').Select(t => t is "AND" or "OR" or "NOT" ? t.ToLowerInvariant() : t));
    }
}
