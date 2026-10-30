using System.Collections;
using System.Runtime.ExceptionServices;
using GraphBridge.EntityFrameworkCore.Schema;
using Neo4j.Driver;

namespace GraphBridge.Tooling.Scaffolding;

/// <summary>Finite client/sample bounds and a server transaction timeout; not a bound on server IO or metadata allocation.</summary>
public sealed class GraphInspectionOptions
{
    public GraphInspectionOptions(int sampleLimit = 32, int maxMetadataRows = 256, int maxTokens = 64,
        int maxProperties = 64, int maxLabels = 16, int maxTotalSamples = 2048,
        TimeSpan? transactionTimeout = null, IEnumerable<string>? labels = null, IEnumerable<string>? relationshipTypes = null)
    {
        SampleLimit = Bound(sampleLimit, 1000, nameof(sampleLimit));
        MaxMetadataRows = Bound(maxMetadataRows, 4096, nameof(maxMetadataRows));
        MaxTokens = Bound(maxTokens, 512, nameof(maxTokens));
        MaxProperties = Bound(maxProperties, 256, nameof(maxProperties));
        MaxLabels = Bound(maxLabels, 128, nameof(maxLabels));
        MaxTotalSamples = Bound(maxTotalSamples, 10000, nameof(maxTotalSamples));
        TransactionTimeout = transactionTimeout ?? TimeSpan.FromSeconds(30);
        if (TransactionTimeout <= TimeSpan.Zero || TransactionTimeout > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(transactionTimeout));
        Labels = Selection(labels);
        RelationshipTypes = Selection(relationshipTypes);
    }
    public int SampleLimit { get; }
    public int MaxMetadataRows { get; }
    public int MaxTokens { get; }
    public int MaxProperties { get; }
    public int MaxLabels { get; }
    public int MaxTotalSamples { get; }
    public TimeSpan TransactionTimeout { get; }
    /// <summary>Null means all discovered labels; an empty list means no node sampling.</summary>
    public IReadOnlyList<string>? Labels { get; }
    public IReadOnlyList<string>? RelationshipTypes { get; }
    private static int Bound(int value, int maximum, string name)
        => value >= 1 && value <= maximum ? value : throw new ArgumentOutOfRangeException(name);
    private static IReadOnlyList<string>? Selection(IEnumerable<string>? values)
        => values is null ? null : Array.AsReadOnly(values.Select(GraphDatabaseInspector.Identifier)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
}

/// <summary>Explicit read-only inspection. Borrows the driver, owns one selected-database transaction and always rolls it back.</summary>
public static class GraphDatabaseInspector
{
    private const string Components = "CALL dbms.components() YIELD name, versions, edition RETURN name, versions, edition LIMIT $limit";
    private const string Constraints = "SHOW CONSTRAINTS YIELD name, type, entityType, labelsOrTypes, properties, propertyType RETURN name, type, entityType, labelsOrTypes, properties, propertyType LIMIT $limit";
    private const string Indexes = "SHOW INDEXES YIELD name, state, type, entityType, labelsOrTypes, properties, owningConstraint RETURN name, state, type, entityType, labelsOrTypes, properties, owningConstraint LIMIT $limit";
    private const string Labels = "CALL db.labels() YIELD label RETURN label LIMIT $limit";
    private const string Types = "CALL db.relationshipTypes() YIELD relationshipType RETURN relationshipType LIMIT $limit";
    // With a selection, only the selected tokens are read, so unrelated labels don't count against MaxTokens. On Neo4j 2026
    // db.labels() lists every label the database ever had, in use or not.
    private const string SelectedLabels = "CALL db.labels() YIELD label WHERE label IN $selection RETURN label LIMIT $limit";
    private const string SelectedTypes = "CALL db.relationshipTypes() YIELD relationshipType WHERE relationshipType IN $selection RETURN relationshipType LIMIT $limit";
    private const string TokensExceeded = "The database has more labels or relationship types than maxTokens allows; select the labels and relationship types to inspect, or raise maxTokens (up to 512).";
    private const string GraphType = "CYPHER 25 SHOW CURRENT GRAPH TYPE YIELD specification RETURN substring(specification, 0, $textLimit) AS specification, size(specification) AS textLength";

    public static async Task<GraphScaffoldSnapshot> InspectAsync(IDriver driver, string databaseName,
        GraphInspectionOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(driver);
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);
        options ??= new();
        cancellationToken.ThrowIfCancellationRequested();
        IAsyncSession? session = null;
        IAsyncTransaction? transaction = null;
        var complete = false;
        try
        {
            session = driver.AsyncSession(builder => builder.WithDatabase(databaseName)
                .WithDefaultAccessMode(AccessMode.Read).WithFetchSize(64));
            transaction = await session.BeginTransactionAsync(builder => builder.WithTimeout(options.TransactionTimeout)
                .WithMetadata(new Dictionary<string, object> { ["graphbridgeOperation"] = "database-first-inspection" })).ConfigureAwait(false);
            var budget = new ObservationBudget();
            async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> Read(string query, int maximum,
                bool sample = false, IReadOnlyList<string>? selection = null, string? exceeded = null)
                => await ReadAsync(transaction, query, maximum, options, sample, budget, cancellationToken, selection, exceeded).ConfigureAwait(false);

            var components = await Read(Components, options.MaxMetadataRows).ConfigureAwait(false);
            var kernels = components.Where(row => Text(row, "name") == "Neo4j Kernel").ToArray();
            if (kernels.Length != 1) throw Invalid("Expected exactly one Neo4j Kernel component.");
            var versions = Strings(kernels[0], "versions", 8);
            if (versions.Count != 1) throw Invalid("Expected one kernel version.");
            var cyphers = components.Where(row => Text(row, "name") == "Cypher").ToArray();
            if (cyphers.Length > 1) throw Invalid("Ambiguous Cypher components.");
            var capabilities = GraphSchemaCapabilities.Detect(versions[0], Text(kernels[0], "edition"),
                cyphers.Length == 0 ? [] : Strings(cyphers[0], "versions", 8));
            // valueType() requires 5.13+. The scaffold profile is deliberately narrower than schema inspection.
            var versionParts = versions[0].Split('.');
            if (capabilities.NodeUniqueness != GraphSchemaCapabilitySupport.Supported
                || versionParts[0] == "5" && int.Parse(versionParts[1], System.Globalization.CultureInfo.InvariantCulture) < 13)
                throw Invalid("Database-first inspection requires a recognized Neo4j 5.13+ or 2025/2026 server profile.");
            var constraints = (await Read(Constraints, options.MaxMetadataRows).ConfigureAwait(false))
                .Select(row => { ValidateSchemaLists(row, options); return GraphSchemaObservationParser.ParseConstraint(row); }).ToArray();
            var indexes = (await Read(Indexes, options.MaxMetadataRows).ConfigureAwait(false))
                .Select(row => { ValidateSchemaLists(row, options); return GraphSchemaObservationParser.ParseIndex(row); }).ToArray();
            string? graphType = null;
            if (capabilities.CanShowCurrentGraphType)
            {
                var graphTypes = await Read(GraphType, 1).ConfigureAwait(false);
                if (graphTypes.Count != 1 || Count(graphTypes[0], "textLength") > 16384)
                    throw Invalid("Current graph type observation exceeds its limit or is missing.");
                graphType = Text(graphTypes[0], "specification", 16384, allowEmpty: true);
            }
            IEnumerable<string> SchemaTokens(string entityType) => constraints.Where(value => value.EntityType == entityType)
                .SelectMany(value => value.LabelsOrTypes ?? []).Concat(indexes.Where(value => value.EntityType == entityType)
                    .SelectMany(value => value.LabelsOrTypes ?? []));
            var labels = SelectInventory(await Read(options.Labels is null ? Labels : SelectedLabels, options.MaxTokens,
                selection: options.Labels, exceeded: TokensExceeded).ConfigureAwait(false), "label", options.Labels, SchemaTokens("NODE"), options.MaxTokens);
            var types = SelectInventory(await Read(options.RelationshipTypes is null ? Types : SelectedTypes, options.MaxTokens,
                selection: options.RelationshipTypes, exceeded: TokensExceeded).ConfigureAwait(false), "relationshipType", options.RelationshipTypes,
                SchemaTokens("RELATIONSHIP"), options.MaxTokens);
            var nodes = new List<GraphScaffoldNodeSample>();
            var relationships = new List<GraphScaffoldRelationshipSample>();
            var nodeTokens = new HashSet<string>(StringComparer.Ordinal);
            var inspectedCount = 0;
            foreach (var label in labels)
            {
                var query = "MATCH (n:`" + Identifier(label) + "`) WITH n LIMIT $limit "
                    + "RETURN elementId(n) AS nodeToken, labels(n)[..$labelLimit] AS labels, size(labels(n)) AS labelCount, "
                    + "[k IN keys(n)[..$propertyLimit] | {name: k, type: valueType(n[k])}] AS properties, size(keys(n)) AS propertyCount";
                foreach (var row in await Read(query, options.SampleLimit, sample: true).ConfigureAwait(false))
                {
                    CheckTotal(inspectedCount++, options);
                    var sample = new GraphScaffoldNodeSample(ReadLabels(row, "labels", "labelCount", options), Properties(row, options));
                    // A multi-label node may be sampled repeatedly. Retain its first observation only; IDs never leave this method.
                    if (nodeTokens.Add(Text(row, "nodeToken"))) nodes.Add(sample);
                }
            }
            foreach (var type in types)
            {
                var query = "MATCH (source)-[r:`" + Identifier(type) + "`]->(target) WITH source, r, target LIMIT $limit "
                    + "RETURN labels(source)[..$labelLimit] AS sourceLabels, size(labels(source)) AS sourceLabelCount, "
                    + "labels(target)[..$labelLimit] AS targetLabels, size(labels(target)) AS targetLabelCount, "
                    + "elementId(r) AS relationshipToken, elementId(source) AS sourceToken, elementId(target) AS targetToken, "
                    + "[k IN keys(r)[..$propertyLimit] | {name: k, type: valueType(r[k])}] AS properties, size(keys(r)) AS propertyCount";
                var observed = await Read(query, options.SampleLimit, sample: true).ConfigureAwait(false);
                foreach (var _ in observed) CheckTotal(inspectedCount++, options);
                var samples = observed.DistinctBy(row => Text(row, "relationshipToken"), StringComparer.Ordinal).ToArray();
                // Transient identifiers detect only observed parallel pairs. They never enter the snapshot or EF keys.
                var pairs = samples.Select(row => (Text(row, "sourceToken"), Text(row, "targetToken"))).ToArray();
                var parallel = pairs.GroupBy(pair => pair).Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet();
                for (var i = 0; i < samples.Length; i++)
                {
                    relationships.Add(new(type, ReadLabels(samples[i], "sourceLabels", "sourceLabelCount", options),
                        ReadLabels(samples[i], "targetLabels", "targetLabelCount", options), Properties(samples[i], options), parallel.Contains(pairs[i])));
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            var result = new GraphScaffoldSnapshot(capabilities, new(constraints, indexes, graphType), labels, types, nodes, relationships, options.SampleLimit);
            complete = true;
            return result;
        }
        finally
        {
            Exception? cleanup = null;
            if (transaction is not null)
            {
                try { await transaction.RollbackAsync().ConfigureAwait(false); } catch (Exception error) { cleanup = error; }
                try { await transaction.DisposeAsync().ConfigureAwait(false); } catch (Exception error) { cleanup ??= error; }
            }
            if (session is not null)
            {
                try { await session.DisposeAsync().ConfigureAwait(false); } catch (Exception error) { cleanup ??= error; }
            }
            if (complete && cleanup is not null) ExceptionDispatchInfo.Capture(cleanup).Throw();
        }
    }

    private static async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ReadAsync(IAsyncTransaction transaction,
        string query, int maximum, GraphInspectionOptions options, bool sample, ObservationBudget budget, CancellationToken cancellation,
        IReadOnlyList<string>? selection = null, string? exceeded = null)
    {
        cancellation.ThrowIfCancellationRequested();
        var parameters = new Dictionary<string, object>
        {
            ["limit"] = sample ? (long)maximum : (long)maximum + 1,
            ["labelLimit"] = (long)options.MaxLabels, ["propertyLimit"] = (long)options.MaxProperties, ["textLimit"] = 16384L
        };
        if (selection is not null) parameters["selection"] = selection.ToArray();
        var cursor = await transaction.RunAsync(query, parameters).ConfigureAwait(false);
        cancellation.ThrowIfCancellationRequested();
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        // An error leaves the result unconsumed; the owning transaction is rolled back/disposed in the outer finally.
        // Do not drain an oversized/malformed response merely to discard it.
        while (await cursor.FetchAsync().ConfigureAwait(false))
        {
            cancellation.ThrowIfCancellationRequested();
            if (rows.Count >= maximum) throw Invalid(exceeded ?? "Inspection row limit exceeded; no partial snapshot is returned.");
            var values = cursor.Current.Values;
            budget.Observe(values);
            rows.Add(values.ToDictionary(pair => pair.Key, pair => (object?)pair.Value, StringComparer.Ordinal));
        }
        cancellation.ThrowIfCancellationRequested();
        await cursor.ConsumeAsync().ConfigureAwait(false);
        cancellation.ThrowIfCancellationRequested();
        return rows;
    }

    private static IReadOnlyList<string> SelectInventory(IReadOnlyList<IReadOnlyDictionary<string, object?>> rows,
        string field, IReadOnlyList<string>? selection, IEnumerable<string> schemaTokens, int maximum)
    {
        var inventory = rows.Select(row => Text(row, field)).ToHashSet(StringComparer.Ordinal);
        // Schema tokens count too: a constraint or index keeps a label that has no nodes. With a selection, only the selected ones.
        inventory.UnionWith(selection is null ? schemaTokens : schemaTokens.Where(selection.Contains));
        if (inventory.Count > maximum) throw Invalid(TokensExceeded);
        if (selection is not null && selection.Any(token => !inventory.Contains(token)))
            throw Invalid("Selected label or relationship type was not present in the observed inventory.");
        return (selection ?? inventory.ToArray()).Order(StringComparer.Ordinal).ToArray();
    }
    internal static string Identifier(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 128 || !(char.IsAsciiLetter(value[0]) || value[0] == '_')
            || value.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '_')))
            throw new ArgumentException("Scaffolding supports only validated ASCII graph identifiers of at most 128 characters.");
        return value;
    }
    private static void CheckTotal(int count, GraphInspectionOptions options)
    {
        if (count >= options.MaxTotalSamples) throw Invalid("Total sample budget exceeded; narrow the selected labels/types or sample limit.");
    }
    private static IReadOnlyList<string> ReadLabels(IReadOnlyDictionary<string, object?> row, string field, string count, GraphInspectionOptions options)
    {
        var length = Count(row, count);
        var values = Strings(row, field, options.MaxLabels);
        if (length != values.Count) throw Invalid("Label list exceeded its bound or is malformed.");
        return values;
    }
    private static IReadOnlyDictionary<string, GraphScaffoldValueKind> Properties(IReadOnlyDictionary<string, object?> row, GraphInspectionOptions options)
    {
        var count = Count(row, "propertyCount");
        if (count > options.MaxProperties) throw Invalid("Property count exceeds the per-element limit.");
        var properties = new Dictionary<string, GraphScaffoldValueKind>(StringComparer.Ordinal);
        foreach (var value in List(row, "properties"))
        {
            if (properties.Count >= options.MaxProperties || value is not IReadOnlyDictionary<string, object> map)
                throw Invalid("Malformed or oversized property-type observation.");
            var fields = map.ToDictionary(pair => pair.Key, pair => (object?)pair.Value, StringComparer.Ordinal);
            if (!properties.TryAdd(Text(fields, "name"), Kind(Text(fields, "type")))) throw Invalid("Duplicate property observation.");
        }
        if (properties.Count != count) throw Invalid("Property count differs from the observed list.");
        return properties;
    }
    private static GraphScaffoldValueKind Kind(string type) => type switch
    {
        "STRING NOT NULL" => GraphScaffoldValueKind.String,
        "BOOLEAN NOT NULL" => GraphScaffoldValueKind.Boolean,
        "INTEGER NOT NULL" => GraphScaffoldValueKind.Integer,
        "FLOAT NOT NULL" => GraphScaffoldValueKind.Float,
        "LIST<STRING NOT NULL> NOT NULL" => GraphScaffoldValueKind.StringList,
        "LIST<BOOLEAN NOT NULL> NOT NULL" => GraphScaffoldValueKind.BooleanList,
        "LIST<INTEGER NOT NULL> NOT NULL" => GraphScaffoldValueKind.IntegerList,
        "LIST<FLOAT NOT NULL> NOT NULL" => GraphScaffoldValueKind.FloatList,
        "LIST<NOTHING> NOT NULL" => GraphScaffoldValueKind.EmptyList,
        "DATE NOT NULL" or "LOCAL TIME NOT NULL" or "ZONED TIME NOT NULL" or "LOCAL DATETIME NOT NULL"
            or "ZONED DATETIME NOT NULL" or "DURATION NOT NULL" => GraphScaffoldValueKind.Temporal,
        "POINT NOT NULL" => GraphScaffoldValueKind.Spatial,
        _ => GraphScaffoldValueKind.Unsupported
    };
    private static void ValidateSchemaLists(IReadOnlyDictionary<string, object?> row, GraphInspectionOptions options)
    {
        foreach (var field in new[] { "name", "type", "entityType" }) _ = Text(row, field);
        foreach (var field in new[] { "labelsOrTypes", "properties" })
            if (row.TryGetValue(field, out var value) && value is not null) _ = Strings(row, field, options.MaxProperties);
        foreach (var field in new[] { "propertyType", "owningConstraint", "state" })
            if (row.TryGetValue(field, out var value) && value is not null) _ = Text(row, field);
    }
    private static long Count(IReadOnlyDictionary<string, object?> row, string field)
        => row.TryGetValue(field, out var value) && value is long count && count >= 0 ? count : throw Invalid("Malformed count: " + field);
    private static string Text(IReadOnlyDictionary<string, object?> row, string field, int max = 512, bool allowEmpty = false)
        => row.TryGetValue(field, out var value) && value is string text && text.Length <= max && (allowEmpty || !string.IsNullOrWhiteSpace(text))
            ? text : throw Invalid("Malformed or oversized text: " + field);
    private static IEnumerable List(IReadOnlyDictionary<string, object?> row, string field)
        => row.TryGetValue(field, out var value) && value is IEnumerable list && value is not string ? list : throw Invalid("Malformed list: " + field);
    private static IReadOnlyList<string> Strings(IReadOnlyDictionary<string, object?> row, string field, int max)
    {
        var result = new List<string>();
        foreach (var value in List(row, field))
        {
            if (result.Count >= max || value is not string text || string.IsNullOrWhiteSpace(text) || text.Length > 512)
                throw Invalid("Malformed or oversized list: " + field);
            result.Add(text);
        }
        return result;
    }
    private static InvalidOperationException Invalid(string message) => new("GraphBridge database inspection: " + message);

    // Bound retained evidence across metadata and all samples, independently of caller-selected row limits.
    // This does not prevent the driver/server from allocating an individual incoming record.
    private sealed class ObservationBudget
    {
        private int _fields;
        private int _characters;
        public void Observe(object? value, int depth = 0)
        {
            if (++_fields > 65536 || depth > 8) throw Invalid("Combined observation field/depth budget exceeded.");
            switch (value)
            {
                case string text:
                    _characters = checked(_characters + text.Length);
                    if (_characters > 1048576) throw Invalid("Combined observation text budget exceeded.");
                    break;
                case IReadOnlyDictionary<string, object> map:
                    foreach (var pair in map) { Observe(pair.Key, depth + 1); Observe(pair.Value, depth + 1); }
                    break;
                case IEnumerable list:
                    foreach (var item in list) Observe(item, depth + 1);
                    break;
                case null or long or bool: break;
                default: throw Invalid("Unexpected value in type-only inspection evidence.");
            }
        }
    }
}
