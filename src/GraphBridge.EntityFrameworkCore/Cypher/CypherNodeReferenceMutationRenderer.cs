using System.Text;

namespace GraphBridge.EntityFrameworkCore.Cypher;

/// <summary>
/// Native principal-to-dependent links owned by the dependent EF node entry. The executor must first
/// lock the dependent and expected principal, require singleton lock/mutation counts, and roll back on failure.
/// Incoming membership is bounded before checking the principal label or identity, so wrong-label/extra parents cannot disappear.
/// </summary>
internal static class CypherNodeReferenceMutationRenderer
{
    public static CypherMutationCommand Create(string relationshipType, string principalLabel,
        IReadOnlyDictionary<string, object?> principalKeys, string dependentLabel, IReadOnlyDictionary<string, object?> dependentKeys)
    {
        var (text, parameters) = Membership(relationshipType, principalLabel, principalKeys, dependentLabel, dependentKeys);
        text.Append("\nWITH p,d,CASE WHEN size(links) = 0 THEN [1] ELSE [] END AS eligible")
            .Append("\nFOREACH (ignored IN eligible | CREATE (p)-[:").Append(Identifier(relationshipType)).Append("]->(d))")
            .Append("\nRETURN size(eligible) AS count");
        return new(text.ToString(), parameters);
    }

    public static CypherMutationCommand Assert(string relationshipType, string principalLabel,
        IReadOnlyDictionary<string, object?> principalKeys, string dependentLabel, IReadOnlyDictionary<string, object?> dependentKeys)
    {
        var (text, parameters) = Membership(relationshipType, principalLabel, principalKeys, dependentLabel, dependentKeys);
        text.Append("\nRETURN CASE WHEN size(links) = 1 AND parents[0] = p THEN 1 ELSE 0 END AS count");
        return new(text.ToString(), parameters);
    }

    public static CypherMutationCommand Delete(string relationshipType, string principalLabel,
        IReadOnlyDictionary<string, object?> principalKeys, string dependentLabel, IReadOnlyDictionary<string, object?> dependentKeys)
    {
        var (text, parameters) = Membership(relationshipType, principalLabel, principalKeys, dependentLabel, dependentKeys);
        text.Append("\nWITH CASE WHEN size(links) = 1 AND parents[0] = p THEN links ELSE [] END AS eligible")
            .Append("\nFOREACH (r IN eligible | DELETE r)")
            .Append("\nRETURN size(eligible) AS count");
        return new(text.ToString(), parameters);
    }

    /// <summary>
    /// Compare-and-transition optional membership after the executor locks the dependent and
    /// all non-null old/new principals. Null denotes no link, never a null endpoint key.
    /// A rejected original membership must neither remove an edge nor create a replacement.
    /// </summary>
    public static CypherMutationCommand TransitionOptional(string relationshipType, string principalLabel,
        IReadOnlyDictionary<string, object?>? originalPrincipalKeys, IReadOnlyDictionary<string, object?>? newPrincipalKeys,
        string dependentLabel, IReadOnlyDictionary<string, object?> dependentKeys)
    {
        // Validate even when both principals are absent; metadata must never bypass validation.
        _ = Identifier(principalLabel);
        var (text, parameters) = IncomingMembership(relationshipType, dependentLabel, dependentKeys);
        if (originalPrincipalKeys is not null)
            text.Append('\n').Append(Match("p", principalLabel, originalPrincipalKeys, parameters));
        if (newPrincipalKeys is not null)
            text.Append('\n').Append(Match("q", principalLabel, newPrincipalKeys, parameters));
        text.Append("\nWITH d,links");
        if (newPrincipalKeys is not null) text.Append(",q");
        text.Append(",CASE WHEN ").Append(originalPrincipalKeys is null
            ? "size(links) = 0" : "size(links) = 1 AND parents[0] = p")
            .Append(" THEN [1] ELSE [] END AS eligible");
        if (originalPrincipalKeys is not null)
            text.Append("\nFOREACH (r IN CASE WHEN size(eligible) = 1 THEN links ELSE [] END | DELETE r)");
        if (newPrincipalKeys is not null)
            text.Append("\nFOREACH (ignored IN eligible | CREATE (q)-[:").Append(Identifier(relationshipType)).Append("]->(d))");
        text.Append("\nRETURN size(eligible) AS count");
        return new(text.ToString(), parameters);
    }

    private static (StringBuilder Text, IDictionary<string, object> Parameters) Membership(string relationshipType,
        string principalLabel, IReadOnlyDictionary<string, object?> principalKeys,
        string dependentLabel, IReadOnlyDictionary<string, object?> dependentKeys)
    {
        var (text, parameters) = IncomingMembership(relationshipType, dependentLabel, dependentKeys);
        text.Append('\n').Append(Match("p", principalLabel, principalKeys, parameters));
        return (text, parameters);
    }

    private static (StringBuilder Text, IDictionary<string, object> Parameters) IncomingMembership(string relationshipType,
        string dependentLabel, IReadOnlyDictionary<string, object?> dependentKeys)
    {
        var parameters = new Dictionary<string, object>();
        var text = new StringBuilder(Match("d", dependentLabel, dependentKeys, parameters))
            .Append("\nCALL {\n    WITH d\n    OPTIONAL MATCH (parent)-[r:").Append(Identifier(relationshipType)).Append("]->(d)")
            .Append("\n    WITH parent,r LIMIT 2\n    RETURN collect(r) AS links,collect(parent) AS parents\n}");
        return (text, parameters);
    }

    private static string Match(string alias, string label, IReadOnlyDictionary<string, object?> keys, IDictionary<string, object> parameters)
    {
        if (keys.Count == 0 || keys.Values.Any(value => value is null))
            throw new InvalidOperationException("Native node-reference mutations require nonempty, non-null endpoint application keys.");
        var slots = keys.Select((pair, index) =>
        {
            var parameter = alias + index;
            parameters[parameter] = pair.Value!;
            return Identifier(pair.Key) + ": $" + parameter;
        });
        return "MATCH (" + alias + ":" + Identifier(label) + " {" + string.Join(", ", slots) + "})";
    }

    private static string Identifier(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || !Initial(name[0]) || name.Any(c => !Initial(c) && c is not (>= '0' and <= '9')))
            throw new ArgumentException("Cypher metadata identifiers must match [A-Za-z_][A-Za-z0-9_]*.", nameof(name));
        return "`" + name + "`";
    }
    private static bool Initial(char character) => character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or '_';
}
