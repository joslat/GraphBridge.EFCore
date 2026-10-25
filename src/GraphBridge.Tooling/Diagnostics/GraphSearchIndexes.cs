using System.Collections;
using System.Globalization;
using GraphBridge.EntityFrameworkCore.Infrastructure;
using GraphBridge.EntityFrameworkCore.Metadata;
using GraphBridge.EntityFrameworkCore.Search;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Neo4j.Driver;

namespace GraphBridge.Tooling.Diagnostics;

/// <summary>A full-text or vector index the application's searches depend on, declared so connected Doctor can check it early.</summary>
/// <remarks>The index name, labels/types and properties are used only to observe the selected database; reports retain the
/// declared ID and finding codes, never the names. Doctor never creates, repairs or drops the index; a migration step can create it.</remarks>
public sealed class GraphSearchIndexExpectation
{
    private GraphSearchIndexExpectation(string id, GraphSearchKind kind, GraphSearchEntityKind entityKind, string indexName,
        IEnumerable<string> labelsOrTypes, IEnumerable<string> properties, int? dimensions, string? similarity, int sampleSize)
    {
        EvidenceIdentity.Validate(id);
        if (!Enum.IsDefined(entityKind)) throw new ArgumentOutOfRangeException(nameof(entityKind));
        if (sampleSize is < 0 or > 10_000) throw new ArgumentOutOfRangeException(nameof(sampleSize), "Sample 0..10,000 stored values; 0 checks metadata only.");
        ArgumentNullException.ThrowIfNull(labelsOrTypes);
        ArgumentNullException.ThrowIfNull(properties);
        var tokens = labelsOrTypes.Take(17).Select(Identifier).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var fields = properties.Take(17).Select(Identifier).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (tokens.Length is 0 or > 16 || fields.Length is 0 or > 16)
            throw new ArgumentException("Declare 1..16 labels or relationship types and 1..16 properties.");
        (Id, Kind, EntityKind, IndexName, Dimensions, Similarity, SampleSize) =
            (id, kind, entityKind, Identifier(indexName), dimensions, similarity, sampleSize);
        LabelsOrTypes = Array.AsReadOnly(tokens);
        Properties = Array.AsReadOnly(fields);
    }

    /// <summary>Declares a vector index over one label (or relationship type) and one embedding property.</summary>
    /// <param name="similarity">Null skips the similarity check; otherwise <c>cosine</c> or <c>euclidean</c>.</param>
    /// <param name="sampleSize">Stored values to check for dimensions and type (0 checks metadata only). No value is retained.</param>
    public static GraphSearchIndexExpectation Vector(string id, string indexName, string labelOrType, string property, int dimensions,
        string? similarity = null, GraphSearchEntityKind entityKind = GraphSearchEntityKind.Node, int sampleSize = 1_000)
    {
        if (dimensions is < 1 or > 4096) throw new ArgumentOutOfRangeException(nameof(dimensions), "Vector dimensions must be 1..4096.");
        var normalized = similarity?.ToLowerInvariant();
        if (normalized is not (null or "cosine" or "euclidean"))
            throw new ArgumentException("Similarity must be cosine, euclidean or null.", nameof(similarity));
        return new(id, GraphSearchKind.Vector, entityKind, indexName, [labelOrType], [property], dimensions, normalized, sampleSize);
    }

    /// <summary>Declares a full-text index over one or more labels (or relationship types) and string properties.</summary>
    /// <param name="sampleSize">Entities to look at for the indexed properties (0 checks metadata only). No value is retained.</param>
    public static GraphSearchIndexExpectation FullText(string id, string indexName, IEnumerable<string> labelsOrTypes,
        IEnumerable<string> properties, GraphSearchEntityKind entityKind = GraphSearchEntityKind.Node, int sampleSize = 1_000)
        => new(id, GraphSearchKind.FullText, entityKind, indexName, labelsOrTypes, properties, null, null, sampleSize);

    public string Id { get; }
    public GraphSearchKind Kind { get; }
    public GraphSearchEntityKind EntityKind { get; }
    public string IndexName { get; }
    public IReadOnlyList<string> LabelsOrTypes { get; }
    public IReadOnlyList<string> Properties { get; }
    public int? Dimensions { get; }
    public string? Similarity { get; }
    public int SampleSize { get; }

    // Names are escaped into metadata and sampling Cypher, so only plain identifiers are accepted.
    private static string Identifier(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 128 || !(char.IsAsciiLetter(value[0]) || value[0] == '_')
            || value.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '_')))
            throw new ArgumentException("Index names, labels, types and properties must be ASCII identifiers of at most 128 characters.");
        return value;
    }
}

/// <summary>What connected Doctor observed for one declared search index: finding codes and sample counts, never names or values.</summary>
public sealed record GraphSearchIndexEvidence(string Id, GraphSearchKind Kind, GraphSearchEntityKind EntityKind,
    GraphEvidenceStatus Status, IReadOnlyList<string> Findings, int? SampledValues = null, int? UnusableValues = null)
{
    internal static GraphSearchIndexEvidence NotObserved(GraphSearchIndexExpectation index)
        => new(index.Id, index.Kind, index.EntityKind, GraphEvidenceStatus.NotRun, []);
}

/// <summary>Read-only checks of declared search indexes in their own rolled-back read transaction.</summary>
internal static class GraphSearchIndexInspection
{
    private const string Index = "SHOW INDEXES YIELD name, state, type, entityType, labelsOrTypes, properties, options WHERE name = $index RETURN name, state, type, entityType, labelsOrTypes, properties, options LIMIT 2";

    internal static async Task<(IReadOnlyList<GraphSearchIndexEvidence> Indexes, IReadOnlyList<GraphDiagnosticResult> Diagnostics)> InspectAsync(
        DbContext context, IReadOnlyList<GraphSearchIndexExpectation> expectations, GraphDatabaseEvidence database, CancellationToken cancellationToken)
    {
        var options = context.GetService<IDbContextOptions>().FindExtension<Neo4jDbContextOptionsExtension>()
            ?? throw new InvalidOperationException("Search index checks require a GraphBridge context configured with UseNeo4j.");
        cancellationToken.ThrowIfCancellationRequested();
        IAsyncSession? session = null;
        IAsyncTransaction? transaction = null;
        try
        {
            session = options.Driver.AsyncSession(builder => builder.WithDatabase(options.DatabaseName)
                .WithDefaultAccessMode(AccessMode.Read).WithFetchSize(64));
            transaction = await session.BeginTransactionAsync(builder => builder.WithTimeout(TimeSpan.FromSeconds(60))
                .WithMetadata(new Dictionary<string, object> { ["graphbridgeOperation"] = "doctor-search-indexes" })).ConfigureAwait(false);
            var results = new List<GraphSearchIndexEvidence>(expectations.Count);
            foreach (var expectation in expectations)
                results.Add(await InspectAsync(transaction, expectation, database, context.Model, cancellationToken).ConfigureAwait(false));
            var codes = results.SelectMany(r => r.Findings).Distinct(StringComparer.Ordinal).ToList();
            if (codes.Count == 0) codes.Add("GBD-SEARCH-000");
            return (results, codes.Select(GraphDiagnosticCatalog.Get).ToArray());
        }
        finally
        {
            // Observation only: never commit, even when every read succeeded.
            if (transaction is not null)
            {
                try { await transaction.RollbackAsync().ConfigureAwait(false); }
                catch { /* Preserve the primary outcome; disposal follows. */ }
                await transaction.DisposeAsync().ConfigureAwait(false);
            }
            if (session is not null) await session.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task<GraphSearchIndexEvidence> InspectAsync(IAsyncTransaction transaction, GraphSearchIndexExpectation expected,
        GraphDatabaseEvidence database, IModel model, CancellationToken cancellationToken)
    {
        var findings = new List<string>();
        if (!Supported(database, expected.Kind)) findings.Add("GBD-SEARCH-010");
        if (ModelDimensionsDiffer(model, expected)) findings.Add("GBD-SEARCH-012");
        var rows = await ReadAsync(transaction, Index, new() { ["index"] = expected.IndexName }, 2, cancellationToken).ConfigureAwait(false);
        if (rows.Count != 1)
        {
            findings.Add(rows.Count == 0 ? "GBD-SEARCH-001" : "GBD-SEARCH-004");
            return Result(expected, findings);
        }
        var row = rows[0];
        var kindMatches = Text(row, "type") == (expected.Kind == GraphSearchKind.Vector ? "VECTOR" : "FULLTEXT")
            && Text(row, "entityType") == (expected.EntityKind == GraphSearchEntityKind.Node ? "NODE" : "RELATIONSHIP");
        if (!kindMatches)
        {
            findings.Add("GBD-SEARCH-004");
            return Result(expected, findings);
        }
        switch (Text(row, "state"))
        {
            case "ONLINE": break;
            case "FAILED": findings.Add("GBD-SEARCH-003"); break;
            default: findings.Add("GBD-SEARCH-002"); break;
        }
        if (!Strings(row, "labelsOrTypes").Order(StringComparer.Ordinal).SequenceEqual(expected.LabelsOrTypes, StringComparer.Ordinal)
            || !Strings(row, "properties").Order(StringComparer.Ordinal).SequenceEqual(expected.Properties, StringComparer.Ordinal))
            findings.Add("GBD-SEARCH-005");
        var config = Map(Map(row, "options"), "indexConfig");
        var cosine = false;
        if (expected.Kind == GraphSearchKind.Vector)
        {
            if (config.TryGetValue("vector.dimensions", out var raw) && raw is long observed && observed != expected.Dimensions)
                findings.Add("GBD-SEARCH-006");
            var similarity = config.TryGetValue("vector.similarity_function", out var function) && function is string text ? text.ToLowerInvariant() : null;
            cosine = similarity == "cosine";
            if (expected.Similarity is not null && similarity is not null && similarity != expected.Similarity) findings.Add("GBD-SEARCH-007");
        }
        else if (config.TryGetValue("fulltext.eventually_consistent", out var eventual) && eventual is true)
            findings.Add("GBD-SEARCH-011");

        // A declared label or property that differs from the index makes sampling meaningless; that finding already explains it.
        if (expected.SampleSize == 0 || findings.Contains("GBD-SEARCH-005") || !CanSample(database))
            return Result(expected, findings);
        var sample = await SampleAsync(transaction, expected, cosine, cancellationToken).ConfigureAwait(false);
        if (sample.Present == 0) findings.Add("GBD-SEARCH-009");
        else if (sample.Unusable > 0) findings.Add("GBD-SEARCH-008");
        return Result(expected, findings, sample.Present, sample.Unusable);
    }

    // ADR 0054: a declared vector index over a property the model maps with IsGraphVector must agree on the dimensions.
    private static bool ModelDimensionsDiffer(IModel model, GraphSearchIndexExpectation expected)
    {
        if (expected.Kind != GraphSearchKind.Vector) return false;
        var annotation = expected.EntityKind == GraphSearchEntityKind.Node ? GraphAnnotationNames.NodeLabel : GraphAnnotationNames.RelationshipType;
        return model.GetEntityTypes().Where(entity => entity[annotation] as string == expected.LabelsOrTypes[0])
            .SelectMany(entity => entity.GetProperties())
            .Any(property => (property[GraphAnnotationNames.PropertyName] as string ?? property.Name) == expected.Properties[0]
                && property[GraphAnnotationNames.VectorDimensions] is int dimensions && dimensions != expected.Dimensions);
    }

    private static async Task<(int Present, int Unusable)> SampleAsync(IAsyncTransaction transaction, GraphSearchIndexExpectation expected,
        bool cosine, CancellationToken cancellationToken)
    {
        var labels = string.Join("|", expected.LabelsOrTypes.Select(t => "`" + t + "`"));
        var match = expected.EntityKind == GraphSearchEntityKind.Node ? $"MATCH (e:{labels})" : $"MATCH ()-[e:{labels}]->()";
        // The scan is bounded too: a label with many entities and no indexed property must not become a full scan.
        var parameters = new Dictionary<string, object> { ["scan"] = (long)expected.SampleSize * 10, ["sample"] = (long)expected.SampleSize };
        if (expected.Kind == GraphSearchKind.FullText)
        {
            var any = string.Join(" OR ", expected.Properties.Select(p => "e.`" + p + "` IS NOT NULL"));
            var text = $"{match} WITH e LIMIT $scan WHERE {any} WITH e LIMIT $sample RETURN count(*) AS present";
            var rows = await ReadAsync(transaction, text, parameters, 1, cancellationToken).ConfigureAwait(false);
            return (Count(rows[0], "present"), 0);
        }
        parameters["dimensions"] = (long)expected.Dimensions!.Value;
        var property = "e.`" + expected.Properties[0] + "`";
        // Neo4j skips values it cannot index without an error: wrong dimensions, non-numeric values and, for cosine, zero vectors.
        var vector = $"{match} WITH e LIMIT $scan WHERE {property} IS NOT NULL WITH {property} AS value LIMIT $sample "
            + "WITH value, valueType(value) AS type "
            + "WITH type, CASE WHEN type STARTS WITH 'LIST<' THEN value ELSE [] END AS list "
            + "WITH type, list, size(list) AS length, CASE WHEN type STARTS WITH 'LIST<' THEN none(x IN list WHERE NOT (x IS :: INTEGER OR x IS :: FLOAT)) ELSE false END AS numeric "
            + "RETURN count(*) AS present, "
            + "count(CASE WHEN numeric AND length > 0 THEN 1 END) AS numeric, "
            + "count(CASE WHEN numeric AND length = $dimensions THEN 1 END) AS sized, "
            + "count(CASE WHEN numeric AND length = $dimensions AND all(x IN list WHERE x = 0) THEN 1 END) AS zero, "
            + "count(CASE WHEN type STARTS WITH 'VECTOR' THEN 1 END) AS vectors";
        var observed = (await ReadAsync(transaction, vector, parameters, 1, cancellationToken).ConfigureAwait(false))[0];
        var (present, numeric, sized, zero, vectors) = (Count(observed, "present"), Count(observed, "numeric"),
            Count(observed, "sized"), Count(observed, "zero"), Count(observed, "vectors"));
        // Native VECTOR values are not dimension-checked here; they are neither counted usable nor unusable.
        return (present, present - numeric - vectors + (numeric - sized) + (cosine ? zero : 0));
    }

    private static GraphSearchIndexEvidence Result(GraphSearchIndexExpectation expected, List<string> findings, int? present = null, int? unusable = null)
    {
        var codes = findings.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var statuses = codes.Select(c => GraphDiagnosticCatalog.Get(c).Status).ToArray();
        var status = statuses.Contains(GraphEvidenceStatus.Fail) ? GraphEvidenceStatus.Fail
            : statuses.Contains(GraphEvidenceStatus.Blocked) ? GraphEvidenceStatus.Blocked
            : statuses.Contains(GraphEvidenceStatus.Unsupported) ? GraphEvidenceStatus.Unsupported : GraphEvidenceStatus.Pass;
        return new(expected.Id, expected.Kind, expected.EntityKind, status, Array.AsReadOnly(codes), present, unusable);
    }

    // Same rule as the provider's typed search: a known Neo4j 2026 release with Cypher 25; full-text needs 2026.09+.
    private static bool Supported(GraphDatabaseEvidence database, GraphSearchKind kind)
    {
        var parts = database.ServerVersion.Split('.');
        return parts.Length >= 2 && parts[0] == "2026" && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var month)
            && month is >= 1 and <= 12 && (kind != GraphSearchKind.FullText || month >= 9)
            && database.AdvertisedCypherVersions.Contains("25", StringComparer.Ordinal) && database.Edition is "community" or "enterprise";
    }

    // valueType() and label expressions need Neo4j 5.13+ (or a calendar release); unrecognized servers are not sampled.
    private static bool CanSample(GraphDatabaseEvidence database)
    {
        var parts = database.ServerVersion.Split('.');
        if (parts.Length < 2 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minor)) return false;
        return major >= 2025 || major == 5 && minor >= 13;
    }

    private static async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ReadAsync(IAsyncTransaction transaction,
        string query, Dictionary<string, object> parameters, int maxRows, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var cursor = await transaction.RunAsync(query, parameters).ConfigureAwait(false);
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        while (await cursor.FetchAsync().ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (rows.Count == maxRows) throw new InvalidOperationException("Search index metadata exceeds its observation bound.");
            rows.Add(cursor.Current.Values.ToDictionary(p => p.Key, p => (object?)p.Value, StringComparer.Ordinal));
        }
        await cursor.ConsumeAsync().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return rows;
    }

    private static string Text(IReadOnlyDictionary<string, object?> row, string name)
        => row.TryGetValue(name, out var value) && value is string text ? text
            : throw new InvalidOperationException("Search index metadata has a missing or invalid string field.");
    private static IReadOnlyDictionary<string, object?> Map(IReadOnlyDictionary<string, object?> row, string name)
        => row.TryGetValue(name, out var value) && value is IReadOnlyDictionary<string, object?> map ? map
            : row.TryGetValue(name, out value) && value is IDictionary<string, object> legacy
                ? legacy.ToDictionary(p => p.Key, p => (object?)p.Value, StringComparer.Ordinal)
                : throw new InvalidOperationException("Search index metadata has a missing or invalid map.");
    private static IEnumerable<string> Strings(IReadOnlyDictionary<string, object?> row, string name)
        => row.TryGetValue(name, out var value) && value is IEnumerable items && value is not string
            ? items.Cast<object?>().Select(item => item as string ?? throw new InvalidOperationException("Invalid search index metadata list."))
            : throw new InvalidOperationException("Search index metadata has a missing or invalid list.");
    private static int Count(IReadOnlyDictionary<string, object?> row, string name)
        => row.TryGetValue(name, out var value) && value is long count && count is >= 0 and <= int.MaxValue ? (int)count
            : throw new InvalidOperationException("Search index sample returned an invalid count.");
}
