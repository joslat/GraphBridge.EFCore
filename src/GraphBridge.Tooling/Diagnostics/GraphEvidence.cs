using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using GraphBridge.EntityFrameworkCore.FluentCypher;
using GraphBridge.EntityFrameworkCore.Schema;
using Microsoft.EntityFrameworkCore;
using Neo4j.Driver;

namespace GraphBridge.Tooling.Diagnostics;

public enum GraphEvidenceStatus { Pass, Fail, Unsupported, Blocked, NotRun }
public enum GraphEvidenceMode { Offline, Inspect, Semantic }
public enum GraphDiagnosticSeverity { Information, Warning, Error }
public enum GraphDiagnosticPhase { Model, Query, Schema, Connection, Execution }

/// <summary>A fixed, actionable diagnosis. Exception text and application values are never retained.</summary>
public sealed record GraphDiagnosticResult
{
    internal GraphDiagnosticResult(string code, GraphEvidenceStatus status, GraphDiagnosticSeverity severity,
        GraphDiagnosticPhase phase, string component, string explanation, string action, string documentation)
        => (Code, Status, Severity, Phase, Component, Explanation, SuggestedAction, Documentation) =
            (code, status, severity, phase, component, explanation, action, documentation);
    public string Code { get; }
    public GraphEvidenceStatus Status { get; }
    public GraphDiagnosticSeverity Severity { get; }
    public GraphDiagnosticPhase Phase { get; }
    public string Component { get; }
    public string Explanation { get; }
    public string SuggestedAction { get; }
    public string Documentation { get; }
    /// <summary>The stable query code behind GBD-QUERY-001 (LINQ, GBQ###) or GBD-QUERY-002 (Fluent, GBFC###), when the failure carried one.</summary>
    public string? Subcode { get; internal init; }
}

/// <summary>Parameter position and broad store kind only; contains no parameter name, value or value length.</summary>
public sealed record GraphParameterEvidence(int Position, string Kind);

/// <summary>An immutable compiled statement snapshot. Text is excluded unless explicitly disclosed.</summary>
public sealed class GraphQueryEvidence
{
    private GraphQueryEvidence(string contractId, string? fingerprint, string? text, IReadOnlyList<GraphParameterEvidence> parameters,
        GraphQueryDefinition? definition = null, GraphDiagnosticResult? diagnostic = null, GraphEvidenceStatus compilation = GraphEvidenceStatus.Pass,
        CypherSemanticsSnapshot? semantics = null)
        => (ContractId, StatementFingerprint, Statement, Parameters, Definition, Diagnostic, Compilation, Semantics) =
            (contractId, fingerprint, text, parameters, definition, diagnostic, compilation, semantics);
    public string ContractId { get; }
    public string? StatementFingerprint { get; }
    public string? Statement { get; }
    public bool SensitiveTextIncluded => Statement is not null;
    public int ParameterCount => Parameters.Count;
    public IReadOnlyList<GraphParameterEvidence> Parameters { get; }
    public GraphQueryDefinition? Definition { get; }
    public GraphDiagnosticResult? Diagnostic { get; }
    public GraphEvidenceStatus Compilation { get; }
    public GraphEvidenceStatus Execution => GraphEvidenceStatus.NotRun;
    /// <summary>Opt-in typed compiler metadata from this exact command; absent without statement disclosure.</summary>
    public CypherSemanticsSnapshot? Semantics { get; }

    /// <summary>Copies an existing Fluent command without reading parameter values into the report or opening transport.</summary>
    /// <remarks>Contract IDs are application-declared public identifiers. Disclosed text may contain sensitive model identifiers.
    /// A statement hash establishes equality, not anonymization against guesses. Values remain available only via the original command.</remarks>
    public static GraphQueryEvidence Capture(string contractId, CypherCommand command, bool includeStatement = false)
    {
        EvidenceIdentity.Validate(contractId);
        ArgumentNullException.ThrowIfNull(command);
        var text = command.Text.ReplaceLineEndings("\n");
        var parameters = command.Parameters.OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select((p, index) => new GraphParameterEvidence(index, Kind(p.Value))).ToArray();
        return new(contractId, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant(),
            includeStatement ? text : null, Array.AsReadOnly(parameters), semantics: includeStatement ? command.Semantics : null);
    }

    internal static GraphQueryEvidence Capture(GraphQueryDefinition definition, CypherCommand command, bool includeStatement)
    {
        var snapshot = Capture(definition.Id, command, includeStatement);
        return new(definition.Id, snapshot.StatementFingerprint, snapshot.Statement, snapshot.Parameters, definition, semantics: snapshot.Semantics);
    }

    internal static GraphQueryEvidence Unobserved(GraphQueryDefinition definition, GraphDiagnosticResult? diagnostic = null)
        => new(definition.Id, null, null, Array.Empty<GraphParameterEvidence>(), definition, diagnostic,
            diagnostic?.Status ?? GraphEvidenceStatus.NotRun);

    // Do not call ToString, arbitrary getters, enumerate arrays/maps or serialize unknown value types.
    private static string Kind(object? value) => value switch
    {
        null => "null", string => "string", bool => "boolean",
        sbyte or byte or short or ushort or int or uint or long or ulong => "integer",
        float or double => "floating-point", byte[] => "bytes", Array => "array",
        _ => "opaque"
    };
}

/// <summary>Storage fingerprint and counts from the existing finalized-model manifest; no schema names are retained.</summary>
public sealed class GraphModelEvidence
{
    internal GraphModelEvidence(GraphSchemaManifest manifest)
        => (Fingerprint, Nodes, Relationships) = (manifest.Fingerprint, manifest.Nodes.Count, manifest.Relationships.Count);
    public string Fingerprint { get; }
    public int Nodes { get; }
    public int Relationships { get; }
}

/// <summary>Actual loaded library versions; server observations are deliberately not inferred from the driver.</summary>
public sealed record GraphEvidenceVersions(string Provider, string EfCore, string Driver, string Tooling)
{
    public static GraphEvidenceVersions Loaded() => new(Version(typeof(CypherCommand).Assembly), Version(typeof(DbContext).Assembly),
        Version(typeof(IDriver).Assembly), Version(typeof(GraphEvidenceVersions).Assembly));
    private static string Version(Assembly assembly)
        => assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString() ?? "unknown";
}

/// <summary>Version 1 offline/connected evidence envelope. It records observations, not a compatibility certificate.</summary>
public sealed class GraphEvidenceReport
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower) }
    };

    public GraphEvidenceReport(GraphEvidenceMode mode, IEnumerable<GraphQueryEvidence> queries,
        IEnumerable<GraphDiagnosticResult> diagnostics, GraphModelEvidence? model = null, GraphDatabaseEvidence? database = null,
        IEnumerable<GraphSearchIndexEvidence>? searchIndexes = null)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        ArgumentNullException.ThrowIfNull(queries);
        ArgumentNullException.ThrowIfNull(diagnostics);
        var querySnapshot = queries.Take(257).ToArray();
        var diagnosticSnapshot = diagnostics.Take(1025).ToArray();
        var indexSnapshot = (searchIndexes ?? []).Take(65).ToArray();
        if (querySnapshot.Length > 256 || querySnapshot.Any(q => q is null)
            || querySnapshot.Select(q => q.ContractId).Distinct(StringComparer.Ordinal).Count() != querySnapshot.Length)
            throw new ArgumentException("Reports require at most 256 queries with distinct contract IDs.", nameof(queries));
        if (diagnosticSnapshot.Length > 1024 || diagnosticSnapshot.Any(d => d is null))
            throw new ArgumentException("Reports require at most 1024 non-null diagnostics.", nameof(diagnostics));
        if (indexSnapshot.Length > 64 || indexSnapshot.Any(i => i is null)
            || indexSnapshot.Select(i => i.Id).Distinct(StringComparer.Ordinal).Count() != indexSnapshot.Length)
            throw new ArgumentException("Reports require at most 64 search index observations with distinct IDs.", nameof(searchIndexes));
        Mode = mode;
        Queries = Array.AsReadOnly(querySnapshot.OrderBy(q => q.ContractId, StringComparer.Ordinal).ToArray());
        Diagnostics = Array.AsReadOnly(diagnosticSnapshot.OrderBy(d => d.Code, StringComparer.Ordinal).ThenBy(d => d.Phase).ToArray());
        Versions = GraphEvidenceVersions.Loaded();
        Model = model;
        Database = database;
        SearchIndexes = indexSnapshot.Length == 0 ? null : Array.AsReadOnly(indexSnapshot.OrderBy(i => i.Id, StringComparer.Ordinal).ToArray());
    }
    public int FormatVersion => 1;
    public GraphEvidenceMode Mode { get; }
    public GraphEvidenceVersions Versions { get; }
    public GraphModelEvidence? Model { get; }
    public GraphDatabaseEvidence? Database { get; }
    /// <summary>Declared search indexes and what connected Doctor observed; null when none are declared.</summary>
    public IReadOnlyList<GraphSearchIndexEvidence>? SearchIndexes { get; }
    public IReadOnlyList<GraphQueryEvidence> Queries { get; }
    public IReadOnlyList<GraphDiagnosticResult> Diagnostics { get; }
    public bool ParameterValuesIncluded => false;
    public bool SensitiveTextIncluded => Queries.Any(q => q.SensitiveTextIncluded);
    public GraphEvidenceStatus DatabaseObservation => Database?.Status ?? GraphEvidenceStatus.NotRun;

    /// <summary>No observations is not-run; a blocked or unsupported observation cannot be an overall pass.</summary>
    public GraphEvidenceStatus Status => Has(GraphEvidenceStatus.Fail) ? GraphEvidenceStatus.Fail
        : Has(GraphEvidenceStatus.Blocked) || Mode == GraphEvidenceMode.Semantic
            || Mode == GraphEvidenceMode.Inspect && Database is null ? GraphEvidenceStatus.Blocked
        : Has(GraphEvidenceStatus.Unsupported) ? GraphEvidenceStatus.Unsupported
        : Has(GraphEvidenceStatus.NotRun) || Queries.Count == 0 && Diagnostics.Count == 0 ? GraphEvidenceStatus.NotRun
        : GraphEvidenceStatus.Pass;
    private bool Has(GraphEvidenceStatus status) => Diagnostics.Any(d => d.Status == status) || Queries.Any(q => q.Compilation == status)
        || Database?.Status == status;
    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);
    /// <summary>Returns the same retained evidence as plain text; does not compile or access the database.</summary>
    public string ToText() => GraphEvidenceText.Format(this);
}

internal static class EvidenceIdentity
{
    internal static void Validate(string id)
    {
        if (id is null || id.Length > 100 || !Regex.IsMatch(id, "\\A[a-z][a-z0-9]*(?:[.-][a-z0-9]+)*\\z", RegexOptions.CultureInvariant))
            throw new ArgumentException("Contract IDs require lowercase ASCII segments separated by dots or hyphens, at most 100 characters.", nameof(id));
    }
}
