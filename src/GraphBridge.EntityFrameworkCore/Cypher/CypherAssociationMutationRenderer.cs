using System.Text;

namespace GraphBridge.EntityFrameworkCore.Cypher;

/// <summary>
/// Renders a directed endpoint-pair association. The executor must first lock both unique endpoints,
/// require each lock command's count to be one, and roll back the transaction unless this count is one.
/// Syntax sources: Neo4j Cypher Manual /5/clauses/foreach/ and /5/clauses/set/;
/// /current/clauses/merge/, /current/clauses/with/, /current/functions/aggregating/,
/// /current/expressions/list-expressions/ and /current/expressions/conditional-expressions/.
/// Documentation review is not live Neo4j acceptance evidence.
/// </summary>
internal static class CypherAssociationMutationRenderer
{
    public static CypherMutationCommand Merge(string relationshipType, string sourceLabel,
        IReadOnlyDictionary<string, object?> sourceKeys, string targetLabel,
        IReadOnlyDictionary<string, object?> targetKeys, IReadOnlyDictionary<string, object?> properties)
    {
        var (text, parameters) = Endpoints(sourceLabel, sourceKeys, targetLabel, targetKeys);
        ValidateNames(properties.Keys);
        parameters["properties"] = properties;
        text.Append("\nMERGE (s)-[r:").Append(Identifier(relationshipType)).Append("]->(t)")
            .Append("\nON CREATE SET r = $properties");
        CollectCandidates(text);
        SelectEligible(text, properties, "p", parameters);
        text.Append("\nRETURN size(eligible) AS count");
        return new(text.ToString(), parameters);
    }

    public static CypherMutationCommand Update(string relationshipType, string sourceLabel,
        IReadOnlyDictionary<string, object?> sourceKeys, string targetLabel,
        IReadOnlyDictionary<string, object?> targetKeys, IReadOnlyDictionary<string, object?> originals,
        IReadOnlyDictionary<string, object?> changes)
    {
        var (text, parameters) = Existing(relationshipType, sourceLabel, sourceKeys, targetLabel, targetKeys);
        ValidateNames(changes.Keys);
        parameters["changes"] = changes;
        LockSingletonBeforeTokens(text, originals);
        SelectEligible(text, originals, "o", parameters);
        text.Append("\nFOREACH (r IN eligible | SET r += $changes)")
            .Append("\nRETURN size(eligible) AS count");
        return new(text.ToString(), parameters);
    }

    public static CypherMutationCommand Delete(string relationshipType, string sourceLabel,
        IReadOnlyDictionary<string, object?> sourceKeys, string targetLabel,
        IReadOnlyDictionary<string, object?> targetKeys, IReadOnlyDictionary<string, object?> originals)
    {
        var (text, parameters) = Existing(relationshipType, sourceLabel, sourceKeys, targetLabel, targetKeys);
        LockSingletonBeforeTokens(text, originals);
        SelectEligible(text, originals, "o", parameters);
        text.Append("\nFOREACH (r IN eligible | DELETE r)")
            .Append("\nRETURN size(eligible) AS count");
        return new(text.ToString(), parameters);
    }

    private static (StringBuilder Text, IDictionary<string, object> Parameters) Existing(string relationshipType,
        string sourceLabel, IReadOnlyDictionary<string, object?> sourceKeys, string targetLabel,
        IReadOnlyDictionary<string, object?> targetKeys)
    {
        var (text, parameters) = Endpoints(sourceLabel, sourceKeys, targetLabel, targetKeys);
        text.Append("\nMATCH (s)-[r:").Append(Identifier(relationshipType)).Append("]->(t)");
        CollectCandidates(text);
        return (text, parameters);
    }

    private static void CollectCandidates(StringBuilder text)
    {
        // Limit BEFORE collect; retaining two candidates distinguishes one edge from every duplicate case.
        // No payload or token predicate can reduce a duplicate pair to one accepted edge.
        text.Append("\nWITH r LIMIT 2\nWITH collect(r) AS edges");
    }

    private static void SelectEligible(StringBuilder text, IReadOnlyDictionary<string, object?> expected,
        string prefix, IDictionary<string, object> parameters)
    {
        text.Append("\nWITH CASE WHEN size(edges) = 1");
        foreach (var (pair, index) in expected.Select((pair, index) => (pair, index)))
        {
            var property = "edges[0]." + Identifier(pair.Key);
            var parameter = prefix + index;
            parameters[parameter] = pair.Value!;
            text.Append(" AND coalesce(").Append(property).Append(" = $").Append(parameter)
                .Append(", (").Append(property).Append(" IS NULL AND $").Append(parameter).Append(" IS NULL))");
        }
        text.Append(" THEN edges ELSE [] END AS eligible");
    }

    private static void LockSingletonBeforeTokens(StringBuilder text, IReadOnlyDictionary<string, object?> originals)
    {
        if (originals.Count == 0)
            return;
        ValidateNames(originals.Keys);
        var property = Identifier(originals.Keys.First());
        // A dependent SET obtains the relationship write lock before reading its property. This leaves
        // null/missing tokens unchanged and locks no candidate when physical parallel edges exist.
        // Operations Manual /current/database-internals/concurrent-data-access/ documents this locking
        // rule; relationship token race behavior still requires live acceptance against the pinned server.
        text.Append("\nWITH CASE WHEN size(edges) = 1 THEN edges ELSE [] END AS edges")
            .Append("\nFOREACH (r IN edges | SET r.").Append(property).Append(" = r.").Append(property).Append(')');
    }

    private static (StringBuilder Text, IDictionary<string, object> Parameters) Endpoints(string sourceLabel,
        IReadOnlyDictionary<string, object?> sourceKeys, string targetLabel,
        IReadOnlyDictionary<string, object?> targetKeys)
    {
        var parameters = new Dictionary<string, object>();
        var text = new StringBuilder(MatchEndpoint("s", sourceLabel, sourceKeys, parameters))
            .Append('\n').Append(MatchEndpoint("t", targetLabel, targetKeys, parameters));
        return (text, parameters);
    }

    private static string MatchEndpoint(string alias, string label, IReadOnlyDictionary<string, object?> keys,
        IDictionary<string, object> parameters)
    {
        if (keys.Count == 0 || keys.Values.Any(v => v is null))
            throw new InvalidOperationException("Association mutations require non-empty, non-null application keys for each endpoint.");
        var slots = keys.Select((pair, index) =>
        {
            var name = alias + index;
            parameters[name] = pair.Value!;
            return Identifier(pair.Key) + ": $" + name;
        }).ToArray();
        return $"MATCH ({alias}:{Identifier(label)} {{{string.Join(", ", slots)}}})";
    }

    private static void ValidateNames(IEnumerable<string> names)
    {
        foreach (var name in names)
            Identifier(name);
    }

    private static string Identifier(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || !Initial(name[0]) || name.Any(c => !Initial(c) && c is not (>= '0' and <= '9')))
            throw new ArgumentException("Cypher metadata identifiers must match [A-Za-z_][A-Za-z0-9_]*.", nameof(name));
        return "`" + name + "`";
    }
    private static bool Initial(char c) => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or '_';
}
