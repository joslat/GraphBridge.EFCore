using System.Reflection;
using GraphBridge.Tooling.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Neo4j.Driver;
using Proxy = GraphBridge.ToolingTests.InspectionTests.Proxy;

namespace GraphBridge.ToolingTests;

public sealed class ConnectedDoctorTests
{
    [Fact]
    public async Task Complete_metadata_reports_actual_profile_and_summarizes_warnings_without_names_or_DDL()
    {
        var protocol = new Protocol();
        using var db = new Context(protocol.Driver);
        var result = await GraphDoctor.InspectAsync(db, new([]), "test.read.v1");
        Assert.Equal(GraphEvidenceStatus.Pass, result.Status);
        Assert.Equal(GraphEvidenceStatus.Pass, result.DatabaseObservation);
        Assert.Equal("2026.09.0", result.Database!.ServerVersion);
        Assert.Equal("community", result.Database.Edition);
        Assert.Contains(result.Diagnostics, d => d.Code == "GBD-SCHEMA-002" && d.Severity == GraphDiagnosticSeverity.Warning);
        Assert.Contains(result.Database.Findings, f => f.Code == "IdentityConstraintPresent");
        foreach (var text in new[] { result.ToJson(), result.ToText() })
        {
            Assert.DoesNotContain("PrivateDoctorLabel", text);
            Assert.DoesNotContain("private_constraint", text);
            Assert.DoesNotContain("private_database", text);
            Assert.DoesNotContain("CREATE", text);
            Assert.Contains("2026.09.0", text);
        }
        Assert.Equal(3, protocol.Queries.Count);
        Assert.StartsWith("CALL dbms.components()", protocol.Queries[0]);
        Assert.StartsWith("SHOW CONSTRAINTS", protocol.Queries[1]);
        Assert.StartsWith("SHOW INDEXES", protocol.Queries[2]);
        protocol.AssertCleanup();
    }

    [Theory]
    [InlineData("permission", "GBD-CONN-002")]
    [InlineData("unreachable", "GBD-CONN-001")]
    [InlineData("unexpected", "GBD-EXEC-002")]
    public async Task Incomplete_or_denied_metadata_never_yields_partial_success(string failure, string code)
    {
        var protocol = new Protocol { Failure = failure switch
        {
            "permission" => new ClientException("Neo.ClientError.Security.Forbidden", "credential-sensitive-message"),
            "unreachable" => new ServiceUnavailableException("credential-sensitive-message"),
            _ => new InvalidOperationException("credential-sensitive-message")
        } };
        using var db = new Context(protocol.Driver);
        var result = await GraphDoctor.InspectAsync(db, new([]), "test.read.v1");
        Assert.Equal(GraphEvidenceStatus.Blocked, result.Status);
        Assert.Equal(GraphEvidenceStatus.NotRun, result.DatabaseObservation);
        Assert.Null(result.Database);
        Assert.Contains(result.Diagnostics, d => d.Code == code);
        Assert.DoesNotContain("credential-sensitive-message", result.ToJson());
        Assert.Equal(2, protocol.Queries.Count); // Failure after components is not retained as a complete profile.
        protocol.AssertCleanup();
    }

    [Fact]
    public async Task Missing_required_constraint_fails_and_unknown_profile_remains_blocked()
    {
        var missing = new Protocol { IncludeConstraint = false };
        using var db = new Context(missing.Driver);
        var failed = await GraphDoctor.InspectAsync(db, new([]), "test.read.v1");
        Assert.Equal(GraphEvidenceStatus.Fail, failed.Status);
        Assert.Contains(failed.Database!.Findings, f => f.Code == "MissingIdentityConstraint");
        missing.AssertCleanup();
        var future = new Protocol { Version = "sensitive-unknown-release" };
        using var other = new Context(future.Driver);
        var blocked = await GraphDoctor.InspectAsync(other, new([]), "test.read.v1");
        Assert.Equal(GraphEvidenceStatus.Blocked, blocked.Status);
        Assert.Equal("unrecognized", blocked.Database!.ServerVersion);
        Assert.Contains(blocked.Diagnostics, d => d.Code == "GBD-CAP-001");
        Assert.DoesNotContain("sensitive-unknown-release", blocked.ToJson());
        future.AssertCleanup();
    }

    [Fact]
    public async Task Cancellation_before_and_during_metadata_is_blocked_and_cleans_up_only_owned_resources()
    {
        var protocol = new Protocol();
        using var db = new Context(protocol.Driver);
        var before = await GraphDoctor.InspectAsync(db, new([]), "test.read.v1", cancellationToken: new CancellationToken(true));
        Assert.Equal(GraphEvidenceStatus.Blocked, before.Status);
        Assert.Empty(protocol.Calls);
        using var cancellation = new CancellationTokenSource();
        protocol.OnRun = cancellation.Cancel;
        var during = await GraphDoctor.InspectAsync(db, new([]), "test.read.v1", cancellationToken: cancellation.Token);
        Assert.Equal(GraphEvidenceStatus.Blocked, during.Status);
        Assert.Contains(during.Diagnostics, d => d.Code == "GBD-EXEC-001");
        Assert.Null(during.Database);
        Assert.Single(protocol.Queries);
        protocol.AssertCleanup();
    }

    [Fact]
    public async Task Row_bound_and_invalid_options_do_not_become_clean_reports()
    {
        var protocol = new Protocol();
        using var db = new Context(protocol.Driver);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => GraphDoctor.InspectAsync(db, new([]), "test.read.v1", maxSchemaRows: 0));
        await Assert.ThrowsAsync<ArgumentException>(() => GraphDoctor.InspectAsync(db, new([]), "secret\nprofile"));
        Assert.Empty(protocol.Calls);
        var result = await GraphDoctor.InspectAsync(db, new([]), "test.read.v1", maxSchemaRows: 1);
        Assert.Equal(GraphEvidenceStatus.Blocked, result.Status);
        Assert.Null(result.Database);
        protocol.AssertCleanup();
    }

    internal sealed class Item { public int Id { get; set; } public string Name { get; set; } = ""; }
    internal sealed class Context(IDriver driver) : DbContext(new DbContextOptionsBuilder().UseNeo4j(driver, "private_database").Options)
    {
        protected override void OnModelCreating(ModelBuilder b)
        {
            b.Entity<Item>().ToGraphNode("PrivateDoctorLabel");
            b.Entity<Item>().Property(p => p.Id).HasGraphPropertyName("id");
        }
    }

    internal sealed class Protocol
    {
        public IDriver Driver { get; }
        public Exception? Failure { get; init; }
        public bool IncludeConstraint { get; init; } = true;
        public string Version { get; init; } = "2026.09.0";
        public Action? OnRun { get; set; }
        public Func<string, IDictionary<string, object>, IResultCursor>? Explain { get; init; }
        public Func<string, IDictionary<string, object>, IResultCursor>? Native { get; init; }
        public Action<string>? OnCleanup { get; init; }
        public List<string> Calls { get; } = [];
        public List<string> Queries { get; } = [];
        public Protocol()
        {
            var transaction = Proxy.For<IAsyncTransaction>((method, args) =>
            {
                Calls.Add("transaction." + method.Name);
                if (method.Name != "RunAsync") { OnCleanup?.Invoke(method.Name); return Complete(method); }
                var query = Assert.IsType<string>(args![0]); Queries.Add(query);
                var parameters = Assert.IsAssignableFrom<IDictionary<string, object>>(args[1]);
                if (Failure is not null && Queries.Count == 2) return Task.FromException<IResultCursor>(Failure);
                OnRun?.Invoke();
                if (query.StartsWith("EXPLAIN ", StringComparison.Ordinal) && Explain is not null)
                    return Task.FromResult(Explain(query, parameters));
                if (!query.StartsWith("CALL dbms.components()", StringComparison.Ordinal)
                    && !query.StartsWith("SHOW ", StringComparison.Ordinal) && Native is not null)
                    return Task.FromResult(Native(query, parameters));
                return Task.FromResult(Cursor(query));
            });
            var session = Proxy.For<IAsyncSession>((method, _) =>
            {
                Calls.Add("session." + method.Name);
                return method.Name == "BeginTransactionAsync" ? Task.FromResult(transaction) : Complete(method);
            });
            Driver = Proxy.For<IDriver>((method, args) =>
            {
                Calls.Add("driver." + method.Name);
                Assert.Equal("AsyncSession", method.Name);
                Assert.IsType<Action<SessionConfigBuilder>>(args![0]);
                return session;
            });
        }
        private static object Complete(MethodInfo method) => method.ReturnType == typeof(ValueTask) ? ValueTask.CompletedTask : Task.CompletedTask;
        public void AssertCleanup(int transactions = 1)
        {
            Assert.Equal(transactions, Calls.Count(c => c == "transaction.RollbackAsync"));
            Assert.Equal(transactions, Calls.Count(c => c == "transaction.DisposeAsync"));
            Assert.Equal(transactions, Calls.Count(c => c == "session.DisposeAsync"));
            Assert.DoesNotContain("transaction.CommitAsync", Calls);
            Assert.DoesNotContain(Calls, c => c.StartsWith("driver.Dispose", StringComparison.Ordinal));
        }
        private IResultCursor Cursor(string query)
        {
            Dictionary<string, object>[] rows = query.StartsWith("CALL dbms.components()", StringComparison.Ordinal)
                ? [new() { ["name"] = "Neo4j Kernel", ["versions"] = new[] { Version }, ["edition"] = "community" },
                   new() { ["name"] = "Cypher", ["versions"] = new[] { "5", "25" }, ["edition"] = "community" }]
                : query.StartsWith("SHOW CONSTRAINTS", StringComparison.Ordinal)
                    ? IncludeConstraint ? [new() { ["name"] = "private_constraint", ["type"] = "NODE_PROPERTY_UNIQUENESS", ["entityType"] = "NODE",
                        ["labelsOrTypes"] = new[] { "PrivateDoctorLabel" }, ["properties"] = new[] { "id" }, ["propertyType"] = null! }] : []
                    : query.StartsWith("SHOW INDEXES", StringComparison.Ordinal) ? [] : throw new InvalidOperationException("Unexpected non-metadata query.");
            var index = -1;
            return Proxy.For<IResultCursor>((method, _) => method.Name switch
            {
                "FetchAsync" => Task.FromResult(++index < rows.Length),
                "get_Current" => Proxy.For<IRecord>((member, _) => member.Name == "get_Values" ? rows[index] : throw new NotSupportedException(member.Name)),
                "get_IsOpen" => true,
                "ConsumeAsync" => Task.FromResult<IResultSummary>(null!),
                _ => throw new NotSupportedException(method.Name)
            });
        }
    }
}
