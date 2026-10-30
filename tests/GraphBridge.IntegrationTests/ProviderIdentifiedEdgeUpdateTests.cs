using Microsoft.EntityFrameworkCore;
using Neo4j.Driver;

namespace GraphBridge.IntegrationTests;

/// <summary>Real EF persistence of relationships identified independently of their endpoints.</summary>
[Collection(Neo4jIntegrationCollection.Name)]
public sealed partial class ProviderIdentifiedEdgeUpdateTests(DisposableNeo4jFixture fixture)
{
    private const string SourceLabel = "GraphBridgeIdentifiedWriteSource";
    private const string TargetLabel = "GraphBridgeIdentifiedWriteTarget";
    private const string Type = "GRAPHBRIDGE_IDENTIFIED_WRITE_EVENT";
    private const string SourceConstraint = "graphbridge_identified_write_source";
    private const string TargetConstraint = "graphbridge_identified_write_target";
    private const string EdgeConstraint = "graphbridge_identified_write_edge";

    [Neo4jIntegrationFact]
    public async Task New_endpoints_and_parallel_edges_save_four_entries_as_two_nodes_and_two_native_relationships()
    {
        var run = NewRun();
        try
        {
            var graph = await InsertGraph(run, parallel: true);
            Assert.NotEqual(graph.First.Id, graph.Second!.Id);
            var stored = await ReadEdges(run);
            Assert.Equal(2, stored.Count);
            Assert.Equal(2L, await NodeCount(run));
            Assert.All(stored, row =>
            {
                Assert.True(Guid.TryParseExact((string)row["edge_key"], "D", out _));
                Assert.Equal(1L, row["version"]);
                Assert.False(row.ContainsKey(nameof(Event.SourceId)));
                Assert.False(row.ContainsKey(nameof(Event.TargetId)));
            });
            Assert.Contains(stored, row => Equals(row["role"], "O'Reilly `$id` MATCH (n) DELETE n"));
            await AssertNoEdgeNodes(graph.First.Id);
        }
        finally { await Cleanup(run); }
    }

    [Neo4jIntegrationFact]
    public async Task Update_and_delete_use_ID_preserving_parallel_edges_endpoints_and_unmapped_properties()
    {
        var run = NewRun();
        try
        {
            var graph = await InsertGraph(run, parallel: true);
            await Execute($"MATCH ()-[r:{Type} {{edge_key:$id}}]->() SET r.external = 'preserved'", new { id = graph.First.Id.ToString("D") });
            await using var context = Context();
            var edge = Copy(graph.First);
            context.Attach(edge);
            edge.Role = "changed";
            edge.Note = null;
            Assert.Equal(1, await context.SaveChangesAsync());
            Assert.Equal(2L, edge.Version);
            var rows = await ReadEdges(run);
            var changed = Assert.Single(rows, row => Equals(row["edge_key"], edge.Id.ToString("D")));
            Assert.Equal("changed", changed["role"]);
            Assert.Equal("preserved", changed["external"]);
            Assert.False(changed.ContainsKey("note"));
            var untouched = Assert.Single(rows, row => Equals(row["edge_key"], graph.Second!.Id.ToString("D")));
            Assert.Equal("parallel", untouched["role"]);
            Assert.Equal(1L, untouched["version"]);
            context.Remove(edge);
            Assert.Equal(1, await context.SaveChangesAsync());
            Assert.Equal(EntityState.Detached, context.Entry(edge).State);
            Assert.Equal(graph.Second!.Id.ToString("D"), Assert.Single(await ReadEdges(run))["edge_key"]);
            Assert.Equal(2L, await NodeCount(run));
        }
        finally { await Cleanup(run); }
    }

    [Neo4jIntegrationFact]
    public async Task Duplicate_ID_at_different_endpoints_fails_CREATE_and_rolls_back_earlier_new_node()
    {
        var run = NewRun();
        try
        {
            var graph = await InsertGraph(run);
            await using var context = Context();
            var target = new Target { RunId = run };
            context.Add(target);
            var duplicate = new Event { Id = graph.First.Id, SourceId = graph.Source.Id, TargetId = target.Id, Role = "must not overwrite" };
            context.Add(duplicate);
            var error = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
            Assert.IsNotType<DbUpdateConcurrencyException>(error);
            Assert.Equal("Neo.ClientError.Schema.ConstraintValidationFailed", Assert.IsAssignableFrom<Neo4jException>(error.InnerException).Code);
            Assert.Contains(error.Entries, entry => ReferenceEquals(entry.Entity, duplicate));
            Assert.Equal(EntityState.Added, context.Entry(target).State);
            Assert.Equal(EntityState.Added, context.Entry(duplicate).State);
            Assert.Equal(0L, duplicate.Version);
            Assert.Null(context.Database.CurrentTransaction);
            Assert.Equal(2L, await NodeCount(run));
            Assert.Equal(graph.First.Role, Assert.Single(await ReadEdges(run))["role"]);
        }
        finally { await Cleanup(run); }
    }

    [Neo4jIntegrationFact]
    public async Task Missing_endpoint_rolls_back_new_node_and_keeps_generated_versions_unpublished()
    {
        var run = NewRun();
        try
        {
            await EnsureSchema();
            await using var context = Context();
            var source = new Source { RunId = run };
            context.Add(source);
            var edge = new Event { SourceId = source.Id, TargetId = Guid.NewGuid() };
            context.Add(edge);
            var error = await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => context.SaveChangesAsync());
            Assert.Same(edge, Assert.Single(error.Entries).Entity);
            // The diagnosis names the command: the lock on the missing target, the second of the edge's two endpoint locks.
            Assert.Equal(("lock 2 of 2", "affected 0"), ((string?)error.Data["GraphBridge.MutationStep"], (string?)error.Data["GraphBridge.MutationResult"]));
            Assert.Equal(0L, source.Version);
            Assert.Equal(0L, edge.Version);
            Assert.Equal(EntityState.Added, context.Entry(edge).State);
            Assert.Equal(0L, await NodeCount(run));
            Assert.Empty(await ReadEdges(run));
        }
        finally { await Cleanup(run); }
    }

    [Neo4jIntegrationFact]
    public async Task Stale_edge_token_aborts_earlier_node_update_without_changing_EF_original_values()
    {
        var run = NewRun();
        try
        {
            var graph = await InsertGraph(run);
            await using (var winner = Context())
            {
                var edge = Copy(graph.First);
                winner.Attach(edge);
                edge.Role = "winner";
                Assert.Equal(1, await winner.SaveChangesAsync());
            }
            await using var loser = Context();
            var source = new Source { Id = graph.Source.Id, RunId = run, Name = "source", Version = 1 };
            var stale = Copy(graph.First);
            loser.AttachRange(source, stale);
            source.Name = "rollback";
            stale.Role = "loser";
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => loser.SaveChangesAsync());
            Assert.Equal(1L, source.Version);
            Assert.Equal(1L, stale.Version);
            Assert.Equal("source", loser.Entry(source).Property(e => e.Name).OriginalValue);
            Assert.Equal(EntityState.Modified, loser.Entry(stale).State);
            Assert.Equal("winner", Assert.Single(await ReadEdges(run))["role"]);
            await using var fresh = Context();
            Assert.Equal("source", (await fresh.Set<Source>().SingleAsync(s => s.Id == source.Id)).Name);
        }
        finally { await Cleanup(run); }
    }

    [Neo4jIntegrationFact]
    public async Task Local_rebind_rejects_and_external_endpoint_change_fails_original_endpoint_guard()
    {
        var run = NewRun();
        try
        {
            var graph = await InsertGraph(run);
            var targetId = Guid.NewGuid();
            await Execute($"CREATE (n:{TargetLabel} {{target_key:$id,run_id:$run}})", new { id = targetId.ToString("D"), run });
            await using (var local = Context())
            {
                var edge = Copy(graph.First);
                local.Attach(edge);
                edge.TargetId = targetId;
                Assert.Contains("rebinding", (await Assert.ThrowsAsync<NotSupportedException>(() => local.SaveChangesAsync())).Message);
                Assert.Equal(1L, edge.Version);
            }
            // Fixture-only external rewrite preserves application ID but moves its physical endpoint.
            await Execute($"MATCH (s:{SourceLabel})-[r:{Type} {{edge_key:$id}}]->(), (t:{TargetLabel} {{target_key:$target}}) " +
                $"WITH s,t,r,properties(r) AS props DELETE r CREATE (s)-[replacement:{Type}]->(t) SET replacement=props",
                new { id = graph.First.Id.ToString("D"), target = targetId.ToString("D") });
            await using var stale = Context();
            var original = Copy(graph.First);
            stale.Attach(original);
            original.Role = "must not match by ID alone";
            var moved = await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => stale.SaveChangesAsync());
            Assert.Equal(("mutation", "affected 0"), ((string?)moved.Data["GraphBridge.MutationStep"], (string?)moved.Data["GraphBridge.MutationResult"]));
            Assert.Equal(1L, original.Version);
            Assert.Equal(graph.First.Role, Assert.Single(await ReadEdges(run))["role"]);
        }
        finally { await Cleanup(run); }
    }

    [Neo4jIntegrationFact]
    public async Task Both_endpoint_constraints_and_type_wide_ID_uniqueness_are_mandatory_before_mutations()
    {
        var run = NewRun();
        try
        {
            await EnsureSchema();
            foreach (var constraint in new[] { SourceConstraint, TargetConstraint, EdgeConstraint })
            {
                await Execute($"DROP CONSTRAINT {constraint}");
                try
                {
                    await using var context = Context();
                    var source = new Source { RunId = run };
                    var target = new Target { RunId = run };
                    context.AddRange(source, target);
                    var edge = new Event { SourceId = source.Id, TargetId = target.Id };
                    context.Add(edge);
                    Assert.Contains("uniqueness constraint", (await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync())).Message);
                    Assert.Equal(0L, source.Version);
                    Assert.Equal(0L, edge.Version);
                    Assert.Equal(EntityState.Added, context.Entry(edge).State);
                    Assert.Equal(0L, await NodeCount(run));
                    await using var session = fixture.OpenSession();
                    var cursor = await session.RunAsync("SHOW CONSTRAINTS YIELD name WHERE name=$name RETURN name", new { name = constraint });
                    Assert.Empty(await cursor.ToListAsync());
                }
                finally { await EnsureSchema(); }
            }
        }
        finally { await Cleanup(run); }
    }

    [Neo4jIntegrationFact]
    public async Task Explicit_transaction_rollback_removes_nodes_and_parallel_edges_from_multiple_saves()
    {
        var run = NewRun();
        try
        {
            await EnsureSchema();
            await using var context = Context();
            await using var transaction = await context.Database.BeginTransactionAsync();
            var source = new Source { RunId = run };
            var target = new Target { RunId = run };
            context.AddRange(source, target);
            var first = new Event { SourceId = source.Id, TargetId = target.Id, Role = "one" };
            context.Add(first);
            Assert.Equal(3, await context.SaveChangesAsync());
            context.Add(new Event { SourceId = source.Id, TargetId = target.Id, Role = "two" });
            Assert.Equal(1, await context.SaveChangesAsync());
            Assert.Same(transaction, context.Database.CurrentTransaction);
            Assert.Empty(await ReadEdges(run));
            await transaction.RollbackAsync();
            Assert.Equal(0L, await NodeCount(run));
            Assert.Empty(await ReadEdges(run));
            Assert.Equal(EntityState.Unchanged, context.Entry(first).State);
            Assert.Equal(1L, first.Version); // Standard EF acceptance is not rewound by an outer rollback.
        }
        finally { await Cleanup(run); }
    }

    [Neo4jIntegrationFact]
    public async Task Competing_updates_of_one_ID_have_one_version_winner()
    {
        var run = NewRun();
        try
        {
            var graph = await InsertGraph(run);
            await using var first = Context();
            await using var second = Context();
            var a = Copy(graph.First);
            var b = Copy(graph.First);
            first.Attach(a); second.Attach(b);
            a.Role = "first"; b.Role = "second";
            var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            async Task<bool> Save(WriteContext context, Event edge)
            {
                await start.Task;
                try { Assert.Equal(1, await context.SaveChangesAsync()); Assert.Equal(2L, edge.Version); return true; }
                catch (DbUpdateConcurrencyException error)
                {
                    Assert.Same(edge, Assert.Single(error.Entries).Entity);
                    Assert.Equal(1L, edge.Version);
                    Assert.Equal(EntityState.Modified, context.Entry(edge).State);
                    return false;
                }
            }
            var one = Save(first, a); var two = Save(second, b);
            start.SetResult(true);
            var result = await Task.WhenAll(one, two);
            Assert.Equal(1, result.Count(success => success));
            var row = Assert.Single(await ReadEdges(run));
            Assert.Equal(result[0] ? "first" : "second", row["role"]);
            Assert.Equal(2L, row["version"]);
        }
        finally { await Cleanup(run); }
    }

    [Neo4jIntegrationFact]
    public Task Concurrent_distinct_ID_creates_allow_parallel_relationships() => AssertConcurrentCreates(sameId: false);

    [Neo4jIntegrationFact]
    public Task Concurrent_duplicate_ID_creates_have_one_constraint_winner() => AssertConcurrentCreates(sameId: true);

    [Neo4jIntegrationFact]
    public async Task An_identified_edge_deleted_and_added_again_with_the_same_key_in_one_transaction_is_written()
    {
        // A tracked delete locks the relationship first. On Neo4j 2026.09 a key written to itself stays taken until the transaction
        // ends, so the lock must not write the key, or adding the same key again in the transaction fails.
        var run = NewRun();
        try
        {
            var graph = await InsertGraph(run);
            await using var context = Context();
            await using (var transaction = await context.Database.BeginTransactionAsync())
            {
                context.Remove(await context.Set<Event>().SingleAsync(e => e.Id == graph.First.Id));
                Assert.Equal(1, await context.SaveChangesAsync());
                context.Add(new Event { Id = graph.First.Id, SourceId = graph.Source.Id, TargetId = graph.Target.Id, Role = "again" });
                Assert.Equal(1, await context.SaveChangesAsync());
                await transaction.CommitAsync();
            }
            var edge = Assert.Single(await ReadEdges(run));
            Assert.Equal((graph.First.Id.ToString("D"), "again", 1L), ((string)edge["edge_key"], (string)edge["role"], (long)edge["version"]));
            Assert.False(edge.ContainsKey("__graphbridge_lock"));
        }
        finally { await Cleanup(run); }
    }

    private async Task AssertConcurrentCreates(bool sameId)
    {
        var run = NewRun();
        try
        {
            await EnsureSchema();
            await using var setup = Context();
            var source = new Source { RunId = run };
            var target = new Target { RunId = run };
            setup.AddRange(source, target);
            Assert.Equal(2, await setup.SaveChangesAsync());
            await using var first = Context();
            await using var second = Context();
            var a = new Event { Id = Guid.NewGuid(), SourceId = source.Id, TargetId = target.Id, Role = "a" };
            var b = new Event { Id = sameId ? a.Id : Guid.NewGuid(), SourceId = source.Id, TargetId = target.Id, Role = "b" };
            first.Add(a); second.Add(b);
            var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            async Task<bool> Save(WriteContext context, Event edge)
            {
                await start.Task;
                try { Assert.Equal(1, await context.SaveChangesAsync()); Assert.Equal(1L, edge.Version); return true; }
                catch (DbUpdateException error) when (sameId && error is not DbUpdateConcurrencyException)
                {
                    Assert.Equal("Neo.ClientError.Schema.ConstraintValidationFailed", Assert.IsAssignableFrom<Neo4jException>(error.InnerException).Code);
                    Assert.Contains(error.Entries, e => ReferenceEquals(e.Entity, edge));
                    Assert.Equal(EntityState.Added, context.Entry(edge).State);
                    Assert.Equal(0L, edge.Version);
                    return false;
                }
            }
            var one = Save(first, a); var two = Save(second, b);
            start.SetResult(true);
            var successes = await Task.WhenAll(one, two);
            Assert.Equal(sameId ? 1 : 2, successes.Count(success => success));
            var rows = await ReadEdges(run);
            Assert.Equal(sameId ? 1 : 2, rows.Count);
            if (sameId) Assert.Equal(successes[0] ? "a" : "b", Assert.Single(rows)["role"]);
            Assert.Equal(2L, await NodeCount(run));
        }
        finally { await Cleanup(run); }
    }

    private WriteContext Context() => new(new DbContextOptionsBuilder<WriteContext>().UseNeo4j(fixture.Driver, fixture.Database).Options);
    private async Task<Graph> InsertGraph(string run, bool parallel = false)
    {
        await EnsureSchema();
        await using var context = Context();
        var source = new Source { RunId = run, Name = "source" };
        var target = new Target { RunId = run };
        context.AddRange(source, target);
        var first = new Event { SourceId = source.Id, TargetId = target.Id, Role = "O'Reilly `$id` MATCH (n) DELETE n", Note = "remove" };
        context.Add(first);
        var second = parallel ? new Event { SourceId = source.Id, TargetId = target.Id, Role = "parallel" } : null;
        if (second is not null) context.Add(second);
        Assert.Equal(parallel ? 4 : 3, await context.SaveChangesAsync());
        Assert.Equal(1L, first.Version);
        Assert.All(context.ChangeTracker.Entries(), entry => Assert.Equal(EntityState.Unchanged, entry.State));
        Assert.Equal(0, await context.SaveChangesAsync());
        return new(source, target, first, second);
    }
    private async Task EnsureSchema()
    {
        await Execute($"CREATE CONSTRAINT {SourceConstraint} IF NOT EXISTS FOR (n:{SourceLabel}) REQUIRE n.source_key IS UNIQUE");
        await Execute($"CREATE CONSTRAINT {TargetConstraint} IF NOT EXISTS FOR (n:{TargetLabel}) REQUIRE n.target_key IS UNIQUE");
        await Execute($"CREATE CONSTRAINT {EdgeConstraint} IF NOT EXISTS FOR ()-[r:{Type}]-() REQUIRE r.edge_key IS UNIQUE");
    }
    private async Task Execute(string text, object? parameters = null)
    {
        await using var session = fixture.OpenSession();
        var cursor = parameters is null ? await session.RunAsync(text) : await session.RunAsync(text, parameters);
        await cursor.ConsumeAsync();
    }
    private async Task<IReadOnlyList<Dictionary<string, object>>> ReadEdges(string run)
    {
        await using var session = fixture.OpenSession();
        var cursor = await session.RunAsync($"MATCH (:{SourceLabel} {{run_id:$run}})-[r:{Type}]->(:{TargetLabel}) RETURN properties(r) AS props", new { run });
        return await cursor.ToListAsync(record => record["props"].As<Dictionary<string, object>>());
    }
    private async Task<long> NodeCount(string run)
    {
        await using var session = fixture.OpenSession();
        var cursor = await session.RunAsync($"MATCH (n) WHERE (n:{SourceLabel} OR n:{TargetLabel}) AND n.run_id=$run RETURN count(n) AS count", new { run });
        return (await cursor.SingleAsync())["count"].As<long>();
    }
    private async Task AssertNoEdgeNodes(Guid id)
    {
        await using var session = fixture.OpenSession();
        var cursor = await session.RunAsync("MATCH (n {edge_key:$id}) RETURN count(n) AS count", new { id = id.ToString("D") });
        Assert.Equal(0L, (await cursor.SingleAsync())["count"].As<long>());
    }
    private async Task Cleanup(string run)
    {
        await Execute($"MATCH (:{SourceLabel} {{run_id:$run}})-[r:{Type}]->(:{TargetLabel}) DELETE r", new { run });
        await Execute($"MATCH (n:{SourceLabel} {{run_id:$run}}) DELETE n", new { run });
        await Execute($"MATCH (n:{TargetLabel} {{run_id:$run}}) DELETE n", new { run });
    }
    private static string NewRun() => Guid.NewGuid().ToString("N");
    private static Event Copy(Event edge) => new()
    {
        Id = edge.Id, SourceId = edge.SourceId, TargetId = edge.TargetId,
        Role = edge.Role, Note = edge.Note, Token = edge.Token, Version = edge.Version
    };
    private sealed record Graph(Source Source, Target Target, Event First, Event? Second);
    public sealed class WriteContext(DbContextOptions<WriteContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder model)
        {
            var source = model.Entity<Source>().ToGraphNode(SourceLabel);
            source.Property(e => e.Id).HasGraphPropertyName("source_key");
            source.Property(e => e.RunId).HasGraphPropertyName("run_id");
            source.Property(e => e.Version).IsRowVersion();
            var target = model.Entity<Target>().ToGraphNode(TargetLabel);
            target.Property(e => e.Id).HasGraphPropertyName("target_key");
            target.Property(e => e.RunId).HasGraphPropertyName("run_id");
            var edge = model.Entity<Event>();
            edge.HasOne<Source>().WithMany().HasForeignKey(e => e.SourceId);
            edge.HasOne<Target>().WithMany().HasForeignKey(e => e.TargetId);
            edge.ToGraphIdentifiedRelationship(Type, nameof(Event.Id), [nameof(Event.SourceId)], [nameof(Event.TargetId)]);
            edge.Property(e => e.Id).HasGraphPropertyName("edge_key");
            edge.Property(e => e.Role).HasGraphPropertyName("role");
            edge.Property(e => e.Note).HasGraphPropertyName("note");
            edge.Property(e => e.Token).HasGraphPropertyName("token").IsConcurrencyToken();
            edge.Property(e => e.Version).HasGraphPropertyName("version").IsRowVersion();
        }
    }
    public sealed class Source { public Guid Id { get; set; } public string RunId { get; set; } = ""; public string Name { get; set; } = ""; public long Version { get; set; } }
    public sealed class Target { public Guid Id { get; set; } public string RunId { get; set; } = ""; }
    public sealed class Event
    {
        public Guid Id { get; set; }
        public Guid SourceId { get; set; }
        public Guid TargetId { get; set; }
        public string? Role { get; set; }
        public string? Note { get; set; }
        public string? Token { get; set; }
        public long Version { get; set; }
    }
}
