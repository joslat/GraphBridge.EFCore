using GraphBridge.EntityFrameworkCore.Diagnostics;
using Neo4j.Driver;

namespace GraphBridge.Tooling.Diagnostics;

/// <summary>Stable report diagnoses. They supplement existing exception contracts without changing thrown types.</summary>
public static class GraphDiagnosticCatalog
{
    private static readonly IReadOnlyDictionary<string, GraphDiagnosticResult> Definitions = new Dictionary<string, GraphDiagnosticResult>(StringComparer.Ordinal)
    {
        ["GBD-MODEL-000"] = Item("GBD-MODEL-000", GraphEvidenceStatus.Pass, GraphDiagnosticSeverity.Information, GraphDiagnosticPhase.Model,
            "Finalized model", "The finalized model passed the selected graph mapping validation.",
            "Review the supported mapping boundary; this result does not observe database state.", "tools/MAPPING_GENERATOR.md"),
        ["GBD-MODEL-001"] = Item("GBD-MODEL-001", GraphEvidenceStatus.Fail, GraphDiagnosticSeverity.Error, GraphDiagnosticPhase.Model,
            "Finalized model", "The application model could not be finalized or validated for graph storage.",
            "Check node labels, identity, scalar mappings and explicit relationship endpoint roles in the application model.", "tools/MAPPING_GENERATOR.md"),
        ["GBD-QUERY-001"] = Item("GBD-QUERY-001", GraphEvidenceStatus.Unsupported, GraphDiagnosticSeverity.Error, GraphDiagnosticPhase.Query,
            "EF query translation", "The selected EF query shape could not be translated within the supported contract.",
            "Reduce the query to a documented shape or use an explicitly reviewed native query; include a synthetic repro when reporting a missing shape.", "reference/KNOWN_LIMITATIONS.md"),
        ["GBD-QUERY-002"] = Item("GBD-QUERY-002", GraphEvidenceStatus.Unsupported, GraphDiagnosticSeverity.Error, GraphDiagnosticPhase.Query,
            "Fluent Cypher compilation", "The selected Fluent query could not be compiled within the supported contract.",
            "Check MATCH and WITH alias scope, mapped endpoint direction, scalar projection and explicit finite limits.", "features/FLUENT_CYPHER.md"),
        ["GBD-SCHEMA-000"] = Item("GBD-SCHEMA-000", GraphEvidenceStatus.Pass, GraphDiagnosticSeverity.Information, GraphDiagnosticPhase.Schema,
            "Schema inspection", "The completed metadata comparison satisfies the selected schema expectations.",
            "Review the observed profile; metadata inspection does not establish data integrity or query result semantics.", "features/SCHEMA_AND_MIGRATIONS.md"),
        ["GBD-SCHEMA-002"] = Item("GBD-SCHEMA-002", GraphEvidenceStatus.Pass, GraphDiagnosticSeverity.Warning, GraphDiagnosticPhase.Schema,
            "Schema inspection", "Required schema expectations pass, with warnings requiring manual review.",
            "Review the summarized warning codes, including application-enforced properties, optional indexes and unmanaged schema. No changes were applied.", "features/SCHEMA_AND_MIGRATIONS.md"),
        ["GBD-SCHEMA-001"] = Item("GBD-SCHEMA-001", GraphEvidenceStatus.Fail, GraphDiagnosticSeverity.Error, GraphDiagnosticPhase.Schema,
            "Schema inspection", "A required schema expectation is not satisfied by the observed profile.",
            "Review the schema inspection details and explicitly provision the approved constraint or index; this tool performs no repair.", "features/SCHEMA_AND_MIGRATIONS.md"),
        ["GBD-CAP-001"] = Item("GBD-CAP-001", GraphEvidenceStatus.Blocked, GraphDiagnosticSeverity.Warning, GraphDiagnosticPhase.Schema,
            "Server capability", "A required capability could not be established from available observations.",
            "Confirm the selected server version, edition and metadata permissions; unknown is not supported.", "reference/COMPATIBILITY.md"),
        ["GBD-CONN-001"] = Item("GBD-CONN-001", GraphEvidenceStatus.Blocked, GraphDiagnosticSeverity.Error, GraphDiagnosticPhase.Connection,
            "Database connection", "The selected database could not be reached or the connection could not be established.",
            "Check the configured Bolt endpoint, database, TLS and server readiness in the application host.", "reference/TROUBLESHOOTING.md"),
        ["GBD-CONN-002"] = Item("GBD-CONN-002", GraphEvidenceStatus.Blocked, GraphDiagnosticSeverity.Error, GraphDiagnosticPhase.Connection,
            "Database permissions", "Authentication or metadata authorization prevented complete inspection.",
            "Review the application credential and least-privilege metadata grants. Do not share credentials or treat partial metadata as a clean result.", "features/SCHEMA_AND_MIGRATIONS.md"),
        ["GBD-EXEC-001"] = Item("GBD-EXEC-001", GraphEvidenceStatus.Blocked, GraphDiagnosticSeverity.Warning, GraphDiagnosticPhase.Execution,
            "Cancellation", "The requested observation was canceled before complete evidence was collected.",
            "Review the cancellation/deadline budget before an explicit rerun; do not infer an outcome or retry a write automatically.", "features/TRANSACTIONS_AND_CONCURRENCY.md"),
        ["GBD-EXEC-002"] = Item("GBD-EXEC-002", GraphEvidenceStatus.Blocked, GraphDiagnosticSeverity.Error, GraphDiagnosticPhase.Execution,
            "Observation", "An unclassified failure prevented complete evidence collection.",
            "Inspect the original exception locally and prepare a sanitized minimal reproduction. The report deliberately omits exception text.", "reference/TROUBLESHOOTING.md")
    };

    public static IReadOnlyList<string> Codes { get; } = Array.AsReadOnly(Definitions.Keys.Order(StringComparer.Ordinal).ToArray());
    public static GraphDiagnosticResult Get(string code)
    {
        ArgumentNullException.ThrowIfNull(code);
        return Definitions.TryGetValue(code, out var item) ? item
            : throw new ArgumentException("Unknown GraphBridge report diagnostic code.", nameof(code));
    }

    /// <summary>Classifies a failure in the declared operation phase without retaining or reading its message/stack.</summary>
    /// <remarks>Query means offline compilation, not server execution. This helper does not throw, wrap, retry or handle
    /// the original failure. Unknown failures remain blocked; callers retain original exceptions locally.</remarks>
    public static GraphDiagnosticResult Classify(Exception failure, GraphDiagnosticPhase phase, bool fluentQuery = false)
    {
        ArgumentNullException.ThrowIfNull(failure);
        if (!Enum.IsDefined(phase)) throw new ArgumentOutOfRangeException(nameof(phase));
        var code = failure switch
        {
            OperationCanceledException => "GBD-EXEC-001",
            AuthenticationException => "GBD-CONN-002",
            Neo4jException server when server.Code?.StartsWith("Neo.ClientError.Security.", StringComparison.Ordinal) == true => "GBD-CONN-002",
            ServiceUnavailableException or SessionExpiredException => "GBD-CONN-001",
            InvalidOperationException or ArgumentException or NotSupportedException when phase == GraphDiagnosticPhase.Model => "GBD-MODEL-001",
            Neo4jQueryTranslationException when phase == GraphDiagnosticPhase.Query => "GBD-QUERY-001",
            InvalidOperationException or ArgumentException or NotSupportedException when phase == GraphDiagnosticPhase.Query && fluentQuery => "GBD-QUERY-002",
            _ => "GBD-EXEC-002"
        };
        var result = Get(code);
        // Only the fixed code is retained; the exception text never is.
        var subcode = code switch
        {
            "GBD-QUERY-001" => failure.Data["GraphBridge.QueryDiagnostic"] as string,
            "GBD-QUERY-002" => failure.Data["GraphBridge.FluentDiagnostic"] as string,
            _ => null
        };
        return subcode is not null && System.Text.RegularExpressions.Regex.IsMatch(subcode, @"\AGB(FC|Q)[0-9]{3}\z")
            ? result with { Subcode = subcode } : result;
    }
    private static GraphDiagnosticResult Item(string code, GraphEvidenceStatus status, GraphDiagnosticSeverity severity,
        GraphDiagnosticPhase phase, string component, string explanation, string action, string page)
        => new(code, status, severity, phase, component, explanation, action,
            "https://github.com/joslat/GraphBridge.EFCore/blob/main/docs/" + page);
}
