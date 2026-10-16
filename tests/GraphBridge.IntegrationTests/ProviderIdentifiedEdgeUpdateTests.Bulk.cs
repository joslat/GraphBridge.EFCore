using Microsoft.EntityFrameworkCore;

namespace GraphBridge.IntegrationTests;

public sealed partial class ProviderIdentifiedEdgeUpdateTests
{
    [Neo4jIntegrationFact]
    public async Task Bulk_identified_edge_operations_target_application_identity_and_preserve_parallel_edges()
    {
        var run = NewRun();
        try
        {
            var graph = await InsertGraph(run, parallel: true);
            await using var context = Context();
            await using (var transaction = await context.Database.BeginTransactionAsync())
            {
                Assert.Equal(1, await context.Set<Event>().Where(e => e.Id == graph.First.Id)
                    .ExecuteUpdateAsync(s => s.SetProperty(e => e.Role, "rolled back")));
                Assert.Same(transaction, context.Database.CurrentTransaction);
                await transaction.RollbackAsync();
            }
            Assert.DoesNotContain(await ReadEdges(run), r => Equals(r["role"], "rolled back"));
            Assert.Equal(1, await context.Set<Event>().Where(e => e.Id == graph.First.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.Role, "bulk").SetProperty(e => e.Note, (string?)null)));
            var rows = await ReadEdges(run);
            Assert.Equal(2, rows.Count);
            var first = rows.Single(r => Equals(r["edge_key"], graph.First.Id.ToString()));
            Assert.Equal("bulk", first["role"]); Assert.False(first.ContainsKey("note")); Assert.Equal(2L, first["version"]);
            Assert.Equal(1L, rows.Single(r => Equals(r["edge_key"], graph.Second!.Id.ToString()))["version"]);
            Assert.Equal(1, await context.Set<Event>().Where(e => e.Id == graph.First.Id).ExecuteDeleteAsync());
            Assert.Equal(graph.Second!.Id.ToString(), Assert.Single(await ReadEdges(run))["edge_key"]);
            Assert.Equal(2L, await NodeCount(run));
        }
        finally { await Cleanup(run); }
    }

    [Neo4jIntegrationFact]
    public async Task Bulk_identified_edge_requires_type_wide_key_constraint_before_mutating()
    {
        var run = NewRun();
        try
        {
            var graph = await InsertGraph(run);
            await Execute($"DROP CONSTRAINT {EdgeConstraint}");
            await using var context = Context();
            await Assert.ThrowsAsync<InvalidOperationException>(() => context.Set<Event>().Where(e => e.Id == graph.First.Id).ExecuteDeleteAsync());
            Assert.Single(await ReadEdges(run));
        }
        finally { await EnsureSchema(); await Cleanup(run); }
    }
}
