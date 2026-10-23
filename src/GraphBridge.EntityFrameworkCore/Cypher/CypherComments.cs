using System.Text;

namespace GraphBridge.EntityFrameworkCore.Cypher;

/// <summary>Query tags as leading Cypher comments (ADR 0050).</summary>
internal static class CypherComments
{
    // Every line terminator Cypher or .NET recognizes: a tag can never end its comment and continue as Cypher.
    private static readonly string[] LineBreaks = ["\r\n", "\r", "\n", "\u0085", "\x2028", "\x2029"];

    /// <summary>The comment lines of the tags: one <c>// </c> line per line of each tag, in order.</summary>
    internal static IReadOnlyList<string> Lines(IEnumerable<string> tags)
        => tags.SelectMany(tag => tag.Split(LineBreaks, StringSplitOptions.None)).Select(line => "// " + line).ToArray();

    /// <summary>Writes the tags before the command, then an empty line, as relational EF writes its <c>--</c> comments.</summary>
    internal static string Prepend(IEnumerable<string> tags, string command)
    {
        var lines = Lines(tags);
        if (lines.Count == 0) return command;
        var text = new StringBuilder();
        foreach (var line in lines) text.Append(line).Append('\n');
        return text.Append('\n').Append(command).ToString();
    }
}
