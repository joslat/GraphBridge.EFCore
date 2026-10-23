using GraphBridge.EntityFrameworkCore.Diagnostics;

namespace GraphBridge.EntityFrameworkCore;

/// <summary>What an upsert did (ADR 0051): nodes it created, nodes that already had their key, and the server's write counters.</summary>
public sealed record GraphUpsertResult(int Created, int Matched, GraphWriteCounters Counters);
