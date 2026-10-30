using System.Globalization;
using GraphBridge.EntityFrameworkCore.Migrations;
using Neo4j.Driver;

namespace GraphBridge.GraphRag;

/// <summary>
/// A scope resolved to documents (ADR 0052, section 12): each document whole, or limited to byte ranges. A chunk is inside
/// when its document is, and, with ranges, when it lies wholly within one of them.
/// </summary>
internal sealed class GraphRagResolvedScope(string description, Dictionary<string, List<(int Start, int End)>> ranges, IReadOnlyList<GraphRagLimit> limits)
{
    public string Description { get; } = description;
    public IReadOnlyList<GraphRagLimit> Limits { get; } = limits;
    public string[] DocumentIds { get; } = [.. ranges.Keys.Order(StringComparer.Ordinal)];
    public bool IsEmpty => ranges.Count == 0;

    public bool Contains(string documentId, int startByte, int endByte)
        => ranges.TryGetValue(documentId, out var parts) && (parts.Count == 0 || parts.Any(r => startByte >= r.Start && endByte <= r.End));

    /// <summary>The parameters every scoped query takes: <c>$documentIds</c>, and <c>$scope</c>, a map from document ID to its ranges.</summary>
    public void AddParameters(Dictionary<string, object?> parameters)
    {
        parameters["documentIds"] = DocumentIds;
        parameters["scope"] = ranges.ToDictionary(p => p.Key, p => (object)p.Value
            .Select(r => new Dictionary<string, object> { ["start"] = (long)r.Start, ["end"] = (long)r.End }).ToList(), StringComparer.Ordinal);
    }
}

/// <summary>
/// Scoped retrieval on the server (ADR 0052, section 12). The vector route scores every chunk in the scope when there are at
/// most <see cref="GraphRagRetrievalOptions.ScopeExactChunks"/>, and otherwise asks the vector index for more candidates and
/// keeps those inside the scope; the full-text route always does the latter. Every query filters by the scope in Cypher, in a
/// read transaction. Identifiers come from the validated layout; values are parameters.
/// </summary>
// Workaround for GR-06 (a filter inside a search): typed search can't filter by the scope inside the index query, so scoped
// routes are native, read-only Cypher.
internal sealed class GraphRagScopedSearch(IDriver driver, string? database, GraphRagLayout layout)
{
    private const int MaxFetch = 10_000;
    private string? _server;

    /// <summary>The server version from the last query's summary, such as <c>2026.09.0</c>.</summary>
    public string? ServerVersion => _server;

    public async Task<GraphRagResolvedScope> ResolveAsync(GraphRagScope scope, CancellationToken cancellationToken)
    {
        var ranges = new Dictionary<string, List<(int, int)>>(StringComparer.Ordinal);
        var limits = new List<GraphRagLimit>();
        switch (scope.Kind)
        {
            case GraphRagScopeKind.Documents:
                foreach (var id in scope.Ids) ranges[id] = [];
                break;
            case GraphRagScopeKind.Sections:
                foreach (var row in await ReadAsync(
                    $"MATCH (s:`{layout.SectionLabel}`) WHERE s.`Id` IN $ids RETURN s.`DocumentId` AS documentId, s.`StartByte` AS startByte, s.`EndByte` AS endByte",
                    new() { ["ids"] = scope.Ids.ToArray() }, MaxFetch, cancellationToken).ConfigureAwait(false))
                    Add(ranges, row["documentId"].As<string>(), (row["startByte"].As<int>(), row["endByte"].As<int>()));
                break;
            case GraphRagScopeKind.Metadata:
                var filter = scope.Filter!;
                var rows = await ReadAsync(
                    $"""
                    MATCH (d:`{layout.DocumentLabel}`)
                    WHERE ($publisher IS NULL OR d.`Publisher` = $publisher) AND ($mediaType IS NULL OR d.`MediaType` = $mediaType)
                      AND ($prefix IS NULL OR d.`SourceUri` STARTS WITH $prefix)
                      AND ($from IS NULL OR d.`Published` >= $from) AND ($before IS NULL OR d.`Published` < $before)
                    RETURN d.`Id` AS documentId ORDER BY documentId LIMIT $limit
                    """,
                    new()
                    {
                        ["publisher"] = filter.Publisher, ["mediaType"] = filter.MediaType, ["prefix"] = filter.SourceUriPrefix,
                        ["from"] = filter.PublishedFrom, ["before"] = filter.PublishedBefore, ["limit"] = (long)GraphRagScope.MaxDocuments + 1
                    }, GraphRagScope.MaxDocuments + 1, cancellationToken).ConfigureAwait(false);
                foreach (var row in rows.Take(GraphRagScope.MaxDocuments)) ranges[row["documentId"].As<string>()] = [];
                if (rows.Count > GraphRagScope.MaxDocuments) limits.Add(new("ScopeDocuments", GraphRagScope.MaxDocuments));
                break;
            case GraphRagScopeKind.Cypher:
                var app = await ReadAsync(scope.Cypher!, new Dictionary<string, object?>(scope.Parameters ?? new Dictionary<string, object?>(), StringComparer.Ordinal),
                    GraphRagScope.MaxDocuments + 1, cancellationToken)
                    .ConfigureAwait(false);
                foreach (var row in app.Take(GraphRagScope.MaxDocuments))
                {
                    if (!row.Keys.Contains("documentId")) throw new InvalidOperationException("A Cypher scope must return the column documentId.");
                    var (hasStart, hasEnd) = (row.Keys.Contains("startByte") && row["startByte"] is not null, row.Keys.Contains("endByte") && row["endByte"] is not null);
                    if (hasStart != hasEnd) throw new InvalidOperationException("A Cypher scope returns both startByte and endByte, or neither.");
                    var id = row["documentId"].As<string>();
                    if (hasStart) Add(ranges, id, (row["startByte"].As<int>(), row["endByte"].As<int>()));
                    else ranges[id] = [];
                }
                if (app.Count > GraphRagScope.MaxDocuments) limits.Add(new("ScopeDocuments", GraphRagScope.MaxDocuments));
                break;
        }
        return new(scope.ToString(), ranges, limits);
    }

    // A whole document wins over its ranges.
    private static void Add(Dictionary<string, List<(int, int)>> ranges, string documentId, (int, int) range)
    {
        if (!ranges.TryGetValue(documentId, out var list)) ranges[documentId] = [range];
        else if (list.Count > 0) list.Add(range);
    }

    /// <summary>The documents of the scope that exist, and their chunks inside the scope.</summary>
    public async Task<(int Documents, int Chunks, int AllChunks)> CountAsync(GraphRagResolvedScope scope, CancellationToken cancellationToken)
    {
        var parameters = new Dictionary<string, object?>();
        scope.AddParameters(parameters);
        var inside = (await ReadAsync(
            $"""
            MATCH (d:`{layout.DocumentLabel}`) WHERE d.`Id` IN $documentIds
            OPTIONAL MATCH (d)<-[:`{layout.ChunkDocumentType}`]-(c:`{layout.ChunkLabel}`)
            WITH d, c, $scope[d.`Id`] AS ranges
            WHERE c IS NULL OR size(ranges) = 0 OR any(r IN ranges WHERE c.`StartByte` >= r.start AND c.`EndByte` <= r.end)
            RETURN count(DISTINCT d) AS documents, count(c) AS chunks
            """, parameters, 1, cancellationToken).ConfigureAwait(false)).Single();
        var all = (await ReadAsync($"MATCH (c:`{layout.ChunkLabel}`) RETURN count(c) AS chunks", new(), 1, cancellationToken).ConfigureAwait(false)).Single();
        return (inside["documents"].As<int>(), inside["chunks"].As<int>(), all["chunks"].As<int>());
    }

    /// <summary>Every chunk in the scope scored against the question with the embedding set's similarity function.</summary>
    public async Task<IReadOnlyList<GraphRagCandidate>> ExactVectorAsync(GraphRagResolvedScope scope, double[] vector, GraphVectorSimilarity similarity, int limit,
        CancellationToken cancellationToken)
    {
        var function = similarity == GraphVectorSimilarity.Euclidean ? "euclidean" : "cosine";
        var parameters = new Dictionary<string, object?> { ["vector"] = vector, ["limit"] = (long)limit };
        scope.AddParameters(parameters);
        return Candidates(await ReadAsync(
            $"""
            CYPHER 25 MATCH (d:`{layout.DocumentLabel}`)<-[:`{layout.ChunkDocumentType}`]-(c:`{layout.ChunkLabel}`)
            WHERE d.`Id` IN $documentIds AND c.`{layout.ChunkEmbeddingProperty}` IS NOT NULL
            WITH c, $scope[d.`Id`] AS ranges
            WHERE size(ranges) = 0 OR any(r IN ranges WHERE c.`StartByte` >= r.start AND c.`EndByte` <= r.end)
            WITH c, vector.similarity.{function}(c.`{layout.ChunkEmbeddingProperty}`, $vector) AS score
            RETURN c.`{layout.ChunkIdProperty}` AS id, score ORDER BY score DESC, id LIMIT $limit
            """, parameters, limit, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>The vector index's top <paramref name="fetch"/> candidates, those inside the scope kept.</summary>
    public async Task<IReadOnlyList<GraphRagCandidate>> OverFetchVectorAsync(GraphRagResolvedScope scope, string index, double[] vector, int fetch, int limit,
        CancellationToken cancellationToken)
    {
        var parameters = new Dictionary<string, object?> { ["vector"] = vector, ["fetch"] = (long)fetch, ["limit"] = (long)limit };
        scope.AddParameters(parameters);
        return Candidates(await ReadAsync(
            $"""
            CYPHER 25 MATCH (c)
            SEARCH c IN (VECTOR INDEX `{index}` FOR $vector LIMIT $fetch) SCORE AS score
            MATCH (c)-[:`{layout.ChunkDocumentType}`]->(d:`{layout.DocumentLabel}`) WHERE d.`Id` IN $documentIds
            WITH c, score, $scope[d.`Id`] AS ranges
            WHERE size(ranges) = 0 OR any(r IN ranges WHERE c.`StartByte` >= r.start AND c.`EndByte` <= r.end)
            RETURN c.`{layout.ChunkIdProperty}` AS id, score ORDER BY score DESC, id LIMIT $limit
            """, parameters, limit, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>The full-text index's top <paramref name="fetch"/> matches, those inside the scope kept.</summary>
    public async Task<IReadOnlyList<GraphRagCandidate>> OverFetchFullTextAsync(GraphRagResolvedScope scope, string index, string terms, int fetch, int limit,
        CancellationToken cancellationToken)
    {
        var parameters = new Dictionary<string, object?> { ["index"] = index, ["query"] = terms, ["fetch"] = (long)fetch, ["limit"] = (long)limit };
        scope.AddParameters(parameters);
        return Candidates(await ReadAsync(
            $"""
            CYPHER 25 CALL db.index.fulltext.queryNodes($index, $query, {"{"}limit: $fetch{"}"}) YIELD node AS c, score
            MATCH (c)-[:`{layout.ChunkDocumentType}`]->(d:`{layout.DocumentLabel}`) WHERE d.`Id` IN $documentIds
            WITH c, score, $scope[d.`Id`] AS ranges
            WHERE size(ranges) = 0 OR any(r IN ranges WHERE c.`StartByte` >= r.start AND c.`EndByte` <= r.end)
            RETURN c.`{layout.ChunkIdProperty}` AS id, score ORDER BY score DESC, id LIMIT $limit
            """, parameters, limit, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// How many candidates to ask an index for so that about twice <paramref name="limit"/> fall inside a scope holding
    /// <paramref name="scoped"/> of <paramref name="all"/> chunks: at least the limit, at most 10,000.
    /// </summary>
    internal static int Fetch(int limit, int scoped, int all)
        => (int)Math.Min(MaxFetch, Math.Max(limit, Math.Ceiling(2.0 * limit * Math.Max(all, 1) / Math.Max(scoped, 1))));

    private static GraphRagCandidate[] Candidates(IReadOnlyList<IRecord> rows)
        => [.. rows.Select(r => new GraphRagCandidate(r["id"].As<string>(), r["score"].As<double>()))];

    // One read transaction per query; at most maxRows rows are read. The driver may retry a read, so rows come from the attempt that returned.
    private async Task<IReadOnlyList<IRecord>> ReadAsync(string cypher, Dictionary<string, object?> parameters, int maxRows, CancellationToken cancellationToken)
    {
        await using var session = driver.AsyncSession(o => o.WithDatabase(database).WithDefaultAccessMode(AccessMode.Read));
        var (rows, agent) = await session.ExecuteReadAsync(async transaction =>
        {
            var cursor = await transaction.RunAsync(cypher, parameters).ConfigureAwait(false);
            var read = new List<IRecord>();
            while (read.Count < maxRows && await cursor.FetchAsync().ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                read.Add(cursor.Current);
            }
            var summary = await cursor.ConsumeAsync().ConfigureAwait(false);
            return (read, summary.Server?.Agent);
        }).ConfigureAwait(false);
        if (agent?.IndexOf('/', StringComparison.Ordinal) is int slash and >= 0) _server = agent[(slash + 1)..];
        return rows;
    }
}
