using GraphBridge.EntityFrameworkCore.Search;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.VectorData;
using Script = GraphBridge.UnitTests.GraphSearchTests.Script;

namespace GraphBridge.UnitTests;

/// <summary>PL-09, public issue #1: the optional IVectorSearchable adapter over the existing vector search, offline.</summary>
public sealed class VectorDataAdapterTests
{
    public sealed record Hit(string Id);
    private sealed class PoolContext(DbContextOptions<PoolContext> options) : DbContext(options);

    [Fact]
    public async Task Node_results_keep_native_scores_order_and_exact_coordinates_without_tracking()
    {
        var script = new Script { Hits = [("a", 0.98), ("b", 0.75)] };
        await using var db = script.Context();
        var search = db.Database.AsNodeVectorSearchable("search_index", n => new Hit(n.ElementId));
        var results = await Collect(search.SearchAsync(new[] { 0.1f, 0.2f, 0.3f }, top: 2));
        Assert.Equal(new[] { "a", "b" }, results.Select(r => r.Record.Id));
        Assert.Equal(new double?[] { 0.98, 0.75 }, results.Select(r => r.Score));
        var (text, parameters) = script.Commands[^1];
        Assert.Contains("SEARCH entity IN (VECTOR INDEX `search_index`", text);
        Assert.Equal(new[] { (double)0.1f, (double)0.2f, (double)0.3f }, (double[])parameters["query"]);
        Assert.Equal(2L, parameters["limit"]);
        Assert.Empty(db.ChangeTracker.Entries());
    }

    [Fact]
    public async Task Relationship_index_uses_the_relationship_search()
    {
        var script = new Script { Relationship = true, Hits = [("r1", 0.9)] };
        await using var db = script.Context();
        var search = db.Database.AsRelationshipVectorSearchable("search_index", r => new Hit(r.ElementId));
        var hit = Assert.Single(await Collect(search.SearchAsync(new ReadOnlyMemory<float>([1f, 0, 0]), top: 1)));
        Assert.Equal("r1", hit.Record.Id);
        Assert.Contains("()-[entity]->()", script.Commands[^1].Text);
    }

    [Fact]
    public async Task Float_arrays_memory_and_embeddings_send_the_same_unnormalized_vector()
    {
        var script = new Script { Hits = [("a", 0.5)] };
        await using var db = script.Context();
        var search = db.Database.AsNodeVectorSearchable("search_index", n => new Hit(n.ElementId));
        float[] values = [0.25f, -1.5f, 3f];
        await Collect(search.SearchAsync(values, 1));
        await Collect(search.SearchAsync(new ReadOnlyMemory<float>(values), 1));
        await Collect(search.SearchAsync(new Embedding<float>(values), 1));
        var sent = script.Commands.Where(c => c.Text.Contains("SEARCH entity")).Select(c => (double[])c.Parameters["query"]).ToList();
        Assert.Equal(3, sent.Count);
        Assert.All(sent, vector => Assert.Equal(new[] { 0.25, -1.5, 3.0 }, vector));
    }

    [Fact]
    public async Task Nothing_runs_before_enumeration_and_each_enumeration_is_a_new_search()
    {
        var script = new Script { Hits = [("a", 0.5)] };
        await using var db = script.Context();
        var search = db.Database.AsNodeVectorSearchable("search_index", n => new Hit(n.ElementId));
        float[] values = [1f, 0, 0];
        var pending = search.SearchAsync(values, 1);
        Assert.Equal(0, script.Sessions);
        values[0] = 2f; // the coordinates are read when enumeration begins
        await Collect(pending);
        Assert.Equal(2.0, ((double[])script.Commands[^1].Parameters["query"])[0]);
        var perSearch = script.Commands.Count;
        await Collect(pending);
        Assert.Equal(2 * perSearch, script.Commands.Count);
    }

    [Fact]
    public async Task Invalid_inputs_and_every_option_fail_before_any_driver_call()
    {
        var script = new Script();
        await using var db = script.Context();
        var search = db.Database.AsNodeVectorSearchable("search_index", n => new Hit(n.ElementId));
        float[] valid = [1f, 0, 0];
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Collect(search.SearchAsync(valid, 0)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Collect(search.SearchAsync(valid, 10_001)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Collect(search.SearchAsync(Array.Empty<float>(), 1)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Collect(search.SearchAsync(new float[4097], 1)));
        await Assert.ThrowsAsync<ArgumentException>(() => Collect(search.SearchAsync(new[] { float.NaN, 0, 0 }, 1)));
        await Assert.ThrowsAsync<ArgumentException>(() => Collect(search.SearchAsync(new[] { float.PositiveInfinity }, 1)));
        Assert.Contains("not System.String", (await Assert.ThrowsAsync<NotSupportedException>(() => Collect(search.SearchAsync("a film about space", 1)))).Message);
        await Assert.ThrowsAsync<NotSupportedException>(() => Collect(search.SearchAsync(new[] { 1d, 0, 0 }, 1)));
        foreach (var (name, options) in new (string, VectorSearchOptions<Hit>)[]
        {
            ("Filter", new() { Filter = h => h.Id == "a" }), ("VectorProperty", new() { VectorProperty = h => h.Id }),
            ("Skip", new() { Skip = 1 }), ("IncludeVectors", new() { IncludeVectors = true }), ("ScoreThreshold", new() { ScoreThreshold = 0.5 }),
        })
            Assert.Contains($"VectorSearchOptions.{name}", (await Assert.ThrowsAsync<NotSupportedException>(() => Collect(search.SearchAsync(valid, 1, options)))).Message);
        Assert.Equal(0, script.Sessions);
        Assert.Single(await Collect(search.SearchAsync(valid, 1, new VectorSearchOptions<Hit>())));
    }

    [Fact]
    public async Task Null_records_and_projector_failures_fail_the_search()
    {
        var script = new Script { Hits = [("a", 0.5)] };
        await using var db = script.Context();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Collect(
            db.Database.AsNodeVectorSearchable<Hit>("search_index", _ => null!).SearchAsync(new[] { 1f, 0, 0 }, 1)));
        var primary = new FormatException("projector");
        Assert.Same(primary, await Assert.ThrowsAsync<FormatException>(() => Collect(
            db.Database.AsNodeVectorSearchable<Hit>("search_index", _ => throw primary).SearchAsync(new[] { 1f, 0, 0 }, 1))));
        Assert.True(script.Rollbacks >= 1);
    }

    [Fact]
    public async Task Cancellation_before_and_during_yielding_stops_the_search()
    {
        var script = new Script { Hits = [("a", 0.9), ("b", 0.8)] };
        await using var db = script.Context();
        var search = db.Database.AsNodeVectorSearchable("search_index", n => new Hit(n.ElementId));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Collect(search.SearchAsync(new[] { 1f, 0, 0 }, 2, cancellationToken: cancelled.Token)));
        Assert.Equal(0, script.Sessions);
        using var cancel = new CancellationTokenSource();
        await using var results = search.SearchAsync(new[] { 1f, 0, 0 }, 2).WithCancellation(cancel.Token).GetAsyncEnumerator();
        Assert.True(await results.MoveNextAsync());
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await results.MoveNextAsync());
    }

    [Fact]
    public async Task GetService_returns_the_adapter_only_for_its_own_types_without_a_key()
    {
        await using var db = new Script().Context();
        var search = db.Database.AsNodeVectorSearchable("search_index", n => new Hit(n.ElementId));
        Assert.Same(search, search.GetService(typeof(IVectorSearchable<Hit>)));
        Assert.Same(search, search.GetService(typeof(object)));
        Assert.Null(search.GetService(typeof(IVectorSearchable<Hit>), "key"));
        Assert.Null(search.GetService(typeof(string)));
        Assert.Throws<ArgumentNullException>(() => search.GetService(null!));
    }

    [Fact]
    public async Task Construction_validates_arguments_and_index_names_fail_before_IO()
    {
        var script = new Script();
        await using var db = script.Context();
        Assert.Throws<ArgumentNullException>(() => db.Database.AsNodeVectorSearchable<Hit>("search_index", null!));
        Assert.Throws<ArgumentNullException>(() => ((DatabaseFacade)null!).AsNodeVectorSearchable("search_index", n => new Hit(n.ElementId)));
        Assert.Throws<ArgumentException>(() => db.Database.AsNodeVectorSearchable(" ", n => new Hit(n.ElementId)));
        Assert.Throws<ArgumentNullException>(() => db.Database.AsRelationshipVectorSearchable<Hit>(null!, r => new Hit(r.ElementId)));
        foreach (var bad in new[] { "bad name`) MATCH (n", "1index", "a-b", "índice", "search index" })
            Assert.Throws<ArgumentException>(() => db.Database.AsNodeVectorSearchable(bad, n => new Hit(n.ElementId)));
        Assert.NotNull(db.Database.AsRelationshipVectorSearchable("_Index_9", r => new Hit(r.ElementId)));
        Assert.Equal(0, script.Sessions);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Offline_indexes_and_servers_without_vector_search_fail_in_preflight_without_searching()
    {
        var offline = new Script { IndexOverride = row => row["state"] = "POPULATING" };
        await using (var db = offline.Context())
            await Assert.ThrowsAsync<InvalidOperationException>(() => Collect(db.Database.AsNodeVectorSearchable("search_index", n => new Hit(n.ElementId)).SearchAsync(new[] { 1f, 0, 0 }, 1)));
        Assert.DoesNotContain(offline.Commands, c => c.Text.Contains("SEARCH entity"));
        var oldServer = new Script { ComponentsOverride = rows => { rows[0]["versions"] = new[] { "5.26.18" }; rows.RemoveAt(1); } };
        await using (var db = oldServer.Context())
            await Assert.ThrowsAsync<NotSupportedException>(() => Collect(db.Database.AsNodeVectorSearchable("search_index", n => new Hit(n.ElementId)).SearchAsync(new[] { 1f, 0, 0 }, 1)));
        Assert.Single(oldServer.Commands);
        Assert.Equal(oldServer.Sessions, oldServer.Disposals);
    }

    [Fact]
    public async Task Boundary_values_are_accepted_and_slices_and_boxed_inputs_send_exact_coordinates()
    {
        var script = new Script { Hits = [("a", 0.5)], IndexOverride = row =>
            ((Dictionary<string, object>)((Dictionary<string, object>)row["options"])["indexConfig"])["vector.dimensions"] = 4096L };
        await using var db = script.Context();
        var search = db.Database.AsNodeVectorSearchable("search_index", n => new Hit(n.ElementId));
        var wide = Enumerable.Range(1, 4096).Select(i => i / 4096f).ToArray();
        Assert.Single(await Collect(search.SearchAsync(wide, 10_000)));
        Assert.Equal(10_000L, script.Commands[^1].Parameters["limit"]);
        Assert.Equal(4096, ((double[])script.Commands[^1].Parameters["query"]).Length);
        // The index has 4096 dimensions, so a 3-coordinate query is the provider's dimension refusal, before the search.
        await Assert.ThrowsAsync<ArgumentException>(() => Collect(search.SearchAsync(new[] { 1f, 0, 0 }, 1)));
        var small = new Script { Hits = [("a", 0.5)] };
        await using var smallDb = small.Context();
        float[] backing = [9f, float.MaxValue, -0f, float.Epsilon, 9f];
        await Collect(smallDb.Database.AsNodeVectorSearchable("search_index", n => new Hit(n.ElementId)).SearchAsync(new ReadOnlyMemory<float>(backing, 1, 3), 1));
        Assert.Equal(new[] { (double)float.MaxValue, -0d, (double)float.Epsilon }, (double[])small.Commands[^1].Parameters["query"]);
    }

    [Fact]
    public async Task Boxed_arrays_work_and_other_shapes_are_refused_before_IO()
    {
        var script = new Script { Hits = [("a", 0.5)] };
        await using var db = script.Context();
        var search = db.Database.AsNodeVectorSearchable("search_index", n => new Hit(n.ElementId));
        Assert.Single(await Collect(search.SearchAsync<object>(new[] { 1f, 0, 0 }, 1)));
        var sessions = script.Sessions;
        await Assert.ThrowsAsync<NotSupportedException>(() => Collect(search.SearchAsync(new Memory<float>([1f, 0, 0]), 1)));
        await Assert.ThrowsAsync<NotSupportedException>(() => Collect(search.SearchAsync(new List<float> { 1f, 0, 0 }, 1)));
        await Assert.ThrowsAsync<NotSupportedException>(() => Collect(search.SearchAsync(new Embedding<double>(new[] { 1d, 0, 0 }), 1)));
        await Assert.ThrowsAsync<ArgumentNullException>(() => Collect(search.SearchAsync<float[]>(null!, 1)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Collect(search.SearchAsync(new Embedding<float>(Array.Empty<float>()), 1)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Collect(search.SearchAsync(new[] { 1f, 0, 0 }, -1)));
        Assert.Equal(sessions, script.Sessions);
    }

    [Fact]
    public async Task Options_are_read_when_each_enumeration_begins()
    {
        var script = new Script { Hits = [("a", 0.5)] };
        await using var db = script.Context();
        var options = new VectorSearchOptions<Hit>();
        var pending = db.Database.AsNodeVectorSearchable("search_index", n => new Hit(n.ElementId)).SearchAsync(new[] { 1f, 0, 0 }, 1, options);
        Assert.Single(await Collect(pending));
        options.Skip = 2;
        var sessions = script.Sessions;
        Assert.Contains("Skip", (await Assert.ThrowsAsync<NotSupportedException>(() => Collect(pending))).Message);
        Assert.Equal(sessions, script.Sessions);
    }

    [Fact]
    public async Task A_null_record_anywhere_fails_before_any_result_is_returned()
    {
        var script = new Script { Hits = [("a", 0.9), ("b", 0.8)] };
        await using var db = script.Context();
        var search = db.Database.AsNodeVectorSearchable<Hit>("search_index", n => n.ElementId == "b" ? null! : new Hit(n.ElementId));
        await using var results = search.SearchAsync(new[] { 1f, 0, 0 }, 2).GetAsyncEnumerator();
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await results.MoveNextAsync());
    }

    [Fact]
    public async Task Owned_transactions_are_committed_or_rolled_back_and_always_disposed()
    {
        var script = new Script { Hits = [("a", 0.5)] };
        await using var db = script.Context();
        Assert.Single(await Collect(db.Database.AsNodeVectorSearchable("search_index", n => new Hit(n.ElementId)).SearchAsync(new[] { 1f, 0, 0 }, 1)));
        var commits = script.Commits;
        await Assert.ThrowsAsync<FormatException>(() => Collect(db.Database.AsNodeVectorSearchable<Hit>("search_index", _ => throw new FormatException()).SearchAsync(new[] { 1f, 0, 0 }, 1)));
        Assert.Equal(commits, script.Commits);
        Assert.Equal(1, script.Rollbacks);
        Assert.Equal(script.Sessions, script.Disposals);
        Assert.Null(db.Database.CurrentTransaction);
        Assert.Empty(db.ChangeTracker.Entries()); // the context is still usable: the adapter never disposes it
    }

    [Fact]
    public async Task A_pooled_context_waiting_in_the_pool_is_not_used()
    {
        var script = new Script { Hits = [("a", 0.5)] };
        var pool = new PooledDbContextFactory<PoolContext>(script.Options<PoolContext>());
        var context = await pool.CreateDbContextAsync();
        var kept = context.Database.AsNodeVectorSearchable("search_index", n => new Hit(n.ElementId));
        await context.DisposeAsync(); // back to the pool, not leased again yet
        var sessions = script.Sessions;
        await Assert.ThrowsAsync<ObjectDisposedException>(() => Collect(kept.SearchAsync(new[] { 1f, 0, 0 }, 1)));
        Assert.Equal(sessions, script.Sessions);
    }

    [Fact]
    public void Core_provider_lock_file_has_no_VectorData_or_Extensions_AI()
    {
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "GraphBridge.sln"))) root = Path.GetDirectoryName(root)!;
        var locks = File.ReadAllText(Path.Combine(root, "src/GraphBridge.EntityFrameworkCore/packages.lock.json"));
        Assert.DoesNotContain("Microsoft.Extensions.VectorData", locks);
        Assert.DoesNotContain("Microsoft.Extensions.AI", locks);
    }

    [Fact]
    public async Task Disposed_and_re_leased_pooled_contexts_fail_clearly()
    {
        var script = new Script { Hits = [("a", 0.5)] };
        var disposed = script.Context();
        var stale = disposed.Database.AsNodeVectorSearchable("search_index", n => new Hit(n.ElementId));
        await disposed.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => Collect(stale.SearchAsync(new[] { 1f, 0, 0 }, 1)));

        var pool = new PooledDbContextFactory<PoolContext>(script.Options<PoolContext>());
        var first = await pool.CreateDbContextAsync();
        var kept = first.Database.AsNodeVectorSearchable("search_index", n => new Hit(n.ElementId));
        Assert.Single(await Collect(kept.SearchAsync(new[] { 1f, 0, 0 }, 1)));
        await first.DisposeAsync(); // back to the pool
        await Assert.ThrowsAsync<ObjectDisposedException>(() => Collect(kept.SearchAsync(new[] { 1f, 0, 0 }, 1)));
        await using var second = await pool.CreateDbContextAsync();
        Assert.Same(first, second); // the same instance, leased to a new scope
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Collect(kept.SearchAsync(new[] { 1f, 0, 0 }, 1)));
        Assert.Contains("earlier lease", error.Message);
        Assert.Single(await Collect(second.Database.AsNodeVectorSearchable("search_index", n => new Hit(n.ElementId)).SearchAsync(new[] { 1f, 0, 0 }, 1)));
    }

    [Fact]
    public void Core_provider_does_not_reference_VectorData_or_Extensions_AI()
        => Assert.DoesNotContain(typeof(GraphSearchResults<>).Assembly.GetReferencedAssemblies(), name =>
            name.Name!.StartsWith("Microsoft.Extensions.VectorData", StringComparison.Ordinal)
            || name.Name.StartsWith("Microsoft.Extensions.AI", StringComparison.Ordinal));

    private static async Task<List<VectorSearchResult<Hit>>> Collect(IAsyncEnumerable<VectorSearchResult<Hit>> source)
    {
        var results = new List<VectorSearchResult<Hit>>();
        await foreach (var result in source) results.Add(result);
        return results;
    }
}
