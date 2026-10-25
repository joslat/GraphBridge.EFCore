using System.Globalization;

namespace GraphBridge.EntityFrameworkCore.Schema;

public enum GraphSchemaCapabilitySupport { Unknown, Unsupported, Supported }

/// <summary>Documented schema capabilities inferred from observed facts, not a certification of a server.</summary>
public sealed class GraphSchemaCapabilities
{
    private GraphSchemaCapabilities(string serverVersion, string edition, IEnumerable<string> advertisedCypherVersions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(edition);
        ArgumentNullException.ThrowIfNull(advertisedCypherVersions);
        ServerVersion = serverVersion;
        Edition = edition;
        AdvertisedCypherVersions = Array.AsReadOnly(advertisedCypherVersions
            .Select(value => value ?? throw new ArgumentException("Cypher versions cannot contain null."))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());

        var parts = serverVersion.Split('.');
        var knownVersion = parts.Length is 2 or 3
            && parts.All(part => part.Length > 0 && part.All(char.IsAsciiDigit))
            && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out _)
            && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out _)
            && (parts.Length == 2 || int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out _));
        var major = knownVersion ? int.Parse(parts[0], CultureInfo.InvariantCulture) : 0;
        var minor = knownVersion ? int.Parse(parts[1], CultureInfo.InvariantCulture) : 0;
        knownVersion &= major == 5 || (major is 2025 or 2026 && minor is >= 1 and <= 12);
        var enterprise = edition.Equals("enterprise", StringComparison.OrdinalIgnoreCase);
        var knownEdition = enterprise || edition.Equals("community", StringComparison.OrdinalIgnoreCase);
        var known = knownVersion && knownEdition;
        bool AtLeast5(int release) => major is 2025 or 2026 || major == 5 && minor >= release;
        GraphSchemaCapabilitySupport Available(bool minimum, bool enterpriseOnly = false)
            => !known ? GraphSchemaCapabilitySupport.Unknown
                : !minimum || enterpriseOnly && !enterprise ? GraphSchemaCapabilitySupport.Unsupported
                : GraphSchemaCapabilitySupport.Supported;

        // Official Cypher 5 constraint syntax: relationship uniqueness/key since 5.7;
        // property types since 5.9. Existence, types and keys are Enterprise features.
        // https://neo4j.com/docs/cypher-manual/5/constraints/syntax/
        NodeUniqueness = Available(true);
        RelationshipUniqueness = Available(AtLeast5(7));
        PropertyExistence = Available(true, enterpriseOnly: true);
        PropertyType = Available(AtLeast5(9), enterpriseOnly: true);
        KeyConstraints = Available(AtLeast5(7), enterpriseOnly: true);
        RangeIndexes = Available(true);
        // Vector and full-text index DDL is Community. GraphBridge has verified it on 2025 and 2026 releases only,
        // so Neo4j 5 stays Unknown rather than Unsupported.
        var searchIndexes = !known || major == 5 ? GraphSchemaCapabilitySupport.Unknown : GraphSchemaCapabilitySupport.Supported;
        VectorIndexes = searchIndexes;
        FullTextIndexes = searchIndexes;
        HasPropertyTypeObservation = knownVersion && AtLeast5(9);
        // Graph types were preview before 2026.06. Never infer the default dialect:
        // the inspector must explicitly execute CYPHER 25 for this optional observation.
        // https://neo4j.com/docs/cypher-manual/current/schema/graph-types/
        CanShowCurrentGraphType = known && enterprise && major == 2026 && minor >= 6
            && AdvertisedCypherVersions.Contains("25", StringComparer.Ordinal);
    }

    public string ServerVersion { get; }
    public string Edition { get; }
    public IReadOnlyList<string> AdvertisedCypherVersions { get; }
    public GraphSchemaCapabilitySupport NodeUniqueness { get; }
    public GraphSchemaCapabilitySupport RelationshipUniqueness { get; }
    public GraphSchemaCapabilitySupport PropertyExistence { get; }
    public GraphSchemaCapabilitySupport PropertyType { get; }
    /// <summary>Whether both node and relationship key constraints are available.</summary>
    public GraphSchemaCapabilitySupport KeyConstraints { get; }
    public GraphSchemaCapabilitySupport RangeIndexes { get; }
    public GraphSchemaCapabilitySupport VectorIndexes { get; }
    public GraphSchemaCapabilitySupport FullTextIndexes { get; }
    public bool HasPropertyTypeObservation { get; }
    public bool CanShowCurrentGraphType { get; }

    public static GraphSchemaCapabilities Detect(string serverVersion, string edition,
        IEnumerable<string> advertisedCypherVersions)
        => new(serverVersion, edition, advertisedCypherVersions);
}
