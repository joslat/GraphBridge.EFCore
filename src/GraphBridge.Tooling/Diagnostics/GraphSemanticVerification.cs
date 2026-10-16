using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Neo4j.Driver;

namespace GraphBridge.Tooling.Diagnostics;

public enum GraphSemanticIssue { None, FixtureMismatch, ReferenceRegistration, ContextNotFresh, ObservationFailed }

/// <summary>Separate native-reference-to-fixed-expectation and Fluent-to-native observations.</summary>
public sealed record GraphSemanticCase(string ContractId, string? ReferenceId, string? ReferenceFingerprint,
    GraphEvidenceStatus Status, GraphResultComparisonReport? ReferenceComparison = null,
    GraphResultComparisonReport? QueryComparison = null, GraphDiagnosticResult? Diagnostic = null);

/// <summary>V2 evidence for the selected fixture only. Raw result values and fixture ownership tokens are not exported.</summary>
public sealed class GraphSemanticVerificationReport
{
    internal GraphSemanticVerificationReport(GraphVerificationFixture fixture, GraphVerificationReport? inspection,
        IEnumerable<GraphSemanticCase> cases, GraphEvidenceStatus fixtureStatus, GraphEvidenceStatus cleanup,
        GraphSemanticIssue issue, IEnumerable<GraphDiagnosticResult> diagnostics)
        => (FixtureId, FixtureFingerprint, Inspection, Cases, FixtureStatus, Cleanup, Issue, Diagnostics) =
            (fixture.FixtureId, fixture.Fingerprint, inspection, Array.AsReadOnly(cases.ToArray()), fixtureStatus,
                cleanup, issue, Array.AsReadOnly(diagnostics.ToArray()));
    public int FormatVersion => 1;
    public string Tier => "V2";
    public string FixtureId { get; }
    public string FixtureFingerprint { get; }
    public GraphVerificationReport? Inspection { get; }
    public IReadOnlyList<GraphSemanticCase> Cases { get; }
    public GraphEvidenceStatus FixtureStatus { get; }
    public GraphEvidenceStatus Cleanup { get; }
    public GraphSemanticIssue Issue { get; }
    public IReadOnlyList<GraphDiagnosticResult> Diagnostics { get; }
    public GraphEvidenceStatus Status => EvidenceOutcome.Combine(new[] {
        Inspection?.Status ?? GraphEvidenceStatus.NotRun, FixtureStatus, Cleanup,
        Issue == GraphSemanticIssue.None ? GraphEvidenceStatus.Pass : GraphEvidenceStatus.Blocked,
        Cases.Count == 0 ? GraphEvidenceStatus.NotRun : GraphEvidenceStatus.Pass }
        .Concat(Cases.Select(c => c.Status)).Concat(Diagnostics.Select(d => d.Status)));
    public int ExitCode => Status == GraphEvidenceStatus.Pass ? 0 : 1;
    public string ToJson() => JsonSerializer.Serialize(this, GraphEvidenceReport.JsonOptions);
    public string ToText()
    {
        var text = new StringBuilder().Append("GraphBridge Verify V2 | status: ").Append(Status)
            .Append(" | exit: ").AppendLine(ExitCode == 0 ? "0" : "1")
            .Append("Fixture: ").Append(FixtureId).Append(" | observation: ").Append(FixtureStatus)
            .Append(" | cleanup: ").Append(Cleanup).Append(" | issue: ").AppendLine(Issue.ToString())
            .AppendLine("Contract | Status | Native vs fixed | Fluent vs native");
        foreach (var item in Cases)
            text.Append(item.ContractId).Append(" | ").Append(item.Status).Append(" | ")
                .Append(item.ReferenceComparison?.Difference.ToString() ?? "NotRun").Append(" | ")
                .AppendLine(item.QueryComparison?.Difference.ToString() ?? "NotRun");
        foreach (var diagnosis in Diagnostics.Concat(Cases.Where(c => c.Diagnostic is not null).Select(c => c.Diagnostic!)))
            text.Append(diagnosis.Code).Append(": ").AppendLine(diagnosis.SuggestedAction);
        if (Inspection is not null) text.AppendLine().Append(Inspection.ToText());
        return text.ToString().ReplaceLineEndings("\n");
    }
}

public static partial class GraphVerify
{
    /// <summary>Explicit V2 opt-in for an exclusively owned disposable fixture. Executes no seed/cleanup DDL or data writes.</summary>
    /// <remarks>The trusted factory must return a new empty context, owned and disposed by this call; its driver remains
    /// application-owned. References are reviewed native reads, not sandboxed input. The harness must provision/clean
    /// its fixture separately and exclude concurrent writers. Each query is compiled once for planning and execution.
    /// Result reads share one owned transaction, always rolled back. Cancellation is cooperative between driver operations;
    /// row limits are client guards, not bounds on server computation or individual payload size. No automatic retries.</remarks>
    public static async Task<GraphSemanticVerificationReport> SemanticAsync(Func<DbContext> contextFactory,
        GraphQueryCatalog catalog, string profileId, GraphVerificationFixture fixture,
        IEnumerable<GraphNativeReference> references, int maxSchemaRows = 1_000, int maxPlanNodes = 256,
        bool includeStatements = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(fixture);
        ArgumentNullException.ThrowIfNull(references);
        EvidenceIdentity.Validate(profileId);
        if (maxSchemaRows is < 1 or > 10_000) throw new ArgumentOutOfRangeException(nameof(maxSchemaRows));
        if (maxPlanNodes is < 1 or > 4096) throw new ArgumentOutOfRangeException(nameof(maxPlanNodes));
        var controls = references.Take(257).ToArray();
        if (controls.Length > 256 || controls.Any(r => r is null)
            || controls.Select(r => r.Id).Distinct(StringComparer.Ordinal).Count() != controls.Length)
            throw new ArgumentException("Provide at most 256 distinct non-null native references.", nameof(references));
        var byId = controls.ToDictionary(r => r.Id, StringComparer.Ordinal);
        var cases = catalog.Contracts.Select(c => new GraphSemanticCase(c.Definition.Id,
            c.Definition.ReferenceId, null, GraphEvidenceStatus.NotRun)).ToArray();
        var diagnostics = new List<GraphDiagnosticResult>();
        var issue = GraphSemanticIssue.None;
        var fixtureStatus = GraphEvidenceStatus.NotRun;
        var cleanup = GraphEvidenceStatus.NotRun;
        GraphVerificationReport? inspection = null;
        DbContext? context = null;
        IDbContextTransaction? transaction = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (catalog.Contracts.Any(c => c.Definition.FixtureId != fixture.FixtureId
                || c.Definition.ReferenceId is not { } id || !byId.TryGetValue(id, out var reference)
                || reference.FixtureId != fixture.FixtureId))
            {
                issue = GraphSemanticIssue.ReferenceRegistration;
            }
            else if (cases.Length != 0)
            {
                context = contextFactory() ?? throw new InvalidOperationException("The context factory returned null.");
                if (context.Database.CurrentTransaction is not null || context.ChangeTracker.Entries().Any())
                {
                    // A violated ownership contract is not permission to roll back/dispose somebody else's context.
                    context = null;
                    issue = GraphSemanticIssue.ContextNotFresh;
                }
                else
                {
                    var capture = await GraphDoctor.CaptureAsync(context, catalog, profileId, maxSchemaRows,
                        includeStatements, cancellationToken, prepareExecution: true).ConfigureAwait(false);
                    inspection = await PlanAsync(context, capture, maxPlanNodes, cancellationToken).ConfigureAwait(false);
                    if (inspection.Status == GraphEvidenceStatus.Pass)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                        var owner = fixture.OwnerId.ToString("N");
                        var owners = await context.Database.QueryCypherAsync(
                            "MATCH (f:GraphBridgeFixture {id: $owner}) RETURN count(f) AS count",
                            new Dictionary<string, object?> { ["owner"] = owner }, r => r["count"].As<long>(), 1, cancellationToken).ConfigureAwait(false);
                        var markers = await context.Database.QueryCypherAsync(
                            "MATCH (f:GraphBridgeVerificationFixture {ownerId: $owner, fixtureId: $fixture, fingerprint: $fingerprint}) RETURN count(f) AS count",
                            new Dictionary<string, object?> { ["owner"] = owner, ["fixture"] = fixture.FixtureId, ["fingerprint"] = fixture.Fingerprint },
                            r => r["count"].As<long>(), 1, cancellationToken).ConfigureAwait(false);
                        cancellationToken.ThrowIfCancellationRequested();
                        fixtureStatus = owners.Count == 1 && owners[0] == 1 && markers.Count == 1 && markers[0] == 1
                            ? GraphEvidenceStatus.Pass : GraphEvidenceStatus.Blocked;
                        if (fixtureStatus != GraphEvidenceStatus.Pass) issue = GraphSemanticIssue.FixtureMismatch;
                        else for (var i = 0; i < cases.Length; i++)
                        {
                            var definition = catalog.Contracts[i].Definition;
                            var reference = byId[definition.ReferenceId!];
                            cases[i] = cases[i] with { ReferenceFingerprint = reference.StatementFingerprint };
                            try
                            {
                                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                                deadline.CancelAfter(definition.Budget.Deadline);
                                deadline.Token.ThrowIfCancellationRequested();
                                var native = await context.Database.QueryCypherAsync(reference.Statement, reference.Parameters,
                                    r => reference.Read(r, definition.Columns.Count), definition.Budget.MaxRows, deadline.Token).ConfigureAwait(false);
                                deadline.Token.ThrowIfCancellationRequested();
                                var expected = GraphResultComparer.Compare(definition, reference.ExpectedRows, native);
                                cases[i] = cases[i] with { ReferenceComparison = expected, Status = expected.Status };
                                if (expected.Status != GraphEvidenceStatus.Pass) continue;
                                var actual = await capture.Prepared[definition.Id].ExecuteAsync(deadline.Token).ConfigureAwait(false);
                                deadline.Token.ThrowIfCancellationRequested();
                                var comparison = GraphResultComparer.Compare(definition, native, actual);
                                cases[i] = cases[i] with { QueryComparison = comparison, Status = comparison.Status };
                            }
                            catch (Exception error) when (error is not OutOfMemoryException)
                            {
                                var diagnosis = GraphDiagnosticCatalog.Classify(error, GraphDiagnosticPhase.Execution);
                                cases[i] = cases[i] with { Status = diagnosis.Status, Diagnostic = diagnosis };
                                break;
                            }
                        }
                    }
                }
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            issue = GraphSemanticIssue.ObservationFailed;
            diagnostics.Add(GraphDiagnosticCatalog.Classify(error, GraphDiagnosticPhase.Execution));
        }
        finally
        {
            // DisposeAsync performs rollback even after cancellation, then releases only the owned transaction/session.
            if (context is not null)
            {
                cleanup = GraphEvidenceStatus.Pass;
                try { if (transaction is not null) await transaction.DisposeAsync().ConfigureAwait(false); }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    cleanup = GraphEvidenceStatus.Blocked;
                    diagnostics.Add(GraphDiagnosticCatalog.Classify(error, GraphDiagnosticPhase.Execution));
                }
                try { await context.DisposeAsync().ConfigureAwait(false); }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    cleanup = GraphEvidenceStatus.Blocked;
                    diagnostics.Add(GraphDiagnosticCatalog.Classify(error, GraphDiagnosticPhase.Execution));
                }
            }
        }
        return new(fixture, inspection, cases, fixtureStatus, cleanup, issue, diagnostics);
    }
}
