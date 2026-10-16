using Microsoft.EntityFrameworkCore;
using Neo4j.Driver;

namespace GraphBridge.IntegrationTests;

public sealed partial class ProviderUpdateTests
{
    [Neo4jIntegrationFact]
    public async Task Bulk_real_competing_write_after_selection_is_preserved_and_aborts_the_stale_batch()
    {
        var run = NewRunId();
        var intervened = 0;
        try
        {
            await EnsureSchemaAsync();
            var person = await InsertAsync(run, "before");
            var driver = CommandBoundaryDriver.Wrap(fixture.Driver, async command =>
            {
                if (command.StartsWith("UNWIND $__gb_bulk_rows", StringComparison.Ordinal) && Interlocked.Exchange(ref intervened, 1) == 0)
                    await ExecuteAsync("MATCH (p:GraphBridgeUpdatePerson {keyId:$id}) SET p.name='concurrent winner'", new { id = person.Id.ToString() });
            });
            await using var context = new UpdateContext(new DbContextOptionsBuilder<UpdateContext>().UseNeo4j(driver, fixture.Database).Options);
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => context.People.Where(p => p.RunId == run)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.Name, "stale loser")));
            Assert.Equal(1, intervened); Assert.Null(context.Database.CurrentTransaction);
            var stored = await StorePropertiesAsync(run, person.Id);
            Assert.Equal("concurrent winner", stored["name"]); Assert.Equal(1L, stored["rowVersion"]);
        }
        finally { await CleanupAsync(run); }
    }

    [Neo4jIntegrationFact]
    public async Task Bulk_live_row_cap_aborts_whole_selection_and_explicit_take_applies_only_its_batch()
    {
        var run = NewRunId();
        try
        {
            await EnsureSchemaAsync();
            var ids = Enumerable.Range(0, 10_001).Select(_ => Guid.NewGuid().ToString()).ToArray();
            await ExecuteAsync("UNWIND range(0,10000) AS i CREATE (:GraphBridgeUpdatePerson {keyId:$ids[i],runId:$run,name:'before',age:i,rowVersion:1})", new { ids, run });
            await using var context = CreateContext();
            await Assert.ThrowsAsync<InvalidOperationException>(() => context.People.Where(p => p.RunId == run)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.Name, "too large")));
            Assert.Null(context.Database.CurrentTransaction);
            Assert.Equal(10_001, await context.People.CountAsync(p => p.RunId == run && p.Name == "before" && p.Version == 1));
            Assert.Equal(10, await context.People.Where(p => p.RunId == run).OrderBy(p => p.Age).Take(10)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.Name, "bounded")));
            Assert.Equal(10, await context.People.CountAsync(p => p.RunId == run && p.Name == "bounded" && p.Version == 2));
            Assert.Equal(9_991, await context.People.CountAsync(p => p.RunId == run && p.Name == "before" && p.Version == 1));
            Assert.Empty(context.ChangeTracker.Entries());
        }
        finally { await CleanupAsync(run); }
    }
}
