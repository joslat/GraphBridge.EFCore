using System.Reflection;
using System.Transactions;
using Microsoft.EntityFrameworkCore;
using Neo4j.Driver;

namespace GraphBridge.UnitTests;

public sealed class GraphMigrationFacadeTests
{
    [Fact]
    public async Task Initialization_precancel_does_not_open_resources()
    {
        var driver = ProviderTestDriver.Create();
        await using var db = new DbContext(new DbContextOptionsBuilder().UseNeo4j(driver).Options);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => db.Database.InitializeGraphMigrationsAsync(
            cancellationToken: new CancellationToken(true)));
        Assert.Empty(((ProviderTestDriver)(object)driver).Calls);
    }

    [Fact]
    public async Task Existing_EF_transaction_is_rejected_and_remains_caller_owned()
    {
        var calls = new List<string>();
        object Complete(MethodInfo method)
            => method.ReturnType == typeof(ValueTask) ? ValueTask.CompletedTask : Task.CompletedTask;
        var transaction = NativeQueryTests.Proxy.For<IAsyncTransaction>((method, _) =>
        {
            calls.Add("transaction." + method.Name);
            if (method.Name == "RunAsync") throw new InvalidOperationException("No migration query may execute.");
            return Complete(method);
        });
        var session = NativeQueryTests.Proxy.For<IAsyncSession>((method, _) =>
        {
            calls.Add("session." + method.Name);
            return method.Name == "BeginTransactionAsync" ? Task.FromResult(transaction) : Complete(method);
        });
        var driver = NativeQueryTests.Proxy.For<IDriver>((method, _) =>
        {
            calls.Add("driver." + method.Name);
            return method.Name == "AsyncSession" ? session : throw new NotSupportedException();
        });
        await using var db = new DbContext(new DbContextOptionsBuilder().UseNeo4j(driver).Options);
        await using var existing = await db.Database.BeginTransactionAsync();
        var error = await Assert.ThrowsAsync<NotSupportedException>(() => db.Database.InitializeGraphMigrationsAsync());
        Assert.Contains("active EF transaction", error.Message);
        Assert.Same(existing, db.Database.CurrentTransaction);
        Assert.Equal(new[] { "driver.AsyncSession", "session.BeginTransactionAsync" }, calls);
        await existing.RollbackAsync();
        Assert.Null(db.Database.CurrentTransaction);
    }

    [Fact]
    public async Task Ambient_transaction_is_rejected_before_any_transport()
    {
        var driver = ProviderTestDriver.Create();
        await using var db = new DbContext(new DbContextOptionsBuilder().UseNeo4j(driver).Options);
        using var scope = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled);
        await Assert.ThrowsAsync<NotSupportedException>(() => db.Database.InitializeGraphMigrationsAsync());
        Assert.Empty(((ProviderTestDriver)(object)driver).Calls);
    }

    [Fact]
    public async Task Unconfigured_and_disposed_contexts_fail_before_transport()
    {
        await using var missing = new DbContext(new DbContextOptionsBuilder().Options);
        await Assert.ThrowsAsync<InvalidOperationException>(() => missing.Database.InitializeGraphMigrationsAsync());
        var driver = ProviderTestDriver.Create();
        var disposed = new DbContext(new DbContextOptionsBuilder().UseNeo4j(driver).Options);
        await disposed.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => disposed.Database.InitializeGraphMigrationsAsync());
        Assert.Empty(((ProviderTestDriver)(object)driver).Calls);
    }
}
