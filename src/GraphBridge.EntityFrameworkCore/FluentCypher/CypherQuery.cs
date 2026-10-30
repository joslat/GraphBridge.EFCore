using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;

namespace GraphBridge.EntityFrameworkCore.FluentCypher;

/// <summary>Immutable read-only Cypher clauses over the context's finalized EF mappings.</summary>
/// <remarks>This is an explicit native query builder, not an IQueryable or a tracked EF entity query.</remarks>
public sealed class CypherQuery
{
    internal CypherQuery(DbContext context, IReadOnlyList<CypherClause>? clauses = null)
    {
        Context = context;
        Clauses = Array.AsReadOnly((clauses ?? []).ToArray());
    }
    internal DbContext Context { get; }
    internal IReadOnlyList<CypherClause> Clauses { get; }
    public CypherQuery Match(params CypherPattern[] patterns) => AppendMatch(patterns, false);
    public CypherQuery OptionalMatch(params CypherPattern[] patterns) => AppendMatch(patterns, true);
    public CypherQuery Match(CypherPath path, CypherPattern pattern)
        => Match((pattern ?? throw new ArgumentNullException(nameof(pattern))).Named(path));
    public CypherQuery OptionalMatch(CypherPath path, CypherPattern pattern)
        => OptionalMatch((pattern ?? throw new ArgumentNullException(nameof(pattern))).Named(path));
    public CypherQuery Match<T>(CypherNode<T> node) where T : class
        => Match((node ?? throw new ArgumentNullException(nameof(node))).Pattern());

    /// <summary>
    /// Starts the query with a vector search over <paramref name="node"/>'s type (ADR 0057):
    /// <c>MATCH (n:Label) SEARCH n IN (VECTOR INDEX index FOR $vector LIMIT $limit) SCORE AS score</c>. It must be the first
    /// clause, and needs Neo4j 2026 with Cypher 25. Before the query runs, the index must exist, be ONLINE, be a node vector index
    /// on the type's own label, and have the vector's dimensions. <paramref name="score"/> is a fresh <c>Cypher.Score()</c>.
    /// </summary>
    public CypherQuery SearchVector<T>(CypherNode<T> node, string index, IReadOnlyList<float> vector, int limit, CypherValue<double>? score = null)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(vector);
        if (vector.Count == 0 || vector.Any(v => !float.IsFinite(v))) throw new ArgumentException("The vector needs one or more finite values.", nameof(vector));
        // Every float32 is a double exactly, as embeddings are stored (ADR 0054).
        return AppendSearch(node, Search.GraphSearchRequest.Vector(index, vector.Select(v => (double)v).ToArray(), limit, Search.GraphSearchEntityKind.Node), score);
    }

    /// <summary>
    /// Starts the query with a full-text search over <paramref name="node"/>'s type (ADR 0057):
    /// <c>MATCH (n:Label) SEARCH n IN (FULLTEXT INDEX index FOR $query LIMIT $limit) SCORE AS score</c>, with a Lucene query.
    /// It must be the first clause, and needs Neo4j 2026.09 or later. Before the query runs, the index must exist, be ONLINE and be
    /// a node full-text index on the type's own label.
    /// </summary>
    public CypherQuery SearchFullText<T>(CypherNode<T> node, string index, string query, int limit, CypherValue<double>? score = null) where T : class
        => AppendSearch(node, Search.GraphSearchRequest.FullText(index, query, limit, Search.GraphSearchEntityKind.Node), score);

    private CypherQuery AppendSearch(CypherNode node, Search.GraphSearchRequest request, CypherAlias? score)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (Clauses.Count != 0) throw CypherDiagnostics.Create("GBFC305", "A search starts the query: call SearchVector or SearchFullText first, then match from its node.");
        if (score is not null && score.Definition is not CypherScoreTerm)
            throw CypherDiagnostics.Create("GBFC306", "A search's score is a fresh Cypher.Score() value.");
        return new(Context, [new CypherSearchClause(node, request, score)]);
    }

    /// <summary>OPTIONAL MATCH whose predicate is visibly owned by this clause; same as OptionalMatch(pattern).Where(where).</summary>
    public CypherQuery OptionalMatch(CypherPattern pattern, CypherPredicate where) => OptionalMatch(pattern).Where(where);

    /// <summary>OPTIONAL MATCH with a C# predicate owned by this clause (ADR 0047 agree-or-reject rules).</summary>
    public CypherQuery OptionalMatch(CypherPattern pattern, Expression<Func<bool>> where) => OptionalMatch(pattern).Where(where);

    /// <summary>Attaches to the immediately preceding MATCH, OPTIONAL MATCH or WITH clause.</summary>
    public CypherQuery Where(CypherPredicate predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        if (Clauses.Count == 0)
            throw new InvalidOperationException("WHERE requires a preceding MATCH or WITH clause.");
        // The index's LIMIT comes first, so a filter here would quietly return fewer than the limit (ADR 0057).
        if (Clauses[^1] is CypherSearchClause)
            throw CypherDiagnostics.Create("GBFC307", "A WHERE can't filter a search's own hits, which the index has already limited; add With(...) and filter there, or match onwards and filter that.");
        var clauses = Clauses.ToArray();
        clauses[^1] = clauses[^1] switch
        {
            CypherMatchClause match => match with { Predicate = Combine(match.Predicate, predicate.Term) },
            CypherWithClause scope => scope with { Predicate = Combine(scope.Predicate, predicate.Term) },
            _ => throw new InvalidOperationException("WHERE requires a preceding MATCH or WITH clause.")
        };
        return new(Context, clauses);
    }

    /// <summary>A C# predicate over alias.Value members, translated when called and never executed.</summary>
    /// <remarks>Accepted only where the native WHERE result equals the C# meaning (ADR 0047); other shapes throw a coded
    /// NotSupportedException naming the explicit predicate to use. Captured values are read now, like Eq(value).</remarks>
    public CypherQuery Where(Expression<Func<bool>> predicate) => Where(CypherPredicateTranslator.Translate(predicate));

    /// <summary>Forwards only the named aliases and introduces explicitly defined aggregate values.</summary>
    public CypherQuery With(params CypherAlias[] aliases) => AppendWith(aliases, false);
    public CypherQuery WithDistinct(params CypherAlias[] aliases) => AppendWith(aliases, true);
    public CypherResultQuery<TResult> Return<TResult>(Expression<Func<TResult>> projection)
        => new(this, projection ?? throw new ArgumentNullException(nameof(projection)), false);
    public CypherResultQuery<TResult> ReturnDistinct<TResult>(Expression<Func<TResult>> projection)
        => new(this, projection ?? throw new ArgumentNullException(nameof(projection)), true);

    private CypherQuery AppendMatch(CypherPattern[] patterns, bool optional)
    {
        ArgumentNullException.ThrowIfNull(patterns);
        if (patterns.Length == 0 || patterns.Any(p => p is null))
            throw new ArgumentException("MATCH requires at least one non-null pattern.", nameof(patterns));
        if (optional && Clauses.Count == 0)
            throw CypherDiagnostics.Create("GBFC301", "Start with MATCH before OPTIONAL MATCH in the supported fluent contract.");
        return new(Context, [.. Clauses, new CypherMatchClause(Array.AsReadOnly(patterns.ToArray()), optional, null)]);
    }
    private CypherQuery AppendWith(CypherAlias[] aliases, bool distinct)
    {
        ArgumentNullException.ThrowIfNull(aliases);
        if (Clauses.Count == 0) throw new InvalidOperationException("WITH requires a preceding MATCH.");
        if (aliases.Length == 0 || aliases.Any(a => a is null) || aliases.Distinct().Count() != aliases.Length)
            throw new ArgumentException("WITH requires distinct, non-null aliases.", nameof(aliases));
        return new(Context, [.. Clauses, new CypherWithClause(Array.AsReadOnly(aliases.ToArray()), distinct, null)]);
    }
    private static CypherTerm Combine(CypherTerm? previous, CypherTerm predicate)
        => previous is null ? predicate : new CypherBooleanTerm(previous, "AND", predicate);
}

/// <summary>A scalar/DTO projection and final ordering/paging. Results are never attached to EF.</summary>
public sealed partial class CypherResultQuery<TResult>
{
    internal CypherResultQuery(CypherQuery query, Expression<Func<TResult>> projection, bool distinct,
        IReadOnlyList<CypherSort>? orderings = null, int? skip = null, int? limit = null,
        IReadOnlyList<string>? tags = null, TimeSpan? timeout = null)
    {
        Query = query; Projection = projection; Distinct = distinct;
        Orderings = Array.AsReadOnly((orderings ?? []).ToArray()); SkipCount = skip; LimitCount = limit;
        Tags = Array.AsReadOnly((tags ?? []).ToArray()); Timeout = timeout;
    }
    internal CypherQuery Query { get; }
    internal Expression<Func<TResult>> Projection { get; }
    internal bool Distinct { get; }
    internal IReadOnlyList<CypherSort> Orderings { get; }
    internal int? SkipCount { get; }
    internal int? LimitCount { get; }
    internal IReadOnlyList<string> Tags { get; }
    internal TimeSpan? Timeout { get; }

    /// <summary>Writes the tag before the query as <c>//</c> comment lines, like EF's TagWith (ADR 0050). Several tags keep
    /// their order. A tag must be constant text, never user data: each distinct tag is its own server plan-cache entry.</summary>
    public CypherResultQuery<TResult> TagWith(string tag)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);
        return new(Query, Projection, Distinct, Orderings, SkipCount, LimitCount, [.. Tags, tag], Timeout);
    }

    /// <summary>The timeout of the transaction GraphBridge begins for this query (ADR 0050), overriding the context's.
    /// Inside an application transaction it is refused when executed: the transaction keeps the timeout it began with.</summary>
    public CypherResultQuery<TResult> WithTimeout(TimeSpan timeout)
        => new(Query, Projection, Distinct, Orderings, SkipCount, LimitCount, Tags,
            Transport.GraphCommandTimeout.Validate(timeout, nameof(timeout)));
    public CypherResultQuery<TResult> OrderBy<T>(CypherExpression<T> value) => Order(value, false, false);
    public CypherResultQuery<TResult> OrderByDescending<T>(CypherExpression<T> value) => Order(value, true, false);
    public CypherResultQuery<TResult> ThenBy<T>(CypherExpression<T> value) => Order(value, false, true);
    public CypherResultQuery<TResult> ThenByDescending<T>(CypherExpression<T> value) => Order(value, true, true);
    /// <summary>Orders by a returned alias.Value member or WITH value, written like the Return projection.</summary>
    public CypherResultQuery<TResult> OrderBy<TKey>(Expression<Func<TKey>> key) => Order(Key(key), false, false);
    public CypherResultQuery<TResult> OrderByDescending<TKey>(Expression<Func<TKey>> key) => Order(Key(key), true, false);
    public CypherResultQuery<TResult> ThenBy<TKey>(Expression<Func<TKey>> key) => Order(Key(key), false, true);
    public CypherResultQuery<TResult> ThenByDescending<TKey>(Expression<Func<TKey>> key) => Order(Key(key), true, true);
    private static CypherExpression<TKey> Key<TKey>(Expression<Func<TKey>> key) => new(CypherPredicateTranslator.OrderingKey(key));
    public CypherResultQuery<TResult> Skip(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (SkipCount.HasValue) throw new InvalidOperationException("SKIP is already specified.");
        if (LimitCount.HasValue) throw new InvalidOperationException("Specify SKIP before LIMIT.");
        return new(Query, Projection, Distinct, Orderings, count, LimitCount, Tags, Timeout);
    }
    public CypherResultQuery<TResult> Limit(int count)
    {
        if (count is < 1 or > 10_000) throw new ArgumentOutOfRangeException(nameof(count), "Fluent buffered results require a limit between 1 and 10,000.");
        if (LimitCount.HasValue) throw new InvalidOperationException("LIMIT is already specified.");
        return new(Query, Projection, Distinct, Orderings, SkipCount, count, Tags, Timeout);
    }
    private CypherResultQuery<TResult> Order<T>(CypherExpression<T> value, bool descending, bool then)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (SkipCount.HasValue || LimitCount.HasValue) throw new InvalidOperationException("Specify ORDER BY before SKIP/LIMIT.");
        if (then != (Orderings.Count != 0))
            throw new InvalidOperationException(then ? "ThenBy requires OrderBy." : "Use ThenBy to add another ordering.");
        return new(Query, Projection, Distinct, [.. Orderings, new(value.Term, descending)], SkipCount, LimitCount, Tags, Timeout);
    }
}

internal abstract record CypherClause;
internal sealed record CypherMatchClause(IReadOnlyList<CypherPattern> Patterns, bool Optional, CypherTerm? Predicate) : CypherClause;
internal sealed record CypherWithClause(IReadOnlyList<CypherAlias> Aliases, bool Distinct, CypherTerm? Predicate) : CypherClause;
internal sealed record CypherSearchClause(CypherNode Node, Search.GraphSearchRequest Request, CypherAlias? Score) : CypherClause;
internal sealed record CypherSort(CypherTerm Expression, bool Descending);
