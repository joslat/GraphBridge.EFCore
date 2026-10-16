using GraphBridge.EntityFrameworkCore.Search;
using Microsoft.EntityFrameworkCore;
using Neo4j.Driver;

namespace GraphBridge.UnitTests;

public sealed class GraphSearchTests
{
    [Theory]
    [InlineData("5.26.18", false)]
    [InlineData("5.26.18", true)]
    [InlineData("2026.09.0", false)]
    public async Task Missing_dialect_advertisement_is_unsupported_without_index_lookup_or_search(string version, bool fullText)
    {
        var script = new Script { FullText = fullText, ComponentsOverride = rows =>
        {
            rows[0]["versions"] = new[] { version };
            rows.RemoveAt(1);
        } };
        await using var db = script.Context();
        var error = await Assert.ThrowsAsync<NotSupportedException>(async () =>
        {
            if (fullText) await db.Database.QueryFullTextNodesAsync("search_index", "value", n => n);
            else await db.Database.QueryVectorNodesAsync("search_index", new[] { 1d, 0, 0 }, n => n);
        });
        Assert.Contains("Cypher 25", error.Message);
        Assert.Equal(GraphSearchPreflight.Components, Assert.Single(script.Commands).Text);
        Assert.Equal(1, script.Rollbacks);
        Assert.Equal(0, script.Commits);
        Assert.Equal(1, script.Disposals);
    }

    [Fact]
    public async Task Duplicate_dialect_observations_remain_invalid_and_never_execute_search()
    {
        var script = new Script { ComponentsOverride = rows => rows.Add(new(rows[1])) };
        await using var db = script.Context();
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.Database.QueryVectorNodesAsync("search_index", new[] { 1d, 0, 0 }, n => n));
        Assert.Single(script.Commands);
        Assert.Equal(1, script.Rollbacks);
    }

    [Theory]
    [InlineData("COSINE")]
    [InlineData("Cosine")]
    [InlineData("EUCLIDEAN")]
    public async Task Similarity_metadata_is_case_insensitive_but_preserves_observed_spelling(string similarity)
    {
        var script = new Script { IndexOverride = row =>
            ((Dictionary<string, object>)((Dictionary<string, object>)row["options"])["indexConfig"])["vector.similarity_function"] = similarity };
        await using var db = script.Context();
        var result = await db.Database.QueryVectorNodesAsync("search_index", new[] { 1d, 0, 0 }, n => n.ElementId);
        Assert.Equal(similarity, result.Provenance.SimilarityFunction);
        if (similarity.Equals("cosine", StringComparison.OrdinalIgnoreCase))
            await Assert.ThrowsAsync<ArgumentException>(() => db.Database.QueryVectorNodesAsync("search_index", new[] { 0d, 0, 0 }, n => n));
        else
            Assert.Single((await db.Database.QueryVectorNodesAsync("search_index", new[] { 0d, 0, 0 }, n => n)).Results);
    }

    [Fact]
    public async Task Vector_snapshot_native_score_and_provenance_share_one_owned_operation()
    {
        var script = new Script(); await using var db = script.Context();
        double[] vector = [1, 0, 0]; script.OnRun = () => vector[0] = 99;
        var result = await db.Database.QueryVectorNodesAsync("search_index", vector, n => n.ElementId, limit: 2);
        Assert.Equal(0.75, Assert.Single(result.Results).Score);
        Assert.Equal("node-id", result.Results[0].Value);
        Assert.Equal("node-id", result.Results[0].ElementId);
        Assert.Equal(3, result.Provenance.Dimensions); Assert.Equal("cosine", result.Provenance.SimilarityFunction);
        Assert.Equal("2026.09.0", result.Provenance.ServerVersion);
        Assert.Equal(1d, ((double[])script.Commands.Last().Parameters["query"])[0]);
        Assert.Equal(2L, script.Commands.Last().Parameters["limit"]);
        Assert.Equal(3, script.Commands.Count); Assert.Equal(1, script.Commits); Assert.Equal(0, script.Rollbacks);
        Assert.Equal(1, script.Disposals); Assert.Empty(db.ChangeTracker.Entries());
        Assert.Throws<NotSupportedException>(() => ((IList<GraphScoredResult<string>>)result.Results).Clear());
    }

    [Fact]
    public async Task Fulltext_relationships_preserve_Lucene_input_and_empty_result_provenance()
    {
        var script = new Script { FullText = true, Relationship = true, Empty = true };
        await using var db = script.Context();
        const string query = "body:neo4j AND \"graph search\"";
        var result = await db.Database.QueryFullTextRelationshipsAsync("search_index", query, r => r.ElementId);
        Assert.Empty(result.Results); Assert.Equal("standard-no-stop-words", result.Provenance.Analyzer);
        Assert.True(result.Provenance.EventuallyConsistent);
        Assert.Equal(GraphSearchEntityKind.Relationship, result.Provenance.EntityKind);
        Assert.Equal(query, script.Commands.Last().Parameters["query"]);
        Assert.DoesNotContain(query, script.Commands.Last().Text);
        Assert.Contains("db.index.fulltext.queryRelationships($index,$query,{limit:$limit})", script.Commands.Last().Text);
        Assert.Equal("search_index", script.Commands.Last().Parameters["index"]);
    }

    [Theory]
    [InlineData("POPULATING", "VECTOR", "NODE")]
    [InlineData("ONLINE", "RANGE", "NODE")]
    [InlineData("ONLINE", "VECTOR", "RELATIONSHIP")]
    public async Task Incompatible_index_fails_preflight_and_rolls_back_without_search(string state, string type, string entity)
    {
        var script = new Script { IndexOverride = row => { row["state"] = state; row["type"] = type; row["entityType"] = entity; } };
        await using var db = script.Context();
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.Database.QueryVectorNodesAsync("search_index", new[] { 1d, 0, 0 }, n => n));
        Assert.Equal(2, script.Commands.Count); Assert.Equal(1, script.Rollbacks); Assert.Equal(0, script.Commits);
    }

    [Fact]
    public async Task Capability_dimension_and_cosine_zero_fail_before_search()
    {
        foreach (var version in new[] { "5.26.0", "2027.01.0", "2026.13", "2026.09-preview" })
            Assert.Throws<NotSupportedException>(() => GraphSearchPreflight.ValidateCapabilities(version, "community", ["25"], GraphSearchKind.Vector));
        Assert.Throws<NotSupportedException>(() => GraphSearchPreflight.ValidateCapabilities("2026.08.0", "community", ["25"], GraphSearchKind.FullText));
        Assert.Throws<NotSupportedException>(() => GraphSearchPreflight.ValidateCapabilities("2026.09.0", "community", ["5"], GraphSearchKind.Vector));
        foreach (var vector in new[] { new[] { 1d, 0 }, new[] { 0d, 0, 0 } })
        {
            var script = new Script(); await using var db = script.Context();
            await Assert.ThrowsAsync<ArgumentException>(() => db.Database.QueryVectorNodesAsync("search_index", vector, n => n));
            Assert.Equal(2, script.Commands.Count); Assert.Equal(1, script.Rollbacks);
        }
    }

    [Fact]
    public async Task Invalid_input_and_precancellation_never_open_a_session()
    {
        var script = new Script(); await using var db = script.Context();
        await Assert.ThrowsAsync<ArgumentException>(() => db.Database.QueryVectorNodesAsync("bad`index", new[] { 1d }, n => n));
        foreach (var vector in new[] { Array.Empty<double>(), new[] { double.NaN }, new[] { double.PositiveInfinity }, new[] { double.MaxValue } })
            await Assert.ThrowsAnyAsync<ArgumentException>(() => db.Database.QueryVectorNodesAsync("search_index", vector, n => n));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => db.Database.QueryFullTextNodesAsync("search_index", "hello", n => n, 10001));
        await Assert.ThrowsAsync<ArgumentException>(() => db.Database.QueryFullTextNodesAsync("search_index", " ", n => n));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => db.Database.QueryFullTextNodesAsync("search_index", "hello", n => n, cancellationToken: new CancellationToken(true)));
        Assert.Equal(0, script.Sessions);
    }

    [Fact]
    public async Task Borrowed_projector_failure_preserves_transaction_and_reentrant_search_is_rejected()
    {
        var script = new Script(); await using var db = script.Context();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var primary = new FormatException("projector");
        Assert.Same(primary, await Assert.ThrowsAsync<FormatException>(() => db.Database.QueryVectorNodesAsync<string>(
            "search_index", new[] { 1d, 0, 0 }, _ => throw primary)));
        Assert.Same(transaction, db.Database.CurrentTransaction); Assert.Equal(0, script.Rollbacks); Assert.Equal(0, script.Commits);
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.Database.QueryVectorNodesAsync("search_index", new[] { 1d, 0, 0 },
            _ => db.Database.QueryVectorNodesAsync("search_index", new[] { 1d, 0, 0 }, n => n).GetAwaiter().GetResult()));
        Assert.Equal(6, script.Commands.Count); await transaction.RollbackAsync(); Assert.Equal(1, script.Rollbacks);
    }

    [Theory]
    [InlineData(-1d)]
    [InlineData(1.1d)]
    [InlineData(double.NaN)]
    public async Task Invalid_vector_score_never_reaches_projector(double score)
    {
        var script = new Script { Score = score }; await using var db = script.Context(); var projected = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.Database.QueryVectorNodesAsync("search_index", new[] { 1d, 0, 0 }, n => { projected = true; return n; }));
        Assert.False(projected); Assert.Equal(1, script.Rollbacks);
    }

    private sealed class Script
    {
        public bool FullText, Relationship, Empty;
        public double Score = 0.75;
        public Action? OnRun;
        public Action<Dictionary<string, object>>? IndexOverride;
        public Action<List<Dictionary<string, object>>>? ComponentsOverride;
        public int Commits, Rollbacks, Disposals, Sessions;
        public List<(string Text, IDictionary<string, object> Parameters)> Commands { get; } = [];
        public DbContext Context()
        {
            var transaction = NativeQueryTests.Proxy.For<IAsyncTransaction>((m, a) =>
            {
                switch (m.Name)
                {
                    case "RunAsync":
                        var text = (string)a![0]!; Commands.Add((text, (IDictionary<string, object>)a[1]!)); OnRun?.Invoke();
                        return Task.FromResult(Cursor(Rows(text)));
                    case "CommitAsync": Commits++; return Task.CompletedTask;
                    case "RollbackAsync": Rollbacks++; return Task.CompletedTask;
                    case "DisposeAsync": Disposals++; return m.ReturnType == typeof(ValueTask) ? new ValueTask() : Task.CompletedTask;
                    default: throw new InvalidOperationException(m.Name);
                }
            });
            var session = NativeQueryTests.Proxy.For<IAsyncSession>((m, _) => m.Name switch
            {
                "BeginTransactionAsync" => Task.FromResult(transaction),
                "DisposeAsync" => m.ReturnType == typeof(ValueTask) ? new ValueTask() : Task.CompletedTask,
                _ => throw new InvalidOperationException(m.Name)
            });
            var driver = NativeQueryTests.Proxy.For<IDriver>((m, _) =>
            {
                if (m.Name != "AsyncSession") throw new InvalidOperationException(m.Name);
                Sessions++; return session;
            });
            return new DbContext(new DbContextOptionsBuilder().UseNeo4j(driver).Options);
        }
        private List<Dictionary<string, object>> Rows(string text)
        {
            if (text == GraphSearchPreflight.Components)
            {
                List<Dictionary<string, object>> rows = [
                    new() { ["name"] = "Neo4j Kernel", ["versions"] = new[] { "2026.09.0" }, ["edition"] = "community" },
                    new() { ["name"] = "Cypher", ["versions"] = new[] { "5", "25" }, ["edition"] = "community" }];
                ComponentsOverride?.Invoke(rows);
                return rows;
            }
            if (text == GraphSearchPreflight.Indexes)
            {
                var config = FullText ? new Dictionary<string, object> { ["fulltext.analyzer"] = "standard-no-stop-words", ["fulltext.eventually_consistent"] = true }
                    : new Dictionary<string, object> { ["vector.dimensions"] = 3L, ["vector.similarity_function"] = "cosine" };
                var row = new Dictionary<string, object> { ["name"] = "search_index", ["state"] = "ONLINE", ["type"] = FullText ? "FULLTEXT" : "VECTOR",
                    ["entityType"] = Relationship ? "RELATIONSHIP" : "NODE", ["labelsOrTypes"] = new[] { "SearchDoc" }, ["properties"] = new[] { "embedding" },
                    ["indexProvider"] = "test-provider", ["options"] = new Dictionary<string, object> { ["indexConfig"] = config } };
                IndexOverride?.Invoke(row); return [row];
            }
            if (Empty) return [];
            var node = NativeQueryTests.Proxy.For<INode>((m, _) => m.Name == "get_ElementId" ? "node-id" : throw new InvalidOperationException(m.Name));
            return [new() { ["entity"] = node, ["score"] = Score }];
        }
        private static IResultCursor Cursor(List<Dictionary<string, object>> rows)
        {
            var position = -1; var open = true;
            return NativeQueryTests.Proxy.For<IResultCursor>((m, _) => m.Name switch
            {
                "FetchAsync" => Task.FromResult(++position < rows.Count),
                "get_Current" => NativeQueryTests.Proxy.For<IRecord>((method, args) => method.Name switch
                {
                    "get_Values" => rows[position], "get_Item" => rows[position][(string)args![0]!],
                    _ => throw new InvalidOperationException(method.Name)
                }),
                "get_IsOpen" => open,
                "ConsumeAsync" => Consume(),
                _ => throw new InvalidOperationException(m.Name)
            });
            Task<IResultSummary> Consume() { open = false; return Task.FromResult<IResultSummary>(null!); }
        }
    }
}
