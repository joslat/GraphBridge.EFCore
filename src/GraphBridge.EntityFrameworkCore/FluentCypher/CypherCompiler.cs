using System.Text;
using GraphBridge.EntityFrameworkCore.Cypher;
using GraphBridge.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace GraphBridge.EntityFrameworkCore.FluentCypher;

internal sealed partial class CypherCompiler
{
    private readonly DbContext _context;
    private readonly IModel _model;
    private readonly GraphBindingCompiler _bindings = new();
    private Dictionary<CypherAlias, Symbol> _symbols = [];
    private int _nextSymbol;
    private readonly Dictionary<string, object?> _parameters = [];
    private HashSet<CypherAlias> _scope = [];
    // Symbols introduced by OPTIONAL MATCH; they can be null after their own clause (ADR 0047 agreement checks).
    private readonly HashSet<CypherAlias> _optional = [];
    private const int MaxListValues = Query.Neo4jQueryParameter.MaxCollectionValues;
    internal CypherCompiler(DbContext context) => (_context, _model) = (context, context.Model);

    internal CompiledCypher<TResult> Compile<TResult>(CypherResultQuery<TResult> query, bool captureSemantics = false)
    {
        if (query.Query.Clauses.Count == 0) throw Unsupported("GBFC301", "Start with MATCH.");
        if (captureSemantics) _snapshotClauses = [];
        var text = new StringBuilder();
        // ADR 0050: tags lead the command as // comment lines and an empty line; snapshot lines count from the first clause.
        var comments = CypherComments.Lines(query.Tags);
        foreach (var comment in comments) text.AppendLine(comment);
        if (comments.Count != 0) { text.AppendLine(); _snapshotLine = comments.Count + 2; }
        var searches = new List<CypherSearchCheck>();
        foreach (var clause in query.Query.Clauses)
        {
            BeginSnapshot(text);
            switch (clause)
            {
                case CypherSearchClause search:
                    // ADR 0057: SEARCH is a Cypher 25 subclause of a MATCH on the node's labels; the version leads the statement.
                    var request = search.Request;
                    var node = RenderPattern(new CypherPattern(search.Node));
                    var symbol = (NodeSymbol)_symbols[search.Node];
                    text.Append("CYPHER 25 MATCH ").Append(node).Append(" SEARCH ").Append(symbol.Name).Append(" IN (")
                        .Append(request.Kind == Search.GraphSearchKind.Vector ? "VECTOR" : "FULLTEXT").Append(" INDEX ").Append(Identifier(request.Index))
                        .Append(" FOR ").Append(Parameter(request.Query)).Append(" LIMIT ").Append(Parameter((long)request.Limit)).Append(')');
                    if (search.Score is { } score)
                    {
                        if (_symbols.ContainsKey(score)) throw Unsupported("GBFC306", "A search's score is a fresh Cypher.Score() value.");
                        var scoreName = Register(score, new ValueSymbol(NextName(score))).Name;
                        _scope.Add(score);
                        text.Append(" SCORE AS ").Append(scoreName);
                    }
                    searches.Add(new(request, symbol.Binding.Label));
                    EndSnapshot(text, CypherClauseKind.Match);
                    break;
                case CypherMatchClause match:
                    var bound = match.Optional ? _symbols.Keys.ToHashSet() : null;
                    text.Append(match.Optional ? "OPTIONAL MATCH " : "MATCH ");
                    text.AppendJoin(", ", match.Patterns.Select(RenderPattern));
                    // The clause's own WHERE sees its new symbols as pattern-bound; later clauses may see null.
                    AppendWhere(text, match.Predicate);
                    if (bound is not null) _optional.UnionWith(_symbols.Keys.Where(alias => !bound.Contains(alias)));
                    EndSnapshot(text, match.Optional ? CypherClauseKind.OptionalMatch : CypherClauseKind.Match, predicate: match.Predicate is not null);
                    break;
                case CypherWithClause with:
                    var forwarded = with.Aliases.Select(alias =>
                    {
                        if (_scope.Contains(alias)) return Name(alias);
                        if (alias.Definition is not (CypherCountTerm or CypherAggregateTerm) || _symbols.ContainsKey(alias))
                            throw Unsupported("GBFC302", "WITH can forward in-scope symbols or introduce a new aggregate value.");
                        var (expression, symbol) = alias.Definition is CypherCountTerm count
                            ? (RenderCount(count), new ValueSymbol(NextName(alias)))
                            : RenderAggregate((CypherAggregateTerm)alias.Definition, NextName(alias));
                        var name = Register(alias, symbol).Name;
                        if (Capturing) _snapshotAggregates!.Add(new(name, expression));
                        return expression + " AS " + name;
                    }).ToArray();
                    _scope = with.Aliases.ToHashSet();
                    text.Append(with.Distinct ? "WITH DISTINCT " : "WITH ").AppendJoin(", ", forwarded);
                    AppendWhere(text, with.Predicate);
                    EndSnapshot(text, CypherClauseKind.With, with.Distinct, with.Predicate is not null);
                    break;
                default: throw Unsupported("GBFC304", "Unknown clause.");
            }
            text.AppendLine();
        }
        var projection = BuildProjection(query.Projection);
        BeginSnapshot(text);
        text.Append(query.Distinct ? "RETURN DISTINCT " : "RETURN ");
        text.AppendJoin(", ", projection.Columns.Select((c, i) => (c.Term is CypherAliasTerm { Alias: var value } && InlineAggregate(value)
            ? RenderInlineAggregate(value, "c" + i)
            : c.Term is CypherAliasTerm { Alias.Kind: CypherAliasKind.Node or CypherAliasKind.Edge } symbol ? c.Entity!.Render(Name(symbol.Alias))
            : Render(c.Term)) + " AS c" + i));
        if (Capturing) _snapshotResultColumns = Enumerable.Range(0, projection.Columns.Count).Select(i => "c" + i).ToArray();
        EndSnapshot(text, CypherClauseKind.Return, query.Distinct);
        if (query.Orderings.Count != 0)
        {
            text.AppendLine();
            BeginSnapshot(text);
            text.Append("ORDER BY ");
            text.AppendJoin(", ", query.Orderings.Select(order =>
            {
                RefuseVector(order.Expression, "ORDER BY");
                var direction = order.Descending ? " DESC" : " ASC";
                var index = projection.Columns.FindIndex(c => c.Term == order.Expression);
                if (index >= 0) return "c" + index + direction;
                // A member stored on a returned entity is a member of its returned map, valid after RETURN DISTINCT too (ADR 0049).
                if (order.Expression is CypherPropertyTerm member)
                {
                    var owner = projection.Columns.FindIndex(c => c is { Entity: not null, List: false } && c.Term is CypherAliasTerm { Alias: var alias } && alias == member.Alias);
                    if (owner >= 0 && projection.Columns[owner].Entity!.StoredName(member.Property) is { } stored)
                        return "c" + owner + "." + Identifier(stored) + direction;
                }
                throw Unsupported("GBFC405", "ORDER BY must name a returned scalar, or a member stored on a returned entity. Include it in RETURN explicitly.");
            }));
            EndSnapshot(text, CypherClauseKind.OrderBy);
        }
        if (query.SkipCount is { } skip)
        {
            text.AppendLine(); BeginSnapshot(text);
            text.Append("SKIP ").Append(Parameter((long)skip));
            EndSnapshot(text, CypherClauseKind.Skip);
        }
        if (query.LimitCount is { } limit)
        {
            text.AppendLine(); BeginSnapshot(text);
            text.Append("LIMIT ").Append(Parameter((long)limit));
            EndSnapshot(text, CypherClauseKind.Limit);
        }
        return new(new CypherCommand(text.ToString(), _parameters, FinishSnapshot()), projection.Read, searches);
    }

    private string NextName(CypherAlias alias)
    {
        var prefix = alias.Kind switch { CypherAliasKind.Node => "n", CypherAliasKind.Edge => "r", CypherAliasKind.Path => "path", _ => "v" };
        return prefix + _nextSymbol++;
    }
    private TSymbol Register<TSymbol>(CypherAlias alias, TSymbol symbol) where TSymbol : Symbol
    {
        _symbols.Add(alias, symbol);
        return symbol;
    }
    private string Name(CypherAlias alias)
    {
        if (!_scope.Contains(alias))
        {
            var visible = _scope.Select(a => _symbols[a].Name + " (" + a.ClrType.Name + ")").Order(StringComparer.Ordinal).ToArray();
            throw Unsupported("GBFC303", "The expression references a symbol outside the current clause scope: it was never matched, or a WITH dropped it. "
                + (visible.Length == 0 ? "No symbols are in scope here." : "In scope here: " + string.Join(", ", visible) + ".")
                + " Forward a symbol through With(...) to keep it.");
        }
        if (_symbols[alias] is EdgeSymbol { Repeated: true }) throw Unsupported("GBFC207", "A repeated edge is a relationship list, not a scalar edge. Return the named path to inspect its relationships.");
        return _symbols[alias].Name;
    }
    private string Parameter(object? value)
    {
        var name = "p" + _parameters.Count;
        _parameters.Add(name, value);
        return "$" + name;
    }
    // A node type's labels, separated by colons (ADR 0055), each validated and quoted.
    private static string Labels(string labels) => string.Join(':', labels.Split(':').Select(Identifier));

    private static string Identifier(string value) => CypherQueryRenderer.Identifier(value);
    private static NotSupportedException Unsupported(string code, string message) => CypherDiagnostics.Create(code, message);
}

internal sealed record CompiledCypher<T>(CypherCommand Command, Func<IReadOnlyDictionary<string, object>, T> Read, IReadOnlyList<CypherSearchCheck>? Searches = null)
{
    /// <summary>Before the command, in its transaction: each searched index must suit its node type (ADR 0057).</summary>
    internal Func<Neo4j.Driver.IAsyncTransaction, CancellationToken, Task>? Preflight => Searches is not { Count: > 0 } searches ? null : async (runner, token) =>
    {
        foreach (var search in searches)
        {
            var provenance = await Search.GraphSearchPreflight.InspectAsync(runner, search.Request, token).ConfigureAwait(false);
            if (provenance.LabelsOrTypes.Count != 1 || provenance.LabelsOrTypes[0] != search.Label)
                throw new InvalidOperationException($"Index '{search.Request.Index}' covers {string.Join(", ", provenance.LabelsOrTypes)}; a Fluent search needs an index on the node type's own label, {search.Label}.");
        }
    };
}

/// <summary>A search clause's index and the label its node type is stored under.</summary>
internal sealed record CypherSearchCheck(Search.GraphSearchRequest Request, string Label);
