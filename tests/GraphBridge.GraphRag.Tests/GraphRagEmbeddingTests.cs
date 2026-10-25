using System.Net;
using GraphBridge.GraphRag;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace GraphBridge.GraphRag.Tests;

/// <summary>Wave 1 (GR-03, GR-24): batched embedding in parallel lanes, transient-only retries and vector checks, with a fake generator.</summary>
public sealed class GraphRagEmbeddingTests
{
    [Fact]
    public async Task Texts_are_embedded_in_order_in_bounded_batches_and_parallel_lanes()
    {
        var generator = new FakeGenerator(4);
        var ingestion = Ingestion(generator, o => { o.Ingestion.EmbeddingBatchSize = 3; o.Ingestion.Lanes = 2; });
        var texts = Enumerable.Range(0, 10).Select(i => "text " + i).ToArray();
        var tally = new GraphRagIngestion<DbContext>.Tally();
        var vectors = await ingestion.EmbedAsync(texts, tally, default);
        Assert.Equal(texts.Select(FakeGenerator.Vector), vectors);
        Assert.Equal([3, 3, 3, 1], generator.BatchSizes.Order().Reverse());
        Assert.True(generator.MaxConcurrent <= 2);
        Assert.Equal(4, tally.Calls);
        Assert.Equal(10L, tally.Tokens);
    }

    [Fact]
    public async Task Only_transient_failures_are_retried_at_most_twice()
    {
        var flaky = new FakeGenerator(4) { Failures = new Queue<Exception>([new HttpRequestException("busy", null, HttpStatusCode.ServiceUnavailable), new TimeoutException()]) };
        var tally = new GraphRagIngestion<DbContext>.Tally();
        Assert.Single(await Ingestion(flaky).EmbedAsync(["a"], tally, default));
        Assert.Equal(3, tally.Calls);

        var persistent = new FakeGenerator(4) { Failures = new Queue<Exception>(Enumerable.Range(0, 3).Select(_ => (Exception)new HttpRequestException("busy", null, HttpStatusCode.TooManyRequests))) };
        Assert.Equal("embedding-failed", (await Assert.ThrowsAsync<GraphRagEmbeddingException>(() => Ingestion(persistent).EmbedAsync(["a"], new(), default))).ErrorCode);
        Assert.Equal(3, persistent.Calls);

        var refused = new FakeGenerator(4) { Failures = new Queue<Exception>([new HttpRequestException("bad key", null, HttpStatusCode.Unauthorized)]) };
        await Assert.ThrowsAsync<GraphRagEmbeddingException>(() => Ingestion(refused).EmbedAsync(["a"], new(), default));
        Assert.Equal(1, refused.Calls);

        // An application test can turn retries off, for a client that already retries.
        var own = new FakeGenerator(4) { Failures = new Queue<Exception>([new TimeoutException()]) };
        await Assert.ThrowsAsync<GraphRagEmbeddingException>(() => Ingestion(own, o => o.Ingestion.IsTransient = _ => false).EmbedAsync(["a"], new(), default));
        Assert.Equal(1, own.Calls);
    }

    [Theory]
    [InlineData(3, "embedding-dimensions")]
    [InlineData(0, "invalid-embedding")]
    public async Task Vectors_of_the_wrong_size_or_without_a_direction_are_refused(int mode, string code)
    {
        var generator = new FakeGenerator(4) { Override = mode == 3 ? [1f, 2f, 3f] : [0f, 0f, 0f, 0f] };
        Assert.Equal(code, (await Assert.ThrowsAsync<GraphRagEmbeddingException>(() => Ingestion(generator).EmbedAsync(["a"], new(), default))).ErrorCode);
        var nan = new FakeGenerator(4) { Override = [1f, float.NaN, 0f, 0f] };
        Assert.Equal("invalid-embedding", (await Assert.ThrowsAsync<GraphRagEmbeddingException>(() => Ingestion(nan).EmbedAsync(["a"], new(), default))).ErrorCode);
    }

    [Fact]
    public async Task A_normalising_set_stores_unit_vectors()
    {
        var generator = new FakeGenerator(4) { Override = [3f, 4f, 0f, 0f] };
        var vector = Assert.Single(await Ingestion(generator, set: new GraphRagEmbeddingSet("fake", 4, Normalize: true)).EmbedAsync(["a"], new(), default));
        Assert.Equal([0.6f, 0.8f, 0f, 0f], vector);
    }

    [Fact]
    public void Registration_validates_options_and_resolves_the_pipeline()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(new FakeGenerator(8));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ServiceCollection().AddGraphRag<GraphRagModelTests.RagContext>(new GraphRagEmbeddingSet("fake", 8), o => o.Ingestion.Lanes = 9));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ServiceCollection().AddGraphRag<GraphRagModelTests.RagContext>(new GraphRagEmbeddingSet("fake", 8), o => o.Chunking.MaxUtf8Bytes = 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ServiceCollection().AddGraphRag<GraphRagModelTests.RagContext>(new GraphRagEmbeddingSet("fake", 0)));
        services.AddGraphRag<GraphRagModelTests.RagContext>(new GraphRagEmbeddingSet("fake", 8), o => o.Chunking.MaxUtf8Bytes = 512);
        using var provider = services.BuildServiceProvider();
        Assert.IsType<GraphRagIngestion<GraphRagModelTests.RagContext>>(provider.GetRequiredService<IGraphRagIngestion>());
        Assert.Contains("bytes=512", Assert.IsType<GraphRagChunker>(provider.GetRequiredService<IGraphRagChunker>()).Fingerprint);
        Assert.IsType<GraphRagTextLoader>(Assert.Single(provider.GetServices<IGraphRagDocumentLoader>()));
    }

    private static GraphRagIngestion<DbContext> Ingestion(FakeGenerator generator, Action<GraphRagOptions>? configure = null, GraphRagEmbeddingSet? set = null)
    {
        var options = new GraphRagOptions();
        configure?.Invoke(options);
        // EmbedAsync touches neither the database nor the scope factory. Retries wait 200 ms, then 800 ms.
        return new(null!, set ?? new GraphRagEmbeddingSet("fake", generator.Dimensions), options, [], new GraphRagChunker(options.Chunking), generator);
    }
}

/// <summary>A deterministic embedding generator: each text maps to a fixed vector; no model, no network.</summary>
internal sealed class FakeGenerator(int dimensions) : IEmbeddingGenerator<string, Embedding<float>>
{
    private int _active;
    public int Dimensions { get; } = dimensions;
    public int Calls;
    public int MaxConcurrent;
    public List<int> BatchSizes { get; } = [];
    public Queue<Exception> Failures { get; init; } = new();
    public float[]? Override { get; init; }

    public static float[] Vector(string text)
    {
        var hash = (uint)StringComparer.Ordinal.GetHashCode(text);
        return [1f + hash % 7, 1f + hash % 5, 1f + hash % 3, 1f];
    }

    public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values, EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref Calls);
        var active = Interlocked.Increment(ref _active);
        lock (BatchSizes) MaxConcurrent = Math.Max(MaxConcurrent, active);
        try
        {
            await Task.Delay(5, cancellationToken);
            lock (Failures) if (Failures.Count > 0) throw Failures.Dequeue();
            var texts = values.ToArray();
            lock (BatchSizes) BatchSizes.Add(texts.Length);
            var vectors = texts.Select(t => new Embedding<float>(Override ?? (Dimensions == 4 ? Vector(t) : Enumerable.Repeat(1f, Dimensions).ToArray())));
            return new GeneratedEmbeddings<Embedding<float>>(vectors) { Usage = new UsageDetails { InputTokenCount = texts.Length } };
        }
        finally { Interlocked.Decrement(ref _active); }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}
