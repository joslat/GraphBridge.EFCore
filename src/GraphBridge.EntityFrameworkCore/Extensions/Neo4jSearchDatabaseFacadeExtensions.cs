using GraphBridge.EntityFrameworkCore.Diagnostics;
using GraphBridge.EntityFrameworkCore.Search;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Neo4j.Driver;

namespace Microsoft.EntityFrameworkCore;

/// <summary>Explicit bounded native search with caller-owned DTO projection; results are never tracked by EF.</summary>
/// <remarks>
/// Requires an existing ONLINE search index and observed Cypher 25 capability. Index names use the provider's
/// validated ASCII identifier subset. Vectors are copied before I/O; query values and limits are parameterized.
/// Limits bound returned rows, not server work or property bytes. Vector results are approximate and cannot see
/// index changes made in the same transaction. Full-text input retains Lucene syntax; scores from different
/// queries/search kinds are not comparable. No normalization, embedding service, implicit index creation,
/// automatic retry or tracking is introduced. Transactions, cancellation and projector errors follow
/// QueryCypherAsync ownership rules; preflight and search share its concurrency guard and transaction.
/// </remarks>
public static class Neo4jSearchDatabaseFacadeExtensions
{
    public static Task<GraphSearchResults<T>> QueryVectorNodesAsync<T>(this DatabaseFacade database, string index,
        IReadOnlyList<double> vector, Func<INode, T> project, int limit = 10, CancellationToken cancellationToken = default)
        => Execute(database, () => GraphSearchRequest.Vector(index, vector, limit, GraphSearchEntityKind.Node), project, cancellationToken);
    public static Task<GraphSearchResults<T>> QueryVectorRelationshipsAsync<T>(this DatabaseFacade database, string index,
        IReadOnlyList<double> vector, Func<IRelationship, T> project, int limit = 10, CancellationToken cancellationToken = default)
        => Execute(database, () => GraphSearchRequest.Vector(index, vector, limit, GraphSearchEntityKind.Relationship), project, cancellationToken);
    public static Task<GraphSearchResults<T>> QueryFullTextNodesAsync<T>(this DatabaseFacade database, string index,
        string query, Func<INode, T> project, int limit = 10, CancellationToken cancellationToken = default)
        => Execute(database, () => GraphSearchRequest.FullText(index, query, limit, GraphSearchEntityKind.Node), project, cancellationToken);
    public static Task<GraphSearchResults<T>> QueryFullTextRelationshipsAsync<T>(this DatabaseFacade database, string index,
        string query, Func<IRelationship, T> project, int limit = 10, CancellationToken cancellationToken = default)
        => Execute(database, () => GraphSearchRequest.FullText(index, query, limit, GraphSearchEntityKind.Relationship), project, cancellationToken);

    private static Task<GraphSearchResults<T>> Execute<T, TEntity>(DatabaseFacade database,
        Func<GraphSearchRequest> create, Func<TEntity, T> project, CancellationToken token) where TEntity : IEntity
        => GraphBridgeDiagnostics.OperationAsync(GraphBridgeDiagnostics.SearchActivityName, async () =>
        {
            ArgumentNullException.ThrowIfNull(database); ArgumentNullException.ThrowIfNull(project);
            token.ThrowIfCancellationRequested();
            var request = create(); GraphSearchProvenance? provenance = null;
            var rows = await Neo4jDatabaseFacadeExtensions.QueryCypherCoreAsync(database, request.Cypher, request.Parameters,
                record =>
                {
                    if (record["entity"] is not TEntity entity || record["score"] is not double score || !double.IsFinite(score)
                        || score < 0 || request.Kind == GraphSearchKind.Vector && score > 1)
                        throw new InvalidOperationException("Graph search returned an invalid entity or native score.");
                    return new GraphScoredResult<T>(project(entity), score, entity.ElementId);
                }, request.Limit, token,
                async (runner, ct) => provenance = await GraphSearchPreflight.InspectAsync(runner, request, ct).ConfigureAwait(false)).ConfigureAwait(false);
            return new GraphSearchResults<T>(rows, provenance!);
        });
}
