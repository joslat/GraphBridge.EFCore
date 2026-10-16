using Microsoft.EntityFrameworkCore;
using Neo4j.Driver;
using Xunit.Abstractions;

namespace GraphBridge.IntegrationTests;

/// <summary>Real EF-owned association writes; direct driver inspection does not depend on edge query activation.</summary>
[Collection(Neo4jIntegrationCollection.Name)]
public sealed partial class ProviderAssociationUpdateTests(DisposableNeo4jFixture fixture, ITestOutputHelper output)
{
    private const string ActorLabel = "GraphBridgeProviderAssociationActor";
    private const string FilmLabel = "GraphBridgeProviderAssociationFilm";
    private const string EdgeType = "GRAPHBRIDGE_PROVIDER_CREDIT";

    [Neo4jIntegrationFact]
    public async Task New_skip_navigation_graph_saves_three_EF_entries_as_two_nodes_and_one_native_edge()
    {
        var runId = NewRunId();
        try
        {
            await EnsureSchemaAsync();
            await using var context = Context();
            var actor = new Actor { RunId = runId, Name = "new actor" };
            var film = new Film { RunId = runId, Title = "new film" };
            actor.Films.Add(film);
            context.Add(actor);
            context.ChangeTracker.DetectChanges();
            var credit = Assert.Single(context.ChangeTracker.Entries<Credit>()).Entity;
            credit.Role = "O'Reilly `$value` MATCH (n) DELETE n";
            Assert.NotEqual(Guid.Empty, actor.Id);
            Assert.NotEqual(Guid.Empty, film.Id);
            Assert.Equal(actor.Id, credit.ActorId);
            Assert.Equal(film.Id, credit.FilmId);

            Assert.Equal(3, await context.SaveChangesAsync());

            Assert.Equal(1L, actor.Version);
            Assert.Equal(1L, credit.Version);
            Assert.All(context.ChangeTracker.Entries(), entry => Assert.Equal(EntityState.Unchanged, entry.State));
            Assert.Equal(0, await context.SaveChangesAsync());
            var properties = Assert.Single(await ReadEdgesAsync(runId));
            Assert.Equal(credit.Role, properties["role"]);
            Assert.Equal(1L, properties["version"]);
            Assert.False(properties.ContainsKey("note"));
            Assert.False(properties.ContainsKey("ActorId"));
            Assert.False(properties.ContainsKey("FilmId"));
            Assert.Equal(2L, await NodeCountAsync(runId));
            await AssertNoJoinNodesAsync(actor.Id, film.Id);
            output.WriteLine($"Real EF node/edge SaveChanges on {fixture.ServerVersion} {fixture.ServerEdition}; three EF entries, two graph nodes, one directed edge.");
        }
        finally { await CleanupAsync(runId); }
    }

    [Neo4jIntegrationFact]
    public async Task Existing_endpoint_skip_navigation_add_counts_one_and_identical_Added_payload_is_idempotent()
    {
        var runId = NewRunId();
        try
        {
            var (actor, film) = await InsertEndpointsAsync(runId);
            Credit credit;
            await using (var context = Context())
            {
                context.AttachRange(actor, film);
                actor.Films.Add(film);
                context.ChangeTracker.DetectChanges();
                credit = Assert.Single(context.ChangeTracker.Entries<Credit>()).Entity;
                credit.Role = "existing endpoints";
                Assert.Equal(1, await context.SaveChangesAsync());
                Assert.Equal(1L, credit.Version);
                Assert.Equal(EntityState.Unchanged, context.Entry(actor).State);
                Assert.Equal(EntityState.Unchanged, context.Entry(film).State);
            }
            await using (var retryContext = Context())
            {
                var identical = Copy(credit);
                identical.Version = 0;
                retryContext.Add(identical);
                Assert.Equal(1, await retryContext.SaveChangesAsync());
                Assert.Equal(1L, identical.Version);
                Assert.Equal(EntityState.Unchanged, retryContext.Entry(identical).State);
            }
            Assert.Equal("existing endpoints", Assert.Single(await ReadEdgesAsync(runId))["role"]);
            Assert.Equal(2L, await NodeCountAsync(runId));
            await AssertNoJoinNodesAsync(actor.Id, film.Id);
        }
        finally { await CleanupAsync(runId); }
    }

    [Neo4jIntegrationFact]
    public async Task Payload_update_preserves_unknown_properties_removes_null_and_delete_keeps_endpoints()
    {
        var runId = NewRunId();
        try
        {
            var graph = await InsertGraphAsync(runId, note: "remove me");
            await ExecuteAsync(
                "MATCH (:GraphBridgeProviderAssociationActor {runId:$runId})-[r:GRAPHBRIDGE_PROVIDER_CREDIT]->(:GraphBridgeProviderAssociationFilm {runId:$runId}) SET r.external = $value",
                new { runId, value = "preserve" });
            await using var context = Context();
            context.Attach(graph.Credit);
            graph.Credit.Role = "after";
            graph.Credit.Note = null;
            Assert.Equal(1, await context.SaveChangesAsync());
            Assert.Equal(2L, graph.Credit.Version);
            var properties = Assert.Single(await ReadEdgesAsync(runId));
            Assert.Equal("after", properties["role"]);
            Assert.Equal("preserve", properties["external"]);
            Assert.Equal(2L, properties["version"]);
            Assert.False(properties.ContainsKey("note"));

            context.Remove(graph.Credit);
            Assert.Equal(1, await context.SaveChangesAsync());
            Assert.Equal(EntityState.Detached, context.Entry(graph.Credit).State);
            Assert.Empty(await ReadEdgesAsync(runId));
            Assert.Equal(2L, await NodeCountAsync(runId));
            await AssertNoJoinNodesAsync(graph.Actor.Id, graph.Film.Id);
        }
        finally { await CleanupAsync(runId); }
    }

    [Neo4jIntegrationFact]
    public async Task Tracked_endpoint_delete_cascades_EF_join_and_removes_edge_before_restrictive_node_delete()
    {
        var runId = NewRunId();
        try
        {
            var graph = await InsertGraphAsync(runId);
            await using var context = Context();
            context.AttachRange(graph.Actor, graph.Film);
            context.Attach(graph.Credit);
            context.Remove(graph.Film);
            context.ChangeTracker.DetectChanges();
            Assert.Equal(EntityState.Deleted, context.Entry(graph.Credit).State);

            Assert.Equal(2, await context.SaveChangesAsync());

            Assert.Equal(EntityState.Detached, context.Entry(graph.Film).State);
            Assert.Equal(EntityState.Detached, context.Entry(graph.Credit).State);
            Assert.Equal(EntityState.Unchanged, context.Entry(graph.Actor).State);
            Assert.Empty(await ReadEdgesAsync(runId));
            Assert.Equal(1L, await NodeCountAsync(runId));
            Assert.Equal("actor", (await ActorPropertiesAsync(runId))["name"]);
        }
        finally { await CleanupAsync(runId); }
    }

    [Neo4jIntegrationFact]
    public async Task Edge_only_save_requires_both_endpoint_constraints_without_creating_schema()
    {
        var runId = NewRunId();
        try
        {
            var (actor, film) = await InsertEndpointsAsync(runId);
            foreach (var (constraint, label) in new[]
            {
                ("graphbridge_provider_association_actor_key", ActorLabel),
                ("graphbridge_provider_association_film_key", FilmLabel)
            })
            {
                // Fixed, reviewed fixture-only DDL; restored before another test can run.
                await ExecuteAsync($"DROP CONSTRAINT {constraint}");
                try
                {
                    await using var context = Context();
                    var credit = new Credit { ActorId = actor.Id, FilmId = film.Id, Role = "must not persist" };
                    context.Add(credit); // No node entries: schema requirements must come from the edge.
                    var error = await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync());
                    Assert.Contains(label, error.Message);
                    Assert.Contains("uniqueness constraint", error.Message);
                    Assert.Equal(EntityState.Added, context.Entry(credit).State);
                    Assert.Equal(0L, credit.Version);
                    Assert.Null(context.Database.CurrentTransaction);
                    Assert.Empty(await ReadEdgesAsync(runId));
                    Assert.Equal(2L, await NodeCountAsync(runId));
                    await using var session = fixture.OpenSession();
                    var cursor = await session.RunAsync(
                        "SHOW CONSTRAINTS YIELD name WHERE name = $name RETURN name", new { name = constraint });
                    Assert.Empty(await cursor.ToListAsync());
                }
                finally { await EnsureSchemaAsync(); }
            }
        }
        finally { await CleanupAsync(runId); }
    }

    [Neo4jIntegrationFact]
    public async Task Missing_endpoint_aborts_earlier_node_creation_and_preserves_pending_EF_entries_and_versions()
    {
        var runId = NewRunId();
        try
        {
            await EnsureSchemaAsync();
            await using var context = Context();
            var actor = new Actor { RunId = runId, Name = "must roll back" };
            context.Add(actor);
            var credit = new Credit { ActorId = actor.Id, FilmId = Guid.NewGuid(), Role = "missing film" };
            context.Add(credit);

            var error = await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => context.SaveChangesAsync());

            Assert.Same(credit, Assert.Single(error.Entries).Entity);
            Assert.Equal(EntityState.Added, context.Entry(actor).State);
            Assert.Equal(EntityState.Added, context.Entry(credit).State);
            Assert.Equal(0L, actor.Version);
            Assert.Equal(0L, credit.Version);
            Assert.Null(context.Database.CurrentTransaction);
            Assert.Equal(0L, await NodeCountAsync(runId));
            Assert.Empty(await ReadEdgesAsync(runId));
            await AssertNoJoinNodesAsync(actor.Id, credit.FilmId);
        }
        finally { await CleanupAsync(runId); }
    }

    [Neo4jIntegrationFact]
    public async Task Parallel_physical_edges_reject_Added_Modified_and_Deleted_EF_entries_without_changing_edges()
    {
        var runId = NewRunId();
        try
        {
            var graph = await InsertGraphAsync(runId);
            await ExecuteAsync(
                "MATCH (s:GraphBridgeProviderAssociationActor {runId:$runId}), (t:GraphBridgeProviderAssociationFilm {runId:$runId}) " +
                "CREATE (s)-[:GRAPHBRIDGE_PROVIDER_CREDIT {role:'other',version:7}]->(t)", new { runId });
            var before = await ReadEdgesAsync(runId);
            Assert.Equal(2, before.Count);
            foreach (var state in new[] { EntityState.Added, EntityState.Modified, EntityState.Deleted })
            {
                await using var context = Context();
                var credit = Copy(graph.Credit);
                if (state == EntityState.Added)
                {
                    credit.Version = 0;
                    context.Add(credit);
                }
                else
                {
                    context.Attach(credit);
                    if (state == EntityState.Deleted) context.Remove(credit);
                    else credit.Role = "must not overwrite matching edge";
                }
                var error = await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => context.SaveChangesAsync());
                Assert.Same(credit, Assert.Single(error.Entries).Entity);
                Assert.Equal(state, context.Entry(credit).State);
                Assert.Equal(state == EntityState.Added ? 0L : 1L, credit.Version);
                Assert.Null(context.Database.CurrentTransaction);
                AssertEdgesEqual(before, await ReadEdgesAsync(runId));
            }
            Assert.Equal(2L, await NodeCountAsync(runId));
            await AssertNoJoinNodesAsync(graph.Actor.Id, graph.Film.Id);
        }
        finally { await CleanupAsync(runId); }
    }

    [Neo4jIntegrationFact]
    public async Task Stale_association_version_rolls_back_earlier_node_update_and_keeps_original_EF_values()
    {
        var runId = NewRunId();
        try
        {
            var graph = await InsertGraphAsync(runId);
            await using var winner = Context();
            var winningCredit = Copy(graph.Credit);
            winner.Attach(winningCredit);
            await using var loser = Context();
            var losingCredit = Copy(graph.Credit);
            loser.Attach(graph.Actor);
            loser.Attach(losingCredit);
            winningCredit.Role = "winner";
            Assert.Equal(1, await winner.SaveChangesAsync());
            Assert.Equal(2L, winningCredit.Version);
            graph.Actor.Name = "must roll back";
            losingCredit.Role = "loser";

            var error = await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => loser.SaveChangesAsync());

            Assert.Same(losingCredit, Assert.Single(error.Entries).Entity);
            Assert.Equal(EntityState.Modified, loser.Entry(graph.Actor).State);
            Assert.Equal(EntityState.Modified, loser.Entry(losingCredit).State);
            Assert.Equal(1L, graph.Actor.Version);
            Assert.Equal(1L, losingCredit.Version);
            Assert.Equal("actor", loser.Entry(graph.Actor).Property(p => p.Name).OriginalValue);
            var actorProperties = await ActorPropertiesAsync(runId);
            Assert.Equal("actor", actorProperties["name"]);
            Assert.Equal(1L, actorProperties["version"]);
            var edge = Assert.Single(await ReadEdgesAsync(runId));
            Assert.Equal("winner", edge["role"]);
            Assert.Equal(2L, edge["version"]);
            Assert.Null(loser.Database.CurrentTransaction);
        }
        finally { await CleanupAsync(runId); }
    }

    [Neo4jIntegrationFact]
    public async Task Explicit_transaction_reuses_mixed_writes_then_rollback_leaves_no_nodes_or_edges()
    {
        var runId = NewRunId();
        try
        {
            await EnsureSchemaAsync();
            await using var context = Context();
            await using var transaction = await context.Database.BeginTransactionAsync();
            var actor = new Actor { RunId = runId, Name = "transaction actor" };
            var film = new Film { RunId = runId, Title = "transaction film" };
            actor.Films.Add(film);
            context.Add(actor);
            context.ChangeTracker.DetectChanges();
            var credit = Assert.Single(context.ChangeTracker.Entries<Credit>()).Entity;
            credit.Role = "first batch";
            Assert.Equal(3, await context.SaveChangesAsync());
            Assert.Same(transaction, context.Database.CurrentTransaction);
            Assert.Equal(1L, credit.Version);
            credit.Role = "second batch";
            Assert.Equal(1, await context.SaveChangesAsync());
            Assert.Equal(2L, credit.Version);
            Assert.Empty(await ReadEdgesAsync(runId));
            await transaction.RollbackAsync();

            Assert.Null(context.Database.CurrentTransaction);
            Assert.Equal(0L, await NodeCountAsync(runId));
            Assert.Empty(await ReadEdgesAsync(runId));
            Assert.Equal(EntityState.Unchanged, context.Entry(credit).State);
            Assert.Equal(2L, credit.Version); // Standard EF acceptance; rollback requires reload/new context.
            await AssertNoJoinNodesAsync(actor.Id, film.Id);
        }
        finally { await CleanupAsync(runId); }
    }

    [Neo4jIntegrationFact]
    public async Task Competing_EF_association_writers_have_one_original_version_winner_and_one_concurrency_exception()
    {
        var runId = NewRunId();
        try
        {
            var graph = await InsertGraphAsync(runId);
            await using var first = Context();
            await using var second = Context();
            var firstCredit = Copy(graph.Credit);
            var secondCredit = Copy(graph.Credit);
            first.Attach(firstCredit);
            second.Attach(secondCredit);
            firstCredit.Role = "first";
            secondCredit.Role = "second";
            var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            async Task<bool> SaveAsync(UpdateContext context, Credit credit)
            {
                await start.Task;
                try
                {
                    Assert.Equal(1, await context.SaveChangesAsync());
                    Assert.Equal(2L, credit.Version);
                    Assert.Equal(EntityState.Unchanged, context.Entry(credit).State);
                    return true;
                }
                catch (DbUpdateConcurrencyException error)
                {
                    Assert.Same(credit, Assert.Single(error.Entries).Entity);
                    Assert.Equal(1L, credit.Version);
                    Assert.Equal(EntityState.Modified, context.Entry(credit).State);
                    return false;
                }
            }
            var firstSave = SaveAsync(first, firstCredit);
            var secondSave = SaveAsync(second, secondCredit);
            start.SetResult(true);
            var winners = await Task.WhenAll(firstSave, secondSave);
            Assert.Equal(1, winners.Count(w => w));
            var edge = Assert.Single(await ReadEdgesAsync(runId));
            Assert.Equal(winners[0] ? "first" : "second", edge["role"]);
            Assert.Equal(2L, edge["version"]);
            Assert.Null(first.Database.CurrentTransaction);
            Assert.Null(second.Database.CurrentTransaction);
            // Same-role endpoint lock order; a simultaneous start does not guarantee a lock wait.
        }
        finally { await CleanupAsync(runId); }
    }

    [Neo4jIntegrationFact]
    public async Task Concurrent_matching_Added_associations_accept_both_EF_entries_and_create_one_physical_edge()
        => await AssertAddedRaceAsync(conflictingPayload: false);

    [Neo4jIntegrationFact]
    public async Task Concurrent_conflicting_Added_associations_have_one_winner_without_payload_overwrite()
        => await AssertAddedRaceAsync(conflictingPayload: true);

    [Neo4jIntegrationFact]
    public async Task Endpoint_replacement_deletes_old_join_and_adds_new_join_in_one_save_then_fresh_queries_confirm_both_pairs()
    {
        var runId = NewRunId();
        try
        {
            var graph = await InsertGraphAsync(runId);
            var newFilm = new Film { RunId = runId, Title = "replacement film" };
            await using (var endpoints = Context())
            {
                endpoints.Add(newFilm);
                Assert.Equal(1, await endpoints.SaveChangesAsync());
            }
            await using (var context = Context())
            {
                var old = await context.Set<Credit>().SingleAsync(c => c.ActorId == graph.Actor.Id && c.FilmId == graph.Film.Id);
                var oldEntry = context.Entry(old);
                var replacement = new Credit
                {
                    ActorId = graph.Actor.Id, FilmId = newFilm.Id, Role = "replacement", Note = "new pair"
                };
                context.Remove(old);
                context.Add(replacement);
                Assert.Equal(EntityState.Deleted, oldEntry.State);
                Assert.Equal(EntityState.Added, context.Entry(replacement).State);
                Assert.Equal(2, await context.SaveChangesAsync());
                Assert.Equal(EntityState.Detached, oldEntry.State);
                Assert.Equal(EntityState.Unchanged, context.Entry(replacement).State);
                Assert.Equal(1L, replacement.Version);
                Assert.Equal(graph.Film.Id, old.FilmId); // Replacement does not mutate an identifying FK.
            }
            await using var fresh = Context();
            Assert.Empty(await fresh.Set<Credit>().Where(c => c.ActorId == graph.Actor.Id && c.FilmId == graph.Film.Id).ToListAsync());
            var stored = await fresh.Set<Credit>().SingleAsync(c => c.ActorId == graph.Actor.Id && c.FilmId == newFilm.Id);
            Assert.Equal("replacement", stored.Role);
            Assert.Equal("new pair", stored.Note);
            Assert.Equal(1L, stored.Version);
            Assert.Single(await fresh.Set<Actor>().Where(a => a.RunId == runId).ToListAsync());
            Assert.Equal(new[] { graph.Film.Id, newFilm.Id }.OrderBy(id => id),
                (await fresh.Set<Film>().Where(f => f.RunId == runId).ToListAsync()).Select(f => f.Id).OrderBy(id => id));
            Assert.Equal("replacement", Assert.Single(await ReadEdgesAsync(runId))["role"]);
            Assert.Equal(3L, await NodeCountAsync(runId));
            await AssertNoJoinNodesAsync(graph.Actor.Id, graph.Film.Id);
            await AssertNoJoinNodesAsync(graph.Actor.Id, newFilm.Id);
        }
        finally { await CleanupAsync(runId); }
    }

    [Neo4jIntegrationFact]
    public async Task Missing_replacement_endpoint_rolls_back_old_edge_deletion_and_keeps_both_pending_EF_entries()
    {
        var runId = NewRunId();
        try
        {
            var graph = await InsertGraphAsync(runId, note: "original note");
            var missingFilmId = Guid.NewGuid();
            var before = await ReadEdgesAsync(runId);
            await using (var context = Context())
            {
                var old = await context.Set<Credit>().SingleAsync(c => c.ActorId == graph.Actor.Id && c.FilmId == graph.Film.Id);
                var replacement = new Credit { ActorId = graph.Actor.Id, FilmId = missingFilmId, Role = "must not persist" };
                context.Remove(old);
                context.Add(replacement);

                var error = await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => context.SaveChangesAsync());

                Assert.Same(replacement, Assert.Single(error.Entries).Entity);
                Assert.Equal(EntityState.Deleted, context.Entry(old).State);
                Assert.Equal(EntityState.Added, context.Entry(replacement).State);
                Assert.Equal(1L, old.Version);
                Assert.Equal(1L, context.Entry(old).Property(c => c.Version).OriginalValue);
                Assert.Equal(0L, replacement.Version);
                Assert.Equal(0L, context.Entry(replacement).Property(c => c.Version).OriginalValue);
                Assert.Null(context.Database.CurrentTransaction);
            }
            await using var fresh = Context();
            var restored = await fresh.Set<Credit>().SingleAsync(c => c.ActorId == graph.Actor.Id && c.FilmId == graph.Film.Id);
            Assert.Equal("before", restored.Role);
            Assert.Equal("original note", restored.Note);
            Assert.Equal(1L, restored.Version);
            Assert.Empty(await fresh.Set<Credit>().Where(c => c.ActorId == graph.Actor.Id && c.FilmId == missingFilmId).ToListAsync());
            Assert.Single(await fresh.Set<Actor>().Where(a => a.RunId == runId).ToListAsync());
            Assert.Equal(graph.Film.Id, (await fresh.Set<Film>().SingleAsync(f => f.RunId == runId)).Id);
            AssertEdgesEqual(before, await ReadEdgesAsync(runId));
            Assert.Equal(2L, await NodeCountAsync(runId));
            await AssertNoJoinNodesAsync(graph.Actor.Id, graph.Film.Id);
            await AssertNoJoinNodesAsync(graph.Actor.Id, missingFilmId);
        }
        finally { await CleanupAsync(runId); }
    }

    private async Task AssertAddedRaceAsync(bool conflictingPayload)
    {
        var runId = NewRunId();
        try
        {
            var (actor, film) = await InsertEndpointsAsync(runId);
            Assert.Empty(await ReadEdgesAsync(runId));
            await using var first = Context();
            await using var second = Context();
            var firstCredit = new Credit { ActorId = actor.Id, FilmId = film.Id, Role = conflictingPayload ? "first" : "same", Note = "shared" };
            var secondCredit = new Credit { ActorId = actor.Id, FilmId = film.Id, Role = conflictingPayload ? "second" : "same", Note = "shared" };
            first.Add(firstCredit);
            second.Add(secondCredit);
            Assert.Equal(EntityState.Added, first.Entry(firstCredit).State);
            Assert.Equal(EntityState.Added, second.Entry(secondCredit).State);
            Assert.Equal(0L, firstCredit.Version);
            Assert.Equal(0L, secondCredit.Version);
            var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            async Task<bool> SaveAsync(UpdateContext context, Credit credit)
            {
                await start.Task;
                try
                {
                    Assert.Equal(1, await context.SaveChangesAsync());
                    Assert.Equal(EntityState.Unchanged, context.Entry(credit).State);
                    Assert.Equal(1L, credit.Version);
                    Assert.Equal(1L, context.Entry(credit).Property(c => c.Version).OriginalValue);
                    return true;
                }
                catch (DbUpdateConcurrencyException error) when (conflictingPayload)
                {
                    Assert.Same(credit, Assert.Single(error.Entries).Entity);
                    Assert.Equal(EntityState.Added, context.Entry(credit).State);
                    Assert.Equal(0L, credit.Version);
                    Assert.Equal(0L, context.Entry(credit).Property(c => c.Version).OriginalValue);
                    return false;
                }
            }
            var firstSave = SaveAsync(first, firstCredit);
            var secondSave = SaveAsync(second, secondCredit);
            start.SetResult(true);
            var successes = await Task.WhenAll(firstSave, secondSave);
            if (conflictingPayload) Assert.Equal(1, successes.Count(success => success));
            else Assert.All(successes, success => Assert.True(success));
            Assert.Null(first.Database.CurrentTransaction);
            Assert.Null(second.Database.CurrentTransaction);
            var edge = Assert.Single(await ReadEdgesAsync(runId));
            Assert.Equal(conflictingPayload ? (successes[0] ? "first" : "second") : "same", edge["role"]);
            Assert.Equal("shared", edge["note"]);
            Assert.Equal(1L, edge["version"]);
            Assert.False(edge.ContainsKey("token"));
            Assert.Equal(2L, await NodeCountAsync(runId));
            await using var fresh = Context();
            var fromStore = await fresh.Set<Credit>().SingleAsync(c => c.ActorId == actor.Id && c.FilmId == film.Id);
            Assert.Equal(edge["role"], fromStore.Role);
            Assert.Equal("shared", fromStore.Note);
            Assert.Equal(1L, fromStore.Version);
            await AssertNoJoinNodesAsync(actor.Id, film.Id);
            // Both saves start together using the same endpoint lock order. This tests outcomes;
            // it does not assert that a lock wait happened, and unexpected driver errors are not accepted.
        }
        finally { await CleanupAsync(runId); }
    }

    private UpdateContext Context() => new(new DbContextOptionsBuilder<UpdateContext>()
        .UseNeo4j(fixture.Driver, fixture.Database).Options);
    private async Task<(Actor Actor, Film Film)> InsertEndpointsAsync(string runId)
    {
        await EnsureSchemaAsync();
        await using var context = Context();
        var actor = new Actor { RunId = runId, Name = "actor" };
        var film = new Film { RunId = runId, Title = "film" };
        context.AddRange(actor, film);
        Assert.Equal(2, await context.SaveChangesAsync());
        return (actor, film);
    }
    private async Task<Graph> InsertGraphAsync(string runId, string? note = null)
    {
        var (actor, film) = await InsertEndpointsAsync(runId);
        var credit = new Credit { ActorId = actor.Id, FilmId = film.Id, Role = "before", Note = note };
        await using var context = Context();
        context.Add(credit);
        Assert.Equal(1, await context.SaveChangesAsync());
        return new(actor, film, credit);
    }
    private static Credit Copy(Credit credit) => new()
    {
        ActorId = credit.ActorId, FilmId = credit.FilmId, Role = credit.Role, Note = credit.Note,
        Token = credit.Token, Version = credit.Version
    };

    private async Task EnsureSchemaAsync()
    {
        // Reviewed fixture-only DDL, matching finalized EF primary keys exactly.
        await ExecuteAsync("CREATE CONSTRAINT graphbridge_provider_association_actor_key IF NOT EXISTS FOR (n:GraphBridgeProviderAssociationActor) REQUIRE n.actorId IS UNIQUE");
        await ExecuteAsync("CREATE CONSTRAINT graphbridge_provider_association_film_key IF NOT EXISTS FOR (n:GraphBridgeProviderAssociationFilm) REQUIRE n.filmId IS UNIQUE");
    }
    private async Task ExecuteAsync(string text, object? parameters = null)
    {
        await using var session = fixture.OpenSession();
        var cursor = parameters is null ? await session.RunAsync(text) : await session.RunAsync(text, parameters);
        await cursor.ConsumeAsync();
    }
    private async Task<IReadOnlyList<IReadOnlyDictionary<string, object>>> ReadEdgesAsync(string runId)
    {
        await using var session = fixture.OpenSession();
        var cursor = await session.RunAsync(
            "MATCH (:GraphBridgeProviderAssociationActor {runId:$runId})-[r:GRAPHBRIDGE_PROVIDER_CREDIT]->(:GraphBridgeProviderAssociationFilm {runId:$runId}) " +
            "RETURN properties(r) AS properties ORDER BY r.role, r.version", new { runId });
        return await cursor.ToListAsync(record => (IReadOnlyDictionary<string, object>)record["properties"].As<Dictionary<string, object>>());
    }
    private async Task<IReadOnlyDictionary<string, object>> ActorPropertiesAsync(string runId)
    {
        await using var session = fixture.OpenSession();
        var cursor = await session.RunAsync("MATCH (n:GraphBridgeProviderAssociationActor {runId:$runId}) RETURN properties(n) AS properties", new { runId });
        return (await cursor.SingleAsync())["properties"].As<Dictionary<string, object>>();
    }
    private async Task<long> NodeCountAsync(string runId)
    {
        await using var session = fixture.OpenSession();
        long count = 0;
        foreach (var label in new[] { ActorLabel, FilmLabel })
        {
            var cursor = await session.RunAsync($"MATCH (n:{label} {{runId:$runId}}) RETURN count(n) AS count", new { runId });
            count += (await cursor.SingleAsync())["count"].As<long>();
        }
        return count;
    }
    private async Task AssertNoJoinNodesAsync(Guid actorId, Guid filmId)
    {
        await using var session = fixture.OpenSession();
        var cursor = await session.RunAsync(
            "MATCH (n) WHERE (n.ActorId = $actorId AND n.FilmId = $filmId) OR (n.actorId = $actorId AND n.filmId = $filmId) RETURN count(n) AS count",
            new { actorId = actorId.ToString("D"), filmId = filmId.ToString("D") });
        Assert.Equal(0L, (await cursor.SingleAsync())["count"].As<long>());
    }
    private async Task CleanupAsync(string runId)
    {
        await ExecuteAsync("MATCH (:GraphBridgeProviderAssociationActor {runId:$runId})-[r:GRAPHBRIDGE_PROVIDER_CREDIT]->(:GraphBridgeProviderAssociationFilm {runId:$runId}) DELETE r", new { runId });
        await ExecuteAsync("MATCH (n:GraphBridgeProviderAssociationActor {runId:$runId}) DELETE n", new { runId });
        await ExecuteAsync("MATCH (n:GraphBridgeProviderAssociationFilm {runId:$runId}) DELETE n", new { runId });
    }
    private static void AssertEdgesEqual(IReadOnlyList<IReadOnlyDictionary<string, object>> expected,
        IReadOnlyList<IReadOnlyDictionary<string, object>> actual)
        => Assert.Equal(expected.SelectMany(p => p.OrderBy(pair => pair.Key)), actual.SelectMany(p => p.OrderBy(pair => pair.Key)));
    private static string NewRunId() => Guid.NewGuid().ToString("N");
    private sealed record Graph(Actor Actor, Film Film, Credit Credit);

    public sealed class UpdateContext(DbContextOptions<UpdateContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder model)
        {
            var actor = model.Entity<Actor>().ToGraphNode(ActorLabel);
            actor.Property(p => p.Id).HasGraphPropertyName("actorId");
            actor.Property(p => p.RunId).HasGraphPropertyName("runId");
            actor.Property(p => p.Name).HasGraphPropertyName("name");
            actor.Property(p => p.Version).HasGraphPropertyName("version").IsRowVersion();
            var film = model.Entity<Film>().ToGraphNode(FilmLabel);
            film.Property(p => p.Id).HasGraphPropertyName("filmId");
            film.Property(p => p.RunId).HasGraphPropertyName("runId");
            film.Property(p => p.Title).HasGraphPropertyName("title");
            actor.HasMany(p => p.Films).WithMany(p => p.Actors).UsingEntity<Credit>(
                right => right.HasOne<Film>().WithMany().HasForeignKey(p => p.FilmId),
                left => left.HasOne<Actor>().WithMany().HasForeignKey(p => p.ActorId),
                join =>
                {
                    join.HasKey(p => new { p.ActorId, p.FilmId });
                    join.ToGraphRelationship(EdgeType, [nameof(Credit.ActorId)], [nameof(Credit.FilmId)]);
                    join.Property(p => p.Role).HasGraphPropertyName("role");
                    join.Property(p => p.Note).HasGraphPropertyName("note");
                    join.Property(p => p.Token).HasGraphPropertyName("token").IsConcurrencyToken();
                    join.Property(p => p.Version).HasGraphPropertyName("version").IsRowVersion();
                });
        }
    }
    public sealed class Actor
    {
        public Guid Id { get; set; }
        public string RunId { get; set; } = "";
        public string Name { get; set; } = "";
        public long Version { get; set; }
        public List<Film> Films { get; } = [];
    }
    public sealed class Film
    {
        public Guid Id { get; set; }
        public string RunId { get; set; } = "";
        public string Title { get; set; } = "";
        public List<Actor> Actors { get; } = [];
    }
    public sealed class Credit
    {
        public Guid ActorId { get; set; }
        public Guid FilmId { get; set; }
        public string? Role { get; set; }
        public string? Note { get; set; }
        public string? Token { get; set; }
        public long Version { get; set; }
    }
}
