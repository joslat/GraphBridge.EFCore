using GraphBridge.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace GraphBridge.GraphRag;

/// <summary>
/// A scope applied outside retrieval (ADR 0052, section 12): the endpoints read a document by ID, and a request's scope
/// decides whether it may see that document and which of its chunks.
/// </summary>
internal static class GraphRagScopeCheck
{
    /// <summary>
    /// Which chunks of <paramref name="documentId"/> <paramref name="scope"/> covers, as a test on a chunk's bytes; null when
    /// the scope leaves the whole document out.
    /// </summary>
    public static async Task<Func<int, int, bool>?> DocumentAsync(DbContext db, GraphRagScope scope, string documentId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(scope);
        var layout = db.Model.FindAnnotation(GraphRagModelBuilderExtensions.LayoutAnnotation)?.Value as GraphRagLayout
            ?? throw new InvalidOperationException($"{db.GetType().Name} doesn't map the GraphRAG model; call modelBuilder.AddGraphRag(...) in OnModelCreating.");
        var extension = db.GetService<IDbContextOptions>().FindExtension<Neo4jDbContextOptionsExtension>()
            ?? throw new InvalidOperationException("A scoped document read needs a context configured with UseNeo4j.");
        var resolved = await new GraphRagScopedSearch(extension.Driver, extension.DatabaseName, layout).ResolveAsync(scope, cancellationToken).ConfigureAwait(false);
        return Array.BinarySearch(resolved.DocumentIds, documentId, StringComparer.Ordinal) < 0
            ? null
            : (startByte, endByte) => resolved.Contains(documentId, startByte, endByte);
    }
}
