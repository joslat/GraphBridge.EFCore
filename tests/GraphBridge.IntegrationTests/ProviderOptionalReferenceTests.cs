using Microsoft.EntityFrameworkCore;
using Neo4j.Driver;

namespace GraphBridge.IntegrationTests;

[Collection(Neo4jIntegrationCollection.Name)]
public sealed partial class ProviderOptionalReferenceTests(DisposableNeo4jFixture fixture)
{
    [Neo4jIntegrationFact]
    public async Task Fresh_optional_Includes_preserve_absence_identity_and_paged_collections_in_all_tracking_modes()
    {
        var run = Guid.NewGuid().ToString("N");
        try
        {
            var (parent, other, alone, linked) = await Seed(run);
            foreach (var mode in new[] { QueryTrackingBehavior.TrackAll, QueryTrackingBehavior.NoTracking, QueryTrackingBehavior.NoTrackingWithIdentityResolution })
            {
                await using var db = Context(); db.ChangeTracker.QueryTrackingBehavior = mode;
                var query = db.Set<Child>().Where(c => c.Run == run).Include(c => c.Parent).OrderBy(c => c.Title);
                Assert.DoesNotContain(run, query.ToQueryString());
                var children = await query.ToListAsync();
                Assert.Equal(3, children.Count);
                var orphan = children.Single(c => c.Id == alone.Id);
                Assert.Null(orphan.ParentId); Assert.Null(orphan.Parent);
                var siblings = children.Where(c => c.ParentId == parent.Id).ToArray();
                Assert.Equal(2, siblings.Length);
                Assert.All(siblings, c => Assert.Contains(c, c.Parent!.Children));
                if (mode != QueryTrackingBehavior.NoTracking) Assert.Same(siblings[0].Parent, siblings[1].Parent);
                if (mode == QueryTrackingBehavior.TrackAll)
                {
                    Assert.True(db.Entry(orphan).Reference(c => c.Parent).IsLoaded);
                    Assert.Equal(4, db.ChangeTracker.Entries().Count());
                }
                else Assert.Empty(db.ChangeTracker.Entries());
                var populated = await db.Set<Parent>().Where(p => p.Run == run).OrderBy(p => p.Name).Take(1).Include(p => p.Children).SingleAsync();
                Assert.Equal(parent.Id, populated.Id); Assert.Equal(2, populated.Children.Count);
                Assert.Contains(populated.Children, c => c.Id == linked.Id);
                Assert.All(populated.Children, c => Assert.Same(populated, c.Parent));
                var empty = await db.Set<Parent>().Include(p => p.Children).SingleAsync(p => p.Id == other.Id);
                Assert.Empty(empty.Children);
                var absent = await db.Set<Child>().Where(c => c.Run == run && c.ParentId == null).Select(c => c.Id).ToListAsync();
                Assert.Equal(alone.Id, Assert.Single(absent));
            }
        }
        finally { await Cleanup(run); }
    }

    [Neo4jIntegrationFact]
    public async Task Optional_navigation_attach_replace_remove_and_delete_round_trip_with_entry_counts_and_fixup()
    {
        var run = Guid.NewGuid().ToString("N");
        try
        {
            var (parent, other, alone, _) = await Seed(run);
            await using (var db = Context())
            {
                var child = await db.Set<Child>().Include(c => c.Parent).SingleAsync(c => c.Id == alone.Id);
                var first = await db.Set<Parent>().SingleAsync(p => p.Id == parent.Id);
                child.Parent = first;
                Assert.Equal(1, await db.SaveChangesAsync()); Assert.Equal(2L, child.Version);
                Assert.Contains(child, first.Children);
                var next = new Parent { Run = run, Name = "new principal" };
                db.Add(next); child.Parent = next;
                Assert.Equal(2, await db.SaveChangesAsync()); Assert.Equal(3L, child.Version);
                Assert.DoesNotContain(child, first.Children); Assert.Contains(child, next.Children);
            }
            await using (var db = Context())
            {
                var child = await db.Set<Child>().Include(c => c.Parent).SingleAsync(c => c.Id == alone.Id);
                Assert.Equal("new principal", child.Parent!.Name);
                child.ParentId = other.Id;
                Assert.Equal(1, await db.SaveChangesAsync()); Assert.Equal(4L, child.Version);
            }
            await using (var db = Context())
            {
                var child = await db.Set<Child>().Include(c => c.Parent).SingleAsync(c => c.Id == alone.Id);
                Assert.Equal(other.Id, child.Parent!.Id);
                child.Parent = null;
                Assert.Equal(1, await db.SaveChangesAsync()); Assert.Null(child.ParentId);
            }
            await using (var db = Context())
            {
                var child = await db.Set<Child>().Include(c => c.Parent).SingleAsync(c => c.Id == alone.Id);
                Assert.Null(child.Parent); Assert.Null(child.ParentId); Assert.Equal(5L, child.Version);
                db.Remove(child); Assert.Equal(1, await db.SaveChangesAsync());
            }
            Assert.Equal(0L, await Scalar<long>("MATCH (n:GraphBridgeOptionalChild {Id:$id}) RETURN count(n) AS value", new { id = Wire(alone.Id) }));
            Assert.Equal(0L, await Scalar<long>("MATCH (n:GraphBridgeOptionalChild {Run:$run}) WHERE n.ParentId IS NOT NULL RETURN count(n) AS value", new { run }));
        }
        finally { await Cleanup(run); }
    }

    [Neo4jIntegrationFact]
    public async Task Stale_scalar_version_rolls_back_membership_and_explicit_rollback_restores_original_parent()
    {
        var run = Guid.NewGuid().ToString("N");
        try
        {
            var (parent, other, _, linked) = await Seed(run);
            await using var loser = Context();
            var stale = await loser.Set<Child>().SingleAsync(c => c.Id == linked.Id);
            await using (var winner = Context())
            {
                var child = await winner.Set<Child>().SingleAsync(c => c.Id == linked.Id);
                child.Title = "winner"; Assert.Equal(1, await winner.SaveChangesAsync());
            }
            stale.ParentId = other.Id;
            var error = await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => loser.SaveChangesAsync());
            Assert.Same(stale, Assert.Single(error.Entries).Entity);
            Assert.Equal(1L, stale.Version); Assert.Equal(EntityState.Modified, loser.Entry(stale).State);
            await using (var verify = Context())
            {
                var child = await verify.Set<Child>().SingleAsync(c => c.Id == linked.Id);
                Assert.Equal(parent.Id, child.ParentId); Assert.Equal("winner", child.Title); Assert.Equal(2L, child.Version);
            }
            await using (var db = Context())
            {
                var child = await db.Set<Child>().SingleAsync(c => c.Id == linked.Id);
                await using var transaction = await db.Database.BeginTransactionAsync();
                child.ParentId = null; Assert.Equal(1, await db.SaveChangesAsync());
                Assert.Null(await db.Set<Child>().AsNoTracking().Where(c => c.Id == linked.Id).Select(c => c.ParentId).SingleAsync());
                await transaction.RollbackAsync();
            }
            await using var fresh = Context();
            var restored = await fresh.Set<Child>().SingleAsync(c => c.Id == linked.Id);
            Assert.Equal(parent.Id, restored.ParentId); Assert.Equal(2L, restored.Version);
        }
        finally { await Cleanup(run); }
    }

    [Neo4jIntegrationFact]
    public async Task Missing_new_parent_and_changed_original_membership_fail_without_partial_writes()
    {
        var run = Guid.NewGuid().ToString("N");
        try
        {
            var (parent, other, alone, linked) = await Seed(run);
            await using (var db = Context())
            {
                var child = await db.Set<Child>().SingleAsync(c => c.Id == alone.Id);
                child.ParentId = Guid.NewGuid(); child.Title = "must not persist";
                await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => db.SaveChangesAsync());
            }
            await using (var db = Context())
            {
                var child = await db.Set<Child>().SingleAsync(c => c.Id == linked.Id);
                // Simulate an external membership change without advancing the scalar version.
                await Execute("MATCH (p:GraphBridgeOptionalParent {Id:$parent}), (c:GraphBridgeOptionalChild {Id:$child}) MATCH ()-[r:GRAPHBRIDGE_OPTIONAL_CHILD]->(c) DELETE r CREATE (p)-[:GRAPHBRIDGE_OPTIONAL_CHILD]->(c)", new { parent = Wire(other.Id), child = Wire(linked.Id) });
                child.ParentId = null;
                await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => db.SaveChangesAsync());
                Assert.Equal(parent.Id, db.Entry(child).Property(c => c.ParentId).OriginalValue);
            }
            await using var fresh = Context();
            var untouched = await fresh.Set<Child>().SingleAsync(c => c.Id == alone.Id);
            Assert.Null(untouched.ParentId); Assert.Equal("alone", untouched.Title); Assert.Equal(1L, untouched.Version);
            Assert.Equal(other.Id, (await fresh.Set<Child>().SingleAsync(c => c.Id == linked.Id)).ParentId);
        }
        finally { await Cleanup(run); }
    }

    [Neo4jIntegrationFact]
    public async Task Absent_Include_does_not_mark_a_locally_changed_unresolved_reference_loaded()
    {
        var run = Guid.NewGuid().ToString("N");
        try
        {
            var (parent, _, alone, _) = await Seed(run);
            await using var db = Context();
            var child = await db.Set<Child>().SingleAsync(c => c.Id == alone.Id);
            child.ParentId = parent.Id; db.ChangeTracker.DetectChanges();
            Assert.False(db.Entry(child).Reference(c => c.Parent).IsLoaded);
            Assert.Same(child, await db.Set<Child>().Include(c => c.Parent).SingleAsync(c => c.Id == alone.Id));
            Assert.Equal(parent.Id, child.ParentId); Assert.Null(child.Parent);
            Assert.False(db.Entry(child).Reference(c => c.Parent).IsLoaded);
            Assert.Null(db.Entry(child).Property(c => c.ParentId).OriginalValue);
        }
        finally { await Cleanup(run); }
    }

    [Neo4jIntegrationFact]
    public async Task Optional_read_guards_reject_duplicate_wrong_label_and_missing_principal_keys_before_tracking()
    {
        foreach (var corruption in new[] { "duplicate", "wrong-label", "missing-key" })
        {
            var run = Guid.NewGuid().ToString("N");
            try
            {
                var (parent, _, alone, linked) = await Seed(run);
                if (corruption == "duplicate")
                    await Execute("MATCH (p:GraphBridgeOptionalParent {Id:$parent}), (c:GraphBridgeOptionalChild {Id:$child}) CREATE (p)-[:GRAPHBRIDGE_OPTIONAL_CHILD]->(c)", new { parent = Wire(parent.Id), child = Wire(linked.Id) });
                else
                {
                    await Execute("MATCH ()-[r:GRAPHBRIDGE_OPTIONAL_CHILD]->(c:GraphBridgeOptionalChild {Id:$child}) DELETE r", new { child = Wire(linked.Id) });
                    var label = corruption == "wrong-label" ? "GraphBridgeOptionalForeign" : "GraphBridgeOptionalParent";
                    await Execute($"MATCH (c:GraphBridgeOptionalChild {{Id:$child}}) CREATE (p:{label} {{Run:$run,Name:'invalid'}})-[:GRAPHBRIDGE_OPTIONAL_CHILD]->(c)", new { run, child = Wire(linked.Id) });
                }
                await using var db = Context();
                await Assert.ThrowsAsync<InvalidOperationException>(() => db.Set<Child>().Include(c => c.Parent).SingleAsync(c => c.Id == linked.Id));
                Assert.Empty(db.ChangeTracker.Entries());
                await Assert.ThrowsAsync<InvalidOperationException>(() => db.Set<Child>().Where(c => c.Id == linked.Id).Select(c => c.Title).ToListAsync());
                Assert.Null((await db.Set<Child>().Include(c => c.Parent).SingleAsync(c => c.Id == alone.Id)).Parent);
            }
            finally { await Cleanup(run); }
        }
    }

    [Neo4jIntegrationFact]
    public async Task Nullable_integer_composite_membership_requires_complete_keys_and_round_trips_absence()
    {
        var run = Guid.NewGuid().ToString("N");
        CompositeContext Composite() => new(new DbContextOptionsBuilder<CompositeContext>().UseNeo4j(fixture.Driver, fixture.Database).Options);
        try
        {
            await Execute("CREATE CONSTRAINT graphbridge_optional_composite_parent_key IF NOT EXISTS FOR (n:GraphBridgeOptionalCompositeParent) REQUIRE (n.Tenant,n.Code) IS UNIQUE");
            await Execute("CREATE CONSTRAINT graphbridge_optional_composite_child_key IF NOT EXISTS FOR (n:GraphBridgeOptionalCompositeChild) REQUIRE n.Id IS UNIQUE");
            var parent = new CompositeParent { Tenant = long.MaxValue, Code = run, Run = run };
            var child = new CompositeChild { Run = run };
            await using (var db = Composite())
            {
                db.AddRange(parent, child); Assert.Equal(2, await db.SaveChangesAsync());
                child.ParentTenant = parent.Tenant;
                await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
                child.ParentCode = parent.Code;
                Assert.Equal(1, await db.SaveChangesAsync());
            }
            await using (var db = Composite())
            {
                var loaded = await db.Set<CompositeChild>().Include(c => c.Parent).SingleAsync(c => c.Id == child.Id);
                Assert.Equal(long.MaxValue, loaded.ParentTenant); Assert.Equal(run, loaded.ParentCode);
                Assert.Equal(long.MaxValue, loaded.Parent!.Tenant);
                loaded.Parent = null; Assert.Equal(1, await db.SaveChangesAsync());
            }
            await using (var db = Composite())
            {
                var loaded = await db.Set<CompositeChild>().Include(c => c.Parent).SingleAsync(c => c.Id == child.Id);
                Assert.Null(loaded.ParentTenant); Assert.Null(loaded.ParentCode); Assert.Null(loaded.Parent);
                loaded.ParentTenant = parent.Tenant; loaded.ParentCode = parent.Code;
                Assert.Equal(1, await db.SaveChangesAsync());
            }
            await Execute("MATCH (p:GraphBridgeOptionalCompositeParent {Run:$run}) REMOVE p.Code", new { run });
            await using var corrupt = Composite();
            await Assert.ThrowsAsync<InvalidOperationException>(() => corrupt.Set<CompositeChild>().Where(c => c.Id == child.Id).Select(c => c.Run).ToListAsync());
            Assert.Empty(corrupt.ChangeTracker.Entries());
        }
        finally
        {
            await Execute("MATCH (p:GraphBridgeOptionalCompositeParent)-[r:GRAPHBRIDGE_OPTIONAL_COMPOSITE]->(c:GraphBridgeOptionalCompositeChild) WHERE p.Run=$run AND c.Run=$run DELETE r", new { run });
            await Execute("MATCH (n) WHERE n.Run=$run AND (n:GraphBridgeOptionalCompositeParent OR n:GraphBridgeOptionalCompositeChild) DELETE n", new { run });
        }
    }

    [Neo4jIntegrationFact]
    public async Task Deleting_linked_optional_child_is_restrictive_and_rolls_back_membership_on_external_edge()
    {
        var run = Guid.NewGuid().ToString("N");
        try
        {
            var (parent, _, _, linked) = await Seed(run);
            await Execute("MATCH (p:GraphBridgeOptionalParent {Id:$parent}), (c:GraphBridgeOptionalChild {Id:$child}) CREATE (c)-[:GRAPHBRIDGE_OPTIONAL_EXTERNAL]->(p)", new { parent = Wire(parent.Id), child = Wire(linked.Id) });
            await using (var db = Context())
            {
                var child = await db.Set<Child>().SingleAsync(c => c.Id == linked.Id);
                db.Remove(child); await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
                Assert.Equal(EntityState.Deleted, db.Entry(child).State);
            }
            await using (var fresh = Context())
                Assert.Equal(parent.Id, (await fresh.Set<Child>().SingleAsync(c => c.Id == linked.Id)).ParentId);
            await Execute("MATCH (c:GraphBridgeOptionalChild {Id:$child})-[r:GRAPHBRIDGE_OPTIONAL_EXTERNAL]->() DELETE r", new { child = Wire(linked.Id) });
            await using (var db = Context())
            {
                var child = await db.Set<Child>().SingleAsync(c => c.Id == linked.Id);
                db.Remove(child); Assert.Equal(1, await db.SaveChangesAsync());
            }
            Assert.Equal(0L, await Scalar<long>("MATCH (n:GraphBridgeOptionalChild {Id:$id}) RETURN count(n) AS value", new { id = Wire(linked.Id) }));
        }
        finally { await Cleanup(run); }
    }

    private OptionalContext Context() => new(new DbContextOptionsBuilder<OptionalContext>().UseNeo4j(fixture.Driver, fixture.Database).Options);
    private async Task<(Parent Parent, Parent Other, Child Alone, Child Linked)> Seed(string run)
    {
        await Execute("CREATE CONSTRAINT graphbridge_optional_parent_key IF NOT EXISTS FOR (n:GraphBridgeOptionalParent) REQUIRE n.Id IS UNIQUE");
        await Execute("CREATE CONSTRAINT graphbridge_optional_child_key IF NOT EXISTS FOR (n:GraphBridgeOptionalChild) REQUIRE n.Id IS UNIQUE");
        var parent = new Parent { Run = run, Name = "a populated" };
        var other = new Parent { Run = run, Name = "z empty" };
        var alone = new Child { Run = run, Title = "alone" };
        var linked = new Child { Run = run, Title = "linked one", Parent = parent };
        var sibling = new Child { Run = run, Title = "linked two", Parent = parent };
        await using var db = Context(); db.AddRange(parent, other, alone, linked, sibling);
        Assert.Equal(5, await db.SaveChangesAsync());
        return (parent, other, alone, linked);
    }
    private async Task Execute(string text, object? parameters = null)
    {
        await using var session = fixture.OpenSession();
        await (parameters is null ? await session.RunAsync(text) : await session.RunAsync(text, parameters)).ConsumeAsync();
    }
    private async Task<T> Scalar<T>(string text, object parameters)
    {
        await using var session = fixture.OpenSession();
        return (await (await session.RunAsync(text, parameters)).SingleAsync())["value"].As<T>();
    }
    private async Task Cleanup(string run)
    {
        // Explicitly delete only this suite's mapped links and randomly scoped fixture nodes.
        await Execute("MATCH (c:GraphBridgeOptionalChild)-[r:GRAPHBRIDGE_OPTIONAL_EXTERNAL]->(p:GraphBridgeOptionalParent) WHERE p.Run=$run AND c.Run=$run DELETE r", new { run });
        await Execute("MATCH (p)-[r:GRAPHBRIDGE_OPTIONAL_CHILD]->(c:GraphBridgeOptionalChild) WHERE p.Run=$run AND c.Run=$run DELETE r", new { run });
        await Execute("MATCH (n) WHERE n.Run=$run AND (n:GraphBridgeOptionalParent OR n:GraphBridgeOptionalChild OR n:GraphBridgeOptionalForeign) DELETE n", new { run });
    }
    private static string Wire(Guid id) => id.ToString("D");
    public sealed class OptionalContext(DbContextOptions<OptionalContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder b)
        {
            b.Entity<Parent>().ToGraphNode("GraphBridgeOptionalParent");
            b.Entity<Child>().ToGraphNode("GraphBridgeOptionalChild").Property(c => c.Version).IsRowVersion();
            b.Entity<Child>().HasOne(c => c.Parent).WithMany(p => p.Children).HasForeignKey(c => c.ParentId)
                .IsRequired(false).OnDelete(DeleteBehavior.Restrict).ToGraphReference("GRAPHBRIDGE_OPTIONAL_CHILD");
        }
    }
    public sealed class CompositeContext(DbContextOptions<CompositeContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder b)
        {
            b.Entity<CompositeParent>().ToGraphNode("GraphBridgeOptionalCompositeParent").HasKey(p => new { p.Tenant, p.Code });
            b.Entity<CompositeChild>().ToGraphNode("GraphBridgeOptionalCompositeChild");
            b.Entity<CompositeChild>().HasOne(c => c.Parent).WithMany().HasForeignKey(c => new { c.ParentTenant, c.ParentCode })
                .IsRequired(false).OnDelete(DeleteBehavior.Restrict).ToGraphReference("GRAPHBRIDGE_OPTIONAL_COMPOSITE");
        }
    }
    public sealed class CompositeParent { public long Tenant { get; set; } public string Code { get; set; } = ""; public string Run { get; set; } = ""; }
    public sealed class CompositeChild { public Guid Id { get; set; } public long? ParentTenant { get; set; } public string? ParentCode { get; set; } public CompositeParent? Parent { get; set; } public string Run { get; set; } = ""; }
    public sealed class Parent { public Guid Id { get; set; } public string Run { get; set; } = ""; public string Name { get; set; } = ""; public List<Child> Children { get; } = []; }
    public sealed class Child { public Guid Id { get; set; } public string Run { get; set; } = ""; public string Title { get; set; } = ""; public Guid? ParentId { get; set; } public Parent? Parent { get; set; } public long Version { get; set; } }
}
