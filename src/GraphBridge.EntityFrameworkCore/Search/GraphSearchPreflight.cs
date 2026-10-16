using System.Collections;
using GraphBridge.EntityFrameworkCore.Diagnostics;
using Neo4j.Driver;

namespace GraphBridge.EntityFrameworkCore.Search;

internal static class GraphSearchPreflight
{
    internal const string Components = "CALL dbms.components() YIELD name, versions, edition RETURN name, versions, edition LIMIT 5";
    internal const string Indexes = "SHOW INDEXES YIELD name, state, type, entityType, labelsOrTypes, properties, indexProvider, options WHERE name = $index RETURN name, state, type, entityType, labelsOrTypes, properties, indexProvider, options LIMIT 2";

    internal static async Task<GraphSearchProvenance> InspectAsync(IAsyncTransaction runner, GraphSearchRequest request, CancellationToken token)
    {
        var components = await Read(runner, Components, new Dictionary<string, object>(), 4, token).ConfigureAwait(false);
        var kernels = components.Where(r => Text(r, "name") == "Neo4j Kernel").ToArray();
        var cyphers = components.Where(r => Text(r, "name") == "Cypher").ToArray();
        if (kernels.Length != 1 || cyphers.Length > 1 || Strings(kernels[0], "versions").Count != 1)
            throw new InvalidOperationException("Graph search requires unambiguous server and Cypher capability observations.");
        var version = Strings(kernels[0], "versions")[0];
        var edition = Text(kernels[0], "edition");
        // Older servers may omit dialect advertisement. Absence is unsupported,
        // not an ambiguous observation and never permission to try a fallback query.
        ValidateCapabilities(version, edition, cyphers.Length == 0 ? [] : Strings(cyphers[0], "versions"), request.Kind);
        var indexes = await Read(runner, Indexes, new Dictionary<string, object> { ["index"] = request.Index }, 1, token).ConfigureAwait(false);
        if (indexes.Count != 1) throw new InvalidOperationException("Graph search requires one visible existing index with the requested name.");
        return ParseIndex(indexes[0], request, version, edition);
    }
    internal static void ValidateCapabilities(string version, string edition, IReadOnlyList<string> cyphers, GraphSearchKind kind)
    {
        var parts = version.Split('.');
        if (parts.Length is not (2 or 3) || parts.Any(p => p.Length == 0 || p.Any(c => !char.IsAsciiDigit(c)))
            || parts[0] != "2026" || !int.TryParse(parts[1], out var month) || month is < 1 or > 12
            || kind == GraphSearchKind.FullText && month < 9
            || !cyphers.Contains("25", StringComparer.Ordinal)
            || !(edition.Equals("community", StringComparison.OrdinalIgnoreCase) || edition.Equals("enterprise", StringComparison.OrdinalIgnoreCase)))
            throw new NotSupportedException("Typed graph SEARCH requires a known Neo4j 2026 release with Cypher 25 (full-text requires 2026.09+). Use native Cypher for other verified deployments.");
    }
    internal static GraphSearchProvenance ParseIndex(IReadOnlyDictionary<string, object?> row, GraphSearchRequest request, string version, string edition)
    {
        if (Text(row, "name") != request.Index || Text(row, "state") != "ONLINE"
            || Text(row, "type") != (request.Kind == GraphSearchKind.Vector ? "VECTOR" : "FULLTEXT")
            || Text(row, "entityType") != (request.EntityKind == GraphSearchEntityKind.Node ? "NODE" : "RELATIONSHIP"))
            throw new InvalidOperationException("Graph search index must be ONLINE and match the requested name, search kind and entity kind.");
        var labels = Strings(row, "labelsOrTypes"); var properties = Strings(row, "properties");
        if (labels.Count == 0 || properties.Count == 0)
            throw new InvalidOperationException("Graph search index metadata must declare labels/types and properties.");
        var config = Map(Map(row, "options"), "indexConfig");
        int? dimensions = null; string? similarity = null; string? analyzer = null; var eventual = false;
        if (request.Kind == GraphSearchKind.Vector)
        {
            if (config.TryGetValue("vector.dimensions", out var raw) && raw is not null)
            {
                if (raw is not long size || size is < 1 or > 4096) throw new InvalidOperationException("Invalid observed vector dimensions.");
                dimensions = (int)size;
                if (((double[])request.Query).Length != dimensions) throw new ArgumentException("Query vector dimensions differ from the index.");
            }
            similarity = Text(config, "vector.similarity_function");
            var cosine = similarity.Equals("cosine", StringComparison.OrdinalIgnoreCase);
            if (!cosine && !similarity.Equals("euclidean", StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException("Unknown vector similarity function.");
            if (cosine && ((double[])request.Query).All(v => (float)v == 0f))
                throw new ArgumentException("Cosine search requires a nonzero vector in float32 representation.");
        }
        else
        {
            analyzer = Text(config, "fulltext.analyzer");
            if (!config.TryGetValue("fulltext.eventually_consistent", out var value) || value is not bool flag)
                throw new InvalidOperationException("Full-text consistency metadata is missing or invalid.");
            eventual = flag;
        }
        return new(request.Kind, request.EntityKind, request.Index, version, edition, Text(row, "indexProvider"),
            labels, properties, dimensions, similarity, analyzer, eventual, request.Limit);
    }
    private static string Text(IReadOnlyDictionary<string, object?> row, string name)
        => row.TryGetValue(name, out var value) && value is string text && !string.IsNullOrWhiteSpace(text) ? text
            : throw new InvalidOperationException("Graph search metadata contains a missing or invalid string field: " + name);
    private static IReadOnlyDictionary<string, object?> Map(IReadOnlyDictionary<string, object?> row, string name)
        => row.TryGetValue(name, out var value) && value is IReadOnlyDictionary<string, object?> map ? map
            : throw new InvalidOperationException("Graph search metadata contains a missing or invalid map: " + name);
    private static IReadOnlyList<string> Strings(IReadOnlyDictionary<string, object?> row, string name)
    {
        if (!row.TryGetValue(name, out var value) || value is not IEnumerable items || value is string)
            throw new InvalidOperationException("Graph search metadata contains a missing or invalid list: " + name);
        var result = new List<string>();
        foreach (var item in items)
            result.Add(item is string text && !string.IsNullOrWhiteSpace(text) ? text : throw new InvalidOperationException("Invalid graph search metadata list item."));
        return result.AsReadOnly();
    }
    private static async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> Read(IAsyncTransaction runner,
        string text, IDictionary<string, object> parameters, int maxRows, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); IResultCursor? cursor = null;
        try
        {
            cursor = await GraphBridgeDiagnostics.RunAsync(runner, text, parameters, kind: GraphCommandKind.SchemaPreflight).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var rows = new List<IReadOnlyDictionary<string, object?>>();
            while (await cursor.FetchAsync().ConfigureAwait(false))
            {
                token.ThrowIfCancellationRequested();
                if (rows.Count == maxRows) throw new InvalidOperationException("Graph search metadata exceeds its observation bound.");
                rows.Add(cursor.Current.Values.ToDictionary(p => p.Key, p => (object?)p.Value, StringComparer.Ordinal));
            }
            token.ThrowIfCancellationRequested();
            await cursor.ConsumeAsync().ConfigureAwait(false); cursor = null;
            token.ThrowIfCancellationRequested(); return rows;
        }
        catch (Exception error)
        {
            GraphBridgeDiagnostics.FailCursor(cursor, error);
            try { if (cursor is { IsOpen: true }) await cursor.ConsumeAsync().ConfigureAwait(false); }
            catch { /* Preserve the primary failure; outer native operation owns transaction cleanup. */ }
            throw;
        }
        finally { GraphBridgeDiagnostics.CompleteCursor(cursor); }
    }
}
