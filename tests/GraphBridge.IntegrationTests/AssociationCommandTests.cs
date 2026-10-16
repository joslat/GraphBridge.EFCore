using GraphBridge.EntityFrameworkCore.Cypher;
using Neo4j.Driver;
using Xunit.Abstractions;

namespace GraphBridge.IntegrationTests;

/// <summary>
/// Acceptance of pure generated Cypher through Neo4j.Driver, not EF association persistence.
/// Endpoint locks model the future executor contract; zero counts roll back after inspecting
/// transaction-local state so rollback cannot conceal an unintended mutation.
/// </summary>
[Collection(Neo4jIntegrationCollection.Name)]
public sealed class AssociationCommandTests(DisposableNeo4jFixture fixture, ITestOutputHelper output)
{
    private const string SourceLabel = "GraphBridgeAssociationSource";
    private const string TargetLabel = "GraphBridgeAssociationTarget";
    private const string RelationshipType = "GRAPHBRIDGE_ASSOCIATION_TEST";

    [Neo4jIntegrationFact]
    public async Task Added_creates_one_directed_edge_and_identical_payload_including_null_is_idempotent()
    {
        var scope = Scope.New();
        const string role = "O'Reilly `$value` \\ MATCH (unrelated) DELETE unrelated //";
        try
        {
            await SeedEndpointsAsync(scope);
            var command = Merge(scope, Map(("role", role), ("note", null), ("version", 1L)));
            Assert.DoesNotContain(role, command.Text);
            Assert.DoesNotContain(scope.RunId, command.Text);
            output.WriteLine($"Direct-driver association commands on Neo4j {fixture.ServerVersion} {fixture.ServerEdition}; no EF edge execution claim.");
            output.WriteLine(command.Text);
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var result = await ApplyAsync(scope, command);
                Assert.Equal(1L, result.Count);
                var edge = Assert.Single(result.Edges);
                Assert.True(edge.Outgoing);
                Assert.Equal(role, edge.Properties["role"]);
                Assert.Equal(1L, edge.Properties["version"]);
                Assert.False(edge.Properties.ContainsKey("note"));
            }
            Assert.Equal(2L, await NodeCountAsync(scope));
            Assert.Single(await ReadAsync(scope));
        }
        finally { await CleanupAsync(scope); }
    }

    [Neo4jIntegrationFact]
    public async Task Added_with_conflicting_payload_reports_zero_and_preserves_existing_edge()
    {
        var scope = Scope.New();
        try
        {
            await SeedEndpointsAsync(scope);
            await SeedEdgeAsync(scope, Map(("role", "original"), ("version", 1L), ("external", "preserve")));
            var before = await ReadAsync(scope);
            var result = await ApplyAsync(scope, Merge(scope, Map(("role", "different"), ("version", 1L))));
            Assert.Equal(0L, result.Count);
            AssertEdgesEqual(before, result.Edges);
            AssertEdgesEqual(before, await ReadAsync(scope));
        }
        finally { await CleanupAsync(scope); }
    }

    [Neo4jIntegrationFact]
    public async Task Physical_duplicate_edges_reject_all_mutations_even_when_only_one_payload_or_token_matches()
    {
        var scope = Scope.New();
        try
        {
            await SeedEndpointsAsync(scope);
            await SeedEdgeAsync(scope, Map(("role", "matching"), ("version", 1L)));
            await SeedEdgeAsync(scope, Map(("role", "other"), ("version", 7L)));
            var before = await ReadAsync(scope);
            Assert.Equal(2, before.Count);
            var commands = new[]
            {
                Merge(scope, Map(("role", "matching"), ("version", 1L))),
                Update(scope, Version(1), Map(("role", "changed"), ("version", 2L))),
                Delete(scope, Version(1))
            };
            foreach (var command in commands)
            {
                var result = await ApplyAsync(scope, command);
                Assert.Equal(0L, result.Count);
                AssertEdgesEqual(before, result.Edges);
                AssertEdgesEqual(before, await ReadAsync(scope));
            }
            Assert.Equal(2L, await NodeCountAsync(scope));
        }
        finally { await CleanupAsync(scope); }
    }

    [Neo4jIntegrationFact]
    public async Task Update_is_selective_removes_null_properties_and_rejects_stale_original_version()
    {
        var scope = Scope.New();
        try
        {
            await SeedEndpointsAsync(scope);
            await SeedEdgeAsync(scope, Map(("role", "before"), ("note", "remove"), ("version", 1L), ("external", "untouched")));
            var command = Update(scope, Version(1), Map(("role", "after"), ("note", null), ("version", 2L)));
            output.WriteLine(command.Text);
            var result = await ApplyAsync(scope, command);
            Assert.Equal(1L, result.Count);
            var edge = Assert.Single(result.Edges);
            Assert.Equal("after", edge.Properties["role"]);
            Assert.Equal("untouched", edge.Properties["external"]);
            Assert.Equal(2L, edge.Properties["version"]);
            Assert.False(edge.Properties.ContainsKey("note"));
            var stale = await ApplyAsync(scope, Update(scope, Version(1), Map(("role", "stale"), ("version", 3L))));
            Assert.Equal(0L, stale.Count);
            AssertEdgesEqual(result.Edges, stale.Edges);
            AssertEdgesEqual(result.Edges, await ReadAsync(scope));
        }
        finally { await CleanupAsync(scope); }
    }

    [Neo4jIntegrationFact]
    public async Task Nullable_first_original_token_matches_missing_property_then_rejects_stale_null()
    {
        var scope = Scope.New();
        try
        {
            await SeedEndpointsAsync(scope);
            await SeedEdgeAsync(scope, Map(("role", "initial"), ("version", 1L)));
            var originals = Map(("token", null), ("version", 1L));
            var success = await ApplyAsync(scope, Update(scope, originals, Map(("token", "new-token"), ("version", 2L))));
            Assert.Equal(1L, success.Count);
            var stale = await ApplyAsync(scope, Update(scope, Map(("token", null), ("version", 2L)), Map(("role", "bad"))));
            Assert.Equal(0L, stale.Count);
            AssertEdgesEqual(success.Edges, stale.Edges);
            var wrongDelete = await ApplyAsync(scope, Delete(scope, Map(("token", null))));
            Assert.Equal(0L, wrongDelete.Count);
            AssertEdgesEqual(success.Edges, wrongDelete.Edges);
            var deleted = await ApplyAsync(scope, Delete(scope, Map(("token", "new-token"), ("version", 2L))));
            Assert.Equal(1L, deleted.Count);
            Assert.Empty(await ReadAsync(scope));
            Assert.Equal(2L, await NodeCountAsync(scope));
        }
        finally { await CleanupAsync(scope); }
    }

    [Neo4jIntegrationFact]
    public async Task Delete_checks_original_token_removes_only_edge_and_reports_zero_when_missing()
    {
        var scope = Scope.New();
        try
        {
            await SeedEndpointsAsync(scope);
            await SeedEdgeAsync(scope, Map(("role", "keep endpoints"), ("version", 1L)));
            var before = await ReadAsync(scope);
            var wrong = await ApplyAsync(scope, Delete(scope, Version(8)));
            Assert.Equal(0L, wrong.Count);
            AssertEdgesEqual(before, wrong.Edges);
            var correct = await ApplyAsync(scope, Delete(scope, Version(1)));
            Assert.Equal(1L, correct.Count);
            Assert.Empty(correct.Edges);
            Assert.Empty(await ReadAsync(scope));
            Assert.Equal(2L, await NodeCountAsync(scope));
            var missing = await ApplyAsync(scope, Delete(scope, Version(1)));
            Assert.Equal(0L, missing.Count);
            Assert.Empty(missing.Edges);
        }
        finally { await CleanupAsync(scope); }
    }

    [Neo4jIntegrationFact]
    public async Task Missing_either_endpoint_returns_zero_without_creating_nodes_or_edges()
    {
        foreach (var missingSource in new[] { true, false })
        {
            var scope = Scope.New();
            try
            {
                await SeedEndpointsAsync(scope, source: !missingSource, target: missingSource);
                foreach (var command in new[]
                         {
                             Merge(scope, Map(("role", "missing"), ("version", 1L))),
                             Update(scope, Version(1), Map(("version", 2L))), Delete(scope, Version(1))
                         })
                {
                    // Deliberately execute after a zero endpoint lock to probe the renderer's own
                    // missing-endpoint result. The future executor must instead abort immediately.
                    var result = await ApplyAsync(scope, command, allowMissingEndpoint: true);
                    Assert.Equal(0L, result.Count);
                    Assert.Empty(result.Edges);
                    Assert.Equal(1L, await NodeCountAsync(scope));
                }
            }
            finally { await CleanupAsync(scope); }
        }
    }

    [Neo4jIntegrationFact]
    public async Task Opposite_direction_edge_does_not_match_update_delete_or_outgoing_merge()
    {
        var scope = Scope.New();
        try
        {
            await SeedEndpointsAsync(scope);
            await SeedEdgeAsync(scope, Map(("role", "reverse"), ("version", 1L)), reverse: true);
            var before = await ReadAsync(scope);
            Assert.False(Assert.Single(before).Outgoing);
            foreach (var command in new[] { Update(scope, Version(1), Map(("version", 2L))), Delete(scope, Version(1)) })
            {
                var result = await ApplyAsync(scope, command);
                Assert.Equal(0L, result.Count);
                AssertEdgesEqual(before, result.Edges);
            }
            var added = await ApplyAsync(scope, Merge(scope, Map(("role", "outgoing"), ("version", 1L))));
            Assert.Equal(1L, added.Count);
            Assert.Equal(2, added.Edges.Count);
            Assert.Equal("reverse", Assert.Single(added.Edges, e => !e.Outgoing).Properties["role"]);
            Assert.Equal("outgoing", Assert.Single(added.Edges, e => e.Outgoing).Properties["role"]);
            Assert.Equal(2L, await NodeCountAsync(scope));
        }
        finally { await CleanupAsync(scope); }
    }

    [Neo4jIntegrationFact]
    public async Task Node_and_edge_generated_mutations_roll_back_together()
    {
        var scope = Scope.New();
        try
        {
            await EnsureSchemaAsync();
            await using (var session = fixture.OpenSession())
            await using (var transaction = await session.BeginTransactionAsync())
            {
                Assert.Equal(1L, await CountAsync(transaction, CypherMutationRenderer.Create(SourceLabel, scope.SourceKeys)));
                Assert.Equal(1L, await CountAsync(transaction, CypherMutationRenderer.Create(TargetLabel, scope.TargetKeys)));
                await LockEndpointsAsync(transaction, scope);
                Assert.Equal(1L, await CountAsync(transaction, Merge(scope, Map(("role", "created"), ("version", 1L)))));
                await LockEndpointsAsync(transaction, scope);
                Assert.Equal(1L, await CountAsync(transaction, Update(scope, Version(1), Map(("role", "changed"), ("version", 2L)))));
                Assert.Equal("changed", Assert.Single(await ReadAsync(transaction, scope)).Properties["role"]);
                await transaction.RollbackAsync();
            }
            Assert.Empty(await ReadAsync(scope));
            Assert.Equal(0L, await NodeCountAsync(scope));
        }
        finally { await CleanupAsync(scope); }
    }

    [Neo4jIntegrationFact]
    public async Task Competing_writers_with_endpoint_locks_have_exactly_one_original_version_winner()
        => await AssertRaceAsync(externalWriter: false);

    [Neo4jIntegrationFact]
    public async Task Competing_direct_relationship_writer_and_generated_writer_have_one_committed_winner()
        => await AssertRaceAsync(externalWriter: true);

    private async Task AssertRaceAsync(bool externalWriter)
    {
        var scope = Scope.New();
        try
        {
            await SeedEndpointsAsync(scope);
            await SeedEdgeAsync(scope, Map(("role", "initial"), ("version", 1L)));
            await using var firstSession = fixture.OpenSession();
            await using var secondSession = fixture.OpenSession();
            await using var firstTransaction = await firstSession.BeginTransactionAsync();
            await using var secondTransaction = await secondSession.BeginTransactionAsync();
            var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            async Task<RaceOutcome> WriteAsync(IAsyncTransaction transaction, string role, bool external)
            {
                await start.Task;
                try
                {
                    long count;
                    if (external)
                    {
                        // This direct writer intentionally bypasses endpoint locks, exercising the
                        // renderer's relationship lock against a non-provider participant.
                        var cursor = await transaction.RunAsync(
                            "MATCH (s:GraphBridgeAssociationSource {runId:$runId,id:$sourceId})-[r:GRAPHBRIDGE_ASSOCIATION_TEST]->(t:GraphBridgeAssociationTarget {runId:$runId,id:$targetId}) " +
                            "SET r.version = r.version WITH r WHERE r.version = $original SET r.version = $next, r.role = $role RETURN count(r) AS count",
                            new { runId = scope.RunId, sourceId = scope.SourceId, targetId = scope.TargetId, original = 1L, next = 2L, role });
                        count = (await cursor.SingleAsync())["count"].As<long>();
                    }
                    else
                    {
                        await LockEndpointsAsync(transaction, scope);
                        count = await CountAsync(transaction, Update(scope, Version(1), Map(("version", 2L), ("role", role))));
                    }
                    Assert.InRange(count, 0L, 1L);
                    if (count == 1)
                    {
                        await transaction.CommitAsync();
                        return RaceOutcome.Committed;
                    }
                    await transaction.RollbackAsync();
                    return RaceOutcome.StaleOriginal;
                }
                catch (Neo4jException error) when (externalWriter
                    && error.Code == "Neo.TransientError.Transaction.DeadlockDetected")
                {
                    // An external writer may take relationship then commit-time node locks,
                    // reversing the provider's endpoint-then-relationship order. Count=1 is
                    // not a winner until commit succeeds. Finish the victim now to release
                    // locks before waiting for the other worker; do not retry either write.
                    await transaction.DisposeAsync();
                    output.WriteLine($"{role}: {error.Code}; transaction aborted, no retry.");
                    return RaceOutcome.DeadlockAborted;
                }
            }
            var first = WriteAsync(firstTransaction, "first", external: false);
            var second = WriteAsync(secondTransaction, "second", external: externalWriter);
            start.SetResult(true);
            var results = await Task.WhenAll(first, second);
            Assert.Single(results, result => result == RaceOutcome.Committed);
            var loser = Assert.Single(results, result => result != RaceOutcome.Committed);
            Assert.True(loser == RaceOutcome.StaleOriginal
                || (externalWriter && loser == RaceOutcome.DeadlockAborted));
            var edge = Assert.Single(await ReadAsync(scope));
            Assert.True(edge.Outgoing);
            Assert.Equal(2L, edge.Properties["version"]);
            Assert.Equal(results[0] == RaceOutcome.Committed ? "first" : "second", edge.Properties["role"]);
            Assert.Equal(2L, await NodeCountAsync(scope));
            // A simultaneous start does not prove a lock wait occurred on every run. This test
            // checks winner/state semantics; it is not a server lock scheduling diagnostic.
        }
        finally { await CleanupAsync(scope); }
    }

    private enum RaceOutcome { Committed, StaleOriginal, DeadlockAborted }

    private static CypherMutationCommand Merge(Scope scope, IReadOnlyDictionary<string, object?> properties)
        => CypherAssociationMutationRenderer.Merge(RelationshipType, SourceLabel, scope.SourceKeys, TargetLabel, scope.TargetKeys, properties);
    private static CypherMutationCommand Update(Scope scope, IReadOnlyDictionary<string, object?> originals, IReadOnlyDictionary<string, object?> changes)
        => CypherAssociationMutationRenderer.Update(RelationshipType, SourceLabel, scope.SourceKeys, TargetLabel, scope.TargetKeys, originals, changes);
    private static CypherMutationCommand Delete(Scope scope, IReadOnlyDictionary<string, object?> originals)
        => CypherAssociationMutationRenderer.Delete(RelationshipType, SourceLabel, scope.SourceKeys, TargetLabel, scope.TargetKeys, originals);
    private static Dictionary<string, object?> Version(long version) => Map(("version", version));
    private static Dictionary<string, object?> Map(params (string Name, object? Value)[] pairs)
        => pairs.ToDictionary(p => p.Name, p => p.Value);

    private async Task<Result> ApplyAsync(Scope scope, CypherMutationCommand command, bool allowMissingEndpoint = false)
    {
        await using var session = fixture.OpenSession();
        await using var transaction = await session.BeginTransactionAsync();
        await LockEndpointsAsync(transaction, scope, allowMissingEndpoint);
        var count = await CountAsync(transaction, command);
        Assert.InRange(count, 0L, 1L);
        var edges = await ReadAsync(transaction, scope);
        if (count == 1) await transaction.CommitAsync();
        else await transaction.RollbackAsync();
        return new(count, edges);
    }

    private static async Task LockEndpointsAsync(IAsyncTransaction transaction, Scope scope, bool allowMissingEndpoint = false)
    {
        var source = await CountAsync(transaction, CypherMutationRenderer.Lock(SourceLabel, scope.SourceKeys));
        var target = await CountAsync(transaction, CypherMutationRenderer.Lock(TargetLabel, scope.TargetKeys));
        if (allowMissingEndpoint)
        {
            Assert.InRange(source, 0L, 1L);
            Assert.InRange(target, 0L, 1L);
            Assert.Equal(1L, source + target);
        }
        else
        {
            Assert.Equal(1L, source);
            Assert.Equal(1L, target);
        }
    }

    private static async Task<long> CountAsync(IAsyncTransaction transaction, CypherMutationCommand command)
    {
        var cursor = await transaction.RunAsync(command.Text, command.Parameters);
        return (await cursor.SingleAsync())["count"].As<long>();
    }

    private async Task EnsureSchemaAsync()
    {
        // Fixture-only DDL; runtime provider must neither create nor infer these constraints.
        await ExecuteAsync("CREATE CONSTRAINT graphbridge_association_source_keys IF NOT EXISTS FOR (n:GraphBridgeAssociationSource) REQUIRE (n.runId,n.id) IS UNIQUE", new Dictionary<string, object>());
        await ExecuteAsync("CREATE CONSTRAINT graphbridge_association_target_keys IF NOT EXISTS FOR (n:GraphBridgeAssociationTarget) REQUIRE (n.runId,n.id) IS UNIQUE", new Dictionary<string, object>());
    }

    private async Task SeedEndpointsAsync(Scope scope, bool source = true, bool target = true)
    {
        await EnsureSchemaAsync();
        await using var session = fixture.OpenSession();
        await using var transaction = await session.BeginTransactionAsync();
        if (source) Assert.Equal(1L, await CountAsync(transaction, CypherMutationRenderer.Create(SourceLabel, scope.SourceKeys)));
        if (target) Assert.Equal(1L, await CountAsync(transaction, CypherMutationRenderer.Create(TargetLabel, scope.TargetKeys)));
        await transaction.CommitAsync();
    }

    private Task SeedEdgeAsync(Scope scope, IReadOnlyDictionary<string, object?> properties, bool reverse = false)
        => ExecuteAsync(
            "MATCH (s:GraphBridgeAssociationSource {runId:$runId,id:$sourceId}), (t:GraphBridgeAssociationTarget {runId:$runId,id:$targetId}) " +
            (reverse ? "CREATE (t)-[r:GRAPHBRIDGE_ASSOCIATION_TEST]->(s) " : "CREATE (s)-[r:GRAPHBRIDGE_ASSOCIATION_TEST]->(t) ") + "SET r = $properties",
            new { runId = scope.RunId, sourceId = scope.SourceId, targetId = scope.TargetId, properties });

    private async Task ExecuteAsync(string text, object parameters)
    {
        await using var session = fixture.OpenSession();
        var cursor = await session.RunAsync(text, parameters);
        await cursor.ConsumeAsync();
    }

    private async Task<IReadOnlyList<Edge>> ReadAsync(Scope scope)
    {
        await using var session = fixture.OpenSession();
        await using var transaction = await session.BeginTransactionAsync();
        var result = await ReadAsync(transaction, scope);
        await transaction.RollbackAsync();
        return result;
    }

    private static async Task<IReadOnlyList<Edge>> ReadAsync(IAsyncTransaction transaction, Scope scope)
    {
        var cursor = await transaction.RunAsync(
            "MATCH (s:GraphBridgeAssociationSource {runId:$runId,id:$sourceId})-[r:GRAPHBRIDGE_ASSOCIATION_TEST]-(t:GraphBridgeAssociationTarget {runId:$runId,id:$targetId}) " +
            "RETURN properties(r) AS properties, startNode(r).id = $sourceId AS outgoing ORDER BY outgoing, r.role, r.version",
            new { runId = scope.RunId, sourceId = scope.SourceId, targetId = scope.TargetId });
        return await cursor.ToListAsync(record => new Edge(record["outgoing"].As<bool>(), record["properties"].As<Dictionary<string, object>>()));
    }

    private async Task<long> NodeCountAsync(Scope scope)
    {
        await using var session = fixture.OpenSession();
        long count = 0;
        foreach (var text in new[]
                 {
                     "MATCH (n:GraphBridgeAssociationSource {runId:$runId}) RETURN count(n) AS count",
                     "MATCH (n:GraphBridgeAssociationTarget {runId:$runId}) RETURN count(n) AS count"
                 })
        {
            var cursor = await session.RunAsync(text, new { runId = scope.RunId });
            count += (await cursor.SingleAsync())["count"].As<long>();
        }
        return count;
    }

    private async Task CleanupAsync(Scope scope)
    {
        // Narrow type, endpoint labels and both run IDs; restrictive node deletes expose leaks.
        await ExecuteAsync("MATCH (s:GraphBridgeAssociationSource {runId:$runId})-[r:GRAPHBRIDGE_ASSOCIATION_TEST]-(t:GraphBridgeAssociationTarget {runId:$runId}) DELETE r", new { runId = scope.RunId });
        await ExecuteAsync("MATCH (n:GraphBridgeAssociationSource {runId:$runId}) DELETE n", new { runId = scope.RunId });
        await ExecuteAsync("MATCH (n:GraphBridgeAssociationTarget {runId:$runId}) DELETE n", new { runId = scope.RunId });
    }

    private static void AssertEdgesEqual(IReadOnlyList<Edge> expected, IReadOnlyList<Edge> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (var index = 0; index < expected.Count; index++)
        {
            Assert.Equal(expected[index].Outgoing, actual[index].Outgoing);
            Assert.Equal(expected[index].Properties.OrderBy(p => p.Key), actual[index].Properties.OrderBy(p => p.Key));
        }
    }

    private sealed record Edge(bool Outgoing, IReadOnlyDictionary<string, object> Properties);
    private sealed record Result(long Count, IReadOnlyList<Edge> Edges);
    private sealed record Scope(string RunId, string SourceId, string TargetId)
    {
        public static Scope New() => new(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"));
        public IReadOnlyDictionary<string, object?> SourceKeys => Map(("runId", RunId), ("id", SourceId));
        public IReadOnlyDictionary<string, object?> TargetKeys => Map(("runId", RunId), ("id", TargetId));
    }
}
