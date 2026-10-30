using System.Text;

namespace GraphBridge.GraphRag;

/// <summary>A route's candidate before fusion: the chunk's identity and the index's own score.</summary>
internal sealed record GraphRagCandidate(string ChunkId, double NativeScore);

/// <summary>A chunk's text and position, read for evidence.</summary>
internal sealed record GraphRagChunkText(string ChunkId, string DocumentId, string Text, int StartByte, int EndByte,
    GraphRagGrounding Grounding = GraphRagGrounding.Source);

/// <summary>Reciprocal rank fusion and the evidence budget (ADR 0052, section 4). Ranks are fused, never scores.</summary>
internal static class GraphRagFusion
{
    internal const int K = 60;

    /// <summary>Fuses ranked lists keyed by chunk ID: each list's 1-based rank r adds 1 / (60 + r). A chunk repeated within one
    /// list counts at its best rank. Ties are broken by chunk ID, ordinally, so the order is deterministic.</summary>
    internal static IReadOnlyList<(string ChunkId, double Score, IReadOnlyList<GraphRagRouteRank> Routes)> Fuse(
        IEnumerable<(GraphRagRoutes Route, IReadOnlyList<GraphRagCandidate> Candidates)> lists)
    {
        var fused = new Dictionary<string, (double Score, List<GraphRagRouteRank> Routes)>(StringComparer.Ordinal);
        foreach (var (route, candidates) in lists)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var rank = 0;
            foreach (var candidate in candidates)
            {
                if (!seen.Add(candidate.ChunkId)) continue;
                rank++;
                var contribution = 1.0 / (K + rank);
                if (!fused.TryGetValue(candidate.ChunkId, out var entry)) entry = (0, []);
                entry.Routes.Add(new(route, rank, candidate.NativeScore, contribution));
                fused[candidate.ChunkId] = (entry.Score + contribution, entry.Routes);
            }
        }
        return fused.Select(pair => (pair.Key, pair.Value.Score, (IReadOnlyList<GraphRagRouteRank>)pair.Value.Routes.AsReadOnly()))
            .OrderByDescending(item => item.Score).ThenBy(item => item.Key, StringComparer.Ordinal).ToArray();
    }

    /// <summary>
    /// Takes items in rank order until <paramref name="maxItems"/> or <paramref name="maxUtf8Bytes"/>. The first item that
    /// doesn't fit whole is cut at a UTF-8 code point boundary to the remaining budget; its end moves to the cut, so its text
    /// is still exactly its span. Everything after it is left out and counted.
    /// </summary>
    internal static (IReadOnlyList<GraphRagEvidenceItem> Items, int Utf8Bytes, int Omitted, IReadOnlyList<GraphRagLimit> Limits) Budget(
        IReadOnlyList<(GraphRagChunkText Chunk, double Score, IReadOnlyList<GraphRagRouteRank> Routes)> ranked, int maxItems, int maxUtf8Bytes)
    {
        var items = new List<GraphRagEvidenceItem>();
        var limits = new List<GraphRagLimit>();
        var used = 0;
        foreach (var (chunk, score, routes) in ranked)
        {
            if (items.Count == maxItems)
            {
                limits.Add(new(nameof(GraphRagRetrievalOptions.MaxItems), maxItems));
                break;
            }
            var bytes = Encoding.UTF8.GetBytes(chunk.Text);
            var remaining = maxUtf8Bytes - used;
            if (bytes.Length <= remaining)
            {
                items.Add(new(chunk.ChunkId, chunk.DocumentId, chunk.Text, chunk.StartByte, chunk.EndByte, score, routes) { Grounding = chunk.Grounding });
                used += bytes.Length;
                continue;
            }
            var cut = remaining;
            while (cut > 0 && (bytes[cut] & 0xC0) == 0x80) cut--;
            if (cut > 0)
            {
                items.Add(new(chunk.ChunkId, chunk.DocumentId, Encoding.UTF8.GetString(bytes, 0, cut), chunk.StartByte, chunk.StartByte + cut, score, routes)
                {
                    Grounding = chunk.Grounding
                });
                used += cut;
            }
            limits.Add(new(nameof(GraphRagRetrievalOptions.MaxUtf8Bytes), maxUtf8Bytes));
            break;
        }
        return (items, used, ranked.Count - items.Count, limits);
    }
}
