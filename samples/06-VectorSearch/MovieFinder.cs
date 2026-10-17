using Microsoft.Extensions.VectorData;

namespace GraphBridge.Samples.VectorSearch;

/// <summary>Application search logic. It knows only the VectorData interface, never GraphBridge or Neo4j, so another
/// vector store can replace the search without changing this class.</summary>
public sealed class MovieFinder(IVectorSearchable<MovieHit> search)
{
    public async Task<IReadOnlyList<VectorSearchResult<MovieHit>>> FindAsync(ReadOnlyMemory<float> taste, int top = 3,
        CancellationToken cancellationToken = default)
    {
        var matches = new List<VectorSearchResult<MovieHit>>();
        await foreach (var match in search.SearchAsync(taste, top, cancellationToken: cancellationToken))
            matches.Add(match);
        return matches;
    }
}
