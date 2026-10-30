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
        ["GBD-LABEL-000"] = Item("GBD-LABEL-000", GraphEvidenceStatus.Pass, GraphDiagnosticSeverity.Information, GraphDiagnosticPhase.Schema,
            "Additional labels", "Every node with the label of a type that has additional labels also carries them.",
            "Nothing to do; the check counts nodes, it doesn't read their properties.", "tools/DOCTOR.md#additional-labels"),
        ["GBD-LABEL-001"] = Item("GBD-LABEL-001", GraphEvidenceStatus.Pass, GraphDiagnosticSeverity.Warning, GraphDiagnosticPhase.Schema,
            "Additional labels", "Some nodes carry a type's label but not all of its additional labels: queries of the type skip them, keyed writes don't touch them, and reading them through a native reference or an Include fails.",
            "Find them with the query in the documentation, then upsert those entities by key, which adds the labels, or add them in a reviewed data step. No changes were applied.", "tools/DOCTOR.md#additional-labels"),
        ["GBD-SEARCH-000"] = Item("GBD-SEARCH-000", GraphEvidenceStatus.Pass, GraphDiagnosticSeverity.Information, GraphDiagnosticPhase.Schema,
            "Search indexes", "Every declared search index exists, is ONLINE and matches its declaration.",
            "Review the per-index sample counts; this does not check search quality or result relevance.", "tools/DOCTOR.md#search-indexes"),
        ["GBD-SEARCH-001"] = Item("GBD-SEARCH-001", GraphEvidenceStatus.Fail, GraphDiagnosticSeverity.Error, GraphDiagnosticPhase.Schema,
            "Search indexes", "A declared search index does not exist in the selected database, so every search against it fails.",
            "Check the index name and the selected database, then create the index in your deployment step and wait until it is ONLINE. GraphBridge never creates it.", "tools/DOCTOR.md#search-indexes"),
        ["GBD-SEARCH-002"] = Item("GBD-SEARCH-002", GraphEvidenceStatus.Blocked, GraphDiagnosticSeverity.Warning, GraphDiagnosticPhase.Schema,
            "Search indexes", "A declared search index exists but is not ONLINE yet; searches fail until it is.",
            "Wait for population to finish (for example with db.awaitIndex) before serving searches, then run Doctor again.", "tools/DOCTOR.md#search-indexes"),
        ["GBD-SEARCH-003"] = Item("GBD-SEARCH-003", GraphEvidenceStatus.Fail, GraphDiagnosticSeverity.Error, GraphDiagnosticPhase.Schema,
            "Search indexes", "A declared search index is in the FAILED state and serves no searches.",
            "Read the population failure in the Neo4j log, fix its cause, then drop and recreate the index.", "tools/DOCTOR.md#search-indexes"),
        ["GBD-SEARCH-004"] = Item("GBD-SEARCH-004", GraphEvidenceStatus.Fail, GraphDiagnosticSeverity.Error, GraphDiagnosticPhase.Schema,
            "Search indexes", "The index with the declared name is a different kind than declared: vector or full-text, node or relationship.",
            "Point the application at the right index, or recreate this one with the declared kind and entity type.", "tools/DOCTOR.md#search-indexes"),
        ["GBD-SEARCH-005"] = Item("GBD-SEARCH-005", GraphEvidenceStatus.Fail, GraphDiagnosticSeverity.Error, GraphDiagnosticPhase.Schema,
            "Search indexes", "The index covers different labels, relationship types or properties than declared, so searches miss the data the application writes.",
            "Recreate the index over the declared labels and properties, or correct the declaration to match what the application writes.", "tools/DOCTOR.md#search-indexes"),
        ["GBD-SEARCH-006"] = Item("GBD-SEARCH-006", GraphEvidenceStatus.Fail, GraphDiagnosticSeverity.Error, GraphDiagnosticPhase.Schema,
            "Search indexes", "The vector index dimensions differ from the declared embedding dimensions, so every search with those embeddings fails.",
            "Recreate the index with the embedding model's dimensions, or re-embed with the model the index was built for.", "tools/DOCTOR.md#search-indexes"),
        ["GBD-SEARCH-007"] = Item("GBD-SEARCH-007", GraphEvidenceStatus.Pass, GraphDiagnosticSeverity.Warning, GraphDiagnosticPhase.Schema,
            "Search indexes", "The vector index uses a different similarity function than declared, so scores mean something else.",
            "Recreate the index with the declared similarity function, or update the declaration and any score thresholds.", "tools/DOCTOR.md#search-indexes"),
        ["GBD-SEARCH-008"] = Item("GBD-SEARCH-008", GraphEvidenceStatus.Pass, GraphDiagnosticSeverity.Warning, GraphDiagnosticPhase.Schema,
            "Search indexes", "Some sampled stored values cannot be indexed and are silently left out of search: wrong dimensions, non-numeric values or zero vectors on a cosine index.",
            "Fix the code that writes the property, then re-embed the affected entities. The per-index evidence counts them.", "tools/DOCTOR.md#search-indexes"),
        ["GBD-SEARCH-009"] = Item("GBD-SEARCH-009", GraphEvidenceStatus.Pass, GraphDiagnosticSeverity.Warning, GraphDiagnosticPhase.Schema,
            "Search indexes", "No sampled entity has the indexed property, so the index has nothing to search.",
            "Check the label and property names the application writes, and whether ingestion or embedding has run against this database.", "tools/DOCTOR.md#search-indexes"),
        ["GBD-SEARCH-010"] = Item("GBD-SEARCH-010", GraphEvidenceStatus.Unsupported, GraphDiagnosticSeverity.Warning, GraphDiagnosticPhase.Schema,
            "Search indexes", "This server cannot run GraphBridge typed search for the declared index kind.",
            "Typed search needs Neo4j 2026 with Cypher 25 (full-text needs 2026.09 or later). Use native Cypher on other servers.", "features/GRAPH_SEARCH.md"),
        ["GBD-SEARCH-011"] = Item("GBD-SEARCH-011", GraphEvidenceStatus.Pass, GraphDiagnosticSeverity.Information, GraphDiagnosticPhase.Schema,
            "Search indexes", "A declared full-text index is eventually consistent, so new writes appear in results after a delay.",
            "That is fine for bulk-loaded data. For read-your-writes, create it with fulltext.eventually_consistent set to false.", "tools/DOCTOR.md#search-indexes"),
        ["GBD-SEARCH-012"] = Item("GBD-SEARCH-012", GraphEvidenceStatus.Fail, GraphDiagnosticSeverity.Error, GraphDiagnosticPhase.Schema,
            "Search indexes", "The declared vector index dimensions differ from the dimensions of the property the model maps with IsGraphVector, so the application writes vectors the index can't use.",
            "Use one embedding model for both: change IsGraphVector or the declaration, then recreate the index or re-embed.", "tools/DOCTOR.md#search-indexes"),
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
