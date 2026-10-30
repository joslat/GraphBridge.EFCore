using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;

namespace GraphBridge.Samples.GraphRagArticles;

/// <summary>One of NASA's Artemis II news releases the sample uses.</summary>
public sealed record NasaRelease(string File, string Release, string Title, string Published, string Url);

/// <summary>
/// NASA's Artemis II news releases, from launch to splashdown (GraphRAG v2, GR-58; approved as decision D8 on 26 October
/// 2026). NASA material is generally not subject to copyright in the United States; NASA's media guidelines ask that AI
/// output is never attributed to NASA or presented as reviewed by it. The sample's answers come from your model, and only
/// the quotes they cite are NASA's words.
/// </summary>
public static partial class Nasa
{
    public const string Publisher = "NASA";

    public static IReadOnlyList<NasaRelease> Releases { get; } =
    [
        new("26-026-liftoff.md", "RELEASE26-026", "Liftoff! NASA Launches Astronauts on Historic Artemis Moon Mission", "2026-04-01",
            "https://www.nasa.gov/news-release/liftoff-nasa-launches-astronauts-on-historic-artemis-moon-mission/"),
        new("26-027-leaves-earth-orbit.md", "RELEASE26-027", "NASA’s Artemis II Mission Leaves Earth Orbit for Flight around Moon", "2026-04-02",
            "https://www.nasa.gov/news-release/nasas-artemis-ii-mission-leaves-earth-orbit-for-flight-around-moon/"),
        new("26-028-distance-record.md", "RELEASE26-028", "NASA’s Artemis II Crew Eclipses Record for Farthest Human Spaceflight", "2026-04-06",
            "https://www.nasa.gov/news-release/nasas-artemis-ii-crew-eclipses-record-for-farthest-human-spaceflight/"),
        new("26-029-flyby-photos.md", "RELEASE26-029", "NASA’s Artemis II Crew Beams Official Moon Flyby Photos to Earth", "2026-04-07",
            "https://www.nasa.gov/news-release/nasas-artemis-ii-crew-beams-official-moon-flyby-photos-to-earth/"),
        new("26-030-splashdown.md", "RELEASE26-030", "NASA Welcomes Record-Setting Artemis II Moonfarers Back to Earth", "2026-04-10",
            "https://www.nasa.gov/news-release/nasa-welcomes-record-setting-artemis-ii-moonfarers-back-to-earth/")
    ];

    /// <summary>The excerpts shipped with the sample: each release's opening paragraphs, made with <c>fetch data/nasa/articles --excerpt 6</c>.</summary>
    public static string Excerpts => Path.Combine(AppContext.BaseDirectory, "data", "nasa", "articles");

    /// <summary>
    /// Downloads each release, keeps its text as Markdown in <paramref name="folder"/>, and writes <c>articles.json</c> next to
    /// the folder with each release's title, date, publisher and URL, which <c>ingest</c> stores as the article's own metadata.
    /// With <paramref name="paragraphs"/>, keeps only that many opening paragraphs.
    /// </summary>
    public static async Task<IReadOnlyList<(NasaRelease Release, int Bytes, string Sha256)>> FetchAsync(string folder, int? paragraphs, CancellationToken cancellationToken)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(1) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("GraphBridge-sample/0.1 (+https://github.com/joslat/GraphBridge.EFCore)");
        Directory.CreateDirectory(folder);
        var fetched = new List<(NasaRelease, int, string)>();
        foreach (var release in Releases)
        {
            var html = await http.GetStringAsync(release.Url, cancellationToken).ConfigureAwait(false);
            var bytes = Encoding.UTF8.GetBytes(Extract(html, release.Title, paragraphs));
            await File.WriteAllBytesAsync(Path.Combine(folder, release.File), bytes, cancellationToken).ConfigureAwait(false);
            fetched.Add((release, bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes))));
        }
        var index = Releases.Select(r => new { file = r.File, title = r.Title, published = r.Published, publisher = Publisher, url = r.Url, release = r.Release });
        var parent = Path.GetDirectoryName(Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar))!;
        await File.WriteAllTextAsync(Path.Combine(parent, "articles.json"),
            JsonSerializer.Serialize(index, new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }).ReplaceLineEndings("\n") + "\n",
            cancellationToken).ConfigureAwait(false);
        return fetched;
    }

    /// <summary>
    /// The release's text: its headings and paragraphs in order, under the title. Image captions are left out, and so is
    /// everything after the <c>-end-</c> line, where the media contacts are.
    /// </summary>
    public static string Extract(string html, string title, int? paragraphs = null)
    {
        var page = new HtmlParser().ParseDocument(html);
        var body = page.QuerySelector("div.entry-content") ?? throw new InvalidDataException("The page has no article body (div.entry-content).");
        var text = new StringBuilder("# ").Append(title).Append("\n\n");
        var kept = 0;
        foreach (var element in body.QuerySelectorAll("p, h2, h3, h4"))
        {
            if (element.Closest("figure") is not null || element.Closest("figcaption") is not null) continue;
            var line = Spaces().Replace(element.TextContent, " ").Trim();
            if (line.Length == 0) continue;
            if (line == "-end-") break;
            if (element.LocalName == "p")
            {
                if (paragraphs is { } limit && kept == limit) break;
                kept++;
                text.Append(line);
            }
            else text.Append('#', element.LocalName[1] - '0').Append(' ').Append(line);
            text.Append("\n\n");
        }
        return text.ToString().TrimEnd('\n') + "\n";
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
