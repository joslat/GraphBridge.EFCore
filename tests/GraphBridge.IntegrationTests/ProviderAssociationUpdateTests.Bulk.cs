using Microsoft.EntityFrameworkCore;

namespace GraphBridge.IntegrationTests;

public sealed partial class ProviderAssociationUpdateTests
{
    [Neo4jIntegrationFact]
    public async Task Bulk_association_rechecks_cardinality_after_selection_before_payload_mutation()
    {
        var run = NewRunId();
        var intervened = 0;
        try
        {
            var graph = await InsertGraphAsync(run);
            var driver = CommandBoundaryDriver.Wrap(fixture.Driver, async command =>
            {
                if (command.StartsWith("UNWIND $__gb_bulk_rows", StringComparison.Ordinal) && Interlocked.Exchange(ref intervened, 1) == 0)
                    await ExecuteAsync($"MATCH (s:{ActorLabel} {{actorId:$actor}}), (t:{FilmLabel} {{filmId:$film}}) CREATE (s)-[:{EdgeType} {{role:'concurrent duplicate',version:1}}]->(t)",
                        new { actor = graph.Actor.Id.ToString(), film = graph.Film.Id.ToString() });
            });
            await using var context = new UpdateContext(new DbContextOptionsBuilder<UpdateContext>().UseNeo4j(driver, fixture.Database).Options);
            await Assert.ThrowsAsync<InvalidOperationException>(() => context.Set<Credit>().Where(c => c.ActorId == graph.Actor.Id).Take(1)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.Role, "must not apply")));
            Assert.Equal(1, intervened); Assert.Null(context.Database.CurrentTransaction);
            var rows = await ReadEdgesAsync(run);
            Assert.Equal(2, rows.Count); Assert.DoesNotContain(rows, r => Equals(r["role"], "must not apply"));
        }
        finally { await CleanupAsync(run); }
    }

    [Neo4jIntegrationFact]
    public async Task Bulk_association_update_and_delete_preserve_endpoints_and_stale_tracking()
    {
        var run = NewRunId();
        try
        {
            var graph = await InsertGraphAsync(run);
            await using var context = Context();
            var tracked = await context.Set<Credit>().SingleAsync(c => c.ActorId == graph.Actor.Id && c.FilmId == graph.Film.Id);
            var originalRole = tracked.Role;
            Assert.Equal(1, await context.Set<Credit>().Where(c => c.ActorId == graph.Actor.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.Role, "bulk").SetProperty(c => c.Note, c => c.Role)));
            Assert.Equal(originalRole, tracked.Role); Assert.Equal(1, tracked.Version);
            var stored = Assert.Single(await ReadEdgesAsync(run));
            Assert.Equal("bulk", stored["role"]); Assert.Equal(originalRole, stored["note"]); Assert.Equal(2L, stored["version"]);
            tracked.Role = "stale";
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => context.SaveChangesAsync());
            Assert.Equal(1, await context.Set<Credit>().Where(c => c.ActorId == graph.Actor.Id).ExecuteDeleteAsync());
            Assert.Empty(await ReadEdgesAsync(run)); Assert.Equal(2L, await NodeCountAsync(run));
        }
        finally { await CleanupAsync(run); }
    }

    [Neo4jIntegrationFact]
    public async Task Bulk_association_duplicate_guard_survives_payload_filter_and_take()
    {
        var run = NewRunId();
        try
        {
            var graph = await InsertGraphAsync(run);
            await ExecuteAsync($"MATCH (s:{ActorLabel} {{actorId:$actor}}), (t:{FilmLabel} {{filmId:$film}}) CREATE (s)-[:{EdgeType} {{role:'hidden duplicate',version:1}}]->(t)",
                new { actor = graph.Actor.Id.ToString(), film = graph.Film.Id.ToString() });
            await using var context = Context();
            var query = context.Set<Credit>().Where(c => c.ActorId == graph.Actor.Id && c.Role == graph.Credit.Role).Take(1);
            await Assert.ThrowsAsync<InvalidOperationException>(() => query.ExecuteDeleteAsync());
            await Assert.ThrowsAsync<InvalidOperationException>(() => query.ExecuteUpdateAsync(s => s.SetProperty(c => c.Role, "bad")));
            var stored = await ReadEdgesAsync(run);
            Assert.Equal(2, stored.Count); Assert.DoesNotContain(stored, r => Equals(r["role"], "bad"));
        }
        finally { await CleanupAsync(run); }
    }
}
