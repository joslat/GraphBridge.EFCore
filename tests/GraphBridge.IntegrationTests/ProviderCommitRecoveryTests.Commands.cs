using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Neo4j.Driver;

namespace GraphBridge.IntegrationTests;

public sealed partial class ProviderCommitRecoveryTests
{
    [Neo4jIntegrationFact]
    public async Task Server_deadlock_rolls_back_entire_loser_transaction_and_fresh_retry_applies_once()
    {
        var firstRun = Guid.NewGuid().ToString("N");
        var secondRun = Guid.NewGuid().ToString("N");
        var firstOperation = Guid.NewGuid();
        var secondOperation = Guid.NewGuid();
        try
        {
            await SeedAsync(firstRun);
            await SeedAsync(secondRun);
            var boundedDriver = BoundedTransactionDriver.Wrap(fixture.Driver);
            await using var first = CreateContext(boundedDriver);
            await using var second = CreateContext(boundedDriver);
            await using var firstTransaction = await first.Database.BeginTransactionAsync();
            await using var secondTransaction = await second.Database.BeginTransactionAsync();
            var firstAttempt = await StageAsync(first, firstRun, firstOperation);
            var secondAttempt = await StageAsync(second, secondRun, secondOperation);
            firstAttempt.Receipt.PayloadFingerprint = "increment-pair:v1";
            secondAttempt.Receipt.PayloadFingerprint = "increment-pair:v1";
            Assert.Equal(3, await first.SaveChangesAsync());
            Assert.Equal(3, await second.SaveChangesAsync());

            // Both transactions hold distinct counter locks. Read-committed observations of
            // the opposite node still see its original committed version (1).
            var firstOther = Assert.Single(await first.Counters.Where(c => c.RunId == secondRun).ToListAsync());
            var secondOther = Assert.Single(await second.Counters.Where(c => c.RunId == firstRun).ToListAsync());
            Assert.Equal(1, firstOther.Version);
            Assert.Equal(1, secondOther.Version);
            firstOther.Value++;
            secondOther.Value++;
            async Task<TransientException?> FinishAsync(RecoveryContext context, IDbContextTransaction transaction)
            {
                try
                {
                    Assert.Equal(1, await context.SaveChangesAsync());
                    await transaction.CommitAsync();
                    return null;
                }
                catch (DbUpdateException error)
                {
                    var transient = Assert.IsAssignableFrom<TransientException>(error.InnerException);
                    Assert.Equal("Neo.TransientError.Transaction.DeadlockDetected", transient.Code);
                    Assert.Null(context.Database.CurrentTransaction);
                    return transient;
                }
            }

            var finishingFirst = FinishAsync(first, firstTransaction);
            var observationError = await Record.ExceptionAsync(() => WaitForBlockedCounterAsync(finishingFirst));
            if (observationError is not null)
            {
                // Release the other lock and drain the operation before reporting a harness failure.
                await secondTransaction.RollbackAsync();
                await finishingFirst;
                Assert.Null(observationError);
            }
            var outcomes = await Task.WhenAll(finishingFirst, FinishAsync(second, secondTransaction));
            Assert.Single(outcomes, error => error is not null);
            var firstLost = outcomes[0] is not null;
            var loserRun = firstLost ? firstRun : secondRun;
            var loserOperation = firstLost ? firstOperation : secondOperation;
            var winnerOperation = firstLost ? secondOperation : firstOperation;
            var loserAttempt = firstLost ? firstAttempt : secondAttempt;
            var loserContext = firstLost ? first : second;
            var loserOther = firstLost ? firstOther : secondOther;
            // Earlier SaveChanges inside an explicit transaction already accepted EF state.
            // A later deadlock rolls it back on the server, not in that stale change tracker.
            Assert.Equal(EntityState.Unchanged, loserContext.Entry(loserAttempt.Receipt).State);
            Assert.Equal(EntityState.Modified, loserContext.Entry(loserOther).State);
            await using (var verification = CreateContext())
            {
                foreach (var runId in new[] { firstRun, secondRun })
                {
                    var counter = Assert.Single(await verification.Counters.Where(c => c.RunId == runId).ToListAsync());
                    Assert.Equal(1, counter.Value);
                    Assert.Equal(2, counter.Version);
                }
                Assert.Null(await verification.Effects.FindAsync(loserAttempt.Effect.Id));
                Assert.Equal(ReceiptOutcome.Absent, await ReconcileAsync(verification, loserOperation, "increment-pair:v1"));
                Assert.Equal(ReceiptOutcome.Confirmed, await ReconcileAsync(verification, winnerOperation, "increment-pair:v1"));
            }

            // The real server reported a deadlock abort. Rebuild the complete application
            // operation in a fresh context; do not retry just the last SaveChanges call.
            await using (var retry = CreateContext())
            {
                var attempt = await StageAsync(retry, loserRun, loserOperation);
                attempt.Receipt.PayloadFingerprint = "increment-pair:v1";
                var otherRun = firstLost ? secondRun : firstRun;
                var other = Assert.Single(await retry.Counters.Where(c => c.RunId == otherRun).ToListAsync());
                other.Value++;
                Assert.Equal(4, await retry.SaveChangesAsync());
            }
            await using var final = CreateContext();
            foreach (var runId in new[] { firstRun, secondRun })
            {
                var counter = Assert.Single(await final.Counters.Where(c => c.RunId == runId).ToListAsync());
                Assert.Equal(2, counter.Value);
                Assert.Equal(3, counter.Version);
                Assert.Single(await final.Effects.Where(e => e.RunId == runId).ToListAsync());
            }
            Assert.Equal(ReceiptOutcome.Confirmed, await ReconcileAsync(final, firstOperation, "increment-pair:v1"));
            Assert.Equal(ReceiptOutcome.Confirmed, await ReconcileAsync(final, secondOperation, "increment-pair:v1"));
            output.WriteLine("Actual server deadlock: one transaction abort, one winner; aborted receipt/effect absent; fresh whole-operation retry yields both counters=2, versions=3, one receipt per operation.");
        }
        finally
        {
            await CleanupAsync(firstRun);
            await CleanupAsync(secondRun);
        }
    }

    [Neo4jIntegrationFact]
    public async Task Cancellation_during_server_lock_wait_drains_command_then_rolls_back_all_effects()
    {
        var runId = Guid.NewGuid().ToString("N");
        var operationId = Guid.NewGuid();
        try
        {
            await SeedAsync(runId);
            await using var context = CreateContext();
            var attempt = await StageAsync(context, runId, operationId);
            using var cancellation = new CancellationTokenSource();
            await using var session = fixture.OpenSession();
            await using var blocker = await session.BeginTransactionAsync(c => c.WithTimeout(TimeSpan.FromSeconds(60)));
            await (await blocker.RunAsync(
                "MATCH (n:GraphBridgeRecoveryCounter {RunId: $runId}) SET n.Value = n.Value RETURN n.Id",
                new { runId })).ConsumeAsync();

            // The save creates its effect and receipt, then waits on the real counter lock.
            var saving = context.SaveChangesAsync(cancellation.Token);
            var observationError = await Record.ExceptionAsync(async () =>
            {
                await WaitForBlockedCounterAsync(saving);
                cancellation.Cancel();
                Assert.False(saving.IsCompleted);
                // Reobserve actual server state after cancellation. Driver 6.3 has no token
                // overload for RunAsync: the provider must not abandon this pending command.
                await WaitForBlockedCounterAsync(saving);
            });
            await blocker.RollbackAsync(); // Release only our own transaction and observe save completion.
            var saveError = await Record.ExceptionAsync(() => saving);
            Assert.Null(observationError);
            var cancelled = Assert.IsAssignableFrom<OperationCanceledException>(saveError);
            Assert.Equal(cancellation.Token, cancelled.CancellationToken);
            AssertPending(context, attempt);
            Assert.Null(context.Database.CurrentTransaction);
            await AssertStoreAsync(runId, operationId, 0);

            await using var retry = CreateContext();
            await StageAsync(retry, runId, operationId);
            Assert.Equal(3, await retry.SaveChangesAsync());
            await AssertStoreAsync(runId, operationId, 1);
            output.WriteLine("Cancellation observed during an actual server lock wait; command drained; all earlier writes rolled back; explicit fresh retry committed once.");
        }
        finally { await CleanupAsync(runId); }
    }

    private async Task WaitForBlockedCounterAsync(Task saving)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        await using var observer = fixture.OpenSession();
        while (DateTime.UtcNow < deadline && !saving.IsCompleted)
        {
            // Collection is serialized and these labels belong only to the recovery tests.
            // Exclude the observer itself and return no query text or parameter data.
            var rows = await (await observer.RunAsync(
                "SHOW TRANSACTIONS YIELD currentQuery, status WHERE currentQuery CONTAINS $label AND status STARTS WITH 'Blocked' RETURN status",
                new { label = "GraphBridgeRecoveryCounter" })).ToListAsync();
            if (rows.Count == 1) return;
            Assert.Empty(rows);
            await Task.Delay(50);
        }
        throw new InvalidOperationException("Recovery test did not observe its actual blocked counter command.");
    }
}
