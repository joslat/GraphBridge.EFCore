using GraphBridge.EntityFrameworkCore.Metadata;

namespace GraphBridge.EntityFrameworkCore.Search;

internal sealed record GraphSearchRequest(GraphSearchKind Kind, GraphSearchEntityKind EntityKind,
    string Index, object Query, int Limit)
{
    public static GraphSearchRequest Vector(string index, IReadOnlyList<double> vector, int limit, GraphSearchEntityKind entity)
    {
        Validate(index, limit);
        ArgumentNullException.ThrowIfNull(vector);
        if (vector.Count is < 1 or > 4096) throw new ArgumentOutOfRangeException(nameof(vector), "Vector dimensions must be between 1 and 4096.");
        var snapshot = new double[vector.Count];
        for (var i = 0; i < snapshot.Length; i++)
        {
            var value = vector[i];
            if (!double.IsFinite(value) || Math.Abs(value) > float.MaxValue)
                throw new ArgumentException("Vector coordinates must be finite and representable in float32 range.", nameof(vector));
            snapshot[i] = value;
        }
        return new(GraphSearchKind.Vector, entity, index, snapshot, limit);
    }
    public static GraphSearchRequest FullText(string index, string query, int limit, GraphSearchEntityKind entity)
    {
        Validate(index, limit);
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        if (query.Length > 16_384) throw new ArgumentOutOfRangeException(nameof(query), "Full-text query length exceeds 16384 characters.");
        return new(GraphSearchKind.FullText, entity, index, query, limit);
    }
    private static void Validate(string index, int limit)
    {
        GraphIdentifier.Validate(index, nameof(index));
        if (limit is < 1 or > 10_000) throw new ArgumentOutOfRangeException(nameof(limit), "Search result limit must be between 1 and 10000.");
    }
    // Confirm the validated identifier against observed index metadata before executing this statement.
    public string Cypher => Kind == GraphSearchKind.FullText
        ? "CYPHER 25 CALL db.index.fulltext." + (EntityKind == GraphSearchEntityKind.Node ? "queryNodes" : "queryRelationships")
            + "($index,$query,{limit:$limit}) YIELD " + (EntityKind == GraphSearchEntityKind.Node ? "node" : "relationship")
            + " AS entity,score RETURN entity,score ORDER BY score DESC LIMIT $limit"
        : "CYPHER 25 MATCH " + (EntityKind == GraphSearchEntityKind.Node ? "(entity)" : "()-[entity]->()")
            + "\nSEARCH entity IN (VECTOR INDEX `" + Index + "` FOR $query LIMIT $limit) SCORE AS score"
            + "\nRETURN entity, score ORDER BY score DESC LIMIT $limit";
    public IReadOnlyDictionary<string, object?> Parameters => Kind == GraphSearchKind.FullText
        ? new Dictionary<string, object?> { ["query"] = Query, ["limit"] = (long)Limit, ["index"] = Index }
        : new Dictionary<string, object?> { ["query"] = Query, ["limit"] = (long)Limit };
}
