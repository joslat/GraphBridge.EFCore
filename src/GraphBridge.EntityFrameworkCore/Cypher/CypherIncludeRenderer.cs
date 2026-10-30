using System.Text;

namespace GraphBridge.EntityFrameworkCore.Cypher;

internal sealed record CypherIncludePlan(CypherQueryPlan Root, CypherQueryPlan Included,
    bool IsCollection, string RelationshipType, string IncludedLabel)
{
    public const string RowsAlias = "__gb_include_rows";
    public const string IncludedLabelAlias = "__gb_include_label";
}

internal static class CypherIncludeRenderer
{
    public static string Render(CypherIncludePlan include)
    {
        var root = include.Root;
        var text = new StringBuilder(CypherQueryRenderer.RenderIncludeRoot(root));
        // Validate the included plan through the same identifier/owner/diagnostic rules as a root.
        _ = CypherQueryRenderer.Render(include.Included);
        if (include.Included.Aggregate != CypherAggregate.None || include.Included.Predicate is not null
            || include.Included.Orderings is not null || include.Included.SkipParameter is not null || include.Included.LimitParameter is not null)
            throw new NotSupportedException("Filtered, ordered, paginated, or aggregated Include is not supported.");
        var rows = Id(CypherIncludePlan.RowsAlias);
        var label = CypherQueryRenderer.Labels(include.IncludedLabel);
        var type = Id(include.RelationshipType);
        if (root.Columns.Concat(include.Included.Columns).Any(c => c.Alias is CypherIncludePlan.RowsAlias or CypherIncludePlan.IncludedLabelAlias))
            throw new InvalidOperationException("Include output aliases are reserved.");

        if (include.IsCollection)
        {
            if (include.Included.Root is not CypherNodeReferenceRoot child
                || child.RelationshipType != include.RelationshipType || child.DependentLabel != include.IncludedLabel)
                throw new InvalidOperationException("Collection Include must use its dependent native-reference binding.");
            // Every owned outgoing link is inspected, including a wrong-label target. No DISTINCT:
            // parallel physical links must fail the child's independent incoming membership check.
            text.Append("\nCALL (n) {\n  MATCH (n)-[:").Append(type).Append("]->(i)");
            CypherQueryRenderer.AppendNodeReference(text, child, "i", "ip");
            text.Append("\n  RETURN collect(").Append(Map(include.Included, "i", "ip", $"coalesce(i:{label}, false)"))
                .Append(") AS ").Append(rows).Append("\n}");
        }
        else
        {
            if (root.Root is not CypherNodeReferenceRoot reference || include.Included.Root is not CypherNodeRoot principal
                || reference.RelationshipType != include.RelationshipType || reference.PrincipalLabel != principal.Label
                || principal.Label != include.IncludedLabel)
                throw new InvalidOperationException("Reference Include must use the root's already diagnosed principal.");
        }

        text.Append("\nRETURN ").AppendJoin(", ", root.Columns.Select(c =>
            $"{CypherQueryRenderer.Property(root.Root, c.Owner, c.Name)} AS {Id(c.Alias)}"));
        if (root.Root is CypherNodeReferenceRoot)
            foreach (var alias in new[] { CypherQueryPlan.NodeReferenceCountAlias, CypherQueryPlan.NodeReferencePrincipalAlias })
                text.Append(", ").Append(Id(alias)).Append(" AS ").Append(Id(alias));
        text.Append(", ");
        if (include.IsCollection) text.Append(rows);
        else
        {
            var optional = root.Root is CypherNodeReferenceRoot { Required: false };
            if (optional) text.Append("CASE WHEN ").Append(Id(CypherQueryPlan.NodeReferenceCountAlias)).Append(" = 0 THEN [] ELSE ");
            text.Append('[').Append(Map(include.Included, "s", "s", $"coalesce(s:{label}, false)")).Append(']');
            if (optional) text.Append(" END");
        }
        text.Append(" AS ").Append(rows);
        // Reassert root order after correlated expansion. Pagination has already selected roots.
        if (root.Orderings is { Count: > 0 })
            text.Append("\nORDER BY ").AppendJoin(", ", root.Orderings.Select(o =>
            {
                var property = CypherQueryRenderer.Property(root.Root, o.Owner, o.Name);
                return $"{property} IS NULL {(o.Ascending ? "DESC" : "ASC")}, {property} {(o.Ascending ? "ASC" : "DESC")}";
            }));
        return text.ToString();
    }

    private static string Map(CypherQueryPlan plan, string nodeAlias, string sourceAlias, string labelDiagnostic)
    {
        var values = plan.Columns.Select(column =>
        {
            var owner = column.Owner switch
            {
                CypherPropertyOwner.Node => nodeAlias,
                CypherPropertyOwner.Source => sourceAlias,
                _ => throw new InvalidOperationException("Include supports node and derived principal-key slots only.")
            };
            return $"{Id(column.Alias)}: {owner}.{Id(column.Name)}";
        }).ToList();
        values.Add($"{Id(CypherIncludePlan.IncludedLabelAlias)}: {labelDiagnostic}");
        if (plan.Root is CypherNodeReferenceRoot)
            foreach (var alias in new[] { CypherQueryPlan.NodeReferenceCountAlias, CypherQueryPlan.NodeReferencePrincipalAlias })
                values.Add($"{Id(alias)}: {Id(alias)}");
        return "{" + string.Join(", ", values) + "}";
    }

    private static string Id(string value) => CypherQueryRenderer.Identifier(value);
}
