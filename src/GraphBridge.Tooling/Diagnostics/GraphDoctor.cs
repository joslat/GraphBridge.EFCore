using Microsoft.EntityFrameworkCore;
using GraphBridge.EntityFrameworkCore.Schema;
using GraphBridge.EntityFrameworkCore.FluentCypher;

namespace GraphBridge.Tooling.Diagnostics;

/// <summary>Application-hosted model and registered-query inspection using the provider's existing interfaces.</summary>
public static class GraphDoctor
{
    /// <summary>Compiles the catalog and explicitly inspects selected-database metadata with the existing read-only provider inspector.</summary>
    /// <remarks>Uses no DDL and executes no registered query results. Profile ID is public application metadata, not a connection string.
    /// Metadata failures retain no partial database success. Cancellation follows the existing inspector's between-operation semantics.
    /// Schema warnings remain visible even when required constraints pass; this is not a database health certificate.</remarks>
    public static async Task<GraphEvidenceReport> InspectAsync(DbContext context, GraphQueryCatalog catalog, string profileId,
        int maxSchemaRows = 1_000, bool includeStatements = false, CancellationToken cancellationToken = default)
        => (await CaptureAsync(context, catalog, profileId, maxSchemaRows, includeStatements, cancellationToken).ConfigureAwait(false)).Report;

    internal static async Task<GraphInspectionCapture> CaptureAsync(DbContext context, GraphQueryCatalog catalog, string profileId,
        int maxSchemaRows, bool includeStatements, CancellationToken cancellationToken, bool prepareExecution = false)
    {
        EvidenceIdentity.Validate(profileId);
        if (maxSchemaRows is < 1 or > 10_000) throw new ArgumentOutOfRangeException(nameof(maxSchemaRows));
        var capture = CaptureOffline(context, catalog, includeStatements, cancellationToken, prepareExecution);
        var offline = capture.Report;
        var diagnostics = offline.Diagnostics.ToList();
        GraphDatabaseEvidence? database = null;
        var searchIndexes = offline.SearchIndexes;
        if (offline.Model is not null)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var inspection = await context.Database.InspectGraphSchemaAsync(maxSchemaRows, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                database = new GraphDatabaseEvidence(profileId, inspection);
                diagnostics.Add(GraphDiagnosticCatalog.Get(database.Status switch
                {
                    GraphEvidenceStatus.Fail => "GBD-SCHEMA-001",
                    GraphEvidenceStatus.Blocked => "GBD-CAP-001",
                    _ when database.SchemaStatus == GraphSchemaStatus.Warn => "GBD-SCHEMA-002",
                    _ => "GBD-SCHEMA-000"
                }));
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                diagnostics.Add(GraphDiagnosticCatalog.Classify(error, GraphDiagnosticPhase.Schema));
            }
        }
        // Search indexes are checked only against a completely observed profile; partial metadata stays not-run.
        if (database is not null && catalog.SearchIndexes.Count != 0)
        {
            try
            {
                var observed = await GraphSearchIndexInspection.InspectAsync(context, catalog.SearchIndexes, database, cancellationToken).ConfigureAwait(false);
                searchIndexes = observed.Indexes;
                diagnostics.AddRange(observed.Diagnostics);
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                diagnostics.Add(GraphDiagnosticCatalog.Classify(error, GraphDiagnosticPhase.Schema));
            }
        }
        // Additional labels (ADR 0055) are checked only when the model has them, against a completely observed profile.
        if (database is not null && context.Database.GetGraphSchemaManifest() is { } manifest && GraphLabelInspection.Applies(manifest))
        {
            try
            {
                diagnostics.AddRange(await GraphLabelInspection.InspectAsync(context, manifest, maxSchemaRows, cancellationToken).ConfigureAwait(false));
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                diagnostics.Add(GraphDiagnosticCatalog.Classify(error, GraphDiagnosticPhase.Schema));
            }
        }
        return capture with { Report = new(GraphEvidenceMode.Inspect, offline.Queries, diagnostics, offline.Model, database, searchIndexes) };
    }

    /// <summary>Finalizes the model, captures its storage fingerprint and compiles trusted factories without executing results.</summary>
    /// <remarks>The caller owns context/driver lifetime. Registered delegates must only construct queries; they are not sandboxed.
    /// Cancellation is observed between synchronous compilation steps and cannot interrupt application factory code.
    /// No schema DDL, query execution, change tracking or session creation is performed by this adapter.</remarks>
    public static GraphEvidenceReport InspectOffline(DbContext context, GraphQueryCatalog catalog,
        bool includeStatements = false, CancellationToken cancellationToken = default)
        => CaptureOffline(context, catalog, includeStatements, cancellationToken).Report;

    internal static GraphInspectionCapture CaptureOffline(DbContext context, GraphQueryCatalog catalog,
        bool includeStatements, CancellationToken cancellationToken, bool prepareExecution = false)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(catalog);
        var queries = catalog.Contracts.Select(c => GraphQueryEvidence.Unobserved(c.Definition)).ToArray();
        var searchIndexes = catalog.SearchIndexes.Select(GraphSearchIndexEvidence.NotObserved).ToArray();
        var diagnostics = new List<GraphDiagnosticResult>();
        var commands = new Dictionary<string, CypherCommand>(StringComparer.Ordinal);
        var prepared = new Dictionary<string, GraphPreparedContract>(StringComparer.Ordinal);
        GraphModelEvidence? model = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            model = new GraphModelEvidence(context.Database.GetGraphSchemaManifest());
            diagnostics.Add(GraphDiagnosticCatalog.Get("GBD-MODEL-000"));
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            diagnostics.Add(GraphDiagnosticCatalog.Classify(error, GraphDiagnosticPhase.Model));
            return new(new(GraphEvidenceMode.Offline, queries, diagnostics, searchIndexes: searchIndexes), commands, prepared);
        }

        for (var i = 0; i < catalog.Contracts.Count; i++)
        {
            var contract = catalog.Contracts[i];
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Typed snapshots disclose mapped metadata under the same explicit text opt-in.
                var executable = prepareExecution ? contract.Prepare(context, captureSemantics: includeStatements) : null;
                var command = executable?.Command ?? (includeStatements ? contract.Inspect(context) : contract.Compile(context));
                cancellationToken.ThrowIfCancellationRequested();
                queries[i] = GraphQueryEvidence.Capture(contract.Definition, command, includeStatements);
                commands.Add(contract.Definition.Id, command);
                if (executable is not null) prepared.Add(contract.Definition.Id, executable);
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                queries[i] = GraphQueryEvidence.Unobserved(contract.Definition,
                    GraphDiagnosticCatalog.Classify(error, GraphDiagnosticPhase.Query, fluentQuery: true));
                if (error is OperationCanceledException) break;
            }
        }
        return new(new(GraphEvidenceMode.Offline, queries, diagnostics, model, searchIndexes: searchIndexes), commands, prepared);
    }
}

// Short-lived application-owned parameter snapshots; never part of a serializable report.
internal sealed record GraphInspectionCapture(GraphEvidenceReport Report, IReadOnlyDictionary<string, CypherCommand> Commands,
    IReadOnlyDictionary<string, GraphPreparedContract> Prepared);
