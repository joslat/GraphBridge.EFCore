using System.Globalization;
using GraphBridge.Tooling.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Neo4j.Driver;
using Graph = GraphBridge.EntityFrameworkCore.FluentCypher.Cypher;
using Proxy = GraphBridge.ToolingTests.InspectionTests.Proxy;
using Context = GraphBridge.ToolingTests.ConnectedDoctorTests.Context;
using Item = GraphBridge.ToolingTests.ConnectedDoctorTests.Item;
using Protocol = GraphBridge.ToolingTests.ConnectedDoctorTests.Protocol;

namespace GraphBridge.ToolingTests;

public sealed class GraphVerifyTests
{
    [Fact]
    public void Offline_success_means_compilation_only_and_empty_or_failed_required_catalog_cannot_pass()
    {
        var protocol = new Protocol();
        using var db = new Context(protocol.Driver);
        var good = GraphVerify.Offline(db, new([Contract("test.good.v1")]));
        Assert.Equal(GraphVerificationTier.V0, good.Tier);
        Assert.Equal(GraphEvidenceStatus.Pass, good.Status);
        Assert.Equal(0, good.ExitCode);
        Assert.Equal(1, good.RequiredContractCount);
        Assert.Equal(GraphEvidenceStatus.NotRun, good.Contracts[0].Planning);
        Assert.Equal(GraphEvidenceStatus.NotRun, good.Contracts[0].Semantics);
        Assert.Equal(GraphEvidenceStatus.NotRun, good.Evidence.DatabaseObservation);
        Assert.Contains("test.good.v1 | Pass | NotRun | NotRun", good.ToText());
        Assert.Equal(good.ToJson(), GraphVerify.Offline(db, new([Contract("test.good.v1")])).ToJson());
        var empty = GraphVerify.Offline(db, new([]));
        Assert.Equal(GraphEvidenceStatus.NotRun, empty.Status);
        Assert.Equal(1, empty.ExitCode);
        var failed = GraphVerify.Offline(db, new([Contract("test.bad.v1", () => throw new NotSupportedException("private-error")), Contract("test.good.v1")]));
        Assert.Equal(1, failed.ExitCode);
        Assert.Equal(GraphEvidenceStatus.Unsupported, failed.Status);
        Assert.Equal(GraphEvidenceStatus.Pass, failed.Contracts[1].Compilation);
        Assert.DoesNotContain("private-error", failed.ToJson());
        var canceled = GraphVerify.Offline(db, new([Contract("test.good.v1")]), cancellationToken: new(true));
        Assert.Equal(GraphEvidenceStatus.Blocked, canceled.Status);
        Assert.Equal(1, canceled.ExitCode);
        Assert.Empty(protocol.Calls);
    }

    [Fact]
    public async Task Connected_plans_the_exact_once_compiled_parameters_and_redacts_plan_fields_and_unknown_operators()
    {
        var calls = 0;
        var planned = new List<string>();
        var protocol = new Protocol { Explain = (query, parameters) =>
        {
            planned.Add(query);
            Assert.Contains("private-parameter", parameters.Values);
            return Cursor(Plan("ProduceResults@private_database", Plan("Projection"), Plan("private-operator")));
        } };
        using var db = new Context(protocol.Driver);
        var catalog = new GraphQueryCatalog([Contract("test.first.v1", () => calls++), Contract("test.second.v1", () => calls++)]);
        var report = await GraphVerify.InspectAsync(db, catalog, "test.read.v1");
        Assert.Equal(GraphEvidenceStatus.Pass, report.Status);
        Assert.Equal(0, report.ExitCode);
        Assert.Equal(2, calls);
        Assert.Equal(2, planned.Count);
        Assert.All(report.Contracts, c =>
        {
            Assert.Equal(GraphEvidenceStatus.Pass, c.Planning);
            Assert.Equal(GraphEvidenceStatus.NotRun, c.Semantics);
            var plan = c.Plan!;
            Assert.Equal(report.Evidence.Queries.Single(q => q.ContractId == c.ContractId).StatementFingerprint, plan.StatementFingerprint);
            Assert.Equal(new[] { "ProduceResults", "Projection", "redacted-operator" }, plan.Operators.Select(n => n.Operator));
            Assert.Equal(new int?[] { null, 0, 0 }, plan.Operators.Select(n => n.Parent));
            Assert.True(plan.HasRedactedOperators);
            Assert.False(plan.ArgumentsIncluded);
            Assert.False(plan.IdentifiersIncluded);
            Assert.Matches("^[a-f0-9]{64}$", plan.ShapeFingerprint);
        });
        foreach (var text in new[] { report.ToJson(), report.ToText() })
        {
            Assert.DoesNotContain("private-", text);
            Assert.DoesNotContain("PrivateDoctorLabel", text);
        }
        var oldCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new("ar-SA");
            var other = await GraphVerify.InspectAsync(db, catalog, "test.read.v1");
            Assert.Equal(report.ToJson(), other.ToJson());
        }
        finally { CultureInfo.CurrentCulture = oldCulture; }
        protocol.AssertCleanup(6); // One metadata and two plan transactions per inspection.
    }

    [Theory]
    [InlineData("future-feature", "2026.09.0")]
    [InlineData("identified-relationships", "5.6.0")]
    public async Task Unknown_or_unavailable_required_features_are_blocked_before_planning(string feature, string version)
    {
        var protocol = new Protocol { Version = version };
        using var db = new Context(protocol.Driver);
        var report = await GraphVerify.InspectAsync(db, new([Contract("test.required.v1", features: [feature])]), "test.read.v1");
        Assert.Equal(GraphEvidenceStatus.Blocked, report.Status);
        Assert.Equal(1, report.ExitCode);
        Assert.Equal("GBD-CAP-001", Assert.Single(report.Contracts).Diagnostic!.Code);
        Assert.Equal(3, protocol.Queries.Count);
        protocol.AssertCleanup();
    }

    [Theory]
    [InlineData("denied", "GBD-CONN-002")]
    [InlineData("rows", "GBD-EXEC-002")]
    [InlineData("profile", "GBD-EXEC-002")]
    [InlineData("updates", "GBD-EXEC-002")]
    [InlineData("no-plan", "GBD-EXEC-002")]
    [InlineData("too-large", "GBD-EXEC-002")]
    [InlineData("cancel", "GBD-EXEC-001")]
    public async Task Planning_failure_or_cancellation_never_passes_and_leaves_remaining_contracts_not_run(string failure, string code)
    {
        using var cancellation = new CancellationTokenSource();
        var protocol = new Protocol { Explain = (_, _) =>
        {
            if (failure == "denied") throw new ClientException("Neo.ClientError.Security.Forbidden", "private-error");
            if (failure == "cancel") cancellation.Cancel();
            return Cursor(Plan("ProduceResults", Plan("Projection")), failure);
        } };
        using var db = new Context(protocol.Driver);
        var report = await GraphVerify.InspectAsync(db, new([Contract("test.first.v1"), Contract("test.second.v1")]),
            "test.read.v1", maxPlanNodes: failure == "too-large" ? 1 : 256, cancellationToken: cancellation.Token);
        Assert.Equal(GraphEvidenceStatus.Blocked, report.Status);
        Assert.Equal(1, report.ExitCode);
        Assert.Equal(code, report.Contracts[0].Diagnostic!.Code);
        Assert.Null(report.Contracts[0].Plan);
        Assert.Equal(GraphEvidenceStatus.NotRun, report.Contracts[1].Planning);
        Assert.Single(protocol.Queries, q => q.StartsWith("EXPLAIN ", StringComparison.Ordinal));
        Assert.DoesNotContain("private-error", report.ToJson());
        protocol.AssertCleanup(2);
    }

    [Fact]
    public async Task Missing_schema_skips_all_planning_and_invalid_bounds_open_no_sessions()
    {
        var protocol = new Protocol { IncludeConstraint = false };
        using var db = new Context(protocol.Driver);
        var catalog = new GraphQueryCatalog([Contract("test.required.v1")]);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => GraphVerify.InspectAsync(db, catalog, "test.read.v1", maxPlanNodes: 0));
        Assert.Empty(protocol.Calls);
        var report = await GraphVerify.InspectAsync(db, catalog, "test.read.v1");
        Assert.Equal(GraphEvidenceStatus.Fail, report.Status);
        Assert.Equal(1, report.ExitCode);
        Assert.All(report.Contracts, c => Assert.Equal(GraphEvidenceStatus.NotRun, c.Planning));
        Assert.Equal(3, protocol.Queries.Count);
        protocol.AssertCleanup();
    }

    private static GraphQueryContract Contract(string id, Action? onCompile = null, string[]? features = null)
        => GraphQueryContract.Fluent(new GraphQueryDefinition(id, "Synthetic query", "test.cs", [new("name", GraphResultKind.String)],
            GraphResultComparison.Ordered, GraphDuplicatePolicy.Preserve, new(10, TimeSpan.FromSeconds(5)), features), db =>
        {
            onCompile?.Invoke();
            var item = Graph.Node<Item>();
            return db.Cypher().Match(item).Where(item.Property(p => p.Name).Eq("private-parameter")).Return(() => item.Value.Name).Limit(10);
        });

    internal static IPlan Plan(string name, params IPlan[] children) => Proxy.For<IPlan>((method, _) => method.Name switch
    {
        "get_OperatorType" => name,
        "get_Children" => children.ToList(),
        _ => throw new InvalidOperationException("Arguments and identifiers must never be accessed: " + method.Name)
    });

    internal static IResultCursor Cursor(IPlan plan, string response = "plan")
    {
        var counters = Proxy.For<ICounters>((method, _) => method.Name switch
        {
            "get_ContainsUpdates" => response == "updates", "get_ContainsSystemUpdates" => false,
            _ => throw new InvalidOperationException(method.Name)
        });
        var summary = Proxy.For<IResultSummary>((method, _) => method.Name switch
        {
            "get_HasPlan" => response != "no-plan", "get_HasProfile" => response == "profile",
            "get_Plan" => plan, "get_Counters" => counters,
            "get_ResultAvailableAfter" or "get_ResultConsumedAfter" => TimeSpan.Zero,
            _ => throw new InvalidOperationException(method.Name)
        });
        return Proxy.For<IResultCursor>((method, _) => method.Name switch
        {
            "FetchAsync" => Task.FromResult(response == "rows"), "ConsumeAsync" => Task.FromResult(summary), "get_IsOpen" => true,
            _ => throw new InvalidOperationException(method.Name)
        });
    }
}
