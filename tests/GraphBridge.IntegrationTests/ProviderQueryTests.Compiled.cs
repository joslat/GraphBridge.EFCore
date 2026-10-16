using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace GraphBridge.IntegrationTests;

public sealed partial class ProviderQueryTests
{
    [Neo4jIntegrationFact]
    public async Task Explicit_compiled_async_query_reuses_translation_with_fresh_contexts_and_parameters()
    {
        var runId = NewRunId();
        var compilations = 0;
        try
        {
            await SeedAsync(runId, Person("one", 1), Person("two", 2), Person("three", 3));
            var options = new DbContextOptionsBuilder<QueryContext>().UseNeo4j(fixture.Driver, fixture.Database)
                .LogTo((id, _) => id == CoreEventId.QueryCompilationStarting, _ => compilations++).Options;
            var compiled = EF.CompileAsyncQuery((QueryContext db, string run, int minimum) => db.People
                .AsNoTracking().Where(p => p.RunId == run && p.Age >= minimum).OrderBy(p => p.Age).Select(p => p.Age));
            for (var minimum = 0; minimum < 5; minimum++)
            {
                await using var context = new QueryContext(options);
                var actual = new List<int>();
                await foreach (var age in compiled(context, runId, minimum)) actual.Add(age);
                Assert.Equal(new[] { 1, 2, 3 }.Where(age => age >= minimum), actual);
                Assert.Empty(context.ChangeTracker.Entries());
            }
            Assert.Equal(1, compilations);
            await using var emptyContext = new QueryContext(options);
            var missing = new List<int>();
            await foreach (var age in compiled(emptyContext, NewRunId(), 0)) missing.Add(age);
            Assert.Empty(missing);
            Assert.Equal(1, compilations);
        }
        finally { await CleanupAsync(runId); }
    }

    [Neo4jIntegrationFact]
    public async Task Compiled_async_stream_materializes_on_demand_and_preserves_cancellation_and_borrowed_transaction()
    {
        var runId = NewRunId();
        try
        {
            await SeedAsync(runId, Enumerable.Range(0, 128).Select(i => Person("row-" + i, i)).ToArray());
            var compiled = EF.CompileAsyncQuery((QueryContext db, string run) => db.People
                .Where(p => p.RunId == run).OrderBy(p => p.Age).Select(p => p));
            await using var context = CreateContext();
            await using var transaction = await context.Database.BeginTransactionAsync();
            using var cancellation = new CancellationTokenSource();
            await using (var stream = compiled(context, runId).GetAsyncEnumerator(cancellation.Token))
            {
                Assert.Empty(context.ChangeTracker.Entries());
                Assert.True(await stream.MoveNextAsync());
                var first = stream.Current;
                Assert.Equal(0, first.Age);
                Assert.Single(context.ChangeTracker.Entries());
                Assert.True(await stream.MoveNextAsync());
                Assert.Equal(1, stream.Current.Age);
                Assert.Equal(0, first.Age);
                Assert.NotSame(first, stream.Current);
                Assert.Equal(2, context.ChangeTracker.Entries().Count());
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await stream.MoveNextAsync());
            }
            Assert.Same(transaction, context.Database.CurrentTransaction);
            Assert.Equal(128, await context.People.Where(p => p.RunId == runId).CountAsync());
            Assert.Equal(2, context.ChangeTracker.Entries().Count());
            await transaction.RollbackAsync();
        }
        finally { await CleanupAsync(runId); }
    }
}
