using Microsoft.EntityFrameworkCore;
using Neo4j.Driver;
using Xunit.Abstractions;

namespace GraphBridge.IntegrationTests;

[Collection(Neo4jIntegrationCollection.Name)]
public sealed partial class ProviderUpdateTests(DisposableNeo4jFixture fixture, ITestOutputHelper output)
{
    [Neo4jIntegrationFact]
    public async Task Add_saves_one_EF_entry_with_generated_Guid_and_round_trips_in_fresh_context()
    {
        var runId = NewRunId();
        try
        {
            await EnsureSchemaAsync();
            output.WriteLine($"Tracked node persistence on Neo4j {fixture.ServerVersion} {fixture.ServerEdition}; explicit fixture keyId constraint.");
            var person = Person(runId, "inserted");
            await using (var context = CreateContext())
            {
                context.Add(person);
                Assert.NotEqual(Guid.Empty, person.Id);
                Assert.False(context.Entry(person).Property(p => p.Id).IsTemporary);
                Assert.Equal(1, await context.SaveChangesAsync());
                Assert.Equal(EntityState.Unchanged, context.Entry(person).State);
                Assert.Equal(1, person.Version);
                Assert.Equal(0, await context.SaveChangesAsync());
            }

            await using var fresh = CreateContext();
            var stored = Assert.Single(await fresh.People.Where(p => p.RunId == runId).ToListAsync());
            Assert.Equal(person.Id, stored.Id);
            Assert.Equal("inserted", stored.Name);
            Assert.Equal(1, stored.Version);
            Assert.NotSame(person, stored);
        }
        finally { await CleanupAsync(runId); }
    }

    [Neo4jIntegrationFact]
    public async Task Update_writes_only_modified_properties_preserves_unknown_properties_and_removes_nulls()
    {
        var runId = NewRunId();
        try
        {
            await EnsureSchemaAsync();
            var person = await InsertAsync(runId, "before", note: "remove me");
            await using var context = CreateContext();
            var tracked = Assert.Single(await context.People.Where(p => p.RunId == runId).ToListAsync());
            await ExecuteAsync(
                "MATCH (p:GraphBridgeUpdatePerson {runId: $runId, keyId: $id}) SET p.externalProperty = $external, p.age = $age",
                new { runId, id = person.Id.ToString("D"), external = "external survives", age = 99 });
            tracked.Name = "after";
            tracked.Note = null;
            context.ChangeTracker.DetectChanges();
            Assert.True(context.Entry(tracked).Property(p => p.Name).IsModified);
            Assert.True(context.Entry(tracked).Property(p => p.Note).IsModified);
            Assert.False(context.Entry(tracked).Property(p => p.Age).IsModified);
            Assert.Equal(1, await context.SaveChangesAsync());
            Assert.Equal(2, tracked.Version);

            var properties = await StorePropertiesAsync(runId, person.Id);
            Assert.Equal("after", properties["name"].As<string>());
            Assert.Equal(99, properties["age"].As<int>());
            Assert.Equal("external survives", properties["externalProperty"].As<string>());
            Assert.False(properties.ContainsKey("note"));
            await using var fresh = CreateContext();
            var persisted = Assert.Single(await fresh.People.Where(p => p.RunId == runId).ToListAsync());
            Assert.Null(persisted.Note);
            Assert.Equal(99, persisted.Age);
            Assert.Equal(2, persisted.Version);
        }
        finally { await CleanupAsync(runId); }
    }

    [Neo4jIntegrationFact]
    public async Task Node_delete_is_restrictive_and_keeps_surviving_relationship_then_allows_explicit_edge_removal()
    {
        var runId = NewRunId();
        try
        {
            await EnsureSchemaAsync();
            var person = await InsertAsync(runId, "connected");
            await ExecuteAsync(
                "MATCH (p:GraphBridgeUpdatePerson {runId: $runId, keyId: $id}) CREATE (s:GraphBridgeUpdateSentinel {runId: $runId}) CREATE (p)-[:GRAPHBRIDGE_TEST_EDGE]->(s)",
                new { runId, id = person.Id.ToString("D") });
            await using var context = CreateContext();
            var tracked = Assert.Single(await context.People.Where(p => p.RunId == runId).ToListAsync());
            context.Remove(tracked);
            var exception = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
            Assert.Contains(exception.Entries, entry => ReferenceEquals(entry.Entity, tracked));
            Assert.Equal(EntityState.Deleted, context.Entry(tracked).State);
            await using (var fresh = CreateContext())
            {
                Assert.Single(await fresh.People.Where(p => p.RunId == runId).ToListAsync());
            }
            await using (var session = fixture.OpenSession())
            {
                var cursor = await session.RunAsync(
                    "MATCH (:GraphBridgeUpdatePerson {runId: $runId})-[r:GRAPHBRIDGE_TEST_EDGE]->(:GraphBridgeUpdateSentinel {runId: $runId}) RETURN count(r) AS count", new { runId });
                Assert.Equal(1, (await cursor.SingleAsync())["count"].As<long>());
            }

            await DeleteFixtureEdgesAsync(runId);
            Assert.Equal(1, await context.SaveChangesAsync());
            Assert.Equal(EntityState.Detached, context.Entry(tracked).State);
            await using var final = CreateContext();
            Assert.Empty(await final.People.Where(p => p.RunId == runId).ToListAsync());
        }
        finally { await CleanupAsync(runId); }
    }

    [Neo4jIntegrationFact]
    public async Task Two_write_constraint_failure_is_atomic_and_preserves_Added_EF_states()
    {
        var runId = NewRunId();
        try
        {
            await EnsureSchemaAsync();
            var existing = await InsertAsync(runId, "existing");
            var earlier = Person(runId, "must roll back");
            var duplicate = Person(runId, "duplicate");
            duplicate.Id = existing.Id;
            await using var context = CreateContext();
            context.Add(earlier);
            context.Add(duplicate);
            var exception = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
            Assert.NotEmpty(exception.Entries);
            Assert.Equal(EntityState.Added, context.Entry(earlier).State);
            Assert.Equal(EntityState.Added, context.Entry(duplicate).State);
            Assert.Equal(0, earlier.Version);
            Assert.Equal(0, duplicate.Version);
            await using var fresh = CreateContext();
            var persisted = Assert.Single(await fresh.People.Where(p => p.RunId == runId).ToListAsync());
            Assert.Equal(existing.Id, persisted.Id);
            Assert.Equal("existing", persisted.Name);
        }
        finally { await CleanupAsync(runId); }
    }

    [Neo4jIntegrationFact]
    public async Task Missing_update_and_delete_raise_concurrency_with_the_affected_entry()
    {
        foreach (var delete in new[] { false, true })
        {
            var runId = NewRunId();
            try
            {
                await EnsureSchemaAsync();
                var person = await InsertAsync(runId, "will disappear");
                await using var context = CreateContext();
                var tracked = Assert.Single(await context.People.Where(p => p.RunId == runId).ToListAsync());
                await ExecuteAsync("MATCH (p:GraphBridgeUpdatePerson {runId: $runId, keyId: $id}) DELETE p",
                    new { runId, id = person.Id.ToString("D") });
                if (delete) context.Remove(tracked);
                else tracked.Name = "cannot update missing node";
                var exception = await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => context.SaveChangesAsync());
                Assert.Same(tracked, Assert.Single(exception.Entries).Entity);
                Assert.Equal(delete ? EntityState.Deleted : EntityState.Modified, context.Entry(tracked).State);
                Assert.Equal(1, tracked.Version);
            }
            finally { await CleanupAsync(runId); }
        }
    }

    [Neo4jIntegrationFact]
    public async Task Duplicate_application_key_raises_DbUpdateException_with_relevant_entry()
    {
        var runId = NewRunId();
        try
        {
            await EnsureSchemaAsync();
            var existing = await InsertAsync(runId, "original");
            var duplicate = Person(runId, "replacement refused");
            duplicate.Id = existing.Id;
            await using var context = CreateContext();
            context.Add(duplicate);
            var exception = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
            Assert.Same(duplicate, Assert.Single(exception.Entries).Entity);
            Assert.NotNull(exception.InnerException);
            Assert.Equal(EntityState.Added, context.Entry(duplicate).State);
            Assert.Equal(0, duplicate.Version);
            await using var fresh = CreateContext();
            Assert.Equal("original", Assert.Single(await fresh.People.Where(p => p.RunId == runId).ToListAsync()).Name);
        }
        finally { await CleanupAsync(runId); }
    }

    [Neo4jIntegrationFact]
    public async Task Concurrent_two_context_updates_have_exactly_one_rowversion_winner()
    {
        var runId = NewRunId();
        try
        {
            await EnsureSchemaAsync();
            await InsertAsync(runId, "original");
            await using var firstContext = CreateContext();
            await using var secondContext = CreateContext();
            var first = Assert.Single(await firstContext.People.Where(p => p.RunId == runId).ToListAsync());
            var second = Assert.Single(await secondContext.People.Where(p => p.RunId == runId).ToListAsync());
            Assert.Equal(1, first.Version);
            Assert.Equal(first.Version, second.Version);
            first.Name = "first writer";
            second.Name = "second writer";
            async Task<bool> TrySaveAsync(UpdateContext context, UpdatePerson person)
            {
                try
                {
                    Assert.Equal(1, await context.SaveChangesAsync());
                    Assert.Equal(2, person.Version);
                    return true;
                }
                catch (DbUpdateConcurrencyException exception)
                {
                    Assert.Same(person, Assert.Single(exception.Entries).Entity);
                    Assert.Equal(EntityState.Modified, context.Entry(person).State);
                    Assert.Equal(1, person.Version);
                    return false;
                }
            }
            var outcomes = await Task.WhenAll(TrySaveAsync(firstContext, first), TrySaveAsync(secondContext, second));
            Assert.Equal(1, outcomes.Count(success => success));
            await using var fresh = CreateContext();
            var stored = Assert.Single(await fresh.People.Where(p => p.RunId == runId).ToListAsync());
            Assert.Equal(2, stored.Version);
            Assert.Equal(outcomes[0] ? "first writer" : "second writer", stored.Name);
        }
        finally { await CleanupAsync(runId); }
    }

    [Neo4jIntegrationFact]
    public async Task Caller_managed_nullable_concurrency_token_compares_original_value()
    {
        var runId = NewRunId();
        try
        {
            await EnsureSchemaAsync();
            await using (var setup = CreateCallerTokenContext())
            {
                setup.Add(new CallerTokenPerson { RunId = runId, Name = "original", Token = null });
                Assert.Equal(1, await setup.SaveChangesAsync());
            }
            await using var firstContext = CreateCallerTokenContext();
            await using var staleContext = CreateCallerTokenContext();
            var first = Assert.Single(await firstContext.People.Where(p => p.RunId == runId).ToListAsync());
            var stale = Assert.Single(await staleContext.People.Where(p => p.RunId == runId).ToListAsync());
            Assert.Null(first.Token);
            Assert.Null(firstContext.Entry(first).Property(p => p.Token).OriginalValue);
            first.Name = "winner";
            first.Token = "version-2";
            Assert.Equal(1, await firstContext.SaveChangesAsync());
            stale.Name = "stale writer";
            var exception = await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => staleContext.SaveChangesAsync());
            Assert.Same(stale, Assert.Single(exception.Entries).Entity);
            Assert.Equal(EntityState.Modified, staleContext.Entry(stale).State);
            await using var fresh = CreateCallerTokenContext();
            var stored = Assert.Single(await fresh.People.Where(p => p.RunId == runId).ToListAsync());
            Assert.Equal("winner", stored.Name);
            Assert.Equal("version-2", stored.Token);
        }
        finally { await CleanupAsync(runId); }
    }

    [Neo4jIntegrationFact]
    public async Task Explicit_transaction_queries_own_writes_and_multiple_saves_commit_or_roll_back()
    {
        foreach (var commit in new[] { false, true })
        {
            var runId = NewRunId();
            try
            {
                await EnsureSchemaAsync();
                await using var context = CreateContext();
                await using var transaction = await context.Database.BeginTransactionAsync();
                var person = Person(runId, "first save");
                context.Add(person);
                Assert.Equal(1, await context.SaveChangesAsync());
                Assert.Equal(1, person.Version);
                Assert.Same(person, Assert.Single(await context.People.Where(p => p.RunId == runId).ToListAsync()));
                await using (var outside = CreateContext())
                {
                    Assert.Empty(await outside.People.Where(p => p.RunId == runId).ToListAsync());
                }
                person.Name = "second save";
                Assert.Equal(1, await context.SaveChangesAsync());
                Assert.Equal(2, person.Version);
                Assert.Equal(2, Assert.Single(await context.People.Where(p => p.RunId == runId).Select(p => p.Version).ToListAsync()));
                if (commit) await transaction.CommitAsync();
                else await transaction.RollbackAsync();
                Assert.Null(context.Database.CurrentTransaction);
                // EF has accepted each successful save; explicit rollback does not rewind its tracker.
                Assert.Equal(EntityState.Unchanged, context.Entry(person).State);
                Assert.Equal(2, person.Version);
                await using var fresh = CreateContext();
                var stored = await fresh.People.Where(p => p.RunId == runId).ToListAsync();
                if (commit)
                {
                    Assert.Equal("second save", Assert.Single(stored).Name);
                    Assert.Equal(2, stored[0].Version);
                }
                else Assert.Empty(stored);
            }
            finally { await CleanupAsync(runId); }
        }
    }

    [Neo4jIntegrationFact]
    public async Task Failed_save_aborts_the_entire_explicit_transaction_without_orphan_commit()
    {
        var runId = NewRunId();
        try
        {
            await EnsureSchemaAsync();
            var existing = await InsertAsync(runId, "committed before transaction");
            await using var context = CreateContext();
            await using var transaction = await context.Database.BeginTransactionAsync();
            var earlier = Person(runId, "pending first save");
            context.Add(earlier);
            Assert.Equal(1, await context.SaveChangesAsync());
            var duplicate = Person(runId, "failed second save");
            duplicate.Id = existing.Id;
            context.Add(duplicate);
            await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
            Assert.Equal(EntityState.Added, context.Entry(duplicate).State);
            Assert.Equal(0, duplicate.Version);
            Assert.Null(context.Database.CurrentTransaction);
            await Assert.ThrowsAsync<InvalidOperationException>(() => transaction.CommitAsync());
            await using var fresh = CreateContext();
            var persisted = Assert.Single(await fresh.People.Where(p => p.RunId == runId).ToListAsync());
            Assert.Equal(existing.Id, persisted.Id);
            Assert.Equal("committed before transaction", persisted.Name);
        }
        finally { await CleanupAsync(runId); }
    }

    [Neo4jIntegrationFact]
    public async Task Cancellation_before_SaveChanges_keeps_pending_entry_and_no_committed_node()
    {
        var runId = NewRunId();
        try
        {
            await EnsureSchemaAsync();
            await using var context = CreateContext();
            var person = Person(runId, "cancelled then retried");
            context.Add(person);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => context.SaveChangesAsync(cancellation.Token));
            Assert.Equal(EntityState.Added, context.Entry(person).State);
            Assert.Equal(0, person.Version);
            Assert.Null(context.Database.CurrentTransaction);
            await using (var outside = CreateContext())
            {
                Assert.Empty(await outside.People.Where(p => p.RunId == runId).ToListAsync());
            }
            Assert.Equal(1, await context.SaveChangesAsync());
            Assert.Equal(1, person.Version);
            await using var fresh = CreateContext();
            Assert.Equal(person.Id, Assert.Single(await fresh.People.Where(p => p.RunId == runId).ToListAsync()).Id);
        }
        finally { await CleanupAsync(runId); }
    }

    [Neo4jIntegrationFact]
    public async Task Nested_transactions_and_savepoints_are_explicitly_unsupported()
    {
        await using var context = CreateContext();
        await using var transaction = await context.Database.BeginTransactionAsync();
        Assert.False(transaction.SupportsSavepoints);
        var nested = await Assert.ThrowsAsync<InvalidOperationException>(() => context.Database.BeginTransactionAsync());
        Assert.Contains("nested", nested.Message, StringComparison.OrdinalIgnoreCase);
        await Assert.ThrowsAsync<NotSupportedException>(() => transaction.CreateSavepointAsync("test_savepoint"));
        await transaction.RollbackAsync();
        Assert.Null(context.Database.CurrentTransaction);
    }

    [Neo4jIntegrationFact]
    public async Task Missing_unique_key_constraint_refuses_write_without_creating_schema()
    {
        var runId = NewRunId();
        try
        {
            Assert.Equal(0, await MissingSchemaConstraintCountAsync());
            await using var context = new MissingSchemaContext(new DbContextOptionsBuilder<MissingSchemaContext>()
                .UseNeo4j(fixture.Driver, fixture.Database).Options);
            var person = Person(runId, "must not persist");
            context.Add(person);
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync());
            Assert.Contains("GraphBridgeMissingSchemaPerson", exception.Message, StringComparison.Ordinal);
            Assert.Contains("keyId", exception.Message, StringComparison.Ordinal);
            Assert.Equal(EntityState.Added, context.Entry(person).State);
            Assert.Equal(0, person.Version);
            Assert.Null(context.Database.CurrentTransaction);
            Assert.Equal(0, await MissingSchemaConstraintCountAsync());
            Assert.Empty(await context.Set<UpdatePerson>().Where(p => p.RunId == runId).ToListAsync());
        }
        finally { await CleanupAsync(runId); }
    }

    private UpdateContext CreateContext() => new(new DbContextOptionsBuilder<UpdateContext>()
        .UseNeo4j(fixture.Driver, fixture.Database).Options);

    private CallerTokenContext CreateCallerTokenContext() => new(new DbContextOptionsBuilder<CallerTokenContext>()
        .UseNeo4j(fixture.Driver, fixture.Database).Options);

    private async Task<long> MissingSchemaConstraintCountAsync()
    {
        await using var session = fixture.OpenSession();
        var cursor = await session.RunAsync(
            "SHOW CONSTRAINTS YIELD labelsOrTypes WHERE $label IN labelsOrTypes RETURN count(*) AS count",
            new { label = "GraphBridgeMissingSchemaPerson" });
        return (await cursor.SingleAsync())["count"].As<long>();
    }

    private async Task<UpdatePerson> InsertAsync(string runId, string name, string? note = null)
    {
        await using var context = CreateContext();
        var person = Person(runId, name);
        person.Note = note;
        context.Add(person);
        Assert.Equal(1, await context.SaveChangesAsync());
        return person;
    }

    private async Task EnsureSchemaAsync()
    {
        await ExecuteAsync("CREATE CONSTRAINT graphbridge_update_person_key IF NOT EXISTS FOR (p:GraphBridgeUpdatePerson) REQUIRE p.keyId IS UNIQUE");
        await ExecuteAsync("CREATE CONSTRAINT graphbridge_caller_token_person_key IF NOT EXISTS FOR (p:GraphBridgeCallerTokenPerson) REQUIRE p.keyId IS UNIQUE");
    }

    private async Task ExecuteAsync(string cypher, object? parameters = null)
    {
        await using var session = fixture.OpenSession();
        var cursor = parameters is null ? await session.RunAsync(cypher) : await session.RunAsync(cypher, parameters);
        await cursor.ConsumeAsync();
    }

    private async Task<IDictionary<string, object>> StorePropertiesAsync(string runId, Guid id)
    {
        await using var session = fixture.OpenSession();
        var cursor = await session.RunAsync(
            "MATCH (p:GraphBridgeUpdatePerson {runId: $runId, keyId: $id}) RETURN properties(p) AS properties",
            new { runId, id = id.ToString("D") });
        return (await cursor.SingleAsync())["properties"].As<IDictionary<string, object>>();
    }

    private Task DeleteFixtureEdgesAsync(string runId) => ExecuteAsync(
        "MATCH (:GraphBridgeUpdatePerson {runId: $runId})-[r:GRAPHBRIDGE_TEST_EDGE]->(:GraphBridgeUpdateSentinel {runId: $runId}) DELETE r", new { runId });

    private async Task CleanupAsync(string runId)
    {
        await DeleteFixtureEdgesAsync(runId);
        await ExecuteAsync("MATCH (p:GraphBridgeUpdateSentinel {runId: $runId}) DELETE p", new { runId });
        await ExecuteAsync("MATCH (p:GraphBridgeUpdatePerson {runId: $runId}) DELETE p", new { runId });
        await ExecuteAsync("MATCH (p:GraphBridgeCallerTokenPerson {runId: $runId}) DELETE p", new { runId });
        await ExecuteAsync("MATCH (p:GraphBridgeMissingSchemaPerson {runId: $runId}) DELETE p", new { runId });
    }

    private static string NewRunId() => Guid.NewGuid().ToString("N");
    private static UpdatePerson Person(string runId, string name) => new() { RunId = runId, Name = name, Age = 30 };

    public sealed class UpdatePerson
    {
        public Guid Id { get; set; }
        public string RunId { get; set; } = "";
        public string? Name { get; set; }
        public int Age { get; set; }
        public string? Note { get; set; }
        public long Version { get; set; }
    }

    public sealed class UpdateContext(DbContextOptions<UpdateContext> options) : DbContext(options)
    {
        public DbSet<UpdatePerson> People => Set<UpdatePerson>();
        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => ConfigureUpdatePerson(modelBuilder, "GraphBridgeUpdatePerson");
    }

    public sealed class MissingSchemaContext(DbContextOptions<MissingSchemaContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => ConfigureUpdatePerson(modelBuilder, "GraphBridgeMissingSchemaPerson");
    }

    public sealed class CallerTokenPerson
    {
        public Guid Id { get; set; }
        public string RunId { get; set; } = "";
        public string? Name { get; set; }
        public string? Token { get; set; }
    }

    public sealed class CallerTokenContext(DbContextOptions<CallerTokenContext> options) : DbContext(options)
    {
        public DbSet<CallerTokenPerson> People => Set<CallerTokenPerson>();
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var person = modelBuilder.Entity<CallerTokenPerson>();
            person.ToGraphNode("GraphBridgeCallerTokenPerson");
            person.HasKey(p => p.Id);
            person.Property(p => p.Id).HasGraphPropertyName("keyId");
            person.Property(p => p.RunId).HasGraphPropertyName("runId");
            person.Property(p => p.Name).HasGraphPropertyName("name");
            person.Property(p => p.Token).HasGraphPropertyName("token").IsConcurrencyToken();
        }
    }

    private static void ConfigureUpdatePerson(ModelBuilder modelBuilder, string label)
    {
            var person = modelBuilder.Entity<UpdatePerson>();
            person.ToGraphNode(label);
            person.HasKey(p => p.Id);
            person.Property(p => p.Id).HasGraphPropertyName("keyId");
            person.Property(p => p.RunId).HasGraphPropertyName("runId");
            person.Property(p => p.Name).HasGraphPropertyName("name");
            person.Property(p => p.Age).HasGraphPropertyName("age");
            person.Property(p => p.Note).HasGraphPropertyName("note");
            person.Property(p => p.Version).HasGraphPropertyName("rowVersion").IsRowVersion();
    }
}
