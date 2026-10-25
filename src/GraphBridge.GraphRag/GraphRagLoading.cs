using System.Text;

namespace GraphBridge.GraphRag;

/// <summary>Something to ingest: a file or a text. Create it with <see cref="GraphRagSources"/>.</summary>
public sealed record GraphRagSource
{
    internal GraphRagSource(string sourceUri, string mediaType, string? path, string? text)
        => (SourceUri, MediaType, Path, Text) = (sourceUri, mediaType, path, text);

    /// <summary>The normalised source URI; the document ID hashes it.</summary>
    public string SourceUri { get; }
    public string MediaType { get; }
    /// <summary>The full path of a file source; null for a text source.</summary>
    public string? Path { get; }
    /// <summary>What the source says about itself, stored on the document; set it with <c>with { Metadata = ... }</c>.</summary>
    public GraphRagSourceMetadata? Metadata { get; init; }
    internal string? Text { get; }
}

/// <summary>
/// A source's own metadata, never extracted: a title that replaces the loaded one, and the article ontology's published date
/// (ISO 8601, as given), publisher and URL.
/// </summary>
public sealed record GraphRagSourceMetadata(string? Title = null, string? Published = null, string? Publisher = null, string? Url = null);

/// <summary>Sources for ingestion. Folder enumeration is ordered by path, so runs are deterministic.</summary>
public static class GraphRagSources
{
    /// <summary>A file. <c>.md</c> and <c>.markdown</c> are <c>text/markdown</c>, <c>.txt</c> is <c>text/plain</c>; other
    /// extensions have no loader in the core and fail with <c>unsupported-media-type</c>.</summary>
    public static GraphRagSource File(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var full = System.IO.Path.GetFullPath(path);
        return new(new Uri(full).AbsoluteUri, MediaTypeOf(full), full, null);
    }

    public static IEnumerable<GraphRagSource> Folder(string path, string searchPattern = "*.md", bool recursive = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(searchPattern);
        return Directory.EnumerateFiles(System.IO.Path.GetFullPath(path), searchPattern,
                recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
            .Order(StringComparer.Ordinal).Select(File).ToArray();
    }

    /// <summary>A text held in memory. Its source URI is <c>text:</c> followed by <paramref name="id"/>.</summary>
    public static GraphRagSource Text(string id, string text, string mediaType = "text/plain")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaType);
        return new("text:" + id, mediaType, null, text);
    }

    private static string MediaTypeOf(string path) => System.IO.Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".md" or ".markdown" => "text/markdown",
        ".txt" => "text/plain",
        _ => "application/octet-stream"
    };
}

/// <summary>A loaded document: its UTF-8 bytes, without a byte-order mark. Positions count these bytes.</summary>
public sealed record GraphRagLoadedDocument(string SourceUri, string MediaType, string? Title, ReadOnlyMemory<byte> Utf8);

/// <summary>Reads a source into UTF-8 bytes. The core loads plain text and Markdown; adapters add other formats.</summary>
public interface IGraphRagDocumentLoader
{
    bool CanLoad(GraphRagSource source);
    Task<GraphRagLoadedDocument> LoadAsync(GraphRagSource source, CancellationToken cancellationToken = default);
}

/// <summary>A load that the toolkit refuses, with a stable error code recorded in the document's outcome.</summary>
public sealed class GraphRagLoadException(string errorCode, string message) : Exception(message)
{
    public string ErrorCode { get; } = errorCode;
}

/// <summary>Plain text and Markdown, from files or memory. Refuses invalid UTF-8 and removes a leading byte-order mark.</summary>
internal sealed class GraphRagTextLoader(int maxDocumentBytes) : IGraphRagDocumentLoader
{
    private static readonly UTF8Encoding Strict = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public bool CanLoad(GraphRagSource source) => source.MediaType is "text/plain" or "text/markdown";

    public async Task<GraphRagLoadedDocument> LoadAsync(GraphRagSource source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!CanLoad(source)) throw new GraphRagLoadException("unsupported-media-type", $"No loader for media type '{source.MediaType}'.");
        byte[] bytes;
        if (source.Path is { } path)
        {
            if (new FileInfo(path).Length > maxDocumentBytes)
                throw new GraphRagLoadException("document-too-large", $"The document is larger than {maxDocumentBytes} bytes.");
            bytes = await System.IO.File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            try { bytes = Strict.GetBytes(source.Text!); }
            catch (EncoderFallbackException) { throw new GraphRagLoadException("invalid-utf8", "The text contains an unpaired surrogate, so it has no UTF-8 form."); }
        }
        var start = bytes.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]) ? 3 : 0;
        var utf8 = bytes.AsMemory(start);
        if (utf8.Length > maxDocumentBytes)
            throw new GraphRagLoadException("document-too-large", $"The document is larger than {maxDocumentBytes} bytes.");
        string text;
        try { text = Strict.GetString(utf8.Span); }
        catch (DecoderFallbackException) { throw new GraphRagLoadException("invalid-utf8", "The document is not valid UTF-8."); }
        return new(source.SourceUri, source.MediaType, Title(source, text), utf8);
    }

    private static string? Title(GraphRagSource source, string text)
    {
        if (source.MediaType == "text/markdown")
            foreach (var line in text.AsSpan().EnumerateLines())
            {
                var trimmed = line.TrimStart(' ');
                if (trimmed.StartsWith("# ")) return trimmed[2..].Trim().TrimEnd('#').Trim().ToString();
            }
        return source.Path is { } path ? System.IO.Path.GetFileNameWithoutExtension(path) : source.SourceUri["text:".Length..];
    }
}
