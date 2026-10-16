namespace GraphBridge.EntityFrameworkCore.Cypher;

internal abstract record CypherExpression;
internal enum CypherPropertyOwner { Node, Source, Relationship, Target }
internal sealed record CypherProperty(string Name, CypherPropertyOwner Owner = CypherPropertyOwner.Node) : CypherExpression;
internal sealed record CypherParameter(string Name) : CypherExpression;
internal sealed record CypherUtcInstant(CypherExpression Operand) : CypherExpression;
// Internal, transaction-local candidate locator. Never exposed as an EF key or user expression.
internal sealed record CypherPhysicalIdentitySet(string ParameterName) : CypherExpression;
internal sealed record CypherNot(CypherExpression Operand) : CypherExpression;
internal sealed record CypherIn(CypherProperty Item, CypherParameter Values) : CypherExpression;
internal sealed record CypherBinary(CypherExpression Left, CypherOperator Operator, CypherExpression Right) : CypherExpression;
internal enum CypherOperator { Equal, NotEqual, GreaterThan, GreaterThanOrEqual, LessThan, LessThanOrEqual, And, Or }
internal enum CypherAggregate { None, Count, Any, Min, Max }
internal sealed record CypherIntegerExtremum(CypherProperty Property, bool Nullable, bool Int32);
internal sealed record CypherColumn(string Name, string Alias, CypherPropertyOwner Owner = CypherPropertyOwner.Node);
internal sealed record CypherOrdering(string Name, bool Ascending, CypherPropertyOwner Owner = CypherPropertyOwner.Node, bool UtcInstant = false);
internal abstract record CypherQueryRoot;
internal sealed record CypherNodeRoot(string Label) : CypherQueryRoot;
internal sealed record CypherNodeReferenceRoot(string DependentLabel, string RelationshipType, string PrincipalLabel, bool Required = true) : CypherQueryRoot;
internal sealed record CypherAssociationRoot(string SourceLabel, string RelationshipType, string TargetLabel) : CypherQueryRoot;
internal sealed record CypherIdentifiedEdgeRoot(string SourceLabel, string RelationshipType, string TargetLabel, string KeyProperty) : CypherQueryRoot;

// This syntax model and renderer have no dependency on EF metadata or driver objects.
internal sealed record CypherQueryPlan
{
    public const string AssociationCountAlias = "__gb_association_count";
    public const string IdentifiedEdgeCountAlias = "__gb_identified_edge_count";
    public const string NodeReferenceCountAlias = "__gb_node_reference_count";
    public const string NodeReferencePrincipalAlias = "__gb_node_reference_principal";
    public const string AggregateInvalidAlias = "__gb_aggregate_invalid";

    private IReadOnlyList<CypherColumn> _columns = [];
    private IReadOnlyList<CypherOrdering>? _orderings;

    public CypherQueryPlan(string Label, IReadOnlyList<CypherColumn> Columns,
        CypherExpression? Predicate = null, IReadOnlyList<CypherOrdering>? Orderings = null,
        string? SkipParameter = null, string? LimitParameter = null, CypherAggregate Aggregate = CypherAggregate.None)
        : this(new CypherNodeRoot(Label), Columns, Predicate, Orderings, SkipParameter, LimitParameter, Aggregate) { }

    public CypherQueryPlan(CypherQueryRoot Root, IReadOnlyList<CypherColumn> Columns,
        CypherExpression? Predicate = null, IReadOnlyList<CypherOrdering>? Orderings = null,
        string? SkipParameter = null, string? LimitParameter = null, CypherAggregate Aggregate = CypherAggregate.None)
    {
        this.Root = Root;
        this.Columns = Columns;
        this.Predicate = Predicate;
        this.Orderings = Orderings;
        this.SkipParameter = SkipParameter;
        this.LimitParameter = LimitParameter;
        this.Aggregate = Aggregate;
    }

    public CypherQueryRoot Root { get; init; }
    public IReadOnlyList<CypherColumn> Columns
    {
        get => _columns;
        init => _columns = Array.AsReadOnly(value.ToArray());
    }
    public CypherExpression? Predicate { get; init; }
    public IReadOnlyList<CypherOrdering>? Orderings
    {
        get => _orderings;
        init => _orderings = value is null ? null : Array.AsReadOnly(value.ToArray());
    }
    public string? SkipParameter { get; init; }
    public string? LimitParameter { get; init; }
    public CypherAggregate Aggregate { get; init; }
    public CypherIntegerExtremum? Extremum { get; init; }
}
