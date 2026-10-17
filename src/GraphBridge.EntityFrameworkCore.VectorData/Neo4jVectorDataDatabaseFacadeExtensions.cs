using GraphBridge.EntityFrameworkCore.VectorData;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.VectorData;
using Neo4j.Driver;

namespace Microsoft.EntityFrameworkCore;

/// <summary>Search an existing GraphBridge vector index through <see cref="IVectorSearchable{TRecord}"/>.</summary>
/// <remarks>
/// A thin adapter over <c>QueryVectorNodesAsync</c> and <c>QueryVectorRelationshipsAsync</c>: the same index preflight,
/// bounds, transaction, concurrency and cancellation rules apply, and results stay untracked. Scores pass through unchanged:
/// Neo4j's normalized similarity in [0, 1], (1 + cos) / 2 for cosine and 1 / (1 + d²) for Euclidean. Inputs are
/// <c>ReadOnlyMemory&lt;float&gt;</c>, <c>float[]</c> and <c>Embedding&lt;float&gt;</c>; every non-default
/// <see cref="VectorSearchOptions{TRecord}"/> setting is refused before I/O. No index creation, embedding generation,
/// EF query filters or provenance. The adapter borrows the context: use it only within that context's scope.
/// </remarks>
public static class Neo4jVectorDataDatabaseFacadeExtensions
{
    /// <summary>Binds an existing ONLINE node vector index; <paramref name="project"/> runs once per match.</summary>
    public static IVectorSearchable<TRecord> AsNodeVectorSearchable<TRecord>(this DatabaseFacade database, string index,
        Func<INode, TRecord> project) where TRecord : class
    {
        ArgumentNullException.ThrowIfNull(project);
        return new Neo4jVectorSearchable<TRecord>(database, index,
            (vector, top, token) => database.QueryVectorNodesAsync(index, vector, project, top, token));
    }

    /// <summary>Binds an existing ONLINE relationship vector index; <paramref name="project"/> runs once per match.</summary>
    public static IVectorSearchable<TRecord> AsRelationshipVectorSearchable<TRecord>(this DatabaseFacade database, string index,
        Func<IRelationship, TRecord> project) where TRecord : class
    {
        ArgumentNullException.ThrowIfNull(project);
        return new Neo4jVectorSearchable<TRecord>(database, index,
            (vector, top, token) => database.QueryVectorRelationshipsAsync(index, vector, project, top, token));
    }
}
