using System.Runtime.CompilerServices;
using GraphBridge.EntityFrameworkCore.Search;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.VectorData;

namespace GraphBridge.EntityFrameworkCore.VectorData;

/// <summary>Search-only <see cref="IVectorSearchable{TRecord}"/> over one existing vector index; it owns nothing.</summary>
internal sealed class Neo4jVectorSearchable<TRecord> : IVectorSearchable<TRecord> where TRecord : class
{
    // The provider enforces the same bounds before I/O; checking here names the VectorData arguments.
    private const int MaxTop = 10_000;
    private const int MaxDimensions = 4096;

    private readonly Func<double[], int, CancellationToken, Task<GraphSearchResults<TRecord>>> search;
    private readonly DbContext context;
    private readonly int lease;

    internal Neo4jVectorSearchable(DatabaseFacade database, string index,
        Func<double[], int, CancellationToken, Task<GraphSearchResults<TRecord>>> search)
    {
        ArgumentNullException.ThrowIfNull(database);
        // The documented identifier rule, checked when binding; the provider checks it again before each search.
        ArgumentException.ThrowIfNullOrWhiteSpace(index);
        if (!index.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') || char.IsAsciiDigit(index[0]))
            throw new ArgumentException("Index names must match [A-Za-z_][A-Za-z0-9_]*.", nameof(index));
        this.search = search;
        // A pooled context returned to the pool can be leased to another scope; the lease number tells them apart.
        context = ((IDatabaseFacadeDependenciesAccessor)database).Context;
        lease = context.ContextId.Lease;
    }

    public async IAsyncEnumerable<VectorSearchResult<TRecord>> SearchAsync<TInput>(TInput searchValue, int top,
        VectorSearchOptions<TRecord>? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        where TInput : notnull
    {
        // Nothing runs until enumeration; each enumeration snapshots its inputs, then performs one new search.
        cancellationToken.ThrowIfCancellationRequested();
        var vector = Snapshot(searchValue);
        if (top is < 1 or > MaxTop) throw new ArgumentOutOfRangeException(nameof(top), top, $"top must be between 1 and {MaxTop}.");
        Reject(options);
        if (context.ContextId.Lease != lease)
            throw new InvalidOperationException("This vector search adapter was created from an earlier lease of a pooled DbContext. "
                + "Create the adapter from the context of the current scope, and never keep it longer than that context.");
        var results = (await search(vector, top, cancellationToken).ConfigureAwait(false)).Results;
        // A null record fails the whole search, before any result is returned.
        if (results.Any(hit => hit.Value is null))
            throw new InvalidOperationException("The vector search projector returned null for a match.");
        foreach (var hit in results)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new VectorSearchResult<TRecord>(hit.Value, hit.Score);
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        return serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
    }

    private static double[] Snapshot<TInput>(TInput searchValue) => searchValue switch
    {
        ReadOnlyMemory<float> memory => Widen(memory.Span),
        float[] array => Widen(array),
        Embedding<float> embedding => Widen(embedding.Vector.Span),
        null => throw new ArgumentNullException(nameof(searchValue)),
        _ => throw new NotSupportedException($"GraphBridge vector search accepts ReadOnlyMemory<float>, float[] or Embedding<float>, "
            + $"not {typeof(TInput)}. Generate the embedding first, for example with an IEmbeddingGenerator."),
    };

    // Widening float to double is exact; coordinates are copied, never normalized.
    private static double[] Widen(ReadOnlySpan<float> coordinates)
    {
        if (coordinates.Length is < 1 or > MaxDimensions)
            throw new ArgumentOutOfRangeException("searchValue", coordinates.Length, $"Embeddings must have between 1 and {MaxDimensions} coordinates.");
        var vector = new double[coordinates.Length];
        for (var i = 0; i < vector.Length; i++)
            vector[i] = float.IsFinite(coordinates[i]) ? coordinates[i]
                : throw new ArgumentException("Embedding coordinates must be finite.", "searchValue");
        return vector;
    }

    // Refused rather than ignored or emulated: filtering after the search would not return the filtered top matches.
    private static void Reject(VectorSearchOptions<TRecord>? options)
    {
        if (options is null) return;
        if (options.Filter is not null)
            throw new NotSupportedException("VectorSearchOptions.Filter is not supported: the index cannot filter during the search, and "
                + "filtering afterwards would not return the top matches. Use an index that holds only the records you want to search.");
        if (options.VectorProperty is not null)
            throw new NotSupportedException("VectorSearchOptions.VectorProperty is not supported: the bound index already names its vector property.");
        if (options.Skip != 0)
            throw new NotSupportedException("VectorSearchOptions.Skip is not supported: request a larger top and skip in memory.");
        if (options.IncludeVectors)
            throw new NotSupportedException("VectorSearchOptions.IncludeVectors is not supported: include the embedding in the projected record if you need it.");
        if (options.ScoreThreshold is not null)
            throw new NotSupportedException("VectorSearchOptions.ScoreThreshold is not supported: compare VectorSearchResult.Score in memory.");
    }
}
