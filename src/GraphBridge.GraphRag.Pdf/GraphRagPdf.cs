using System.Text;
using GraphBridge.GraphRag;
using Microsoft.Extensions.DependencyInjection;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.PageSegmenter;
using UglyToad.PdfPig.DocumentLayoutAnalysis.ReadingOrderDetector;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;
using UglyToad.PdfPig.Exceptions;

namespace GraphBridge.GraphRag.Pdf;

/// <summary>Bounds for the PDF loader (ADR 0052, section 14).</summary>
public sealed class GraphRagPdfOptions
{
    /// <summary>1 KiB to 1 GiB, 64 MiB by default: the largest PDF file read. Its extracted text is bounded by the ingestion's <c>MaxDocumentBytes</c>.</summary>
    public long MaxFileBytes { get; set; } = 64L << 20;

    /// <summary>1 to 100,000, 5,000 by default: the most pages read; a PDF with more is refused as <c>document-too-large</c>.</summary>
    public int MaxPages { get; set; } = 5000;

    internal void Validate()
    {
        if (MaxFileBytes is < 1024 or > 1L << 30) throw new ArgumentOutOfRangeException(nameof(MaxFileBytes), "MaxFileBytes must be 1 KiB to 1 GiB.");
        if (MaxPages is < 1 or > 100_000) throw new ArgumentOutOfRangeException(nameof(MaxPages), "MaxPages must be 1 to 100,000.");
    }
}

public static class GraphRagPdfServiceCollectionExtensions
{
    /// <summary>
    /// Adds the PDF loader to the toolkit registered with <c>AddGraphRag</c>, so <c>GraphRagSources.File</c> and <c>.Folder</c>
    /// ingest <c>.pdf</c> files. A PDF's document is its text layer; citations name their pages.
    /// </summary>
    public static IServiceCollection AddGraphRagPdf(this IServiceCollection services, Action<GraphRagPdfOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = new GraphRagPdfOptions();
        configure?.Invoke(options);
        options.Validate();
        services.AddSingleton<IGraphRagDocumentLoader>(provider => new GraphRagPdfLoader(options,
            provider.GetService<GraphRagOptions>()?.Ingestion.MaxDocumentBytes ?? new GraphRagIngestionOptions().MaxDocumentBytes));
        return services;
    }
}

/// <summary>
/// Reads a PDF's text layer with PdfPig, page by page in reading order: words, then text blocks, one paragraph per block. A
/// blank line separates paragraphs and pages, and the page map records where each page starts. Refusals: <c>pdf-no-text</c>
/// when no page has text, as in a scan, which needs OCR first; <c>pdf-unreadable</c> when PdfPig can't open the file or read a
/// page of it, or it is encrypted; <c>document-too-large</c> for the file, its pages or its text.
/// </summary>
public sealed class GraphRagPdfLoader(GraphRagPdfOptions options, int maxDocumentBytes) : IGraphRagDocumentLoader
{
    public const string MediaType = "application/pdf";

    private readonly GraphRagPdfOptions options = Validated(options, maxDocumentBytes);

    private static GraphRagPdfOptions Validated(GraphRagPdfOptions options, int maxDocumentBytes)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxDocumentBytes);
        return options;
    }

    public bool CanLoad(GraphRagSource source) => source is { MediaType: MediaType, Path: not null };

    public async Task<GraphRagLoadedDocument> LoadAsync(GraphRagSource source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!CanLoad(source)) throw new GraphRagLoadException("unsupported-media-type", $"The PDF loader reads PDF files, not '{source.MediaType}'.");
        if (new FileInfo(source.Path!).Length > options.MaxFileBytes)
            throw new GraphRagLoadException("document-too-large", $"The PDF file is larger than {options.MaxFileBytes} bytes.");
        var bytes = await File.ReadAllBytesAsync(source.Path!, cancellationToken).ConfigureAwait(false);

        PdfDocument document;
        try { document = PdfDocument.Open(bytes); }
        catch (PdfDocumentEncryptedException) { throw new GraphRagLoadException("pdf-unreadable", "The PDF is encrypted."); }
        catch (Exception error) when (error is not OperationCanceledException) { throw new GraphRagLoadException("pdf-unreadable", "PdfPig could not open the PDF."); }
        using (document)
        {
            if (document.NumberOfPages > options.MaxPages)
                throw new GraphRagLoadException("document-too-large", $"The PDF has more than {options.MaxPages} pages.");
            var text = new StringBuilder();
            var starts = new List<int>(document.NumberOfPages);
            var length = 0;
            var letters = 0;
            // PdfPig parses a page's content as it is read: a page it can't read refuses the document, not the ingestion run.
            for (var number = 1; number <= document.NumberOfPages; number++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string[] paragraphs;
                try
                {
                    var page = document.GetPage(number);
                    letters += page.Letters.Count;
                    var words = NearestNeighbourWordExtractor.Instance.GetWords(page.Letters);
                    var blocks = UnsupervisedReadingOrderDetector.Instance.Get(DocstrumBoundingBoxes.Instance.GetBlocks(words));
                    paragraphs = [.. blocks.Select(b => string.Join('\n', b.TextLines.Select(l => l.Text.Trim())).Trim()).Where(p => p.Length > 0)];
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    throw new GraphRagLoadException("pdf-unreadable", $"PdfPig could not read page {number} of the PDF.");
                }
                if (paragraphs.Length > 0 && text.Length > 0) length += Append(text, "\n\n");
                starts.Add(length);
                for (var i = 0; i < paragraphs.Length; i++)
                {
                    if (i > 0) length += Append(text, "\n\n");
                    length += Append(text, paragraphs[i]);
                }
                if (length > maxDocumentBytes)
                    throw new GraphRagLoadException("document-too-large", $"The PDF's text is larger than {maxDocumentBytes} bytes.");
            }
            if (letters == 0 || length == 0)
                throw new GraphRagLoadException("pdf-no-text", "The PDF has no text layer, as in a scan; it needs OCR first.");
            string? given;
            try { given = document.Information.Title; }
            catch (Exception error) when (error is not OperationCanceledException) { given = null; }
            var title = given is { Length: > 0 } && !string.IsNullOrWhiteSpace(given) ? given.Trim() : Path.GetFileNameWithoutExtension(source.Path);
            return new(source.SourceUri, MediaType, title, Encoding.UTF8.GetBytes(text.ToString())) { PageStarts = starts };
        }

        static int Append(StringBuilder text, string value)
        {
            text.Append(value);
            return Encoding.UTF8.GetByteCount(value);
        }
    }
}
