using GraphBridge.Tooling.Diagnostics;

namespace GraphBridge.Samples.FluentCypher;

/// <summary>Display excerpts from the actual source embedded during this sample's build.</summary>
public static class FluentMovieSources
{
    public static IReadOnlyList<GraphQuerySource> Create()
    {
        using var stream = typeof(FluentMovieSources).Assembly.GetManifestResourceStream("GraphBridge.FluentMovieQueries.cs")
            ?? throw new InvalidOperationException("Embedded sample source is missing.");
        using var reader = new StreamReader(stream);
        var lines = reader.ReadToEnd().ReplaceLineEndings("\n").Split('\n');
        return Array.AsReadOnly(new[] { Excerpt("movies.coactors.v1", "Coactors"),
            Excerpt("movies.optional.v1", "OptionalMovies"), Excerpt("movies.shared-movies.v1", "SharedMovies") });

        GraphQuerySource Excerpt(string id, string method)
        {
            var start = Array.FindIndex(lines, l => l.Trim() == "// query-lab:start " + method);
            var end = Array.FindIndex(lines, l => l.Trim() == "// query-lab:end " + method);
            if (start < 0 || end <= start) throw new InvalidOperationException("Sample excerpt markers are invalid.");
            return new(id, string.Join("\n", lines[(start + 1)..end].Select(l => l.StartsWith("    ", StringComparison.Ordinal) ? l[4..] : l)));
        }
    }
}
