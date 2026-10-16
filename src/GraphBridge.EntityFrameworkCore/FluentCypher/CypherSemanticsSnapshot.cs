namespace GraphBridge.EntityFrameworkCore.FluentCypher;

public enum CypherSnapshotStatus { Complete, NestedExistsNotRepresented, BudgetExceeded }
public enum CypherClauseKind { Match, OptionalMatch, With, Return, OrderBy, Skip, Limit }
public enum CypherStepDirection { Outgoing, Incoming, Both }

/// <summary>Immutable compiler observations for a query pattern, not observed database nodes or results.</summary>
/// <remarks>Captured only by explicit inspection. Names and expression text disclose mapped metadata, as does
/// CypherCommand.Text; parameter values and CLR/EF objects are not included. Source lines are one-based within
/// that command. Incomplete snapshots contain no clauses; consumers must not invent a partial graph.</remarks>
public sealed class CypherSemanticsSnapshot
{
    internal CypherSemanticsSnapshot(CypherSnapshotStatus status, IEnumerable<CypherClauseSnapshot> clauses)
        => (Status, Clauses) = (status, Array.AsReadOnly(clauses.ToArray()));
    public int FormatVersion => 1;
    public CypherSnapshotStatus Status { get; }
    public IReadOnlyList<CypherClauseSnapshot> Clauses { get; }
}

/// <summary>A rendered clause, its lexical scope and WHERE attachment. Scope names are compiler aliases.</summary>
public sealed class CypherClauseSnapshot
{
    internal CypherClauseSnapshot(CypherClauseKind kind, int firstLine, int lastLine, bool distinct, bool hasPredicate,
        IEnumerable<string> scopeBefore, IEnumerable<string> scopeAfter,
        IEnumerable<CypherPatternSnapshot> patterns, IEnumerable<CypherAggregateSnapshot> aggregates)
        => (Kind, FirstLine, LastLine, Distinct, HasPredicate, ScopeBefore, ScopeAfter, Patterns, Aggregates) =
            (kind, firstLine, lastLine, distinct, hasPredicate, Array.AsReadOnly(scopeBefore.ToArray()),
                Array.AsReadOnly(scopeAfter.ToArray()), Array.AsReadOnly(patterns.ToArray()), Array.AsReadOnly(aggregates.ToArray()));
    public CypherClauseKind Kind { get; }
    public int FirstLine { get; }
    public int LastLine { get; }
    public bool Distinct { get; }
    public bool HasPredicate { get; }
    public IReadOnlyList<string> ScopeBefore { get; }
    public IReadOnlyList<string> ScopeAfter { get; }
    public IReadOnlyList<CypherPatternSnapshot> Patterns { get; }
    public IReadOnlyList<CypherAggregateSnapshot> Aggregates { get; }
}

public sealed record CypherNodeSnapshot(string Alias, string Label);
/// <summary>One traversal step. Physical endpoint aliases are absent for undirected/repeated paths,
/// whose individual relationship endpoints cannot be represented by the two outer node aliases.</summary>
public sealed record CypherStepSnapshot(string Alias, string RelationshipType, bool Identified,
    string StartAlias, string EndAlias, CypherStepDirection Direction, string? MappedSourceAlias, string? MappedTargetAlias,
    bool Repeated);
public sealed record CypherAggregateSnapshot(string Alias, string Expression);

/// <summary>One pattern occurrence. Repeated use of an alias retains its identity; separate patterns remain separate.</summary>
public sealed class CypherPatternSnapshot
{
    internal CypherPatternSnapshot(IEnumerable<CypherNodeSnapshot> nodes, IEnumerable<CypherStepSnapshot> steps,
        string? pathAlias, int? minimumHops, int? maximumHops)
        => (Nodes, Steps, PathAlias, MinimumHops, MaximumHops) =
            (Array.AsReadOnly(nodes.ToArray()), Array.AsReadOnly(steps.ToArray()), pathAlias, minimumHops, maximumHops);
    public IReadOnlyList<CypherNodeSnapshot> Nodes { get; }
    public IReadOnlyList<CypherStepSnapshot> Steps { get; }
    public string? PathAlias { get; }
    public int? MinimumHops { get; }
    public int? MaximumHops { get; }
}
