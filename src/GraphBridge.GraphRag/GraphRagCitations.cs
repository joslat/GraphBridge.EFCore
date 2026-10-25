using System.Text;

namespace GraphBridge.GraphRag;

/// <summary>A verified citation: <see cref="Quote"/> is the stored text at the document's bytes [<see cref="StartByte"/>, <see cref="EndByte"/>).</summary>
public sealed record GraphRagCitation(string DocumentId, string ChunkId, int StartByte, int EndByte, string Quote);

/// <summary>
/// Locates a model's quote in the chunk it cites (ADR 0052, section 4). Only whitespace runs, quotation marks, typographic
/// apostrophes and dashes are normalised, and at most two <c>...</c> elisions are allowed, in order. Nothing else is
/// forgiven: no fuzzy matching, no case folding, no match across chunks. The citation records the stored text.
/// </summary>
public static class GraphRagCitationVerifier
{
    public static GraphRagCitation? Locate(GraphRagEvidenceItem item, string quote)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (string.IsNullOrWhiteSpace(quote)) return null;
        var segments = quote.Replace("…", "...", StringComparison.Ordinal).Split("...", StringSplitOptions.None)
            .Select(segment => Normalise(segment).Text.Trim(' ')).ToArray();
        // Leading or trailing elisions only mark that the quote is part of a longer sentence.
        var parts = segments.SkipWhile(s => s.Length == 0).Reverse().SkipWhile(s => s.Length == 0).Reverse().ToArray();
        if (parts.Length is 0 or > 3 || parts.Any(p => p.Length == 0)) return null;

        var (text, map) = Normalise(item.Text);
        var start = -1;
        var end = -1;
        var position = 0;
        foreach (var part in parts)
        {
            var found = text.IndexOf(part, position, StringComparison.Ordinal);
            if (found < 0) return null;
            if (start < 0) start = found;
            end = found + part.Length;
            position = end;
        }
        var originalStart = map[start];
        var originalEnd = map[end - 1] + 1;
        // A surrogate pair is one code point; never end inside it.
        if (char.IsHighSurrogate(item.Text[originalEnd - 1]) && originalEnd < item.Text.Length) originalEnd++;
        var startByte = item.StartByte + Encoding.UTF8.GetByteCount(item.Text.AsSpan(0, originalStart));
        var endByte = item.StartByte + Encoding.UTF8.GetByteCount(item.Text.AsSpan(0, originalEnd));
        return new(item.DocumentId, item.ChunkId, startByte, endByte, item.Text[originalStart..originalEnd]);
    }

    // The normalised text and, for each of its characters, the index of the original character it came from.
    // A whitespace run becomes one space that maps to the run's first character.
    private static (string Text, int[] Map) Normalise(string value)
    {
        var text = new StringBuilder(value.Length);
        var map = new List<int>(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (char.IsWhiteSpace(c))
            {
                if (text.Length > 0 && text[^1] == ' ' && map.Count > 0 && char.IsWhiteSpace(value[map[^1]])) continue;
                text.Append(' ');
                map.Add(i);
                continue;
            }
            text.Append(c switch
            {
                '“' or '”' or '„' or '‟' or '«' or '»' or '″' => '"',
                '‘' or '’' or '‚' or '‛' or '‹' or '›' or '′' => '\'',
                '‐' or '‑' or '‒' or '–' or '—' or '―' or '−' => '-',
                _ => c
            });
            map.Add(i);
        }
        return (text.ToString(), map.ToArray());
    }
}
