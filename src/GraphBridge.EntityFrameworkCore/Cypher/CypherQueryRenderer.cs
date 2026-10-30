using System.Text;

namespace GraphBridge.EntityFrameworkCore.Cypher;

internal static class CypherQueryRenderer
{
    public static string Render(CypherQueryPlan plan)
        => RenderCore(plan, includeRoot: false);

    internal static string RenderIncludeRoot(CypherQueryPlan plan)
        => RenderCore(plan, includeRoot: true);

    internal static string RenderBulkSelection(CypherQueryPlan plan)
    {
        if (plan.Aggregate != CypherAggregate.None)
            throw new NotSupportedException("Bulk selection requires a nonaggregate entity plan.");
        return RenderCore(plan, includeRoot: false, bulkSelection: true);
    }

    private static string RenderCore(CypherQueryPlan plan, bool includeRoot, bool bulkSelection = false)
    {
        var text = new StringBuilder(RenderRootMatch(plan.Root));
        var diagnosticAlias = plan.Root switch
        {
            CypherAssociationRoot => CypherQueryPlan.AssociationCountAlias,
            CypherIdentifiedEdgeRoot => CypherQueryPlan.IdentifiedEdgeCountAlias,
            CypherNodeReferenceRoot => CypherQueryPlan.NodeReferenceCountAlias,
            _ => null
        };
        if (diagnosticAlias is not null && plan.Columns.Any(c => c.Alias == diagnosticAlias))
            throw new InvalidOperationException($"The column alias '{diagnosticAlias}' is reserved for the physical relationship identity diagnostic.");
        if (plan.Root is CypherNodeReferenceRoot && plan.Columns.Any(c => c.Alias == CypherQueryPlan.NodeReferencePrincipalAlias))
            throw new InvalidOperationException("The native-reference principal diagnostic alias is reserved.");
        if (diagnosticAlias is not null && plan.Columns.GroupBy(c => c.Alias, StringComparer.Ordinal).Any(group => group.Count() > 1))
            throw new InvalidOperationException("Relationship query columns require distinct output aliases.");
        // Validate even columns/orderings omitted by an aggregate, so invalid owners never pass silently.
        var columns = plan.Columns.Select(c => $"{Property(plan.Root, c.Owner, c.Name)} AS {Identifier(c.Alias)}").ToArray();
        var orderings = plan.Orderings?.Select(o =>
        {
            var property = Property(plan.Root, o.Owner, o.Name);
            if (o.UtcInstant) property = NormalizeInstant(property);
            return $"{property} IS NULL {(o.Ascending ? "DESC" : "ASC")}, {property} {(o.Ascending ? "ASC" : "DESC")}";
        }).ToArray();
        if (plan.Root is CypherAssociationRoot associationRoot)
            AppendAssociationCount(text, associationRoot);
        if (plan.Root is CypherIdentifiedEdgeRoot identifiedRoot)
            AppendIdentifiedEdgeCount(text, identifiedRoot);
        if (plan.Root is CypherNodeReferenceRoot referenceRoot)
            AppendNodeReference(text, referenceRoot);
        if (plan.Predicate is not null)
            text.Append("\nWHERE ").Append(RenderExpression(plan.Root, plan.Predicate));

        if (includeRoot)
        {
            if (plan.Aggregate != CypherAggregate.None || plan.Root is not (CypherNodeRoot or CypherNodeReferenceRoot))
                throw new NotSupportedException("Include requires a non-aggregate node root.");
            text.Append("\nWITH n");
            if (plan.Root is CypherNodeReferenceRoot)
                text.Append(", s, ").Append(Identifier(CypherQueryPlan.NodeReferenceCountAlias))
                    .Append(", ").Append(Identifier(CypherQueryPlan.NodeReferencePrincipalAlias));
            if (orderings is { Length: > 0 }) text.Append("\nORDER BY ").AppendJoin(", ", orderings);
            if (plan.SkipParameter is not null) text.Append("\nSKIP $").Append(ParameterName(plan.SkipParameter));
            if (plan.LimitParameter is not null) text.Append("\nLIMIT $").Append(ParameterName(plan.LimitParameter));
            return text.ToString();
        }

        text.Append("\nRETURN ");
        if (bulkSelection) text.Append("elementId(").Append(ElementAlias(plan.Root)).Append(") AS __gb_bulk_id, ");
        if (plan.Aggregate != CypherAggregate.None)
        {
            if (plan.Root is CypherAssociationRoot)
                throw new NotSupportedException("Association aggregates require a physical-relationship cardinality diagnostic before runtime activation.");
            if (plan.Root is CypherIdentifiedEdgeRoot)
                throw new NotSupportedException("Identified-edge aggregates require preservation of each relationship identity diagnostic.");
            if (plan.Root is CypherNodeReferenceRoot)
                throw new NotSupportedException("Native-reference aggregates require preservation of each dependent membership diagnostic.");
            if (plan.Aggregate is CypherAggregate.Min or CypherAggregate.Max)
            {
                var input = plan.Extremum ?? throw new InvalidOperationException("Integer extremum input is required.");
                var property = Property(plan.Root, input.Property.Owner, input.Property.Name);
                var valid = $"{property} IS :: INTEGER NOT NULL";
                if (input.Int32) valid += $" AND {property} >= -2147483648 AND {property} <= 2147483647";
                if (input.Nullable) valid = $"{property} IS NULL OR ({valid})";
                text.Append(plan.Aggregate == CypherAggregate.Min ? "min(" : "max(")
                    .Append(property).Append(") AS aggregate, count(CASE WHEN ").Append(valid)
                    .Append(" THEN null ELSE 1 END) AS ").Append(CypherQueryPlan.AggregateInvalidAlias);
                return text.ToString();
            }
            text.Append(plan.Aggregate switch
            {
                CypherAggregate.Count => "count(*)",
                CypherAggregate.Any => "count(*) > 0",
                _ => throw new InvalidOperationException("Unknown Cypher aggregate.")
            }).Append(" AS aggregate");
            return text.ToString();
        }

        // A constant/empty DTO still returns one row per matched node, without
        // reading an unrelated property or accidentally introducing aggregation.
        if (columns.Length == 0 && diagnosticAlias is null) text.Append("1 AS __gb_row");
        else text.AppendJoin(", ", columns);
        if (diagnosticAlias is not null)
        {
            if (columns.Length > 0)
                text.Append(", ");
            var diagnostic = Identifier(diagnosticAlias);
            text.Append(diagnostic).Append(" AS ").Append(diagnostic);
        }
        if (plan.Root is CypherNodeReferenceRoot)
        {
            var principal = Identifier(CypherQueryPlan.NodeReferencePrincipalAlias);
            text.Append(", ").Append(principal).Append(" AS ").Append(principal);
        }
        if (orderings is { Length: > 0 })
            text.Append("\nORDER BY ").AppendJoin(", ", orderings);
        if (plan.SkipParameter is not null)
            text.Append("\nSKIP $").Append(ParameterName(plan.SkipParameter));
        if (plan.LimitParameter is not null)
            text.Append("\nLIMIT $").Append(ParameterName(plan.LimitParameter));
        return text.ToString();
    }

    internal static void AppendNodeReference(StringBuilder text, CypherNodeReferenceRoot root,
        string nodeAlias = "n", string sourceAlias = "s")
    {
        // Begin at the dependent so missing membership survives as an invalid row. Count physical
        // links before testing labels; collect preserves parallel links, bounded to two candidates.
        // Malformed memberships expose no arbitrary parent/FK to predicates or ordering. Diagnostics
        // cover returned rows only: filters and pagination can exclude malformed dependents.
        var count = Identifier(CypherQueryPlan.NodeReferenceCountAlias);
        var principal = Identifier(CypherQueryPlan.NodeReferencePrincipalAlias);
        var label = Labels(root.PrincipalLabel);
        ValidateToken(nodeAlias);
        ValidateToken(sourceAlias);
        text.Append("\nCALL (").Append(nodeAlias).Append(") {\n  OPTIONAL MATCH (parent)-[candidate:").Append(Identifier(root.RelationshipType)).Append("]->(").Append(nodeAlias).Append(")")
            .Append("\n  WITH parent LIMIT 2\n  RETURN collect(parent) AS parents\n}")
            .Append("\nWITH ").Append(nodeAlias).Append(", size(parents) AS ").Append(count)
            .Append(", CASE WHEN size(parents) = 1 THEN parents[0] ELSE null END AS parent")
            .Append("\nWITH ").Append(nodeAlias).Append(", ").Append(count).Append(", coalesce(parent:").Append(label).Append(", false) AS ").Append(principal)
            .Append(", CASE WHEN parent:").Append(label).Append(" THEN parent ELSE null END AS ").Append(sourceAlias);
    }

    private static void AppendAssociationCount(StringBuilder text, CypherAssociationRoot root)
    {
        // Count all physical edges for the same bound directed endpoint pair before payload predicates
        // or paging can hide a duplicate. Only two rows enter aggregation; no collect or DISTINCT.
        // Official syntax: /cypher-manual/5/subqueries/call-subquery/ (scope clause introduced in 5.23),
        // /cypher-manual/current/clauses/limit/ and /cypher-manual/current/functions/aggregating/.
        // This is a rendered diagnostic contract; pinned-server execution remains a separate live gate.
        var diagnostic = Identifier(CypherQueryPlan.AssociationCountAlias);
        text.Append("\nCALL (s, t) {\n  MATCH (s)-[candidate:").Append(Identifier(root.RelationshipType)).Append("]->(t)")
            .Append("\n  WITH candidate LIMIT 2\n  RETURN count(*) AS ").Append(diagnostic)
            .Append("\n}\nWITH s, r, t, ").Append(diagnostic);
    }

    private static void AppendIdentifiedEdgeCount(StringBuilder text, CypherIdentifiedEdgeRoot root)
    {
        // Identity is scoped to the entire relationship type, independent of labels, endpoint pair,
        // payload filters or pagination. A missing ID produces zero; two candidates prove corruption.
        // Keep physical rows intact so EF identity resolution cannot conceal invalid storage.
        var diagnostic = Identifier(CypherQueryPlan.IdentifiedEdgeCountAlias);
        var key = Identifier(root.KeyProperty);
        text.Append("\nCALL (r) {\n  MATCH ()-[candidate:").Append(Identifier(root.RelationshipType)).Append("]->()")
            .Append("\n  WHERE candidate.").Append(key).Append(" = r.").Append(key)
            .Append("\n  WITH candidate LIMIT 2\n  RETURN count(*) AS ").Append(diagnostic)
            .Append("\n}\nWITH s, r, t, ").Append(diagnostic);
    }

    internal static string Property(CypherQueryRoot root, CypherPropertyOwner owner, string name)
    {
        var alias = (root, owner) switch
        {
            (CypherNodeRoot, CypherPropertyOwner.Node) => "n",
            (CypherNodeReferenceRoot, CypherPropertyOwner.Node) => "n",
            (CypherNodeReferenceRoot, CypherPropertyOwner.Source) => "s",
            (CypherAssociationRoot, CypherPropertyOwner.Source) => "s",
            (CypherAssociationRoot, CypherPropertyOwner.Relationship) => "r",
            (CypherAssociationRoot, CypherPropertyOwner.Target) => "t",
            (CypherIdentifiedEdgeRoot, CypherPropertyOwner.Source) => "s",
            (CypherIdentifiedEdgeRoot, CypherPropertyOwner.Relationship) => "r",
            (CypherIdentifiedEdgeRoot, CypherPropertyOwner.Target) => "t",
            _ => throw new InvalidOperationException("The Cypher property owner is not available in this query root.")
        };
        return $"{alias}.{Identifier(name)}";
    }

    internal static string ElementAlias(CypherQueryRoot root) => root switch
    {
        CypherNodeRoot or CypherNodeReferenceRoot => "n",
        CypherAssociationRoot or CypherIdentifiedEdgeRoot => "r",
        _ => throw new InvalidOperationException("Unknown Cypher query root.")
    };

    internal static string RenderRootMatch(CypherQueryRoot root) => root switch
    {
        CypherNodeRoot node => $"MATCH (n:{Labels(node.Label)})",
        CypherNodeReferenceRoot reference => $"MATCH (n:{Labels(reference.DependentLabel)})",
        CypherAssociationRoot association => $"MATCH (s:{Labels(association.SourceLabel)})-[r:{Identifier(association.RelationshipType)}]->(t:{Labels(association.TargetLabel)})",
        CypherIdentifiedEdgeRoot edge => $"MATCH (s:{Labels(edge.SourceLabel)})-[r:{Identifier(edge.RelationshipType)}]->(t:{Labels(edge.TargetLabel)})",
        _ => throw new InvalidOperationException("Unknown Cypher query root.")
    };

    private static string RenderExpression(CypherQueryRoot root, CypherExpression expression) => expression switch
    {
        CypherPhysicalIdentitySet ids => $"elementId({ElementAlias(root)}) IN ${ParameterName(ids.ParameterName)}",
        CypherProperty property => Property(root, property.Owner, property.Name),
        CypherParameter parameter => "$" + ParameterName(parameter.Name),
        CypherUtcInstant instant => NormalizeInstant(RenderExpression(root, instant.Operand)),
        CypherNot not => $"(NOT coalesce({RenderExpression(root, not.Operand)}, false))",
        CypherIn contains => $"(coalesce({RenderExpression(root, contains.Item)} IN {RenderExpression(root, contains.Values)}, false)"
            + $" OR ({RenderExpression(root, contains.Item)} IS NULL AND any(__gb_contains_value IN {RenderExpression(root, contains.Values)} WHERE __gb_contains_value IS NULL)))",
        CypherBinary binary => RenderBinary(root, binary),
        _ => throw new InvalidOperationException("Unknown Cypher expression.")
    };

    private static string NormalizeInstant(string operand)
        => $"CASE WHEN {operand} IS NULL THEN null ELSE datetime({{datetime: {operand}, timezone: '+00:00'}}) END";

    private static string RenderBinary(CypherQueryRoot root, CypherBinary binary)
    {
        var left = RenderExpression(root, binary.Left);
        var right = RenderExpression(root, binary.Right);
        // Equality is a non-null boolean even when Neo4j properties are missing.
        var equality = $"coalesce({left} = {right}, ({left} IS NULL AND {right} IS NULL))";
        return binary.Operator switch
        {
            CypherOperator.Equal => equality,
            CypherOperator.NotEqual => $"(NOT {equality})",
            CypherOperator.And => $"({left} AND {right})",
            CypherOperator.Or => $"({left} OR {right})",
            CypherOperator.GreaterThan => $"coalesce({left} > {right}, false)",
            CypherOperator.GreaterThanOrEqual => $"coalesce({left} >= {right}, false)",
            CypherOperator.LessThan => $"coalesce({left} < {right}, false)",
            CypherOperator.LessThanOrEqual => $"coalesce({left} <= {right}, false)",
            _ => throw new InvalidOperationException("Unknown Cypher operator.")
        };
    }

    internal static string Identifier(string value)
    {
        ValidateToken(value);
        return "`" + value + "`";
    }

    /// <summary>A node type's labels, separated by colons as <c>NodeBinding.Labels</c> holds them, each validated and quoted
    /// (ADR 0055): <c>`Person`:`__Entity__`</c>. In a pattern or a label predicate, a node must carry every one.</summary>
    internal static string Labels(string labels) => string.Join(':', labels.Split(':').Select(Identifier));

    private static string ParameterName(string value)
    {
        ValidateToken(value);
        return value;
    }

    private static void ValidateToken(string value)
    {
        if (string.IsNullOrEmpty(value) || !(char.IsAsciiLetter(value[0]) || value[0] == '_')
            || value.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '_')))
            throw new InvalidOperationException("Cypher syntax requires a validated metadata identifier.");
    }
}
