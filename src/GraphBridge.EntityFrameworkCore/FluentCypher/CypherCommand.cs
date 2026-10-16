using System.Collections.ObjectModel;

namespace GraphBridge.EntityFrameworkCore.FluentCypher;

/// <summary>A snapshot for explicit inspection or native execution. Parameters can contain sensitive values.</summary>
public sealed class CypherCommand
{
    internal CypherCommand(string text, IDictionary<string, object?> parameters, CypherSemanticsSnapshot? semantics = null)
    {
        Text = text;
        Parameters = new ReadOnlyDictionary<string, object?>(new Dictionary<string, object?>(parameters));
        Semantics = semantics;
    }
    public string Text { get; }
    public IReadOnlyDictionary<string, object?> Parameters { get; }
    /// <summary>Opt-in compiler semantics; null for normal compilation. Contains mapped names, never parameter values.</summary>
    public CypherSemanticsSnapshot? Semantics { get; }
    public override string ToString() => Text;
}
