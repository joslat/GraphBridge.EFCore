namespace GraphBridge.EntityFrameworkCore.Cypher;

internal sealed record CypherMutationCommand(string Text, IDictionary<string, object> Parameters);

/// <summary>Mutation rendering uses validated names and native values without EF metadata dependencies.</summary>
internal static class CypherMutationRenderer
{
    public static CypherMutationCommand Create(string label, IReadOnlyDictionary<string, object?> properties)
    {
        ValidateNames(properties.Keys);
        return new($"CREATE (n:{Labels(label)})\nSET n = $properties\nRETURN count(n) AS count",
            new Dictionary<string, object> { ["properties"] = properties });
    }

    /// <summary>
    /// A transient property that a lock writes and removes in one statement. Writing it takes the element's write lock without
    /// touching a key: on Neo4j 2026.09, writing a unique key to itself before deleting the element keeps that key taken for the
    /// rest of the transaction, so the element couldn't be deleted and added again in one transaction.
    /// </summary>
    internal const string LockProperty = "__graphbridge_lock";

    /// <summary>Takes the write lock of each element: sets and removes <see cref="LockProperty"/>, leaving no trace.</summary>
    internal static string LockWrite(params string[] elements)
    {
        var property = Identifier(LockProperty);
        return "SET " + string.Join(", ", elements.Select(e => $"{e}.{property} = true")) + " REMOVE " + string.Join(", ", elements.Select(e => $"{e}.{property}"));
    }

    public static CypherMutationCommand Lock(string label, IReadOnlyDictionary<string, object?> keys)
    {
        var (match, parameters) = Match(label, keys);
        return new($"{match}\n{LockWrite("n")}\nRETURN count(n) AS count", parameters);
    }

    public static CypherMutationCommand Update(string label, IReadOnlyDictionary<string, object?> keys,
        IReadOnlyDictionary<string, object?> originals, IReadOnlyDictionary<string, object?> changes)
    {
        ValidateNames(changes.Keys);
        var (match, parameters) = Match(label, keys);
        var condition = Concurrency(originals, parameters);
        parameters["changes"] = changes;
        return new($"{match}{condition}\nSET n += $changes\nRETURN count(n) AS count", parameters);
    }

    public static CypherMutationCommand Delete(string label, IReadOnlyDictionary<string, object?> keys,
        IReadOnlyDictionary<string, object?> originals)
    {
        var (match, parameters) = Match(label, keys);
        return new($"{match}{Concurrency(originals, parameters)}\nDELETE n\nRETURN count(n) AS count", parameters);
    }

    private static (string Match, IDictionary<string, object> Parameters) Match(string label,
        IReadOnlyDictionary<string, object?> keys)
    {
        if (keys.Count == 0 || keys.Values.Any(v => v is null))
            throw new InvalidOperationException("Node mutations require non-null application keys.");
        var parameters = new Dictionary<string, object>();
        var comparisons = keys.Select((pair, index) =>
        {
            parameters["k" + index] = pair.Value!;
            return Identifier(pair.Key) + ": $k" + index;
        }).ToArray();
        return ($"MATCH (n:{Labels(label)} {{{string.Join(", ", comparisons)}}})", parameters);
    }

    private static string Concurrency(IReadOnlyDictionary<string, object?> originals, IDictionary<string, object> parameters)
    {
        var conditions = originals.Select((pair, index) =>
        {
            parameters["o" + index] = pair.Value!;
            return $"coalesce(n.{Identifier(pair.Key)} = $o{index}, (n.{Identifier(pair.Key)} IS NULL AND $o{index} IS NULL))";
        }).ToArray();
        return conditions.Length == 0 ? "" : "\nWHERE " + string.Join(" AND ", conditions);
    }

    private static void ValidateNames(IEnumerable<string> names)
    {
        foreach (var name in names)
            Identifier(name);
    }
    // A node type's labels, separated by colons (ADR 0055), each validated and quoted.
    private static string Labels(string labels) => string.Join(':', labels.Split(':').Select(Identifier));

    private static string Identifier(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || !Initial(name[0]) || name.Any(c => !Initial(c) && c is not (>= '0' and <= '9')))
            throw new ArgumentException("Cypher metadata identifiers must match [A-Za-z_][A-Za-z0-9_]*.", nameof(name));
        return "`" + name + "`";
    }
    private static bool Initial(char c) => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or '_';
}
