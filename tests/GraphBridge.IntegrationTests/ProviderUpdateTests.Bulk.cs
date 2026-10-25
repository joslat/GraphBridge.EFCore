using System.Diagnostics;
using GraphBridge.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Neo4j.Driver;

namespace GraphBridge.IntegrationTests;

public sealed partial class ProviderUpdateTests
{
    [Neo4jIntegrationFact]
    public async Task Bulk_update_respects_paging_simultaneous_values_versions_and_stale_tracking()
    {
        var runId = NewRunId();
        try
        {
            await EnsureSchemaAsync();
            await using var context = CreateContext();
            var people = Enumerable.Range(0, 5).Select(i => new UpdatePerson { RunId = runId, Name = "before-" + i, Age = i }).ToArray();
            context.AddRange(people);
            await context.SaveChangesAsync();
            await ExecuteAsync("MATCH (p:GraphBridgeUpdatePerson {runId:$runId}) SET p.externalProperty = 'keep'", new { runId });
            var updated = await context.People.Where(p => p.RunId == runId).OrderBy(p => p.Age).Skip(1).Take(2)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.Name, "after").SetProperty(p => p.Note, p => p.Name)
                    .SetProperty(p => p.Age, p => p.Age + 10));
            Assert.Equal(2, updated);
            Assert.All(people, p => Assert.Equal(1, p.Version));
            Assert.Equal("before-1", people[1].Name);
            await using var fresh = CreateContext();
            var rows = await fresh.People.Where(p => p.RunId == runId).ToListAsync();
            Assert.Equal(5, rows.Count);
            foreach (var index in new[] { 1, 2 })
            {
                var stored = rows.Single(p => p.Id == people[index].Id);
                Assert.Equal("after", stored.Name); Assert.Equal("before-" + index, stored.Note);
                Assert.Equal(index + 10, stored.Age); Assert.Equal(2, stored.Version);
                Assert.Equal("keep", (await StorePropertiesAsync(runId, stored.Id))["externalProperty"]);
            }
            Assert.Equal(3, rows.Count(p => p.Version == 1));
            people[1].Name = "stale-save";
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => context.SaveChangesAsync());
            Assert.Equal("after", (await StorePropertiesAsync(runId, people[1].Id))["name"]);
            Assert.Equal(2, await fresh.People.Where(p => p.RunId == runId && p.Name == "after")
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.Note, (string?)null)));
            Assert.False((await StorePropertiesAsync(runId, people[1].Id)).ContainsKey("note"));
        }
        finally { await CleanupAsync(runId); }
    }

    [Neo4jIntegrationFact]
    public async Task A_node_deleted_in_bulk_can_be_added_again_with_the_same_key_in_the_same_transaction()
    {
        // Found by the GraphRAG toolkit (wave 7): re-ingesting a document deletes its chunks and adds them again under the same keys.
        var runId = NewRunId();
        try
        {
            await EnsureSchemaAsync();
            var person = await InsertAsync(runId, "before");
            await using var context = CreateContext();
            await using (var transaction = await context.Database.BeginTransactionAsync())
            {
                Assert.Equal(1, await context.People.Where(p => p.Id == person.Id).ExecuteDeleteAsync());
                context.Add(new UpdatePerson { Id = person.Id, RunId = runId, Name = "after", Age = 2 });
                Assert.Equal(1, await context.SaveChangesAsync());
                await transaction.CommitAsync();
            }
            await using var fresh = CreateContext();
            var stored = await fresh.People.SingleAsync(p => p.RunId == runId);
            Assert.Equal((person.Id, "after", 2), (stored.Id, stored.Name, stored.Age));
            Assert.False((await StorePropertiesAsync(runId, stored.Id)).ContainsKey("__graphbridge_lock"));
        }
        finally { await CleanupAsync(runId); }
    }

    [Neo4jIntegrationFact]
    public async Task Bulk_delete_is_restrictive_atomic_and_can_borrow_an_explicit_transaction()
    {
        var runId = NewRunId();
        try
        {
            await EnsureSchemaAsync();
            var first = await InsertAsync(runId, "first");
            var second = await InsertAsync(runId, "second");
            await ExecuteAsync("MATCH (a:GraphBridgeUpdatePerson {keyId:$first}), (b:GraphBridgeUpdatePerson {keyId:$second}) CREATE (a)-[:BULK_TEST_LINK]->(b)",
                new { first = first.Id.ToString(), second = second.Id.ToString() });
            await using var context = CreateContext();
            await Assert.ThrowsAsync<DbUpdateException>(() => context.People.Where(p => p.RunId == runId).ExecuteDeleteAsync());
            Assert.Equal(2, await context.People.CountAsync(p => p.RunId == runId));
            await ExecuteAsync("MATCH (a:GraphBridgeUpdatePerson {runId:$runId})-[r:BULK_TEST_LINK]->() DELETE r", new { runId });
            await using (var transaction = await context.Database.BeginTransactionAsync())
            {
                Assert.Equal(2, await context.People.Where(p => p.RunId == runId).ExecuteDeleteAsync());
                Assert.Same(transaction, context.Database.CurrentTransaction);
                Assert.Equal(0, await context.People.CountAsync(p => p.RunId == runId));
                await transaction.RollbackAsync();
            }
            Assert.Equal(2, await context.People.CountAsync(p => p.RunId == runId));
            Assert.Equal(2, await context.People.Where(p => p.RunId == runId).ExecuteDeleteAsync());
            Assert.Equal(0, await context.People.Where(p => p.RunId == runId).ExecuteDeleteAsync());
        }
        finally
        {
            await ExecuteAsync("MATCH (a:GraphBridgeUpdatePerson {runId:$runId})-[r:BULK_TEST_LINK]->() DELETE r", new { runId });
            await CleanupAsync(runId);
        }
    }

    [Neo4jIntegrationFact]
    public async Task Bulk_cancellation_after_mutation_consumption_rolls_back_before_commit()
    {
        var runId = NewRunId();
        try
        {
            await EnsureSchemaAsync();
            var person = await InsertAsync(runId, "unchanged");
            using var cancellation = new CancellationTokenSource();
            var mutations = 0;
            using var listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == GraphBridgeDiagnostics.ActivitySourceName,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
                ActivityStopped = activity =>
                {
                    if (activity.OperationName == GraphBridgeDiagnostics.StatementActivityName
                        && activity.Parent?.OperationName == GraphBridgeDiagnostics.BulkActivityName
                        && Equals(activity.GetTagItem("graphbridge.command.kind"), "mutation")
                        && Interlocked.Increment(ref mutations) == 2) cancellation.Cancel();
                }
            };
            ActivitySource.AddActivityListener(listener);
            await using var context = CreateContext();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => context.People.Where(p => p.RunId == runId)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.Name, "must rollback"), cancellation.Token));
            Assert.Equal(2, mutations);
            Assert.Null(context.Database.CurrentTransaction);
            Assert.Equal("unchanged", (await StorePropertiesAsync(runId, person.Id))["name"]);
            Assert.Equal(1L, (await StorePropertiesAsync(runId, person.Id))["rowVersion"]);
        }
        finally { await CleanupAsync(runId); }
    }

    [Neo4jIntegrationFact]
    public async Task Bulk_integer_overflow_aborts_without_publishing_any_changes()
    {
        var runId = NewRunId();
        try
        {
            await EnsureSchemaAsync();
            var person = await InsertAsync(runId, "unchanged");
            await using var context = CreateContext();
            await Assert.ThrowsAsync<OverflowException>(() => context.People.Where(p => p.RunId == runId)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.Name, "must rollback").SetProperty(p => p.Age, p => p.Age + int.MaxValue)));
            Assert.Equal("unchanged", (await StorePropertiesAsync(runId, person.Id))["name"]);
            Assert.Equal(1L, (await StorePropertiesAsync(runId, person.Id))["rowVersion"]);
        }
        finally { await CleanupAsync(runId); }
    }
}
