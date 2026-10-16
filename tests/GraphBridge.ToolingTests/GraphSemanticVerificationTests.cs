using GraphBridge.Tooling.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Neo4j.Driver;
using Graph = GraphBridge.EntityFrameworkCore.FluentCypher.Cypher;
using Proxy = GraphBridge.ToolingTests.InspectionTests.Proxy;
using Context = GraphBridge.ToolingTests.ConnectedDoctorTests.Context;
using Item = GraphBridge.ToolingTests.ConnectedDoctorTests.Item;
using Protocol = GraphBridge.ToolingTests.ConnectedDoctorTests.Protocol;

namespace GraphBridge.ToolingTests;

public sealed class GraphSemanticVerificationTests
{
    private const string Fingerprint = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static GraphVerificationFixture Fixture() => new("test.fixture.v1", Guid.NewGuid(), Fingerprint, true);
    private static GraphQueryDefinition Definition(string id = "test.query.v1", int maxRows = 10) => new(id,
        "Synthetic query", "test.cs", [new("name", GraphResultKind.String)], GraphResultComparison.Ordered,
        GraphDuplicatePolicy.Preserve, new(maxRows, TimeSpan.FromSeconds(5)), referenceId: "test.native.v1", fixtureId: "test.fixture.v1");
    private static GraphQueryContract Contract(Action? factory = null, Func<string, IReadOnlyList<object?>>? projection = null,
        int limit = 10, int maxRows = 10, string id = "test.query.v1") => GraphQueryContract.Fluent(Definition(id, maxRows), db =>
        {
            factory?.Invoke();
            var item = Graph.Node<Item>();
            return db.Cypher().Match(item).Where(item.Property(p => p.Name).Eq("private-value"))
                .Return(() => item.Value.Name).Limit(limit);
        }, projection ?? (value => [value]));
    private static GraphNativeReference Reference() => new("test.native.v1", "test.fixture.v1",
        "MATCH (i:PrivateDoctorLabel) WHERE i.Name = $name RETURN i.Name AS name ORDER BY name",
        new Dictionary<string, object?> { ["name"] = "private-value" }, r => [r["name"]], [["private-value"]]);

    [Fact]
    public async Task Executes_once_prepared_query_and_independent_reference_then_rolls_back_and_redacts()
    {
        var compiles = 0;
        var projectionCalls = 0;
        string? planned = null;
        var protocol = new Protocol
        {
            Explain = (query, _) => { planned = query[8..]; return GraphVerifyTests.Cursor(GraphVerifyTests.Plan("ProduceResults")); },
            Native = (query, parameters) =>
            {
                if (query.Contains("GraphBridge", StringComparison.Ordinal)) return Rows(new Dictionary<string, object>() { ["count"] = 1L });
                Assert.Equal("private-value", parameters.Values.First());
                if (query.Contains(" AS name", StringComparison.Ordinal)) return Rows(new Dictionary<string, object>() { ["name"] = "private-value" });
                Assert.Equal(planned, query.ReplaceLineEndings("\n"));
                return Rows(new Dictionary<string, object>() { ["c0"] = "private-value" });
            }
        };
        var owner = Guid.NewGuid();
        var fixture = new GraphVerificationFixture("test.fixture.v1", owner, Fingerprint, true);
        var report = await GraphVerify.SemanticAsync(() => new Context(protocol.Driver),
            new([Contract(() => compiles++, v => { projectionCalls++; return [v]; })]), "test.profile.v1", fixture, [Reference()]);
        Assert.Equal(GraphEvidenceStatus.Pass, report.Status);
        Assert.Equal(0, report.ExitCode);
        Assert.Equal(1, compiles);
        Assert.Equal(1, projectionCalls);
        Assert.Equal(GraphResultDifference.None, report.Cases[0].QueryComparison!.Difference);
        Assert.Equal(GraphResultDifference.None, report.Cases[0].ReferenceComparison!.Difference);
        Assert.Equal(Fingerprint, report.FixtureFingerprint);
        Assert.NotNull(report.Cases[0].ReferenceFingerprint);
        Assert.Equal(GraphEvidenceStatus.Pass, report.Cleanup);
        foreach (var text in new[] { report.ToJson(), report.ToText() })
        {
            Assert.DoesNotContain("private-value", text);
            Assert.DoesNotContain("PrivateDoctorLabel", text);
            Assert.DoesNotContain(owner.ToString("N"), text);
        }
        protocol.AssertCleanup(3); // metadata, EXPLAIN, and one shared fixture/results transaction
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(2L)]
    public async Task Missing_or_ambiguous_fixture_never_executes_results(long count)
    {
        var protocol = ProtocolWithRows(query =>
        {
            Assert.Contains("GraphBridge", query);
            return Rows(new Dictionary<string, object>() { ["count"] = count });
        });
        var report = await Run(protocol);
        Assert.Equal(GraphEvidenceStatus.Blocked, report.Status);
        Assert.Equal(GraphSemanticIssue.FixtureMismatch, report.Issue);
        Assert.Equal(GraphEvidenceStatus.NotRun, report.Cases[0].Status);
        Assert.Null(report.Cases[0].ReferenceComparison);
        protocol.AssertCleanup(3);
    }

    [Fact]
    public async Task Wrong_fixed_expectation_fails_reference_and_skips_fluent_results()
    {
        var protocol = ProtocolWithRows(query => query.Contains("GraphBridge", StringComparison.Ordinal)
            ? Rows(new Dictionary<string, object>() { ["count"] = 1L }) : Rows(new Dictionary<string, object>() { ["name"] = "wrong" }));
        var report = await Run(protocol);
        Assert.Equal(GraphEvidenceStatus.Fail, report.Status);
        Assert.Equal(GraphResultDifference.Values, report.Cases[0].ReferenceComparison!.Difference);
        Assert.Null(report.Cases[0].QueryComparison);
        Assert.Equal(7, protocol.Queries.Count); // 3 metadata + plan + 2 markers + native
        protocol.AssertCleanup(3);
    }

    [Fact]
    public async Task Actual_scalar_difference_fails_and_row_projection_exception_is_blocked_without_exception_text()
    {
        var protocol = ValidProtocol();
        var failed = await Run(protocol, Contract(projection: _ => ["wrong"]));
        Assert.Equal(GraphEvidenceStatus.Fail, failed.Status);
        Assert.Equal(GraphResultDifference.Values, failed.Cases[0].QueryComparison!.Difference);
        var blocked = await Run(protocol, Contract(projection: _ => throw new InvalidOperationException("private-error")));
        Assert.Equal(GraphEvidenceStatus.Blocked, blocked.Status);
        Assert.Equal("GBD-EXEC-002", blocked.Cases[0].Diagnostic!.Code);
        Assert.DoesNotContain("private-error", blocked.ToJson());
        protocol.AssertCleanup(6);
    }

    [Fact]
    public async Task Row_budget_rejects_query_before_planning_and_missing_projection_cannot_be_a_V2_pass()
    {
        var protocol = ValidProtocol();
        var over = await Run(protocol, Contract(limit: 10, maxRows: 5));
        Assert.Equal(GraphEvidenceStatus.Unsupported, over.Status);
        Assert.DoesNotContain(protocol.Queries, q => q.StartsWith("EXPLAIN ", StringComparison.Ordinal));
        var missing = GraphQueryContract.Fluent(Definition(), db =>
        {
            var item = Graph.Node<Item>();
            return db.Cypher().Match(item).Return(() => item.Value.Name).Limit(10);
        });
        Assert.Equal(GraphEvidenceStatus.Unsupported, (await Run(protocol, missing)).Status);
        protocol.AssertCleanup(2);
    }

    [Fact]
    public async Task Missing_reference_empty_catalog_and_precancellation_open_no_context()
    {
        var factories = 0;
        DbContext Factory() { factories++; throw new InvalidOperationException("Should not be called."); }
        var missing = await GraphVerify.SemanticAsync(Factory, new([Contract()]), "test.profile.v1", Fixture(), []);
        Assert.Equal(GraphSemanticIssue.ReferenceRegistration, missing.Issue);
        Assert.Equal(GraphEvidenceStatus.Blocked, missing.Status);
        var empty = await GraphVerify.SemanticAsync(Factory, new([]), "test.profile.v1", Fixture(), []);
        Assert.Equal(GraphEvidenceStatus.NotRun, empty.Status);
        var canceled = await GraphVerify.SemanticAsync(Factory, new([Contract()]), "test.profile.v1", Fixture(), [Reference()], cancellationToken: new(true));
        Assert.Equal(GraphEvidenceStatus.Blocked, canceled.Status);
        Assert.Equal("GBD-EXEC-001", canceled.Diagnostics[0].Code);
        Assert.Equal(0, factories);
    }

    [Fact]
    public async Task Borrowed_transaction_is_rejected_without_disposal_or_rollback()
    {
        var protocol = ValidProtocol();
        await using var db = new Context(protocol.Driver);
        await using var borrowed = await db.Database.BeginTransactionAsync();
        var report = await GraphVerify.SemanticAsync(() => db, new([Contract()]), "test.profile.v1", Fixture(), [Reference()]);
        Assert.Equal(GraphSemanticIssue.ContextNotFresh, report.Issue);
        Assert.Equal(GraphEvidenceStatus.Blocked, report.Status);
        Assert.Same(borrowed, db.Database.CurrentTransaction);
        Assert.Empty(protocol.Queries);
        Assert.DoesNotContain("transaction.RollbackAsync", protocol.Calls);
        await borrowed.RollbackAsync();
        protocol.AssertCleanup();
    }

    [Fact]
    public async Task Cleanup_failure_cannot_be_a_pass_even_when_both_results_match()
    {
        var rollbacks = 0;
        var protocol = new Protocol
        {
            Explain = (_, _) => GraphVerifyTests.Cursor(GraphVerifyTests.Plan("ProduceResults")),
            Native = (query, _) => ValidRows(query),
            OnCleanup = name => { if (name == "RollbackAsync" && ++rollbacks == 3) throw new InvalidOperationException("private-cleanup"); }
        };
        var report = await Run(protocol);
        Assert.Equal(GraphEvidenceStatus.Pass, report.Cases[0].Status);
        Assert.Equal(GraphEvidenceStatus.Blocked, report.Status);
        Assert.Equal(GraphEvidenceStatus.Blocked, report.Cleanup);
        Assert.Equal(1, report.ExitCode);
        Assert.DoesNotContain("private-cleanup", report.ToJson());
        var html = GraphQueryLab.RenderHtml(report);
        Assert.Contains("Blocked", html[..html.IndexOf("</header>", StringComparison.Ordinal)]);
        Assert.Contains("Context / transaction cleanup", html);
        protocol.AssertCleanup(3);
    }

    private static Task<GraphSemanticVerificationReport> Run(Protocol protocol, GraphQueryContract? contract = null)
        => GraphVerify.SemanticAsync(() => new Context(protocol.Driver), new([contract ?? Contract()]), "test.profile.v1", Fixture(), [Reference()]);

    [Fact]
    public async Task Explicit_semantic_statement_disclosure_retains_the_same_prepared_typed_snapshot()
    {
        var protocol = ValidProtocol();
        var compiles = 0;
        var result = await GraphVerify.SemanticAsync(() => new Context(protocol.Driver), new([Contract(() => compiles++)]),
            "test.profile.v1", Fixture(), [Reference()], includeStatements: true);
        Assert.Equal(GraphEvidenceStatus.Pass, result.Status);
        Assert.Equal(1, compiles);
        var evidence = result.Inspection!.Evidence.Queries[0];
        Assert.Equal(GraphBridge.EntityFrameworkCore.FluentCypher.CypherSnapshotStatus.Complete, evidence.Semantics!.Status);
        Assert.Contains("PrivateDoctorLabel", result.ToJson());
        Assert.DoesNotContain("private-value", result.ToJson());
        Assert.Equal(GraphEvidenceStatus.Pass, result.Cases[0].QueryComparison!.Status);
        protocol.AssertCleanup(3);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_or_reference_row_overflow_blocks_and_leaves_later_cases_not_run(bool overflow)
    {
        using var cancellation = new CancellationTokenSource();
        var protocol = ProtocolWithRows(query =>
        {
            if (query.Contains("GraphBridge", StringComparison.Ordinal)) return Rows(new Dictionary<string, object> { ["count"] = 1L });
            if (!overflow) cancellation.Cancel();
            return Rows(Enumerable.Range(0, 11).Select(_ => new Dictionary<string, object> { ["name"] = "private-value" }).ToArray());
        });
        var report = await GraphVerify.SemanticAsync(() => new Context(protocol.Driver),
            new([Contract(id: "test.first.v1"), Contract(id: "test.second.v1")]), "test.profile.v1", Fixture(), [Reference()],
            cancellationToken: cancellation.Token);
        Assert.Equal(GraphEvidenceStatus.Blocked, report.Status);
        Assert.Equal(overflow ? "GBD-EXEC-002" : "GBD-EXEC-001", report.Cases[0].Diagnostic!.Code);
        Assert.Equal(GraphEvidenceStatus.NotRun, report.Cases[1].Status);
        Assert.Equal(GraphEvidenceStatus.Pass, report.Cleanup);
        protocol.AssertCleanup(4);
    }

    [Fact]
    public void Disposable_acknowledgment_and_valid_owner_fingerprint_are_required()
    {
        Assert.Throws<ArgumentException>(() => new GraphVerificationFixture("test.fixture.v1", Guid.NewGuid(), Fingerprint, false));
        Assert.Throws<ArgumentException>(() => new GraphVerificationFixture("test.fixture.v1", Guid.Empty, Fingerprint, true));
        Assert.Throws<ArgumentException>(() => new GraphVerificationFixture("test.fixture.v1", Guid.NewGuid(), "unchecked", true));
    }
    private static Protocol ValidProtocol() => ProtocolWithRows(ValidRows);
    private static Protocol ProtocolWithRows(Func<string, IResultCursor> rows) => new()
    {
        Explain = (_, _) => GraphVerifyTests.Cursor(GraphVerifyTests.Plan("ProduceResults")), Native = (query, _) => rows(query)
    };
    private static IResultCursor ValidRows(string query) => query.Contains("GraphBridge", StringComparison.Ordinal)
        ? Rows(new Dictionary<string, object>() { ["count"] = 1L }) : Rows(new Dictionary<string, object>() { [query.Contains(" AS name", StringComparison.Ordinal) ? "name" : "c0"] = "private-value" });
    private static IResultCursor Rows(params Dictionary<string, object>[] rows)
    {
        var index = -1;
        return Proxy.For<IResultCursor>((method, _) => method.Name switch
        {
            "FetchAsync" => Task.FromResult(++index < rows.Length),
            "get_Current" => Proxy.For<IRecord>((member, args) => member.Name switch
            {
                "get_Values" => rows[index], "get_Item" => rows[index][(string)args![0]!], _ => throw new NotSupportedException(member.Name)
            }),
            "get_IsOpen" => true, "ConsumeAsync" => Task.FromResult<IResultSummary>(null!),
            _ => throw new NotSupportedException(method.Name)
        });
    }
}
