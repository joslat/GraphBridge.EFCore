using Microsoft.EntityFrameworkCore;
using Neo4j.Driver;
using Xunit.Abstractions;

namespace GraphBridge.IntegrationTests;

[Collection(Neo4jIntegrationCollection.Name)]
public sealed partial class ProviderCommitRecoveryTests(DisposableNeo4jFixture fixture, ITestOutputHelper output)
{
    private const string Fingerprint = "increment-counter-by-one:v1";

    [Neo4jIntegrationFact]
    public Task Disconnect_before_COMMIT_leaves_no_effect_and_explicit_receipt_retry_commits_once()
        => ExerciseCutAsync(CommitCut.BeforeForwardingCommit);

    [Neo4jIntegrationFact]
    public Task Lost_COMMIT_SUCCESS_leaves_committed_effect_and_receipt_blocks_duplicate_retry()
        => ExerciseCutAsync(CommitCut.AfterServerSuccess);

    private async Task ExerciseCutAsync(CommitCut mode)
    {
        var runId = Guid.NewGuid().ToString("N");
        var operationId = Guid.NewGuid();
        try
        {
            await SeedAsync(runId);
            await using (var proxy = new CommitFaultProxy(fixture))
            await using (var driver = GraphDatabase.Driver(proxy.Uri, AuthTokens.Basic(
                Required("GRAPHBRIDGE_NEO4J_USERNAME"), Required("GRAPHBRIDGE_NEO4J_PASSWORD")),
                config => config.WithMaxConnectionPoolSize(1).WithConnectionTimeout(TimeSpan.FromSeconds(5))))
            await using (var context = CreateContext(CommitBoundaryDriver.Wrap(driver, proxy, mode)))
            {
                var attempt = await StageAsync(context, runId, operationId);
                var error = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
                Assert.IsAssignableFrom<Neo4jException>(error.InnerException);
                Assert.Contains("outcome may be unknown", error.Message);
                var cut = await proxy.ObserveCutAsync();
                Assert.Equal(mode == CommitCut.AfterServerSuccess, cut.CommitForwarded);
                Assert.Equal(mode == CommitCut.AfterServerSuccess, cut.ServerSuccessObserved);
                Assert.Equal(1, proxy.CommitCalls);
                AssertPending(context, attempt);
                Assert.Null(context.Database.CurrentTransaction);
                output.WriteLine($"Verified wire cut: {mode}; COMMIT forwarded={cut.CommitForwarded}; server SUCCESS observed={cut.ServerSuccessObserved}; commit calls={proxy.CommitCalls}.");
            }

            var committed = mode == CommitCut.AfterServerSuccess;
            await AssertStoreAsync(runId, operationId, committed ? 1 : 0);
            await using (var observation = CreateContext())
            {
                var query = observation.Receipts.AsNoTracking().Where(r => r.Id == operationId);
                output.WriteLine(query.ToQueryString());
                Assert.DoesNotContain(operationId.ToString(), query.ToQueryString());
                Assert.Equal(committed ? ReceiptOutcome.Confirmed : ReceiptOutcome.Absent,
                    await ReconcileAsync(observation, operationId, Fingerprint));
            }

            // This is an explicit test replay. Absence alone would not justify retry in an
            // application: the before-cut harness independently proves COMMIT was not sent.
            await using (var retry = CreateContext())
            {
                var attempt = await StageAsync(retry, runId, operationId);
                if (committed)
                {
                    var error = await Assert.ThrowsAsync<DbUpdateException>(() => retry.SaveChangesAsync());
                    Assert.IsAssignableFrom<Neo4jException>(error.InnerException);
                    Assert.Equal(EntityState.Added, retry.Entry(attempt.Effect).State);
                    Assert.Equal(EntityState.Added, retry.Entry(attempt.Receipt).State);
                    Assert.Equal(EntityState.Modified, retry.Entry(attempt.Counter).State);
                    Assert.Equal(2, attempt.Counter.Version); // Fresh attempt saw the first commit.
                    await using var fresh = CreateContext();
                    Assert.Null(await fresh.Effects.FindAsync(attempt.Effect.Id));
                    Assert.Equal(ReceiptOutcome.Confirmed, await ReconcileAsync(fresh, operationId, Fingerprint));
                }
                else
                {
                    Assert.Equal(3, await retry.SaveChangesAsync());
                    Assert.Equal(2, attempt.Counter.Version);
                }
            }
            await AssertStoreAsync(runId, operationId, 1);
            output.WriteLine("Fresh graph verification after reconciliation/retry: counter=1; version=2; receipts=1; effects=1.");
        }
        finally { await CleanupAsync(runId); }
    }

    [Neo4jIntegrationFact]
    public async Task Competing_same_receipt_attempts_have_one_winner_and_no_loser_effect()
    {
        var runId = Guid.NewGuid().ToString("N");
        var operationId = Guid.NewGuid();
        try
        {
            await SeedAsync(runId);
            await using var first = CreateContext();
            await using var second = CreateContext();
            var firstAttempt = await StageAsync(first, runId, operationId);
            var secondAttempt = await StageAsync(second, runId, operationId);
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            async Task<bool> TrySaveAsync(RecoveryContext context, Attempt attempt)
            {
                await start.Task;
                try
                {
                    Assert.Equal(3, await context.SaveChangesAsync());
                    return true;
                }
                catch (DbUpdateException error)
                {
                    // Do not reinterpret arbitrary failures as success. Only the separate
                    // fresh-context receipt check establishes this operation's outcome.
                    Assert.IsAssignableFrom<Neo4jException>(error.InnerException);
                    AssertPending(context, attempt);
                    return false;
                }
            }
            var firstSave = TrySaveAsync(first, firstAttempt);
            var secondSave = TrySaveAsync(second, secondAttempt);
            start.SetResult();
            var winners = await Task.WhenAll(firstSave, secondSave);
            Assert.Single(winners, winner => winner);
            await AssertStoreAsync(runId, operationId, 1);
            await using var fresh = CreateContext();
            Assert.Equal(ReceiptOutcome.Confirmed, await ReconcileAsync(fresh, operationId, Fingerprint));
            var loser = winners[0] ? secondAttempt : firstAttempt;
            Assert.Null(await fresh.Effects.FindAsync(loser.Effect.Id));
            output.WriteLine("Competing receipt attempts: one commit, one rejected attempt; loser effect absent; counter=1, version=2.");
        }
        finally { await CleanupAsync(runId); }
    }

    [Neo4jIntegrationFact]
    public async Task Receipt_payload_conflict_and_unrelated_constraint_failure_are_not_success()
    {
        var runId = Guid.NewGuid().ToString("N");
        var operationId = Guid.NewGuid();
        try
        {
            await SeedAsync(runId);
            Guid existingEffect;
            await using (var initial = CreateContext())
            {
                existingEffect = (await StageAsync(initial, runId, operationId)).Effect.Id;
                Assert.Equal(3, await initial.SaveChangesAsync());
            }
            await using var fresh = CreateContext();
            Assert.Equal(ReceiptOutcome.Conflict, await ReconcileAsync(fresh, operationId, "different-payload"));

            var unrelatedOperation = Guid.NewGuid();
            await using (var attemptContext = CreateContext())
            {
                await StageAsync(attemptContext, runId, unrelatedOperation, existingEffect);
                await Assert.ThrowsAsync<DbUpdateException>(() => attemptContext.SaveChangesAsync());
            }
            Assert.Equal(ReceiptOutcome.Absent, await ReconcileAsync(fresh, unrelatedOperation, Fingerprint));
            await AssertStoreAsync(runId, operationId, 1);
        }
        finally { await CleanupAsync(runId); }
    }

    private static void AssertPending(RecoveryContext context, Attempt attempt)
    {
        Assert.Equal(EntityState.Modified, context.Entry(attempt.Counter).State);
        Assert.Equal(1, context.Entry(attempt.Counter).Property(c => c.Version).OriginalValue);
        Assert.Equal(1, attempt.Counter.Version);
        Assert.Equal(EntityState.Added, context.Entry(attempt.Receipt).State);
        Assert.Equal(EntityState.Added, context.Entry(attempt.Effect).State);
    }

    private static async Task<Attempt> StageAsync(RecoveryContext context, string runId, Guid operationId, Guid? effectId = null)
    {
        var counter = Assert.Single(await context.Counters.Where(c => c.RunId == runId).ToListAsync());
        counter.Value++;
        var effect = new RecoveryEffect { Id = effectId ?? Guid.NewGuid(), RunId = runId, CounterId = counter.Id };
        var receipt = new RecoveryReceipt { Id = operationId, RunId = runId, PayloadFingerprint = Fingerprint, CounterId = counter.Id };
        // An independent mutation precedes the receipt constraint. A rejected duplicate
        // must roll back this earlier CREATE as well as prevent the counter increment.
        context.Add(effect);
        context.Add(receipt);
        return new(counter, effect, receipt);
    }

    private enum ReceiptOutcome { Absent, Confirmed, Conflict }
    private static async Task<ReceiptOutcome> ReconcileAsync(RecoveryContext context, Guid operationId, string fingerprint)
    {
        var receipts = await context.Receipts.AsNoTracking().Where(r => r.Id == operationId).ToListAsync();
        if (receipts.Count == 0) return ReceiptOutcome.Absent;
        return Assert.Single(receipts).PayloadFingerprint == fingerprint ? ReceiptOutcome.Confirmed : ReceiptOutcome.Conflict;
    }

    private async Task AssertStoreAsync(string runId, Guid operationId, int expectedEffects)
    {
        await using var fresh = CreateContext();
        var counter = Assert.Single(await fresh.Counters.AsNoTracking().Where(c => c.RunId == runId).ToListAsync());
        Assert.Equal(expectedEffects, counter.Value);
        Assert.Equal(1 + expectedEffects, counter.Version);
        var receipts = await fresh.Receipts.AsNoTracking().Where(r => r.RunId == runId).ToListAsync();
        var effects = await fresh.Effects.AsNoTracking().Where(e => e.RunId == runId).ToListAsync();
        Assert.Equal(expectedEffects, receipts.Count);
        Assert.Equal(expectedEffects, effects.Count);
        if (expectedEffects == 1)
        {
            Assert.Equal(operationId, receipts[0].Id);
            Assert.Equal(Fingerprint, receipts[0].PayloadFingerprint);
            Assert.Equal(counter.Id, receipts[0].CounterId);
            Assert.Equal(counter.Id, effects[0].CounterId);
        }
    }

    private RecoveryContext CreateContext(IDriver? driver = null)
        => new(new DbContextOptionsBuilder<RecoveryContext>().UseNeo4j(driver ?? fixture.Driver, fixture.Database).Options);

    private async Task SeedAsync(string runId)
    {
        await ExecuteAsync("CREATE CONSTRAINT graphbridge_recovery_counter_key IF NOT EXISTS FOR (n:GraphBridgeRecoveryCounter) REQUIRE n.Id IS UNIQUE");
        await ExecuteAsync("CREATE CONSTRAINT graphbridge_recovery_receipt_key IF NOT EXISTS FOR (n:GraphBridgeRecoveryReceipt) REQUIRE n.Id IS UNIQUE");
        await ExecuteAsync("CREATE CONSTRAINT graphbridge_recovery_effect_key IF NOT EXISTS FOR (n:GraphBridgeRecoveryEffect) REQUIRE n.Id IS UNIQUE");
        await using var context = CreateContext();
        var counter = new RecoveryCounter { Id = Guid.NewGuid(), RunId = runId };
        context.Add(counter);
        Assert.Equal(1, await context.SaveChangesAsync());
        Assert.Equal(1, counter.Version);
    }

    private async Task CleanupAsync(string runId)
    {
        await ExecuteAsync("MATCH (n:GraphBridgeRecoveryEffect {RunId: $runId}) DELETE n", new { runId });
        await ExecuteAsync("MATCH (n:GraphBridgeRecoveryReceipt {RunId: $runId}) DELETE n", new { runId });
        await ExecuteAsync("MATCH (n:GraphBridgeRecoveryCounter {RunId: $runId}) DELETE n", new { runId });
    }

    private async Task ExecuteAsync(string cypher, object? parameters = null)
    {
        await using var session = fixture.OpenSession();
        var cursor = parameters is null ? await session.RunAsync(cypher) : await session.RunAsync(cypher, parameters);
        await cursor.ConsumeAsync();
    }

    private static string Required(string name) => Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException($"Owned fixture variable {name} is required.");
    private sealed record Attempt(RecoveryCounter Counter, RecoveryEffect Effect, RecoveryReceipt Receipt);

    public sealed class RecoveryCounter
    {
        public Guid Id { get; set; }
        public string RunId { get; set; } = "";
        public int Value { get; set; }
        public long Version { get; set; }
    }
    public sealed class RecoveryReceipt
    {
        public Guid Id { get; set; }
        public string RunId { get; set; } = "";
        public string PayloadFingerprint { get; set; } = "";
        public Guid CounterId { get; set; }
    }
    public sealed class RecoveryEffect
    {
        public Guid Id { get; set; }
        public string RunId { get; set; } = "";
        public Guid CounterId { get; set; }
    }
    public sealed class RecoveryContext(DbContextOptions<RecoveryContext> options) : DbContext(options)
    {
        public DbSet<RecoveryCounter> Counters => Set<RecoveryCounter>();
        public DbSet<RecoveryReceipt> Receipts => Set<RecoveryReceipt>();
        public DbSet<RecoveryEffect> Effects => Set<RecoveryEffect>();
        protected override void OnModelCreating(ModelBuilder builder)
        {
            var counter = builder.Entity<RecoveryCounter>();
            counter.ToGraphNode("GraphBridgeRecoveryCounter");
            counter.HasKey(c => c.Id);
            counter.Property(c => c.Id).ValueGeneratedNever();
            counter.Property(c => c.Version).IsRowVersion();
            var receipt = builder.Entity<RecoveryReceipt>();
            receipt.ToGraphNode("GraphBridgeRecoveryReceipt");
            receipt.HasKey(r => r.Id);
            receipt.Property(r => r.Id).ValueGeneratedNever();
            var effect = builder.Entity<RecoveryEffect>();
            effect.ToGraphNode("GraphBridgeRecoveryEffect");
            effect.HasKey(e => e.Id);
            effect.Property(e => e.Id).ValueGeneratedNever();
        }
    }
}
