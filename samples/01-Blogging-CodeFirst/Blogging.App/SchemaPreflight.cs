using System.Text.Json;
using System.Text.Json.Serialization;
using GraphBridge.EntityFrameworkCore.Schema;
using Microsoft.EntityFrameworkCore;
using Neo4j.Driver;

namespace GraphBridge.Samples.Blogging;

/// <summary>Copyable application-owned CI policy over the provider's read-only schema inspection.</summary>
public static class SchemaPreflight
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <returns>0 for compatible metadata, 3 for drift, 2 when inspection could not complete.</returns>
    public static async Task<int> RunAsync(DbContext context, TextWriter output,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(output);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var inspection = await context.Database.InspectGraphSchemaAsync(cancellationToken: cancellationToken);
            // Missing optional performance indexes are warnings in the general comparer, but this
            // application's deployment policy requires every explicitly mapped index to be ready.
            var blockers = inspection.Comparison.Issues.Where(issue => issue.Status == GraphSchemaStatus.Fail
                || issue.Category == GraphSchemaIssueCategory.Index && issue.Status == GraphSchemaStatus.Warn).ToArray();
            var unknownCapabilities = inspection.Capabilities.NodeUniqueness == GraphSchemaCapabilitySupport.Unknown
                || inspection.Capabilities.RelationshipUniqueness == GraphSchemaCapabilitySupport.Unknown
                || inspection.Capabilities.RangeIndexes == GraphSchemaCapabilitySupport.Unknown;
            var exitCode = blockers.Length > 0 || unknownCapabilities ? 3 : 0;
            var report = new
            {
                formatVersion = 1,
                outcome = exitCode == 0 ? "compatible" : "drift",
                exitCode,
                database = inspection.DatabaseName,
                modelFingerprint = inspection.Manifest.Fingerprint,
                capabilities = inspection.Capabilities,
                policy = "required-identities-and-mapped-indexes-v1",
                unknownCapabilities,
                blockers,
                issues = inspection.Comparison.Issues,
                dataScanned = false,
                schemaModified = false
            };
            await output.WriteLineAsync(JsonSerializer.Serialize(report, Json));
            return exitCode;
        }
        catch (Exception error) when (error is Neo4jException or InvalidOperationException
            or NotSupportedException or OperationCanceledException or IOException)
        {
            // Driver/server exception messages can contain query values or addresses. Only a stable
            // category crosses the machine-readable boundary; credentials/stack traces are omitted.
            await output.WriteLineAsync(JsonSerializer.Serialize(new
            {
                formatVersion = 1, outcome = "error", exitCode = 2,
                code = error is OperationCanceledException ? "InspectionCanceled" : "InspectionFailed",
                message = "Inspection did not complete. Check connectivity, selected database, permissions and supported model metadata.",
                dataScanned = false, schemaModified = false
            }, Json));
            return 2;
        }
    }
}
