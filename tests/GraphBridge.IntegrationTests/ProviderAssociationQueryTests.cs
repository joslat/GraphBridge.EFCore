using GraphBridge.EntityFrameworkCore.Transport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Neo4j.Driver;
using Xunit.Abstractions;

namespace GraphBridge.IntegrationTests;

/// <summary>Actual EF association roots; driver commands only prepare and clean the dedicated fixture.</summary>
[Collection(Neo4jIntegrationCollection.Name)]
public sealed class ProviderAssociationQueryTests(DisposableNeo4jFixture fixture, ITestOutputHelper output)
{
    private const string ActorLabel = "GraphBridgeQueryAssociationActor";
    private const string FilmLabel = "GraphBridgeQueryAssociationFilm";
    private const string EdgeType = "GRAPHBRIDGE_QUERY_CREDIT";
    private const string FavoriteType = "GRAPHBRIDGE_QUERY_FAVORITE";

    [Neo4jIntegrationFact]
    public async Task Explicit_root_fixes_tracked_endpoints_and_roundtrips_changes_and_delete_across_fresh_contexts()
    {
        var scope = Scope.New();
        try
        {
            await SeedEndpointsAsync(scope);
            await SeedCreditAsync(scope, "lead", 3);
            await using var context = Context();
            var fromQuery = 0;
            context.ChangeTracker.Tracked += (_, args) => { Assert.True(args.FromQuery); fromQuery++; };
            var actor = await context.Set<Actor>().SingleAsync(a => a.RunId == scope.RunId);
            var film = await context.Set<Film>().SingleAsync(f => f.RunId == scope.RunId);
            var query = context.Set<Credit>().Where(c => c.ActorRunId == scope.RunId);
            output.WriteLine(query.ToQueryString());
            var credit = Assert.Single(await query.ToListAsync());
            Assert.Equal(3, fromQuery);
            Assert.Equal(EntityState.Unchanged, context.Entry(credit).State);
            Assert.Equal("lead", context.Entry(credit).Property(c => c.Role).OriginalValue);
            Assert.Null(context.Entry(credit).Property<string?>("Note").CurrentValue);
            Assert.Same(actor, credit.Actor);
            Assert.Same(film, credit.Film);
            Assert.Same(credit, Assert.Single(actor.Credits));
            Assert.Same(credit, Assert.Single(film.Credits));
            Assert.Same(film, Assert.Single(actor.Films));
            Assert.Same(actor, Assert.Single(film.Actors));
            Assert.False(context.Entry(actor).Collection(a => a.Films).IsLoaded);
            Assert.False(context.Entry(actor).Collection(a => a.Credits).IsLoaded);
            credit.Role = "local pending";
            context.ChangeTracker.DetectChanges();
            Assert.Same(credit, Assert.Single(await query.ToListAsync()));
            Assert.Equal("local pending", credit.Role);
            Assert.Equal("lead", context.Entry(credit).Property(c => c.Role).OriginalValue);

            await using (var fresh = Context())
            {
                // Query the join first: later endpoint queries must also trigger EF fix-up.
                var loaded = await fresh.Set<Credit>().SingleAsync(c => c.ActorRunId == scope.RunId);
                Assert.NotSame(credit, loaded);
                Assert.Equal("lead", loaded.Role);
                Assert.Null(loaded.Actor);
                Assert.Null(loaded.Film);
                var freshActor = await fresh.Set<Actor>().SingleAsync(a => a.RunId == scope.RunId);
                var freshFilm = await fresh.Set<Film>().SingleAsync(f => f.RunId == scope.RunId);
                Assert.Same(freshActor, loaded.Actor);
                Assert.Same(freshFilm, loaded.Film);
                Assert.Same(freshFilm, Assert.Single(freshActor.Films));
                loaded.Role = "persisted";
                Assert.Equal(1, await fresh.SaveChangesAsync());
                Assert.Equal(2L, loaded.Version);
                Assert.Equal(EntityState.Unchanged, fresh.Entry(loaded).State);
            }
            await using (var afterSave = Context())
            {
                var saved = await afterSave.Set<Credit>().SingleAsync(c => c.ActorRunId == scope.RunId);
                Assert.Equal("persisted", saved.Role);
                Assert.Equal(2L, saved.Version);
                afterSave.Remove(saved);
                Assert.Equal(1, await afterSave.SaveChangesAsync());
            }
            await using var afterDelete = Context();
            Assert.Empty(await afterDelete.Set<Credit>().Where(c => c.ActorRunId == scope.RunId).ToListAsync());
            Assert.Single(await afterDelete.Set<Actor>().Where(a => a.RunId == scope.RunId).ToListAsync());
            Assert.Single(await afterDelete.Set<Film>().Where(f => f.RunId == scope.RunId).ToListAsync());
        }
        finally { await CleanupAsync(scope); }
    }

    [Neo4jIntegrationFact]
    public async Task Named_dictionary_root_uses_EF_property_slots_and_fixes_skip_navigations_without_marking_loaded()
    {
        var scope = Scope.New();
        try
        {
            await SeedEndpointsAsync(scope);
            await ExecuteAsync($"MATCH (a:{ActorLabel} {{runId:$runId}}), (f:{FilmLabel} {{runId:$runId}}) CREATE (a)-[:{FavoriteType}]->(f)", Parameters(scope));
            await using var context = Context();
            var actor = await context.Set<Actor>().SingleAsync(a => a.RunId == scope.RunId);
            var film = await context.Set<Film>().SingleAsync(f => f.RunId == scope.RunId);
            var query = context.Set<Dictionary<string, object>>("Favorite")
                .Where(j => EF.Property<string>(j, "ActorRunId") == scope.RunId && EF.Property<Guid>(j, "ActorId") == scope.ActorId);
            var text = query.ToQueryString();
            Assert.Contains($"[r:`{FavoriteType}`]", text);
            Assert.Contains("s.`actor_key` = $", text);
            Assert.DoesNotContain(scope.RunId, text);
            var join = Assert.Single(await query.ToListAsync());
            Assert.Equal(scope.ActorId, join["ActorId"]);
            Assert.Equal(scope.FilmId, join["FilmId"]);
            Assert.Null(join["Note"]);
            Assert.Same(join, Assert.Single(await query.ToListAsync()));
            Assert.Equal(EntityState.Unchanged, context.Entry(join).State);
            Assert.Equal("Favorite", context.Entry(join).Metadata.Name);
            Assert.Same(film, Assert.Single(actor.Favorites));
            Assert.Same(actor, Assert.Single(film.Fans));
            Assert.False(context.Entry(actor).Collection(a => a.Favorites).IsLoaded);
            join["Note"] = "local dictionary edit";
            context.ChangeTracker.DetectChanges();
            Assert.True(context.Entry(join).Property("Note").IsModified);
            Assert.Null(context.Entry(join).Property("Note").OriginalValue);
        }
        finally { await CleanupAsync(scope); }
    }

    [Neo4jIntegrationFact]
    public async Task Dictionary_skip_navigation_add_payload_update_and_removal_persist_across_fresh_contexts()
    {
        var scope = Scope.New();
        try
        {
            await SeedEndpointsAsync(scope);
            Dictionary<string, object> added;
            await using (var context = Context())
            {
                var actor = await context.Set<Actor>().SingleAsync(a => a.RunId == scope.RunId);
                var film = await context.Set<Film>().SingleAsync(f => f.RunId == scope.RunId);
                actor.Favorites.Add(film);
                context.ChangeTracker.DetectChanges();
                var entry = Assert.Single(context.ChangeTracker.Entries(), e => e.Metadata.Name == "Favorite");
                Assert.Equal(EntityState.Added, entry.State);
                added = Assert.IsType<Dictionary<string, object>>(entry.Entity);
                added["Note"] = "created through skip navigation";
                Assert.Equal(1, await context.SaveChangesAsync());
                Assert.Equal(EntityState.Unchanged, entry.State);
                Assert.Equal("created through skip navigation", entry.Property("Note").OriginalValue);
                Assert.Equal(EntityState.Unchanged, context.Entry(actor).State);
                Assert.Equal(EntityState.Unchanged, context.Entry(film).State);
            }
            await AssertNativeFavoriteAsync(scope, "created through skip navigation");
            await using (var fresh = Context())
            {
                var join = await fresh.Set<Dictionary<string, object>>("Favorite")
                    .SingleAsync(j => EF.Property<string>(j, "ActorRunId") == scope.RunId);
                Assert.NotSame(added, join);
                Assert.Equal(scope.ActorId, join["ActorId"]);
                Assert.Equal(scope.FilmId, join["FilmId"]);
                Assert.Equal("created through skip navigation", join["Note"]);
                join["Note"] = "persisted dictionary edit";
                fresh.ChangeTracker.DetectChanges();
                Assert.Equal(EntityState.Modified, fresh.Entry(join).State);
                Assert.Equal(1, await fresh.SaveChangesAsync());
                Assert.Equal("persisted dictionary edit", fresh.Entry(join).Property("Note").OriginalValue);
            }
            await AssertNativeFavoriteAsync(scope, "persisted dictionary edit");
            await using (var removal = Context())
            {
                var actor = await removal.Set<Actor>().SingleAsync(a => a.RunId == scope.RunId);
                var film = await removal.Set<Film>().SingleAsync(f => f.RunId == scope.RunId);
                var join = await removal.Set<Dictionary<string, object>>("Favorite")
                    .SingleAsync(j => EF.Property<string>(j, "ActorRunId") == scope.RunId);
                Assert.Equal("persisted dictionary edit", join["Note"]);
                Assert.Same(film, Assert.Single(actor.Favorites));
                Assert.True(actor.Favorites.Remove(film));
                removal.ChangeTracker.DetectChanges();
                var entry = removal.Entry(join);
                Assert.Equal(EntityState.Deleted, entry.State);
                Assert.Equal(1, await removal.SaveChangesAsync());
                Assert.Equal(EntityState.Detached, entry.State);
                Assert.Equal(EntityState.Unchanged, removal.Entry(actor).State);
                Assert.Equal(EntityState.Unchanged, removal.Entry(film).State);
            }
            await using var final = Context();
            Assert.Empty(await final.Set<Dictionary<string, object>>("Favorite")
                .Where(j => EF.Property<string>(j, "ActorRunId") == scope.RunId).ToListAsync());
            Assert.Empty(await final.Set<Credit>().Where(c => c.ActorRunId == scope.RunId).ToListAsync());
            Assert.Single(await final.Set<Actor>().Where(a => a.RunId == scope.RunId).ToListAsync());
            Assert.Single(await final.Set<Film>().Where(f => f.RunId == scope.RunId).ToListAsync());
        }
        finally { await CleanupAsync(scope); }
    }

    [Neo4jIntegrationFact]
    public async Task New_endpoints_and_implicit_dictionary_join_save_three_entries_and_roundtrip_as_one_native_edge()
    {
        var scope = Scope.New();
        try
        {
            await EnsureSchemaAsync();
            await using (var context = Context())
            {
                var actor = new Actor { RunId = scope.RunId, Id = scope.ActorId };
                var film = new Film { RunId = scope.RunId, Id = scope.FilmId };
                actor.Favorites.Add(film);
                context.Add(actor);
                context.ChangeTracker.DetectChanges();
                var entry = Assert.Single(context.ChangeTracker.Entries(), e => e.Metadata.Name == "Favorite");
                var join = Assert.IsType<Dictionary<string, object>>(entry.Entity);
                join["Note"] = "new implicit graph";
                Assert.Equal(3, await context.SaveChangesAsync());
                Assert.All(context.ChangeTracker.Entries(), tracked => Assert.Equal(EntityState.Unchanged, tracked.State));
            }
            await AssertNativeFavoriteAsync(scope, "new implicit graph");
            await using var fresh = Context();
            var actorFromStore = await fresh.Set<Actor>().SingleAsync(a => a.RunId == scope.RunId);
            var filmFromStore = await fresh.Set<Film>().SingleAsync(f => f.RunId == scope.RunId);
            var stored = await fresh.Set<Dictionary<string, object>>("Favorite")
                .SingleAsync(j => EF.Property<string>(j, "ActorRunId") == scope.RunId);
            Assert.Equal("new implicit graph", stored["Note"]);
            Assert.Same(filmFromStore, Assert.Single(actorFromStore.Favorites));
            Assert.Same(actorFromStore, Assert.Single(filmFromStore.Fans));
            Assert.Empty(await fresh.Set<Credit>().Where(c => c.ActorRunId == scope.RunId).ToListAsync());
        }
        finally { await CleanupAsync(scope); }
    }

    [Neo4jIntegrationFact]
    public async Task Filters_ordering_paging_and_scalar_projection_use_endpoint_and_payload_storage_with_hidden_keys()
    {
        var scope = Scope.New();
        try
        {
            await SeedEndpointsAsync(scope);
            await SeedCreditAsync(scope, null, 10);
            var second = scope with { FilmId = Guid.NewGuid() };
            await SeedFilmAsync(second);
            await SeedCreditAsync(second, "b", 20);
            var third = scope with { FilmId = Guid.NewGuid() };
            await SeedFilmAsync(third);
            await SeedCreditAsync(third, "a", 30);
            await using var context = Context();
            var minimum = 10;
            var selected = context.Set<Credit>().Where(c => c.ActorRunId == scope.RunId && c.ActorId == scope.ActorId && c.Rank >= minimum);
            var page = selected.OrderBy(c => c.Role).ThenBy(c => c.Rank).Skip(1).Take(1).Select(c => c.Role);
            var text = page.ToQueryString();
            output.WriteLine(text);
            Assert.Contains("s.`actor_key` = $", text);
            Assert.Contains("r.`rank` >= $", text);
            Assert.Contains("t.`film_key` AS", text);
            Assert.Contains("__gb_association_count", text);
            Assert.DoesNotContain(scope.RunId, text);
            Assert.Equal("a", Assert.Single(await page.ToListAsync()));
            Assert.Equal(new string?[] { null, "a", "b" }, await selected.OrderBy(c => c.Role).Select(c => c.Role).ToListAsync());
            Assert.Equal(scope.FilmId, Assert.Single(await selected.Where(c => c.Rank == 10).Select(c => c.FilmId).ToListAsync()));
            Assert.Null(Assert.Single(await selected.Where(c => c.Rank == 10).Select(c => EF.Property<string?>(c, "Note")).ToListAsync()));
            minimum = 30;
            Assert.Equal("a", Assert.Single(await selected.Select(c => c.Role).ToListAsync()));
            Assert.Empty(context.ChangeTracker.Entries());
        }
        finally { await CleanupAsync(scope); }
    }

    [Neo4jIntegrationFact]
    public async Task No_tracking_modes_isolate_association_materialization_from_tracked_endpoint_state()
    {
        var scope = Scope.New();
        try
        {
            await SeedEndpointsAsync(scope);
            await SeedCreditAsync(scope, "untracked", 1);
            await using var context = Context();
            var actor = await context.Set<Actor>().SingleAsync(a => a.RunId == scope.RunId);
            var film = await context.Set<Film>().SingleAsync(f => f.RunId == scope.RunId);
            foreach (var identityResolution in new[] { false, true })
            {
                var query = context.Set<Credit>().Where(c => c.ActorRunId == scope.RunId);
                query = identityResolution ? query.AsNoTrackingWithIdentityResolution() : query.AsNoTracking();
                var first = Assert.Single(await query.ToListAsync());
                var second = Assert.Single(await query.ToListAsync());
                Assert.NotSame(first, second);
                Assert.Equal(first.ActorId, second.ActorId);
                Assert.Null(first.Actor);
                Assert.Null(first.Film);
                Assert.Empty(context.ChangeTracker.Entries<Credit>());
                Assert.Empty(actor.Credits);
                Assert.Empty(actor.Films);
                Assert.Empty(film.Credits);
            }
            Assert.Equal(2, context.ChangeTracker.Entries().Count());
        }
        finally { await CleanupAsync(scope); }
    }

    [Neo4jIntegrationFact]
    public async Task Parallel_relationships_fail_before_identity_reuse_even_when_filter_take_or_scalar_select_hides_one()
    {
        var scope = Scope.New();
        try
        {
            await SeedEndpointsAsync(scope);
            await SeedCreditAsync(scope, "selected", 1);
            await using var context = Context();
            var query = context.Set<Credit>().Where(c => c.ActorRunId == scope.RunId && c.Role == "selected").Take(1);
            var tracked = Assert.Single(await query.ToListAsync());
            Assert.Equal("selected", Assert.Single(await query.Select(c => new { c.Role }).ToListAsync()).Role);
            Assert.Single(await query.Select(c => new { }).ToListAsync());
            await SeedCreditAsync(scope, "other", 2);
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => query.ToListAsync());
            Assert.Contains("cardinality diagnostic", failure.Message);
            Assert.Same(tracked, Assert.Single(context.ChangeTracker.Entries<Credit>()).Entity);
            Assert.Equal("selected", tracked.Role);
            Assert.Equal(EntityState.Unchanged, context.Entry(tracked).State);
            await using var fresh = Context();
            var scalar = fresh.Set<Credit>().Where(c => c.ActorRunId == scope.RunId && c.Role == "selected").Take(1).Select(c => c.Role);
            Assert.Contains("cardinality diagnostic", (await Assert.ThrowsAsync<InvalidOperationException>(() => scalar.ToListAsync())).Message);
            Assert.Contains("cardinality diagnostic", (await Assert.ThrowsAsync<InvalidOperationException>(() => query.Select(c => new { c.Role }).ToListAsync())).Message);
            Assert.Contains("cardinality diagnostic", (await Assert.ThrowsAsync<InvalidOperationException>(() => query.Select(c => new { }).ToListAsync())).Message);
            Assert.Empty(fresh.ChangeTracker.Entries());
        }
        finally { await CleanupAsync(scope); }
    }

    [Neo4jIntegrationFact]
    public async Task Missing_endpoint_key_fails_full_and_scalar_reads_despite_existing_uniqueness_constraints()
    {
        var scope = Scope.New();
        try
        {
            await SeedEndpointsAsync(scope, missingActorKey: true);
            await SeedCreditAsync(scope, "invalid endpoint", 1);
            await using var context = Context();
            var query = context.Set<Credit>().Where(c => c.ActorRunId == scope.RunId);
            Assert.Contains("required property", (await Assert.ThrowsAsync<InvalidOperationException>(() => query.ToListAsync())).Message);
            Assert.Contains("required property", (await Assert.ThrowsAsync<InvalidOperationException>(() => query.Select(c => c.Role).ToListAsync())).Message);
            Assert.Contains("required property", (await Assert.ThrowsAsync<InvalidOperationException>(() => query.Select(c => new { }).ToListAsync())).Message);
            Assert.Empty(context.ChangeTracker.Entries());
        }
        finally { await CleanupAsync(scope); }
    }

    [Neo4jIntegrationFact]
    public async Task Missing_endpoint_schema_fails_even_for_an_empty_query_and_does_not_create_constraints()
    {
        await using var context = new MissingSchemaContext(new DbContextOptionsBuilder<MissingSchemaContext>()
            .UseNeo4j(fixture.Driver, fixture.Database).Options);
        var runId = Guid.NewGuid().ToString("N");
        var query = context.Set<Credit>().Where(c => c.ActorRunId == runId).Select(c => c.Role);
        Assert.Contains("__gb_association_count", query.ToQueryString());
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => query.ToListAsync());
        Assert.Contains("existing uniqueness constraint", error.Message);
        Assert.Contains("GraphBridgeQueryAssociationNoSchemaActor", error.Message);
        Assert.Empty(context.ChangeTracker.Entries());
        await using var session = fixture.OpenSession();
        var constraints = await (await session.RunAsync("SHOW CONSTRAINTS YIELD labelsOrTypes RETURN labelsOrTypes")).ToListAsync();
        Assert.DoesNotContain(constraints, record => record["labelsOrTypes"].As<List<string>>().Contains("GraphBridgeQueryAssociationNoSchemaActor"));
    }

    [Neo4jIntegrationFact]
    public async Task Association_queries_borrow_current_transaction_and_cleanup_early_enumeration_and_cancellation()
    {
        var scope = Scope.New();
        try
        {
            await SeedEndpointsAsync(scope);
            await using var context = Context();
            var query = context.Set<Credit>().Where(c => c.ActorRunId == scope.RunId);
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => query.ToListAsync(canceled.Token));
            Assert.Null(context.Database.CurrentTransaction);
            await using (var transaction = await context.Database.BeginTransactionAsync())
            {
                var runner = context.GetService<Neo4jTransactionManager>().CurrentDriverTransaction!;
                await SeedCreditAsync(scope, "uncommitted", 1, runner);
                await using (var enumerator = query.AsAsyncEnumerable().GetAsyncEnumerator())
                {
                    Assert.True(await enumerator.MoveNextAsync());
                    Assert.Equal("uncommitted", enumerator.Current.Role);
                }
                Assert.Same(transaction, context.Database.CurrentTransaction);
                Assert.Equal("uncommitted", Assert.Single(await query.ToListAsync()).Role);
                await transaction.RollbackAsync();
            }
            context.ChangeTracker.Clear();
            Assert.Empty(await query.ToListAsync());
            await SeedCreditAsync(scope, "committed", 2);
            await using (var enumerator = query.AsAsyncEnumerable().GetAsyncEnumerator())
                Assert.True(await enumerator.MoveNextAsync());
            Assert.Equal("committed", Assert.Single(await query.ToListAsync()).Role);
        }
        finally { await CleanupAsync(scope); }
    }

    private AssociationContext Context() => new(new DbContextOptionsBuilder<AssociationContext>()
        .UseNeo4j(fixture.Driver, fixture.Database).Options);

    private async Task SeedEndpointsAsync(Scope scope, bool missingActorKey = false)
    {
        await EnsureSchemaAsync();
        var parameters = Parameters(scope);
        if (missingActorKey) parameters["actorId"] = null!;
        await ExecuteAsync($"CREATE (:{ActorLabel} {{runId:$runId,actor_key:$actorId}})", parameters);
        await SeedFilmAsync(scope);
    }

    private async Task EnsureSchemaAsync()
    {
        // Fixed reviewed fixture-only DDL. Provider queries and writes never create schema.
        await ExecuteAsync($"CREATE CONSTRAINT graphbridge_query_association_actor_key IF NOT EXISTS FOR (n:{ActorLabel}) REQUIRE (n.runId,n.actor_key) IS UNIQUE", new Dictionary<string, object>());
        await ExecuteAsync($"CREATE CONSTRAINT graphbridge_query_association_film_key IF NOT EXISTS FOR (n:{FilmLabel}) REQUIRE (n.runId,n.film_key) IS UNIQUE", new Dictionary<string, object>());
    }

    private async Task AssertNativeFavoriteAsync(Scope scope, string note)
    {
        await using var session = fixture.OpenSession();
        var edges = await (await session.RunAsync(
            $"MATCH (a:{ActorLabel} {{runId:$runId,actor_key:$actorId}})-[r:{FavoriteType}]->(f:{FilmLabel} {{runId:$runId,film_key:$filmId}}) RETURN properties(r) AS properties",
            Parameters(scope))).ToListAsync();
        var properties = Assert.Single(edges)["properties"].As<Dictionary<string, object>>();
        Assert.Equal(note, properties["note"]);
        Assert.Single(properties);
        var nodes = await session.RunAsync(
            "MATCH (n) WHERE n.ActorRunId=$runId AND n.ActorId=$actorId AND n.FilmRunId=$runId AND n.FilmId=$filmId RETURN count(n) AS count",
            Parameters(scope));
        Assert.Equal(0L, (await nodes.SingleAsync())["count"].As<long>());
    }

    private Task SeedFilmAsync(Scope scope)
        => ExecuteAsync($"CREATE (:{FilmLabel} {{runId:$runId,film_key:$filmId}})", Parameters(scope));

    private async Task SeedCreditAsync(Scope scope, string? role, int? rank, IAsyncQueryRunner? runner = null)
    {
        var parameters = Parameters(scope);
        parameters["role"] = role!;
        parameters["rank"] = rank.HasValue ? (object)(long)rank.Value : null!;
        var text = $"MATCH (a:{ActorLabel} {{runId:$runId}}), (f:{FilmLabel} {{runId:$runId,film_key:$filmId}}) CREATE (a)-[r:{EdgeType}]->(f) SET r.role=$role, r.rank=$rank, r.version=1";
        if (runner is not null)
            await (await runner.RunAsync(text, parameters)).ConsumeAsync();
        else
            await ExecuteAsync(text, parameters);
    }

    private async Task ExecuteAsync(string text, object parameters)
    {
        await using var session = fixture.OpenSession();
        await (await session.RunAsync(text, parameters)).ConsumeAsync();
    }

    private async Task CleanupAsync(Scope scope)
    {
        foreach (var type in new[] { EdgeType, FavoriteType })
            await ExecuteAsync($"MATCH (a:{ActorLabel} {{runId:$runId}})-[r:{type}]->(f:{FilmLabel} {{runId:$runId}}) DELETE r", Parameters(scope));
        foreach (var label in new[] { ActorLabel, FilmLabel })
            await ExecuteAsync($"MATCH (n:{label} {{runId:$runId}}) DELETE n", Parameters(scope));
    }

    private static Dictionary<string, object> Parameters(Scope scope) => new()
    {
        ["runId"] = scope.RunId, ["actorId"] = scope.ActorId.ToString("D"), ["filmId"] = scope.FilmId.ToString("D")
    };
    private sealed record Scope(string RunId, Guid ActorId, Guid FilmId)
    {
        public static Scope New() => new(Guid.NewGuid().ToString("N"), Guid.NewGuid(), Guid.NewGuid());
    }

    public sealed class AssociationContext(DbContextOptions<AssociationContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder model) => Configure(model, ActorLabel, FilmLabel);
    }
    public sealed class MissingSchemaContext(DbContextOptions<MissingSchemaContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder model)
            => Configure(model, "GraphBridgeQueryAssociationNoSchemaActor", "GraphBridgeQueryAssociationNoSchemaFilm");
    }

    private static void Configure(ModelBuilder model, string actorLabel, string filmLabel)
    {
        Endpoint(model.Entity<Actor>(), actorLabel, "actor_key");
        Endpoint(model.Entity<Film>(), filmLabel, "film_key");
        model.Entity<Actor>().HasMany(a => a.Films).WithMany(f => f.Actors).UsingEntity<Credit>(
            right => right.HasOne(c => c.Film).WithMany(f => f.Credits).HasForeignKey(c => new { c.FilmRunId, c.FilmId }),
            left => left.HasOne(c => c.Actor).WithMany(a => a.Credits).HasForeignKey(c => new { c.ActorRunId, c.ActorId }),
            join =>
            {
                join.HasKey(c => new { c.ActorRunId, c.ActorId, c.FilmRunId, c.FilmId });
                join.Property(c => c.Role).HasGraphPropertyName("role");
                join.Property(c => c.Rank).HasGraphPropertyName("rank");
                join.Property(c => c.Version).HasGraphPropertyName("version").IsRowVersion();
                join.Property<string?>("Note").HasGraphPropertyName("note");
                join.ToGraphRelationship(EdgeType, [nameof(Credit.ActorRunId), nameof(Credit.ActorId)], [nameof(Credit.FilmRunId), nameof(Credit.FilmId)]);
            });
        model.Entity<Actor>().HasMany(a => a.Favorites).WithMany(f => f.Fans).UsingEntity<Dictionary<string, object>>("Favorite",
            right => right.HasOne<Film>().WithMany().HasForeignKey("FilmRunId", "FilmId"),
            left => left.HasOne<Actor>().WithMany().HasForeignKey("ActorRunId", "ActorId"),
            join =>
            {
                join.HasKey("ActorRunId", "ActorId", "FilmRunId", "FilmId");
                join.IndexerProperty<string?>("Note").HasGraphPropertyName("note");
                join.ToGraphRelationship(FavoriteType, ["ActorRunId", "ActorId"], ["FilmRunId", "FilmId"]);
            });
    }

    private static void Endpoint<T>(EntityTypeBuilder<T> entity, string label, string keyName) where T : class
    {
        entity.ToGraphNode(label);
        entity.HasKey("RunId", "Id");
        entity.Property<string>("RunId").HasGraphPropertyName("runId");
        entity.Property<Guid>("Id").HasGraphPropertyName(keyName).ValueGeneratedNever();
    }

    public sealed class Actor
    {
        public string RunId { get; set; } = "";
        public Guid Id { get; set; }
        public List<Credit> Credits { get; } = [];
        public List<Film> Films { get; } = [];
        public List<Film> Favorites { get; } = [];
    }
    public sealed class Film
    {
        public string RunId { get; set; } = "";
        public Guid Id { get; set; }
        public List<Credit> Credits { get; } = [];
        public List<Actor> Actors { get; } = [];
        public List<Actor> Fans { get; } = [];
    }
    public sealed class Credit
    {
        public string ActorRunId { get; set; } = "";
        public Guid ActorId { get; set; }
        public string FilmRunId { get; set; } = "";
        public Guid FilmId { get; set; }
        public string? Role { get; set; }
        public int? Rank { get; set; }
        public long Version { get; set; }
        public Actor Actor { get; set; } = null!;
        public Film Film { get; set; } = null!;
    }
}
