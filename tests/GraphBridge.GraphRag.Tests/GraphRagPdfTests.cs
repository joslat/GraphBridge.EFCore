using System.Text;
using GraphBridge.GraphRag;
using GraphBridge.GraphRag.Pdf;
using Microsoft.Extensions.DependencyInjection;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace GraphBridge.GraphRag.Tests;

/// <summary>GR-53 (ADR 0052, section 14): the PDF loader and page-aware citations. The PDFs are built here with PdfPig.</summary>
public sealed class GraphRagPdfTests : IDisposable
{
    private readonly DirectoryInfo _folder = Directory.CreateTempSubdirectory("graphrag-pdf-");

    public void Dispose() => _folder.Delete(recursive: true);

    /// <summary>A PDF with one page per entry, each line of a page drawn 120 points below the last, so each is its own block.</summary>
    internal static byte[] Build(string? title, params string[][] pages)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        foreach (var lines in pages)
        {
            var page = builder.AddPage(PageSize.A4);
            for (var i = 0; i < lines.Length; i++) page.AddText(lines[i], 11, new PdfPoint(40, 760 - 120 * i), font);
        }
        if (title is not null) builder.DocumentInformation.Title = title;
        return builder.Build();
    }

    private string Write(string name, byte[] bytes)
    {
        var path = Path.Combine(_folder.FullName, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static GraphRagPdfLoader Loader(int maxDocumentBytes = 1 << 20) => new(new GraphRagPdfOptions(), maxDocumentBytes);

    [Fact]
    public async Task A_PDF_loads_its_text_page_by_page_with_a_page_map_and_its_title()
    {
        var path = Write("mission.pdf", Build("Mission notes",
            ["The crew boarded at dawn.", "The engines lit at noon."],
            ["The capsule reached orbit."],
            ["Splashdown came ten days later."]));
        var source = GraphRagSources.File(path);
        Assert.Equal("application/pdf", source.MediaType);
        Assert.True(Loader().CanLoad(source));

        var loaded = await Loader().LoadAsync(source);
        var text = Encoding.UTF8.GetString(loaded.Utf8.Span);
        Assert.Equal("The crew boarded at dawn.\n\nThe engines lit at noon.\n\nThe capsule reached orbit.\n\nSplashdown came ten days later.", text);
        Assert.Equal(("Mission notes", "application/pdf"), (loaded.Title, loaded.MediaType));
        var starts = Assert.IsAssignableFrom<IReadOnlyList<int>>(loaded.PageStarts);
        Assert.Equal(3, starts.Count);
        Assert.Equal(0, starts[0]);
        Assert.Equal("The capsule", Encoding.UTF8.GetString(loaded.Utf8.Span[starts[1]..(starts[1] + 11)]));
        Assert.Equal("Splashdown", Encoding.UTF8.GetString(loaded.Utf8.Span[starts[2]..(starts[2] + 10)]));

        // Without a title in its information dictionary, the file name.
        var untitled = await Loader().LoadAsync(GraphRagSources.File(Write("untitled-report.pdf", Build(null, ["Only one page of text here."]))));
        Assert.Equal("untitled-report", untitled.Title);
    }

    [Fact]
    public async Task A_scan_an_unreadable_file_and_a_PDF_over_the_limits_are_refused_with_their_codes()
    {
        var scan = GraphRagSources.File(Write("scan.pdf", Build(null, [], [])));
        Assert.Equal("pdf-no-text", (await Assert.ThrowsAsync<GraphRagLoadException>(() => Loader().LoadAsync(scan))).ErrorCode);
        var broken = GraphRagSources.File(Write("broken.pdf", "%PDF-1.7 not really a PDF"u8.ToArray()));
        Assert.Equal("pdf-unreadable", (await Assert.ThrowsAsync<GraphRagLoadException>(() => Loader().LoadAsync(broken))).ErrorCode);
        var large = GraphRagSources.File(Write("large.pdf", Build(null, [new string('x', 60)], [new string('y', 60)])));
        Assert.Equal("document-too-large", (await Assert.ThrowsAsync<GraphRagLoadException>(() => Loader(maxDocumentBytes: 64).LoadAsync(large))).ErrorCode);
        Assert.Equal("document-too-large", (await Assert.ThrowsAsync<GraphRagLoadException>(
            () => new GraphRagPdfLoader(new GraphRagPdfOptions { MaxFileBytes = 1024 }, 1 << 20).LoadAsync(large))).ErrorCode);
        Assert.False(Loader().CanLoad(GraphRagSources.Text("note", "Not a PDF.")));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ServiceCollection().AddGraphRagPdf(o => o.MaxFileBytes = 10));
        // GR-69: the page bound, and options checked however the loader is made.
        var pages = GraphRagSources.File(Write("pages.pdf", Build(null, ["One."], ["Two."], ["Three."])));
        Assert.Equal("document-too-large", (await Assert.ThrowsAsync<GraphRagLoadException>(
            () => new GraphRagPdfLoader(new GraphRagPdfOptions { MaxPages = 2 }, 1 << 20).LoadAsync(pages))).ErrorCode);
        Assert.Throws<ArgumentOutOfRangeException>(() => new GraphRagPdfLoader(new GraphRagPdfOptions { MaxPages = 0 }, 1 << 20));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GraphRagPdfLoader(new GraphRagPdfOptions(), 0));
    }

    [Fact]
    public async Task A_PDF_that_opens_but_has_a_page_PdfPig_cannot_read_is_refused_as_unreadable()
    {
        // GR-69: the first page's contents point at the catalog, a dictionary, instead of a stream; the object number keeps its
        // digits, so the cross-reference table still holds and the file opens.
        var pdf = Encoding.Latin1.GetString(Build(null, ["The crew boarded at dawn."], ["The capsule reached orbit."]));
        var catalog = System.Text.RegularExpressions.Regex.Match(pdf, @"(\d+) 0 obj\s*<<\s*/Type\s*/Catalog").Groups[1].Value;
        var contents = System.Text.RegularExpressions.Regex.Match(pdf, @"/Contents (\d+) 0 R");
        Assert.True(catalog.Length > 0 && contents.Success && contents.Groups[1].Value.Length == catalog.Length, "The built PDF has the expected objects.");
        var broken = pdf[..contents.Groups[1].Index] + catalog + pdf[(contents.Groups[1].Index + catalog.Length)..];
        var source = GraphRagSources.File(Write("damaged-page.pdf", Encoding.Latin1.GetBytes(broken)));
        var refused = await Assert.ThrowsAsync<GraphRagLoadException>(() => Loader().LoadAsync(source));
        Assert.Equal("pdf-unreadable", refused.ErrorCode);
    }

    [Fact]
    public void Pages_follow_an_item_and_a_citation_names_the_pages_of_its_first_and_last_bytes()
    {
        int[] starts = [0, 100, 100, 248];   // page 3 starts where empty page 2 does; page 4 at "Splashdown" below
        Assert.Equal([new GraphRagPage(1, 0)], GraphRagPages.Spanning(starts, 10, 90));
        Assert.Equal([new GraphRagPage(1, 0), new GraphRagPage(3, 100)], GraphRagPages.Spanning(starts, 50, 150));
        Assert.Equal([new GraphRagPage(3, 100), new GraphRagPage(4, 248)], GraphRagPages.Spanning(starts, 120, 300));
        Assert.Equal([0, 100, 100, 248], GraphRagPages.Parse("0,100,100,248"));

        const string text = "Orbit came first. Splashdown came later.";
        var item = new GraphRagEvidenceItem("chunk", "document", text, 230, 230 + text.Length, 1, []) { Pages = GraphRagPages.Spanning(starts, 230, 230 + text.Length) };
        var early = GraphRagCitationVerifier.Locate(item, "Orbit came first.")!;
        Assert.Equal((3, 3), (early.Page, early.EndPage));
        var spanning = GraphRagCitationVerifier.Locate(item, "first. Splashdown")!;
        Assert.Equal((3, 4), (spanning.Page, spanning.EndPage));
        var late = GraphRagCitationVerifier.Locate(item, "Splashdown came later.")!;
        Assert.Equal((4, 4), (late.Page, late.EndPage));
        Assert.Null(GraphRagCitationVerifier.Locate(item with { Pages = null }, "Orbit came first.")!.Page);
    }
}
