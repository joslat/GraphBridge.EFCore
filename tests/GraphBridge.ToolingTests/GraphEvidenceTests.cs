using System.Reflection;
using System.Text.Json;
using GraphBridge.Tooling.Diagnostics;
using GraphBridge.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Neo4j.Driver;
using Graph = GraphBridge.EntityFrameworkCore.FluentCypher.Cypher;

namespace GraphBridge.ToolingTests;

public sealed class GraphEvidenceTests
{
    [Fact]
    public void Snapshot_is_deterministic_offline_and_redacts_values_and_schema_text_by_default()
    {
        var driver = DispatchProxy.Create<IDriver, NeverTransport>();
        using var db = new Context(driver);
        var secret = "credential-like-sensitive-value";
        var node = Graph.Node<Item>();
        var command = db.Cypher().Match(node).Where(node.Property(p => p.Name).Eq(secret))
            .Return(() => node.Value.Name).Limit(3).ToCommand();
        var snapshot = GraphQueryEvidence.Capture("movies.sample.v1", command);
        var report = new GraphEvidenceReport(GraphEvidenceMode.Offline, [snapshot], []);
        var json = report.ToJson();
        Assert.Equal(json, new GraphEvidenceReport(GraphEvidenceMode.Offline, [GraphQueryEvidence.Capture("movies.sample.v1", command)], []).ToJson());
        Assert.DoesNotContain(secret, json);
        Assert.DoesNotContain("PrivateModelLabel", json);
        Assert.DoesNotContain("MATCH", json);
        Assert.Equal(2, snapshot.ParameterCount);
        Assert.Equal(new[] { "string", "integer" }, snapshot.Parameters.Select(p => p.Kind));
        Assert.NotNull(snapshot.StatementFingerprint);
        Assert.Equal(64, snapshot.StatementFingerprint.Length);
        Assert.Equal(GraphEvidenceStatus.Pass, report.Status);
        Assert.Equal(GraphEvidenceStatus.NotRun, snapshot.Execution);
        Assert.Equal(GraphEvidenceStatus.NotRun, report.DatabaseObservation);
        Assert.Equal(0, ((NeverTransport)(object)driver).Calls);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(1, document.RootElement.GetProperty("formatVersion").GetInt32());
        Assert.False(document.RootElement.GetProperty("parameterValuesIncluded").GetBoolean());
        Assert.Equal("not-run", document.RootElement.GetProperty("databaseObservation").GetString());

        var disclosed = GraphQueryEvidence.Capture("movies.sample.v1", command, includeStatement: true);
        var textReport = new GraphEvidenceReport(GraphEvidenceMode.Offline, [disclosed], []);
        Assert.True(textReport.SensitiveTextIncluded);
        Assert.Contains("PrivateModelLabel", textReport.ToJson());
        Assert.DoesNotContain(secret, textReport.ToJson());
        Assert.Equal(snapshot.StatementFingerprint, disclosed.StatementFingerprint);
    }

    [Fact]
    public void Incomplete_observations_never_become_success_and_diagnostics_are_actionable()
    {
        Assert.Equal(GraphEvidenceStatus.NotRun, new GraphEvidenceReport(GraphEvidenceMode.Offline, [], []).Status);
        Assert.Equal(GraphEvidenceStatus.Blocked, new GraphEvidenceReport(GraphEvidenceMode.Inspect, [], []).Status);
        foreach (var code in GraphDiagnosticCatalog.Codes)
        {
            var diagnostic = GraphDiagnosticCatalog.Get(code);
            Assert.NotEmpty(diagnostic.Component);
            Assert.NotEmpty(diagnostic.Explanation);
            Assert.NotEmpty(diagnostic.SuggestedAction);
            Assert.StartsWith("https://github.com/joslat/GraphBridge.EFCore/blob/main/docs/", diagnostic.Documentation);
            Assert.Equal(diagnostic.Status, new GraphEvidenceReport(GraphEvidenceMode.Offline, [], [diagnostic]).Status);
        }
        Assert.Throws<ArgumentException>(() => GraphDiagnosticCatalog.Get("unrecognized-sensitive-input"));
    }

    [Fact]
    public void Report_snapshots_collections_and_sorts_without_losing_required_failure()
    {
        var diagnostics = new List<GraphDiagnosticResult> { GraphDiagnosticCatalog.Get("GBD-MODEL-000"), GraphDiagnosticCatalog.Get("GBD-CONN-001") };
        var report = new GraphEvidenceReport(GraphEvidenceMode.Offline, [], diagnostics);
        var reversed = new GraphEvidenceReport(GraphEvidenceMode.Offline, [], diagnostics.AsEnumerable().Reverse());
        Assert.Equal(report.ToJson(), reversed.ToJson());
        diagnostics.Clear();
        Assert.Equal(2, report.Diagnostics.Count);
        Assert.Equal(GraphEvidenceStatus.Blocked, report.Status);
        Assert.Throws<ArgumentException>(() => new GraphEvidenceReport(GraphEvidenceMode.Offline, [], Enumerable.Repeat(GraphDiagnosticCatalog.Get("GBD-MODEL-000"), 1025)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GraphEvidenceReport((GraphEvidenceMode)100, [], []));
    }

    [Theory]
    [InlineData("")]
    [InlineData("query<script>")]
    [InlineData("query\nsecret")]
    [InlineData("query\n")]
    [InlineData("UPPER")]
    public void External_contract_identifiers_have_a_small_explicit_grammar(string id)
        => Assert.Throws<ArgumentException>(() => GraphQueryEvidence.Capture(id, null!));

    [Fact]
    public void Query_codes_become_subcodes_but_untrusted_data_values_do_not()
    {
        var linq = new Neo4jQueryTranslationException("secret");
        linq.Data["GraphBridge.QueryDiagnostic"] = "GBQ101";
        var fluent = new NotSupportedException("secret");
        fluent.Data["GraphBridge.FluentDiagnostic"] = "GBFC303";
        var forged = new NotSupportedException("secret");
        forged.Data["GraphBridge.FluentDiagnostic"] = "secret value";
        var newline = new NotSupportedException("secret");
        newline.Data["GraphBridge.FluentDiagnostic"] = "GBFC303\n";
        Assert.Equal("GBQ101", GraphDiagnosticCatalog.Classify(linq, GraphDiagnosticPhase.Query).Subcode);
        Assert.Equal("GBFC303", GraphDiagnosticCatalog.Classify(fluent, GraphDiagnosticPhase.Query, fluentQuery: true).Subcode);
        Assert.Null(GraphDiagnosticCatalog.Classify(forged, GraphDiagnosticPhase.Query, fluentQuery: true).Subcode);
        Assert.Null(GraphDiagnosticCatalog.Classify(newline, GraphDiagnosticPhase.Query, fluentQuery: true).Subcode);
        Assert.Null(GraphDiagnosticCatalog.Classify(fluent, GraphDiagnosticPhase.Model).Subcode);
    }

    [Fact]
    public void Actual_failure_types_map_to_stable_codes_without_retaining_sensitive_exception_content()
    {
        const string secret = "secret-in-driver-or-compiler-message";
        (Exception Failure, GraphDiagnosticPhase Phase, bool Fluent, string Code)[] cases =
        [
            (new Neo4jQueryTranslationException(secret), GraphDiagnosticPhase.Query, false, "GBD-QUERY-001"),
            (new NotSupportedException(secret), GraphDiagnosticPhase.Query, true, "GBD-QUERY-002"),
            (new InvalidOperationException(secret), GraphDiagnosticPhase.Model, false, "GBD-MODEL-001"),
            (new OperationCanceledException(secret), GraphDiagnosticPhase.Execution, false, "GBD-EXEC-001"),
            (new AuthenticationException(secret), GraphDiagnosticPhase.Connection, false, "GBD-CONN-002"),
            (new ClientException("Neo.ClientError.Security.Forbidden", secret), GraphDiagnosticPhase.Schema, false, "GBD-CONN-002"),
            (new ServiceUnavailableException(secret), GraphDiagnosticPhase.Connection, false, "GBD-CONN-001"),
            (new ClientException("Neo.ClientError.Statement.SyntaxError", secret), GraphDiagnosticPhase.Execution, false, "GBD-EXEC-002"),
            (new HostileException(), GraphDiagnosticPhase.Execution, false, "GBD-EXEC-002")
        ];
        foreach (var item in cases)
        {
            var diagnosis = GraphDiagnosticCatalog.Classify(item.Failure, item.Phase, item.Fluent);
            Assert.Equal(item.Code, diagnosis.Code);
            Assert.DoesNotContain(secret, new GraphEvidenceReport(GraphEvidenceMode.Offline, [], [diagnosis]).ToJson());
        }
    }

    private sealed class HostileException : Exception
    {
        public override string Message => throw new InvalidOperationException("Report read an exception getter.");
        public override string ToString() => throw new InvalidOperationException("Report formatted an exception.");
    }

    private sealed class Item { public int Id { get; set; } public string Name { get; set; } = ""; }
    private sealed class Context(IDriver driver) : DbContext(new DbContextOptionsBuilder().UseNeo4j(driver, "offline").Options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.Entity<Item>().ToGraphNode("PrivateModelLabel");
    }
    public class NeverTransport : DispatchProxy
    {
        public int Calls { get; private set; }
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            Calls++;
            throw new InvalidOperationException("Offline evidence must not touch the driver.");
        }
    }
}
