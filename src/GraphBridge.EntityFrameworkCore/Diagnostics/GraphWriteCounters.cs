using System.Diagnostics;
using Neo4j.Driver;

namespace GraphBridge.EntityFrameworkCore.Diagnostics;

/// <summary>What a write changed, as reported by the server in the statement's summary (ADR 0050).</summary>
/// <remarks>Read only from summaries GraphBridge already consumes; collecting them costs no extra round trip.</remarks>
public sealed record GraphWriteCounters(int NodesCreated, int NodesDeleted, int RelationshipsCreated,
    int RelationshipsDeleted, int PropertiesSet, int LabelsAdded, int LabelsRemoved)
{
    public static GraphWriteCounters None { get; } = new(0, 0, 0, 0, 0, 0, 0);

    /// <summary>True when any counter is non-zero.</summary>
    public bool ContainsUpdates => this != None;

    public static GraphWriteCounters operator +(GraphWriteCounters left, GraphWriteCounters right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        return new(left.NodesCreated + right.NodesCreated, left.NodesDeleted + right.NodesDeleted,
            left.RelationshipsCreated + right.RelationshipsCreated, left.RelationshipsDeleted + right.RelationshipsDeleted,
            left.PropertiesSet + right.PropertiesSet, left.LabelsAdded + right.LabelsAdded, left.LabelsRemoved + right.LabelsRemoved);
    }

    internal static GraphWriteCounters From(ICounters counters)
        => new(counters.NodesCreated, counters.NodesDeleted, counters.RelationshipsCreated, counters.RelationshipsDeleted,
            counters.PropertiesSet, counters.LabelsAdded, counters.LabelsRemoved);

    internal static readonly string[] TagNames =
    [
        "graphbridge.counters.nodes_created", "graphbridge.counters.nodes_deleted", "graphbridge.counters.relationships_created",
        "graphbridge.counters.relationships_deleted", "graphbridge.counters.properties_set", "graphbridge.counters.labels_added",
        "graphbridge.counters.labels_removed"
    ];

    private int[] Values() => [NodesCreated, NodesDeleted, RelationshipsCreated, RelationshipsDeleted, PropertiesSet, LabelsAdded, LabelsRemoved];

    /// <summary>Tags a statement still in progress, and adds to the totals of the operation that encloses it.</summary>
    internal static void Record(Activity statement, bool statementOpen, IResultSummary summary)
    {
        if (summary?.Counters is not { ContainsUpdates: true } counters) return;
        var values = From(counters).Values();
        if (statementOpen && statement.IsAllDataRequested)
            for (var i = 0; i < TagNames.Length; i++) statement.SetTag(TagNames[i], values[i]);
        for (var parent = statement.Parent; parent is not null; parent = parent.Parent)
        {
            if (parent.Source.Name != GraphBridgeDiagnostics.ActivitySourceName
                || parent.OperationName is not (GraphBridgeDiagnostics.SaveChangesActivityName or GraphBridgeDiagnostics.BulkActivityName
                    or GraphBridgeDiagnostics.NativeQueryActivityName or GraphBridgeDiagnostics.UpsertActivityName)) continue;
            if (parent.IsAllDataRequested)
                for (var i = 0; i < TagNames.Length; i++)
                    parent.SetTag(TagNames[i], (parent.GetTagItem(TagNames[i]) is int total ? total : 0) + values[i]);
            break;
        }
    }
}
