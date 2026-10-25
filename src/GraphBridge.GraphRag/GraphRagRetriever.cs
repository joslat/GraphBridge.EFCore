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
        }
        return evidence;
    }

    private async Task<GraphRagEvidence> RetrieveCoreAsync(string question, GraphRagRetrievalOptions? retrieval, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(question);
        retrieval ??= options.Retrieval;
        retrieval.Validate();
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();
        var layout = db.Model.FindAnnotation(Microsoft.EntityFrameworkCore.GraphRagModelBuilderExtensions.LayoutAnnotation)?.Value as GraphRagLayout
            ?? throw new InvalidOperationException($"{typeof(TContext).Name} doesn't map the GraphRAG model; call modelBuilder.AddGraphRag(...) in OnModelCreating.");

        var lists = new List<(GraphRagRoutes, IReadOnlyList<GraphRagCandidate>)>();
        var indexes = new List<string>();
        string? server = null;
        string? similarity = null;
        if (retrieval.Routes.HasFlag(GraphRagRoutes.Vector))
        {
            if (generator is null) throw new InvalidOperationException("The vector route needs an IEmbeddingGenerator<string, Embedding<float>> in the container.");
            var embedded = await generator.GenerateAsync([question], cancellationToken: cancellationToken).ConfigureAwait(false);
            var vector = embedded.Count == 1 ? embedded[0].Vector : throw new InvalidOperationException("The embedding generator returned no vector for the question.");
            if (vector.Length != embeddingSet.Dimensions)
                throw new InvalidOperationException($"The question's embedding has {vector.Length} dimensions; the embedding set has {embeddingSet.Dimensions}.");
            var index = embeddingSet.VectorIndexNameFor(layout.SchemaPrefix);
            var hits = await db.Database.QueryVectorNodesAsync(index, Normalised(vector.Span), node => node[layout.ChunkIdProperty].As<string>(),
                retrieval.CandidatesPerRoute, cancellationToken).ConfigureAwait(false);
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

        var fused = GraphRagFusion.Fuse(lists);
        var ids = fused.Select(f => f.ChunkId).ToArray();
        var chunks = (await db.Set<GraphRagChunk>().AsNoTracking().Where(c => ids.Contains(c.Id))
                .Select(c => new { c.Id, c.DocumentId, c.Text, c.StartByte, c.EndByte, c.Order })
                .ToListAsync(cancellationToken).ConfigureAwait(false))
            .ToDictionary(c => c.Id, StringComparer.Ordinal);
        // A chunk removed between the search and this read is simply not evidence.
        var ranked = fused.Where(f => chunks.ContainsKey(f.ChunkId))
            .Select(f => (new GraphRagChunkText(f.ChunkId, chunks[f.ChunkId].DocumentId, chunks[f.ChunkId].Text, chunks[f.ChunkId].StartByte, chunks[f.ChunkId].EndByte), f.Score, f.Routes))
            .ToArray();
        var (items, bytes, omitted, limits) = GraphRagFusion.Budget(ranked, retrieval.MaxItems, retrieval.MaxUtf8Bytes);

        var context = new List<GraphRagContextItem>();
        if (retrieval.NeighbourChunks > 0)
            context.AddRange(await NeighboursAsync(db, items.Select(i => (i.ChunkId, chunks[i.ChunkId].DocumentId, chunks[i.ChunkId].Order)).ToArray(),
                retrieval.NeighbourChunks, cancellationToken).ConfigureAwait(false));
        var expansionLimits = new List<GraphRagLimit>();
        if (retrieval.CypherExpansion is { } cypher)
            context.AddRange(await ExpandAsync(db, cypher, retrieval.CypherExpansionParameters, items.Select(i => i.ChunkId).ToArray(), expansionLimits, cancellationToken).ConfigureAwait(false));

        return new(question, items, context, bytes, omitted, [.. limits, .. expansionLimits],
            new GraphRagProvenance(server ?? "", embeddingSet.Id, indexes, similarity));
    }

    // Chunks next to each item in the same document, by order, which is the NEXT_CHUNK chain. Bounded: at most 2n per item.
    private static async Task<IReadOnlyList<GraphRagContextItem>> NeighboursAsync(TContext db,
        IReadOnlyList<(string ChunkId, string DocumentId, int Order)> items, int distance, CancellationToken cancellationToken)
    {
        var cited = items.Select(i => i.ChunkId).ToHashSet(StringComparer.Ordinal);
        var result = new List<GraphRagContextItem>();
        foreach (var document in items.GroupBy(i => i.DocumentId, StringComparer.Ordinal))
        {
            var orders = document.SelectMany(i => Enumerable.Range(i.Order - distance, 2 * distance + 1)).Where(o => o >= 0).Distinct().ToArray();
            var documentId = document.Key;
            var nearby = await db.Set<GraphRagChunk>().AsNoTracking().Where(c => c.DocumentId == documentId && orders.Contains(c.Order))
                .OrderBy(c => c.Order).Select(c => new { c.Id, c.Text, c.Order }).ToListAsync(cancellationToken).ConfigureAwait(false);
            foreach (var chunk in nearby.Where(c => !cited.Contains(c.Id)))
            {
                var of = document.OrderBy(i => Math.Abs(i.Order - chunk.Order)).ThenBy(i => i.ChunkId, StringComparer.Ordinal).First();
                result.Add(new(chunk.Id, chunk.Text, $"neighbour of {of.ChunkId}"));
            }
        }
        return result;
    }

    // The application's Cypher, in a read transaction, with $chunkIds bound; at most 256 rows, and a limit reached is reported.
    private static async Task<IReadOnlyList<GraphRagContextItem>> ExpandAsync(TContext db, string cypher, IReadOnlyDictionary<string, object?>? parameters,
        string[] chunkIds, List<GraphRagLimit> limits, CancellationToken cancellationToken)
    {
        var extension = db.GetService<IDbContextOptions>().FindExtension<Neo4jDbContextOptionsExtension>()
            ?? throw new InvalidOperationException("Cypher expansion needs a context configured with UseNeo4j.");
        var values = new Dictionary<string, object?>(parameters ?? new Dictionary<string, object?>(), StringComparer.Ordinal) { ["chunkIds"] = chunkIds };
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
