using System.Linq.Expressions;
using GraphBridge.EntityFrameworkCore;
using GraphBridge.EntityFrameworkCore.Update;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Microsoft.EntityFrameworkCore;

/// <summary>Typed batch upserts of graph nodes (ADR 0051).</summary>
public static class Neo4jUpsertDbSetExtensions
{
    /// <summary>
    /// Creates a node for each entity whose key no node has, and copies the selected properties onto the node that has it,
    /// in one statement and one transaction. Untracked, like <c>ExecuteUpdateAsync</c>: tracked instances are not refreshed.
    /// </summary>
    /// <param name="entities">At most 10,000 entities with client-supplied keys, no two with the same key.</param>
    /// <param name="updateProperties">
    /// The properties copied onto a node that already exists, for example <c>p => new { p.Name, p.Email }</c>.
    /// Null copies every stored non-key property; <c>p => new { }</c> leaves existing nodes unchanged.
    /// </param>
    /// <remarks>The key must be covered by a uniqueness constraint, checked before anything is written.</remarks>
    public static Task<GraphUpsertResult> ExecuteUpsertAsync<TEntity>(this DbSet<TEntity> set, IEnumerable<TEntity> entities,
        Expression<Func<TEntity, object?>>? updateProperties = null, CancellationToken cancellationToken = default)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(entities);
        return Neo4jUpsertExecutor.ExecuteAsync(set.GetService<ICurrentDbContext>().Context, set.EntityType, entities,
            updateProperties, cancellationToken);
    }
}
