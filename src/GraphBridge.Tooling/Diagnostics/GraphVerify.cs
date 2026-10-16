using System.Text;
using System.Text.Json;
using GraphBridge.EntityFrameworkCore.Schema;
using Microsoft.EntityFrameworkCore;

namespace GraphBridge.Tooling.Diagnostics;

public enum GraphVerificationTier { V0, V1 }

/// <summary>Required-contract observations. Planning is separate from compilation and result execution.</summary>
public sealed record GraphContractVerification
{
    internal GraphContractVerification(string id, GraphEvidenceStatus compilation, GraphEvidenceStatus planning = GraphEvidenceStatus.NotRun,
        GraphPlanEvidence? plan = null, GraphDiagnosticResult? diagnostic = null)
        => (ContractId, Compilation, Planning, Plan, Diagnostic) = (id, compilation, planning, plan, diagnostic);
    public string ContractId { get; }
    public GraphEvidenceStatus Compilation { get; }
    public GraphEvidenceStatus Planning { get; }
    public GraphEvidenceStatus Semantics => GraphEvidenceStatus.NotRun;
    public GraphPlanEvidence? Plan { get; }
    public GraphDiagnosticResult? Diagnostic { get; }
}

/// <summary>Versioned verification outcome for every required catalog entry, with a conservative CI exit code.</summary>
public sealed class GraphVerificationReport
{
    internal GraphVerificationReport(GraphVerificationTier tier, GraphEvidenceReport evidence, IEnumerable<GraphContractVerification> contracts)
        => (Tier, Evidence, Contracts) = (tier, evidence, Array.AsReadOnly(contracts.ToArray()));
    public int FormatVersion => 1;
    public GraphVerificationTier Tier { get; }
    public GraphEvidenceReport Evidence { get; }
    public IReadOnlyList<GraphContractVerification> Contracts { get; }
    public int RequiredContractCount => Contracts.Count;
    public GraphEvidenceStatus Status => EvidenceOutcome.Combine(new[] { Evidence.Status }
        .Concat(Contracts.Count == 0 ? [GraphEvidenceStatus.NotRun] : Contracts.SelectMany(c => Tier == GraphVerificationTier.V0
            ? new[] { c.Compilation } : new[] { c.Compilation, c.Planning })));
    public int ExitCode => Status == GraphEvidenceStatus.Pass ? 0 : 1;
    public string ToJson() => JsonSerializer.Serialize(this, GraphEvidenceReport.JsonOptions);
    public string ToText()
    {
        var text = new StringBuilder().Append("GraphBridge Verify ").Append(Tier).Append(" | status: ")
            .Append(Status.ToString().ToLowerInvariant()).Append(" | exit: ").AppendLine(ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .AppendLine("Every registered contract is required. Result semantics remain not-run.")
            .AppendLine("Contract | Compile | Plan | Semantics");
        foreach (var item in Contracts)
        {
            text.Append(item.ContractId).Append(" | ").Append(item.Compilation).Append(" | ").Append(item.Planning).AppendLine(" | NotRun");
            if (item.Diagnostic is not null) text.Append(item.Diagnostic.Code).Append(": ").AppendLine(item.Diagnostic.SuggestedAction);
        }
        return text.AppendLine().Append(Evidence.ToText()).ToString().ReplaceLineEndings("\n");
    }
}

/// <summary>Application-hosted verification with separate offline, planning and explicit disposable-fixture semantic tiers.</summary>
public static partial class GraphVerify
{
    public static GraphVerificationReport Offline(DbContext context, GraphQueryCatalog catalog,
        bool includeStatements = false, CancellationToken cancellationToken = default)
    {
        var report = GraphDoctor.InspectOffline(context, catalog, includeStatements, cancellationToken);
        return new(GraphVerificationTier.V0, report, report.Queries.Select(q => new GraphContractVerification(q.ContractId, q.Compilation)));
    }

    /// <summary>Explicit V1 opt-in: inspect metadata, then EXPLAIN each required compiled query. No PROFILE or result execution.</summary>
    /// <remarks>Each trusted factory is invoked once. Its exact command is reused for planning. Per-contract cancellation deadlines
    /// are checked between driver operations; they cannot interrupt in-flight I/O or bound server work. Schema failure skips planning.
    /// Capability declarations outside the supported catalog vocabulary remain blocked rather than assumed supported.</remarks>
    public static async Task<GraphVerificationReport> InspectAsync(DbContext context, GraphQueryCatalog catalog, string profileId,
        int maxSchemaRows = 1_000, int maxPlanNodes = 256, bool includeStatements = false, CancellationToken cancellationToken = default)
    {
        if (maxPlanNodes is < 1 or > 4096) throw new ArgumentOutOfRangeException(nameof(maxPlanNodes));
        var capture = await GraphDoctor.CaptureAsync(context, catalog, profileId, maxSchemaRows, includeStatements, cancellationToken).ConfigureAwait(false);
        return await PlanAsync(context, capture, maxPlanNodes, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<GraphVerificationReport> PlanAsync(DbContext context, GraphInspectionCapture capture,
        int maxPlanNodes, CancellationToken cancellationToken)
    {
        var results = capture.Report.Queries.Select(q => new GraphContractVerification(q.ContractId, q.Compilation)).ToArray();
        if (capture.Report.Status != GraphEvidenceStatus.Pass) return new(GraphVerificationTier.V1, capture.Report, results);
        for (var i = 0; i < results.Length; i++)
        {
            var query = capture.Report.Queries[i];
            var definition = query.Definition!;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (definition.RequiredFeatures.Any(f => f is not ("identified-relationships" or "distinct" or "optional-match" or "aggregation" or "with"))
                    || definition.RequiredFeatures.Contains("identified-relationships")
                        && capture.Report.Database!.RelationshipUniqueness != GraphSchemaCapabilitySupport.Supported)
                {
                    results[i] = new(query.ContractId, query.Compilation, GraphEvidenceStatus.Blocked, diagnostic: GraphDiagnosticCatalog.Get("GBD-CAP-001"));
                    continue;
                }
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                deadline.CancelAfter(definition.Budget.Deadline);
                var command = capture.Commands[query.ContractId];
                var plan = await context.Database.ExplainCypherAsync(command.Text.ReplaceLineEndings("\n"), command.Parameters,
                    maxPlanNodes, deadline.Token).ConfigureAwait(false);
                deadline.Token.ThrowIfCancellationRequested();
                results[i] = new(query.ContractId, query.Compilation, GraphEvidenceStatus.Pass,
                    GraphPlanEvidence.Capture(plan, query.StatementFingerprint!, maxPlanNodes));
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                var diagnosis = GraphDiagnosticCatalog.Classify(error, GraphDiagnosticPhase.Execution);
                results[i] = new(query.ContractId, query.Compilation, diagnosis.Status, diagnostic: diagnosis);
                // Preserve the remaining entries as not-run. No retry after an unknown server outcome.
                break;
            }
        }
        return new(GraphVerificationTier.V1, capture.Report, results);
    }
}

internal static class EvidenceOutcome
{
    internal static GraphEvidenceStatus Combine(IEnumerable<GraphEvidenceStatus> statuses)
    {
        var rank = 0;
        foreach (var status in statuses) rank = Math.Max(rank, status switch
        {
            GraphEvidenceStatus.Fail => 5, GraphEvidenceStatus.Blocked => 4, GraphEvidenceStatus.Unsupported => 3,
            GraphEvidenceStatus.NotRun => 2, GraphEvidenceStatus.Pass => 1, _ => 4
        });
        return rank switch { 5 => GraphEvidenceStatus.Fail, 4 => GraphEvidenceStatus.Blocked, 3 => GraphEvidenceStatus.Unsupported,
            1 => GraphEvidenceStatus.Pass, _ => GraphEvidenceStatus.NotRun };
    }
}
