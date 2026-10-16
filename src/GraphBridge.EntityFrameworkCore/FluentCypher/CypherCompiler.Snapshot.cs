using System.Text;
using GraphBridge.EntityFrameworkCore.Metadata;

namespace GraphBridge.EntityFrameworkCore.FluentCypher;

internal sealed partial class CypherCompiler
{
    private List<CypherClauseSnapshot>? _snapshotClauses;
    private List<CypherPatternSnapshot>? _snapshotPatterns;
    private List<CypherAggregateSnapshot>? _snapshotAggregates;
    private CypherSnapshotStatus _snapshotStatus;
    private string[] _snapshotBefore = [];
    private string[]? _snapshotResultColumns;
    private int _snapshotStart, _snapshotLine = 1, _snapshotElements;
    private bool Capturing => _snapshotClauses is not null && _snapshotStatus == CypherSnapshotStatus.Complete;

    private void BeginSnapshot(StringBuilder text)
    {
        if (!Capturing) return;
        if (_snapshotClauses!.Count >= 256) { _snapshotStatus = CypherSnapshotStatus.BudgetExceeded; return; }
        _snapshotStart = text.Length;
        _snapshotBefore = SnapshotScope();
        _snapshotPatterns = [];
        _snapshotAggregates = [];
    }
    private string[] SnapshotScope() => _snapshotResultColumns ?? _scope.Select(a => _symbols[a].Name).Order(StringComparer.Ordinal).ToArray();
    private void EndSnapshot(StringBuilder text, CypherClauseKind kind, bool distinct = false, bool predicate = false)
    {
        if (!Capturing) return;
        var lastLine = _snapshotLine;
        for (var i = _snapshotStart; i < text.Length; i++) if (text[i] == '\n') lastLine++;
        _snapshotClauses!.Add(new(kind, _snapshotLine, lastLine, distinct, predicate, _snapshotBefore, SnapshotScope(),
            _snapshotPatterns!, _snapshotAggregates!));
        _snapshotLine = lastLine + 1;
    }
    private void SnapshotPattern(CypherPattern pattern)
    {
        if (!Capturing) return;
        // Budgets omit the whole snapshot, never change compilation or silently truncate a diagram.
        if (pattern.Steps.Count > 2047 || _snapshotElements + 1L + 2L * pattern.Steps.Count > 4096)
        { _snapshotStatus = CypherSnapshotStatus.BudgetExceeded; return; }
        _snapshotElements += 1 + 2 * pattern.Steps.Count;
        var nodes = new[] { pattern.Start }.Concat(pattern.Steps.Select(s => s.End)).Distinct()
            .Select(a => { var symbol = (NodeSymbol)_symbols[a]; return new CypherNodeSnapshot(symbol.Name, symbol.Binding.Label); });
        var steps = pattern.Steps.Select(s =>
        {
            var symbol = (EdgeSymbol)_symbols[s.Edge];
            return new CypherStepSnapshot(symbol.Name, symbol.RelationshipType,
                _bindings.GetStorageKind(symbol.Entity) == GraphStorageKind.IdentifiedEdge,
                _symbols[s.Start].Name, _symbols[s.End].Name,
                s.Undirected ? CypherStepDirection.Both : s.Outgoing ? CypherStepDirection.Outgoing : CypherStepDirection.Incoming,
                s.Undirected || symbol.Repeated ? null : _symbols[symbol.SourceAlias].Name,
                s.Undirected || symbol.Repeated ? null : _symbols[symbol.TargetAlias].Name, symbol.Repeated);
        });
        _snapshotPatterns!.Add(new(nodes, steps, pattern.Path is null ? null : _symbols[pattern.Path].Name,
            pattern.Depth?.Min, pattern.Depth?.Max));
    }
    private CypherSemanticsSnapshot? FinishSnapshot() => _snapshotClauses is null ? null
        : new(_snapshotStatus, _snapshotStatus == CypherSnapshotStatus.Complete ? _snapshotClauses : []);
}
