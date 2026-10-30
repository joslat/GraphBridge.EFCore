using System.Text;

namespace GraphBridge.GraphRag;

/// <summary>
/// Finds a passage in a document's text from a position on (ADR 0052, sections 8 and 13): byte-exact first, then with every
/// whitespace run as one space. Used to place pipeline chunks (DataIngestion) and adopted chunks in their sources. Positions
/// are UTF-16 indexes into the text.
/// </summary>
internal sealed class GraphRagTextLocator(string text)
{
    private Normalized? _normalized;

    public (int Start, int End)? Find(string content, int from)
    {
        if (content.Length == 0) return null;
        var found = text.IndexOf(content, from, StringComparison.Ordinal);
        if (found >= 0) return (found, found + content.Length);
        _normalized ??= new Normalized(text);
        return _normalized.Find(content, from);
    }

    /// <summary>The UTF-8 byte offset of a UTF-16 index in the text.</summary>
    public int ByteOffset(int index) => Encoding.UTF8.GetByteCount(text.AsSpan(0, index));

    // The text with every whitespace run as one space, and for each character the original index it came from.
    private sealed class Normalized
    {
        private readonly string _text;
        private readonly int[] _map;

        public Normalized(string text) => (_text, _map) = Normalize(text);

        public (int Start, int End)? Find(string content, int from)
        {
            var (needle, _) = Normalize(content);
            needle = needle.Trim(' ');
            if (needle.Length == 0) return null;
            var start = Array.FindIndex(_map, m => m >= from);
            if (start < 0) return null;
            var found = _text.IndexOf(needle, start, StringComparison.Ordinal);
            if (found < 0) return null;
            return (_map[found], _map[found + needle.Length - 1] + 1);
        }

        private static (string Text, int[] Map) Normalize(string value)
        {
            var text = new StringBuilder(value.Length);
            var map = new List<int>(value.Length);
            for (var i = 0; i < value.Length; i++)
            {
                if (char.IsWhiteSpace(value[i]))
                {
                    if (text.Length > 0 && text[^1] == ' ') continue;
                    text.Append(' ');
                }
                else text.Append(value[i]);
                map.Add(i);
            }
            return (text.ToString(), map.ToArray());
        }
    }
}
