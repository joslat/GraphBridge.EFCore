using GraphBridge.EntityFrameworkCore.Diagnostics;
using GraphBridge.EntityFrameworkCore.Paths;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Neo4j.Driver;

namespace Microsoft.EntityFrameworkCore;

/// <summary>Explicit bounded native path traversal with typed, untracked node/relationship projection.</summary>
public static class Neo4jPathDatabaseFacadeExtensions
{
    /// <summary>Returns paths from an exact native anchor; direction, maximum depth and result limit are mandatory.</summary>
    /// <remarks>
    /// GraphPathPattern is explicit native metadata, not an EF mapping. Anchor values use stored property names
    /// and native scalar representations; no EF converters or global query filters are applied. An ambiguous
    /// anchor fails instead of picking one node. Nodes may repeat; each physical relationship is used at most
    /// once per path. No shortest-path, weighted-path or deterministic ordering promise is made. One additional
    /// path is buffered to report HasMorePaths. Limits bound path count/depth, not property bytes or server work.
    /// Projected values are never attached to EF. Transaction ownership, cancellation and errors follow the
    /// native QueryCypherAsync contract; no implicit DDL, client predicate execution or automatic retry occurs.
    /// </remarks>
    public static Task<GraphPathResults<TNode, TRelationship>> QueryPathsAsync<TNode, TRelationship>(this DatabaseFacade database,
        GraphPathPattern pattern, IReadOnlyDictionary<string, object?> startKeys, GraphTraversalDirection direction,
        int maxDepth, int resultLimit, Func<INode, TNode> projectNode, Func<IRelationship, TRelationship> projectRelationship,
        int minDepth = 1, CancellationToken cancellationToken = default)
        => GraphBridgeDiagnostics.OperationAsync(GraphBridgeDiagnostics.PathActivityName, async () =>
        {
            ArgumentNullException.ThrowIfNull(database); ArgumentNullException.ThrowIfNull(projectNode); ArgumentNullException.ThrowIfNull(projectRelationship);
            cancellationToken.ThrowIfCancellationRequested();
            var request = new GraphPathRequest(pattern, startKeys, direction, maxDepth, resultLimit, minDepth);
            var rows = await Neo4jDatabaseFacadeExtensions.QueryCypherCoreAsync(database, request.Cypher, request.Parameters,
                row => GraphPathReader.Read(row, request, projectNode, projectRelationship, cancellationToken), 1, cancellationToken).ConfigureAwait(false);
            if (rows.Count != 1) throw new InvalidOperationException("Path query did not return its single anchor/collection diagnostic row.");
            return rows[0];
        });
}
