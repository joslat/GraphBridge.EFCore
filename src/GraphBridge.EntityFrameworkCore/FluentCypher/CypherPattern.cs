namespace GraphBridge.EntityFrameworkCore.FluentCypher;

/// <summary>An immutable graph pattern; each step describes direction, not a client traversal.</summary>
public sealed class CypherPattern
{
    internal CypherPattern(CypherAlias start) : this(start, []) { }
    private CypherPattern(CypherAlias start, IReadOnlyList<CypherPatternStep> steps, CypherDepth? depth = null, CypherPath? path = null)
    {
        Start = start;
        Steps = Array.AsReadOnly(steps.ToArray());
        Depth = depth; Path = path;
    }
    internal CypherAlias Start { get; }
    internal IReadOnlyList<CypherPatternStep> Steps { get; }
    internal CypherDepth? Depth { get; }
    internal CypherPath? Path { get; }
    internal CypherAlias End => Steps.Count == 0 ? Start : Steps[^1].End;
    public CypherPattern Out<TEdge, TTarget>(CypherEdge<TEdge> edge, CypherNode<TTarget> target)
        where TEdge : class where TTarget : class => Add(edge, target, true);
    public CypherPattern In<TEdge, TSource>(CypherEdge<TEdge> edge, CypherNode<TSource> source)
        where TEdge : class where TSource : class => Add(edge, source, false);
    /// <summary>Outgoing step through a new anonymous edge symbol.</summary>
    public CypherPattern Out<TEdge>(CypherNode target) where TEdge : class => Add(new CypherEdge<TEdge>(), target, true);
    /// <summary>Incoming step through a new anonymous edge symbol.</summary>
    public CypherPattern In<TEdge>(CypherNode source) where TEdge : class => Add(new CypherEdge<TEdge>(), source, false);
    public CypherPattern Undirected<TEdge, TNode>(CypherEdge<TEdge> edge, CypherNode<TNode> other)
        where TEdge : class where TNode : class => Add(edge, other, true, true);
    /// <summary>Repeats a single homogeneous relationship pattern with finite native relationship uniqueness.</summary>
    public CypherPattern Repeat(int minDepth, int maxDepth)
    {
        if (minDepth < 0 || maxDepth < minDepth || maxDepth > 64)
            throw new ArgumentOutOfRangeException(nameof(maxDepth), "Path bounds require 0 <= minDepth <= maxDepth <= 64.");
        if (Steps.Count != 1 || Depth is not null || Path is not null)
            throw CypherDiagnostics.Create("GBFC214", "Repeat requires exactly one unbounded pattern step and can be specified once.");
        return new(Start, Steps, new(minDepth, maxDepth));
    }
    internal CypherPattern Named(CypherPath path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (Depth is null || Path is not null) throw CypherDiagnostics.Create("GBFC214", "A named fluent path requires one explicitly bounded Repeat pattern.");
        return new(Start, Steps, Depth, path);
    }
    private CypherPattern Add(CypherAlias edge, CypherAlias end, bool outgoing, bool undirected = false)
    {
        ArgumentNullException.ThrowIfNull(edge);
        ArgumentNullException.ThrowIfNull(end);
        if (Depth is not null || Path is not null) throw CypherDiagnostics.Create("GBFC214", "A bounded pattern cannot append another step in the current fluent contract.");
        return new(Start, [.. Steps, new(End, edge, end, outgoing, undirected)]);
    }
}

internal sealed record CypherPatternStep(CypherAlias Start, CypherAlias Edge, CypherAlias End, bool Outgoing, bool Undirected = false);
internal sealed record CypherDepth(int Min, int Max);
