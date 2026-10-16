using System.Reflection;
using GraphBridge.EntityFrameworkCore.Schema;
using Microsoft.EntityFrameworkCore;
using Neo4j.Driver;

namespace GraphBridge.UnitTests;

/// <summary>Protocol and ownership tests; server schema behavior is covered by live tests.</summary>
public sealed class GraphSchemaInspectionTests
{
    [Fact]
    public async Task Community_inspection_reads_metadata_without_graph_type_DDL_or_commit()
    {
        var script = new Script();
        await using var db = Context(script);
        var manifest = db.Database.GetGraphSchemaManifest();
        Assert.Empty(script.Calls);
        var report = await db.Database.InspectGraphSchemaAsync();
        Assert.Equal("selected", report.DatabaseName);
        Assert.Equal(manifest.Fingerprint, report.Manifest.Fingerprint);
        Assert.False(report.Capabilities.CanShowCurrentGraphType);
        Assert.Null(report.Observation.GraphTypeSpecification);
        Assert.Equal(3, script.Queries.Count);
        Assert.Equal(1, script.Count("session.BeginTransactionAsync"));
        Assert.Equal(1, script.Count("transaction.RollbackAsync"));
        Assert.Equal(1, script.Count("transaction.DisposeAsync"));
        Assert.Equal(1, script.Count("session.DisposeAsync"));
        Assert.Equal(0, script.Count("transaction.CommitAsync"));
        Assert.Null(db.Database.CurrentTransaction);
    }

    [Theory]
    [InlineData("2026.09.0", "enterprise", true)]
    [InlineData("2026.05.0", "enterprise", false)]
    [InlineData("unknown", "enterprise", false)]
    [InlineData("2026.09.0", "community", false)]
    public async Task Graph_type_query_is_explicitly_versioned_and_capability_gated(string version, string edition, bool expected)
    {
        var script = new Script { Version = version, Edition = edition };
        await using var db = Context(script);
        var report = await db.Database.InspectGraphSchemaAsync();
        Assert.Equal(expected, script.Queries.Any(q => q.StartsWith("CYPHER 25 SHOW CURRENT GRAPH TYPE", StringComparison.Ordinal)));
        Assert.Equal(expected ? "{}" : null, report.Observation.GraphTypeSpecification);
    }

    [Fact]
    public async Task Borrowed_transaction_is_not_completed_or_disposed_even_on_schema_failure()
    {
        var primary = new UnauthorizedAccessException("schema privilege absent");
        var script = new Script { ConstraintFailure = primary };
        await using var db = Context(script);
        await using var transaction = await db.Database.BeginTransactionAsync();
        Assert.Same(primary, await Assert.ThrowsAsync<UnauthorizedAccessException>(() => db.Database.InspectGraphSchemaAsync()));
        Assert.Same(transaction, db.Database.CurrentTransaction);
        Assert.Equal(1, script.Count("driver.AsyncSession"));
        Assert.Equal(0, script.Count("transaction.RollbackAsync"));
        Assert.Equal(0, script.Count("transaction.DisposeAsync"));
        Assert.Equal(0, script.Count("session.DisposeAsync"));
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task Row_cap_throws_without_returning_truncated_schema_and_releases_operation()
    {
        var script = new Script();
        await using var db = Context(script);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => db.Database.InspectGraphSchemaAsync(maxSchemaRows: 1));
        Assert.Contains("maxSchemaRows", error.Message);
        Assert.Single(script.Queries);
        Assert.Equal(1, script.Count("transaction.RollbackAsync"));
        Assert.NotNull(await db.Database.InspectGraphSchemaAsync());
    }

    [Fact]
    public async Task Malformed_metadata_is_an_error_instead_of_an_empty_success_report()
    {
        var script = new Script { MalformedConstraint = true };
        await using var db = Context(script);
        await Assert.ThrowsAnyAsync<Exception>(() => db.Database.InspectGraphSchemaAsync());
        Assert.Equal(1, script.Count("transaction.RollbackAsync"));
        Assert.Equal(1, script.Count("session.DisposeAsync"));
    }

    [Fact]
    public async Task Cancellation_after_driver_fetch_cleans_up_without_abandoning_work()
    {
        using var cancellation = new CancellationTokenSource();
        var script = new Script { OnFetch = cancellation.Cancel };
        await using var db = Context(script);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => db.Database.InspectGraphSchemaAsync(cancellationToken: cancellation.Token));
        Assert.Equal(1, script.Count("cursor.ConsumeAsync"));
        Assert.Equal(1, script.Count("transaction.RollbackAsync"));
        Assert.Equal(1, script.Count("session.DisposeAsync"));
    }

    [Fact]
    public async Task Original_failure_survives_rollback_and_disposal_failures()
    {
        var primary = new IOException("metadata read failed");
        var script = new Script { ConstraintFailure = primary, CleanupFailure = true };
        await using var db = Context(script);
        Assert.Same(primary, await Assert.ThrowsAsync<IOException>(() => db.Database.InspectGraphSchemaAsync()));
        Assert.Equal(1, script.Count("transaction.RollbackAsync"));
        Assert.Equal(1, script.Count("transaction.DisposeAsync"));
        Assert.Equal(1, script.Count("session.DisposeAsync"));
    }

    [Fact]
    public async Task Invalid_cap_and_precancel_do_not_open_driver_resources()
    {
        var script = new Script();
        await using var db = Context(script);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => db.Database.InspectGraphSchemaAsync(0));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => db.Database.InspectGraphSchemaAsync(cancellationToken: new CancellationToken(true)));
        Assert.Empty(script.Calls);
    }

    private static DbContext Context(Script script) => new(new DbContextOptionsBuilder().UseNeo4j(script.Driver, "selected").Options);

    private sealed class Script
    {
        public string Version { get; init; } = "2026.09.0";
        public string Edition { get; init; } = "community";
        public Exception? ConstraintFailure { get; init; }
        public bool MalformedConstraint { get; init; }
        public bool CleanupFailure { get; init; }
        public Action? OnFetch { get; init; }
        public List<string> Calls { get; } = [];
        public List<string> Queries { get; } = [];
        public IDriver Driver { get; }
        public int Count(string name) => Calls.Count(c => c == name);

        public Script()
        {
            var transaction = NativeQueryTests.Proxy.For<IAsyncTransaction>((method, args) =>
            {
                Calls.Add("transaction." + method.Name);
                if (method.Name == "RunAsync")
                {
                    var query = Assert.IsType<string>(args![0]);
                    Queries.Add(query);
                    if (query is GraphSchemaInspector.ConstraintsQuery or GraphSchemaInspector.ConstraintsLegacyQuery && ConstraintFailure is not null)
                        return Task.FromException<IResultCursor>(ConstraintFailure);
                    return Task.FromResult(Cursor(query));
                }
                return Completion(method);
            });
            var session = NativeQueryTests.Proxy.For<IAsyncSession>((method, _) =>
            {
                Calls.Add("session." + method.Name);
                return method.Name == "BeginTransactionAsync" ? Task.FromResult(transaction) : Completion(method);
            });
            Driver = NativeQueryTests.Proxy.For<IDriver>((method, _) =>
            {
                Calls.Add("driver." + method.Name);
                return method.Name == "AsyncSession" ? session : throw new NotSupportedException(method.Name);
            });
        }

        private IResultCursor Cursor(string query)
        {
            Dictionary<string, object>[] rows = query switch
            {
                GraphSchemaInspector.ComponentsQuery =>
                [
                    new() { ["name"] = "Neo4j Kernel", ["versions"] = new[] { Version }, ["edition"] = Edition },
                    new() { ["name"] = "Cypher", ["versions"] = new[] { "5", "25" }, ["edition"] = Edition }
                ],
                GraphSchemaInspector.ConstraintsQuery or GraphSchemaInspector.ConstraintsLegacyQuery => MalformedConstraint ? [new() { ["name"] = "broken" }] : [],
                GraphSchemaInspector.IndexesQuery => [],
                GraphSchemaInspector.GraphTypeQuery => [new() { ["specification"] = "{}" }],
                _ => throw new InvalidOperationException("Unexpected query")
            };
            var index = -1;
            var open = true;
            return NativeQueryTests.Proxy.For<IResultCursor>((method, _) =>
            {
                Calls.Add("cursor." + method.Name);
                switch (method.Name)
                {
                    case "FetchAsync": OnFetch?.Invoke(); return Task.FromResult(++index < rows.Length);
                    case "get_Current": return NativeQueryTests.Proxy.For<IRecord>((m, _) => m.Name == "get_Values" ? rows[index] : throw new NotSupportedException(m.Name));
                    case "get_IsOpen": return open;
                    case "ConsumeAsync": open = false; return Task.FromResult<IResultSummary>(null!);
                    default: throw new NotSupportedException(method.Name);
                }
            });
        }

        private object Completion(MethodInfo method)
        {
            var task = CleanupFailure ? Task.FromException(new IOException("cleanup failed")) : Task.CompletedTask;
            return method.ReturnType == typeof(ValueTask) ? new ValueTask(task) : task;
        }
    }
}
