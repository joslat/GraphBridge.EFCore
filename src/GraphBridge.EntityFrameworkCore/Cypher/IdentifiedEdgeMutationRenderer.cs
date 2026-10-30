using System.Text;

namespace GraphBridge.EntityFrameworkCore.Cypher;

/// <summary>
/// Native identified relationships. The executor verifies endpoint and relationship-key constraints,
/// locks both endpoints, and requires each command to report exactly one affected element.
/// Syntax: https://neo4j.com/docs/cypher-manual/current/clauses/create/ and
/// https://neo4j.com/docs/cypher-manual/current/schema/constraints/create-constraints/.
/// </summary>
internal static class IdentifiedEdgeMutationRenderer
{
    public static CypherMutationCommand Create(string type, string keyProperty, object key,
        string sourceLabel, IReadOnlyDictionary<string, object?> sourceKeys,
        string targetLabel, IReadOnlyDictionary<string, object?> targetKeys,
        IReadOnlyDictionary<string, object?> properties)
    {
        var (text, parameters) = Endpoints(sourceLabel, sourceKeys, targetLabel, targetKeys);
        ValidateKey(keyProperty, key);
        ValidateChanges(properties, keyProperty);
        var stored = properties.ToDictionary(p => p.Key, p => p.Value);
        stored.Add(keyProperty, key);
        parameters["properties"] = stored;
        text.Append("\nCREATE (s)-[r:").Append(Identifier(type)).Append("]->(t)")
            .Append("\nSET r = $properties\nRETURN count(r) AS count");
        return new(text.ToString(), parameters);
    }

    public static CypherMutationCommand Update(string type, string keyProperty, object key,
        string sourceLabel, IReadOnlyDictionary<string, object?> sourceKeys,
        string targetLabel, IReadOnlyDictionary<string, object?> targetKeys,
        IReadOnlyDictionary<string, object?> originals, IReadOnlyDictionary<string, object?> changes)
    {
        var (text, parameters) = Existing(type, keyProperty, key, sourceLabel, sourceKeys, targetLabel, targetKeys);
        ValidateChanges(changes, keyProperty);
        parameters["changes"] = changes;
        SelectEligible(text, originals, parameters);
        text.Append("\nFOREACH (r IN eligible | SET r += $changes)\nRETURN size(eligible) AS count");
        return new(text.ToString(), parameters);
    }

    public static CypherMutationCommand Delete(string type, string keyProperty, object key,
        string sourceLabel, IReadOnlyDictionary<string, object?> sourceKeys,
        string targetLabel, IReadOnlyDictionary<string, object?> targetKeys,
        IReadOnlyDictionary<string, object?> originals)
    {
        var (text, parameters) = Existing(type, keyProperty, key, sourceLabel, sourceKeys, targetLabel, targetKeys);
        SelectEligible(text, originals, parameters);
        text.Append("\nFOREACH (r IN eligible | DELETE r)\nRETURN size(eligible) AS count");
        return new(text.ToString(), parameters);
    }

    private static (StringBuilder Text, IDictionary<string, object> Parameters) Existing(string type, string keyProperty,
        object key, string sourceLabel, IReadOnlyDictionary<string, object?> sourceKeys,
        string targetLabel, IReadOnlyDictionary<string, object?> targetKeys)
    {
        ValidateKey(keyProperty, key);
        var (text, parameters) = Endpoints(sourceLabel, sourceKeys, targetLabel, targetKeys);
        parameters["id"] = key;
        text.Append("\nMATCH (a)-[r:").Append(Identifier(type)).Append(" {")
            .Append(Identifier(keyProperty)).Append(": $id}]->(b)")
            // Count globally by type/key before endpoint or token conditions can hide a duplicate.
            .Append("\nWITH s, t, a, b, r LIMIT 2")
            .Append("\nWITH s, t, collect({edge: r, source: a, target: b}) AS candidates")
            .Append("\nWITH CASE WHEN size(candidates) = 1 AND candidates[0].source = s AND candidates[0].target = t")
            .Append(" THEN [candidates[0].edge] ELSE [] END AS edges")
            // Acquire the relationship lock before reading original tokens, even with no caller token. A transient property takes
            // it: on Neo4j 2026.09 a key written to itself stays taken until the transaction ends, so the same key couldn't be added
            // again after the delete.
            .Append("\nFOREACH (r IN edges | ").Append(CypherMutationRenderer.LockWrite("r")).Append(')');
        return (text, parameters);
    }

    private static void SelectEligible(StringBuilder text, IReadOnlyDictionary<string, object?> originals,
        IDictionary<string, object> parameters)
    {
        text.Append("\nWITH CASE WHEN size(edges) = 1");
        foreach (var (pair, index) in originals.Select((pair, index) => (pair, index)))
        {
            var property = "edges[0]." + Identifier(pair.Key);
            parameters["o" + index] = pair.Value!;
            text.Append(" AND coalesce(").Append(property).Append(" = $o").Append(index)
                .Append(", (").Append(property).Append(" IS NULL AND $o").Append(index).Append(" IS NULL))");
        }
        text.Append(" THEN edges ELSE [] END AS eligible");
    }

    private static (StringBuilder Text, IDictionary<string, object> Parameters) Endpoints(string sourceLabel,
        IReadOnlyDictionary<string, object?> sourceKeys, string targetLabel, IReadOnlyDictionary<string, object?> targetKeys)
    {
        var parameters = new Dictionary<string, object>();
        var text = new StringBuilder(Endpoint("s", sourceLabel, sourceKeys, parameters))
            .Append('\n').Append(Endpoint("t", targetLabel, targetKeys, parameters));
        return (text, parameters);
    }

    private static string Endpoint(string alias, string label, IReadOnlyDictionary<string, object?> keys,
        IDictionary<string, object> parameters)
    {
        if (keys.Count == 0 || keys.Values.Any(v => v is null))
            throw new InvalidOperationException("Identified-edge mutations require nonempty, non-null endpoint keys.");
        var properties = keys.Select((pair, index) =>
        {
            var name = alias + index;
            parameters[name] = pair.Value!;
            return Identifier(pair.Key) + ": $" + name;
        });
        return $"MATCH ({alias}:{Labels(label)} {{{string.Join(", ", properties)}}})";
    }

    private static void ValidateChanges(IReadOnlyDictionary<string, object?> properties, string keyProperty)
    {
        foreach (var name in properties.Keys) Identifier(name);
        if (properties.ContainsKey(keyProperty))
            throw new InvalidOperationException("An identified-edge application key cannot be written as a mutable payload property.");
    }

    private static void ValidateKey(string keyProperty, object key)
    {
        Identifier(keyProperty);
        if (key is null || key is string text && string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("An identified edge requires a non-null, nonempty application key.");
    }

    // A node type's labels, separated by colons (ADR 0055), each validated and quoted.
    private static string Labels(string labels) => string.Join(':', labels.Split(':').Select(Identifier));

    private static string Identifier(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || !(char.IsAsciiLetter(name[0]) || name[0] == '_')
            || name.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '_')))
            throw new ArgumentException("Cypher metadata identifiers must match [A-Za-z_][A-Za-z0-9_]*.", nameof(name));
        return "`" + name + "`";
    }
}
