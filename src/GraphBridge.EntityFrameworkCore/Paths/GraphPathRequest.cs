using System.Globalization;
using GraphBridge.EntityFrameworkCore.Metadata;

namespace GraphBridge.EntityFrameworkCore.Paths;

internal sealed class GraphPathRequest
{
    public GraphPathRequest(GraphPathPattern pattern, IReadOnlyDictionary<string, object?> keys,
        GraphTraversalDirection direction, int maxDepth, int resultLimit, int minDepth)
    {
        ArgumentNullException.ThrowIfNull(pattern); ArgumentNullException.ThrowIfNull(keys);
        if (!Enum.IsDefined(direction)) throw new ArgumentOutOfRangeException(nameof(direction));
        if (maxDepth is < 0 or > 64) throw new ArgumentOutOfRangeException(nameof(maxDepth), "Maximum path depth must be between 0 and 64.");
        if (minDepth < 0 || minDepth > maxDepth) throw new ArgumentOutOfRangeException(nameof(minDepth));
        if (resultLimit is < 1 or > 10_000) throw new ArgumentOutOfRangeException(nameof(resultLimit), "Path result limit must be between 1 and 10000.");
        if (keys.Count is < 1 or > 16) throw new ArgumentException("A path anchor requires 1 to 16 non-null native scalar key values.", nameof(keys));
        var parameters = new Dictionary<string, object?>(); var slots = new List<string>();
        foreach (var (name, value) in keys.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            GraphIdentifier.Validate(name, nameof(keys));
            var parameter = "key" + slots.Count.ToString(CultureInfo.InvariantCulture);
            parameters.Add(parameter, value switch
            {
                string or bool or long => value,
                int number => (long)number,
                double number when double.IsFinite(number) => number,
                _ => throw new ArgumentException("Path anchor values must be non-null string, bool, int, long or finite double in native store representation.", nameof(keys))
            });
            slots.Add("`" + name + "`: $" + parameter);
        }
        parameters.Add("probe", (long)resultLimit + 1);
        Pattern = pattern; Direction = direction; MaxDepth = maxDepth; MinDepth = minDepth; ResultLimit = resultLimit;
        Parameters = parameters;
        var edge = "[:" + string.Join("|", pattern.RelationshipTypes.Select(t => "`" + t + "`"))
            + "*" + minDepth.ToString(CultureInfo.InvariantCulture) + ".." + maxDepth.ToString(CultureInfo.InvariantCulture) + "]";
        var traversal = direction switch
        {
            GraphTraversalDirection.Outgoing => "-" + edge + "->",
            GraphTraversalDirection.Incoming => "<-" + edge + "-",
            _ => "-" + edge + "-"
        };
        // The anchor diagnostic and traversal share one statement. Duplicate starts cannot be silently chosen.
        Cypher = "CYPHER 5 MATCH (start:`" + pattern.StartLabel + "` {" + string.Join(", ", slots) + "})"
            + "\nWITH start LIMIT 2\nWITH collect(start) AS candidates"
            + "\nCALL {\n  WITH candidates\n  UNWIND CASE WHEN size(candidates) = 1 THEN candidates ELSE [] END AS start"
            + "\n  MATCH path=(start)" + traversal + "()"
            + "\n  WITH path LIMIT $probe\n  RETURN collect(path) AS paths\n}"
            + "\nRETURN size(candidates) AS starts, paths";
    }
    public GraphPathPattern Pattern { get; }
    public GraphTraversalDirection Direction { get; }
    public int MaxDepth { get; }
    public int MinDepth { get; }
    public int ResultLimit { get; }
    public string Cypher { get; }
    public IReadOnlyDictionary<string, object?> Parameters { get; }
}
