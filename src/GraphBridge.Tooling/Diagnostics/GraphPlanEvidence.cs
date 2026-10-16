using System.Security.Cryptography;
using System.Text;
using GraphBridge.EntityFrameworkCore.Diagnostics;
using Neo4j.Driver;

namespace GraphBridge.Tooling.Diagnostics;

/// <summary>A pre-order plan node with a parent ordinal; contains no arguments, identifiers or database names.</summary>
public sealed record GraphPlanOperatorEvidence(int Index, int? Parent, string Operator);

/// <summary>A redacted shape from an actual EXPLAIN summary, not execution or performance evidence.</summary>
public sealed class GraphPlanEvidence
{
    // Display allow-list only, never a query grammar or capability assertion. Unknown names stay redacted.
    // https://neo4j.com/docs/cypher-manual/current/planning-and-tuning/operators/
    private static readonly HashSet<string> PublicOperators = new(StringComparer.Ordinal)
    {
        "ProduceResults", "Projection", "Filter", "Limit", "Skip", "Sort", "PartialSort", "Top", "PartialTop",
        "Distinct", "OrderedDistinct", "EagerAggregation", "OrderedAggregation", "Eager", "Argument", "Apply",
        "SemiApply", "AntiSemiApply", "LetSemiApply", "LetAntiSemiApply", "SelectOrSemiApply", "SelectOrAntiSemiApply",
        "LetSelectOrSemiApply", "LetSelectOrAntiSemiApply", "Anti", "Optional", "OptionalExpand(All)", "OptionalExpand(Into)",
        "Expand(All)", "Expand(Into)", "VarLengthExpand(All)", "VarLengthExpand(Into)", "PruningVarLengthExpand", "BFSPruningVarLengthExpand",
        "AllNodesScan", "NodeByLabelScan", "NodeIndexSeek", "NodeUniqueIndexSeek", "NodeIndexSeekByRange", "NodeIndexScan",
        "DirectedRelationshipTypeScan", "UndirectedRelationshipTypeScan", "DirectedAllRelationshipsScan", "UndirectedAllRelationshipsScan",
        "DirectedRelationshipIndexSeek", "UndirectedRelationshipIndexSeek", "NodeHashJoin", "ValueHashJoin", "NodeLeftOuterHashJoin",
        "NodeRightOuterHashJoin", "CartesianProduct", "CacheProperties", "EmptyResult", "EmptyRow", "Union", "OrderedUnion"
    };
    private GraphPlanEvidence(string statementFingerprint, IReadOnlyList<GraphPlanOperatorEvidence> operators)
    {
        StatementFingerprint = statementFingerprint;
        Operators = operators;
        var canonical = string.Join("\n", operators.Select(n => FormattableString.Invariant($"{n.Index}:{n.Parent?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-"}:{n.Operator}")));
        ShapeFingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
    public string StatementFingerprint { get; }
    public string ShapeFingerprint { get; }
    public IReadOnlyList<GraphPlanOperatorEvidence> Operators { get; }
    public bool ArgumentsIncluded => false;
    public bool IdentifiersIncluded => false;
    public bool HasRedactedOperators => Operators.Any(n => n.Operator == "redacted-operator");

    internal static GraphPlanEvidence Capture(GraphQueryExplanation explanation, string expectedFingerprint, int maxNodes)
    {
        if (explanation.StatementFingerprint != expectedFingerprint)
            throw new InvalidOperationException("The plan does not identify the compiled statement.");
        var pending = new Stack<(IPlan Plan, int? Parent)>();
        var nodes = new List<GraphPlanOperatorEvidence>();
        pending.Push((explanation.Plan, null));
        while (pending.TryPop(out var current))
        {
            if (nodes.Count >= maxNodes) throw new InvalidOperationException("Plan evidence exceeds its node budget.");
            var index = nodes.Count;
            var name = current.Plan.OperatorType.Split('@', 2)[0];
            nodes.Add(new(index, current.Parent, PublicOperators.Contains(name) ? name : "redacted-operator"));
            var children = current.Plan.Children;
            if (children.Count > maxNodes - nodes.Count - pending.Count)
                throw new InvalidOperationException("Plan evidence exceeds its node budget.");
            for (var child = children.Count - 1; child >= 0; child--) pending.Push((children[child], index));
        }
        return new(explanation.StatementFingerprint, Array.AsReadOnly(nodes.ToArray()));
    }
}
