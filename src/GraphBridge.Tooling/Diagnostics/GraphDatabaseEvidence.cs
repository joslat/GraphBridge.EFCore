using System.Text.RegularExpressions;
using GraphBridge.EntityFrameworkCore.Schema;

namespace GraphBridge.Tooling.Diagnostics;

/// <summary>Counts a fixed schema finding without retaining schema names, messages or proposed DDL.</summary>
public sealed record GraphSchemaFindingEvidence(string Code, GraphSchemaIssueCategory Category, GraphSchemaStatus Status, int Count);

/// <summary>Complete metadata observations from the provider inspector; not a data scan or query-result certificate.</summary>
public sealed class GraphDatabaseEvidence
{
    internal GraphDatabaseEvidence(string profileId, GraphSchemaInspection inspection)
    {
        EvidenceIdentity.Validate(profileId);
        ProfileId = profileId;
        var capabilities = inspection.Capabilities;
        ServerVersion = Regex.IsMatch(capabilities.ServerVersion, @"\A[0-9]{1,4}\.[0-9]{1,2}(?:\.[0-9]{1,3})?\z")
            ? capabilities.ServerVersion : "unrecognized";
        Edition = capabilities.Edition.ToLowerInvariant() is "community" or "enterprise"
            ? capabilities.Edition.ToLowerInvariant() : "unrecognized";
        AdvertisedCypherVersions = Array.AsReadOnly(capabilities.AdvertisedCypherVersions
            .Where(v => Regex.IsMatch(v, @"\A[0-9]{1,3}\z")).Take(8).ToArray());
        NodeUniqueness = capabilities.NodeUniqueness;
        RelationshipUniqueness = capabilities.RelationshipUniqueness;
        RangeIndexes = capabilities.RangeIndexes;
        PropertyExistence = capabilities.PropertyExistence;
        PropertyType = capabilities.PropertyType;
        KeyConstraints = capabilities.KeyConstraints;
        GraphTypeObserved = inspection.Observation.GraphTypeSpecification is not null;
        SchemaStatus = inspection.Comparison.Status;
        Findings = Array.AsReadOnly(inspection.Comparison.Issues
            .GroupBy(i => (i.Code, i.Category, i.Status))
            .OrderBy(g => g.Key.Code, StringComparer.Ordinal).ThenBy(g => g.Key.Category).ThenBy(g => g.Key.Status)
            .Select(g => new GraphSchemaFindingEvidence(g.Key.Code, g.Key.Category, g.Key.Status, g.Count())).ToArray());
        Status = SchemaStatus == GraphSchemaStatus.Fail ? GraphEvidenceStatus.Fail
            : NodeUniqueness == GraphSchemaCapabilitySupport.Unknown || RangeIndexes == GraphSchemaCapabilitySupport.Unknown
                ? GraphEvidenceStatus.Blocked : GraphEvidenceStatus.Pass;
    }
    public string ProfileId { get; }
    public string ServerVersion { get; }
    public string Edition { get; }
    public IReadOnlyList<string> AdvertisedCypherVersions { get; }
    public GraphSchemaCapabilitySupport NodeUniqueness { get; }
    public GraphSchemaCapabilitySupport RelationshipUniqueness { get; }
    public GraphSchemaCapabilitySupport RangeIndexes { get; }
    public GraphSchemaCapabilitySupport PropertyExistence { get; }
    public GraphSchemaCapabilitySupport PropertyType { get; }
    public GraphSchemaCapabilitySupport KeyConstraints { get; }
    public bool GraphTypeObserved { get; }
    public GraphSchemaStatus SchemaStatus { get; }
    public IReadOnlyList<GraphSchemaFindingEvidence> Findings { get; }
    public GraphEvidenceStatus Status { get; }
}
