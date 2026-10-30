using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.DataIngestion;

namespace GraphBridge.GraphRag.DataIngestion;

// ADR 0052, section 8: DataIngestion documents and chunks become toolkit documents with exact chunk positions. A document's
// text is its reader's Markdown; positions are UTF-16 offsets into that text until GraphRagChunkedDocument turns them into bytes.

/// <summary>The text the adapter stores for a DataIngestion document, and the metadata keys of positioned chunks.</summary>
public static class GraphRagIngestionText
{
    /// <summary>A chunk's start in the document's text, as a UTF-16 offset; set by <see cref="GraphRagSpanChunker"/>.</summary>
    public const string StartKey = "graphbridge.start";
    /// <summary>A chunk's end, exclusive.</summary>
    public const string EndKey = "graphbridge.end";
    /// <summary>The chunker's fingerprint.</summary>
    public const string ChunkerKey = "graphbridge.chunker";

    /// <summary>The document's elements as Markdown, in reading order, separated by blank lines, with <c>\n</c> line breaks.</summary>
    public static string Of(IngestionDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return string.Join("\n\n", document.EnumerateContent().Select(e => (e.GetMarkdown() ?? "").ReplaceLineEndings("\n").Trim('\n'))
            .Where(markdown => markdown.Trim().Length > 0));
    }

    /// <summary>The first heading's text, else the identifier's file name.</summary>
    public static string TitleOf(IngestionDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        // A header's Text may be unset; its Markdown then gives the title without the leading hashes.
        var heading = document.EnumerateContent().OfType<IngestionDocumentHeader>()
            .Select(h => (h.Text ?? h.GetMarkdown() ?? "").Trim().TrimStart('#').Trim()).FirstOrDefault(t => t.Length > 0);
        return heading ?? Path.GetFileNameWithoutExtension(document.Identifier);
    }
}

/// <summary>
/// An <see cref="IngestionChunker{T}"/> that chunks a document's text with the toolkit's own chunker, so every chunk carries
/// its exact span in metadata and <see cref="GraphBridgeChunkWriter"/> stores it without recovery.
/// </summary>
public sealed class GraphRagSpanChunker(GraphRagChunkerOptions? options = null, IGraphRagTokenCounter? counter = null) : IngestionChunker<string>
{
    internal const string Version = "graphbridge-span-chunker-1";
    private readonly IGraphRagChunker _chunker = GraphRagChunkers.Create(options, counter);

    /// <summary>The adapter's version and the core chunker's settings.</summary>
    public string Fingerprint => Version + ";" + GraphRagChunkers.FingerprintOf(_chunker);

    public override async IAsyncEnumerable<IngestionChunk<string>> ProcessAsync(IngestionDocument document,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        await Task.Yield();
        var text = GraphRagIngestionText.Of(document);
        var utf8 = Encoding.UTF8.GetBytes(text);
        var plan = _chunker.Chunk(new GraphRagLoadedDocument("text:" + document.Identifier, "text/markdown", null, utf8));
        var characters = new Utf16Offsets(text);
        foreach (var span in plan.Chunks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (start, end) = (characters.Of(span.StartByte), characters.Of(span.EndByte));
            var section = span.SectionOrder is { } order ? plan.Sections[order].Title : null;
            var chunk = new IngestionChunk<string>(text[start..end], document, section ?? "");
            chunk.Metadata[GraphRagIngestionText.StartKey] = start;
            chunk.Metadata[GraphRagIngestionText.EndKey] = end;
            chunk.Metadata[GraphRagIngestionText.ChunkerKey] = Fingerprint;
            yield return chunk;
        }
    }

    // UTF-16 offset of each UTF-8 byte offset at a code point boundary.
    private sealed class Utf16Offsets
    {
        private readonly Dictionary<int, int> _offsets = new() { [0] = 0 };

        public Utf16Offsets(string text)
        {
            var bytes = 0;
            for (var i = 0; i < text.Length; i++)
            {
                var pair = char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]);
                bytes += pair ? 4 : text[i] < 0x80 ? 1 : text[i] < 0x800 ? 2 : 3;
                if (pair) i++;
                _offsets[bytes] = i + 1;
            }
        }

        public int Of(int bytes) => _offsets[bytes];
    }
}

/// <summary>A chunk the writer couldn't place in its document's text; nothing of that document was written.</summary>
public sealed class GraphRagChunkPlacementException(string documentIdentifier, int chunk)
    : Exception($"Chunk {chunk} (zero-based) of '{documentIdentifier}' isn't found in the document's text, so the document wasn't written. Use GraphRagSpanChunker, or a chunker that keeps the reader's text.")
{
    public string ErrorCode => "position-not-recovered";
    public string DocumentIdentifier { get; } = documentIdentifier;
    public int Chunk { get; } = chunk;
}

/// <summary>Settings for <see cref="GraphBridgeChunkWriter"/>.</summary>
public sealed class GraphBridgeChunkWriterOptions
{
    /// <summary>
    /// Names the pipeline's chunker and its settings, such as <c>HeaderChunker;512</c>, for chunks without
    /// <see cref="GraphRagSpanChunker"/>'s metadata. It joins the settings fingerprint, so changing it re-ingests.
    /// </summary>
    public string ChunkerName { get; set; } = "dataingestion";

    /// <summary>
    /// Where the reader's text came from when it converts the source, such as <c>markitdown 0.1.9</c> for an Office document
    /// (ADR 0052, section 21). Every document the writer stores then carries it, and so does every citation from it. Null: the
    /// reader's text is the source's own.
    /// </summary>
    public string? TextOrigin { get; set; }
}

/// <summary>
/// An <see cref="IngestionChunkWriter{T}"/> that ingests each document's chunks through the toolkit (ADR 0052, section 8): leased,
/// skipped when unchanged, embedded with the toolkit's generator, extracted when an ontology is mapped, one transaction per
/// document. Every chunk keeps its exact span in the document's text; a chunk that can't be placed fails its document.
/// </summary>
public sealed class GraphBridgeChunkWriter(IGraphRagIngestion ingestion, GraphBridgeChunkWriterOptions? options = null) : IngestionChunkWriter<string>
{
    internal const string Version = "graphbridge-dataingestion-1";
    private readonly List<GraphRagIngestionReceipt> _receipts = [];
    private readonly GraphBridgeChunkWriterOptions _options = options ?? new GraphBridgeChunkWriterOptions();

    /// <summary>One receipt per document written, in order.</summary>
    public IReadOnlyList<GraphRagIngestionReceipt> Receipts
    {
        get { lock (_receipts) return _receipts.ToArray(); }
    }

    public override async Task WriteAsync(IAsyncEnumerable<IngestionChunk<string>> chunks, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        var received = new List<IngestionChunk<string>>();
        await foreach (var chunk in chunks.WithCancellation(cancellationToken).ConfigureAwait(false)) received.Add(chunk);
        foreach (var group in received.GroupBy(c => c.Document))
        {
            var document = group.Key;
            var text = GraphRagIngestionText.Of(document);
            var list = group.ToList();
            var spans = SpanRecovery.Place(text, list, document.Identifier);
            var fingerprint = list.Select(c => c.HasMetadata && c.Metadata.TryGetValue(GraphRagIngestionText.ChunkerKey, out var value) ? value as string : null)
                .FirstOrDefault(f => f is not null) ?? Version + ";" + _options.ChunkerName;
            var source = GraphRagSources.Text(document.Identifier, text, "text/markdown");
            if (_options.TextOrigin is { } origin) source = source with { Metadata = new GraphRagSourceMetadata(TextOrigin: origin) };
            var chunked = GraphRagChunkedDocument.Create(source, text, spans, fingerprint, GraphRagIngestionText.TitleOf(document));
            var receipt = await ingestion.IngestChunkedAsync([chunked], cancellationToken).ConfigureAwait(false);
            lock (_receipts) _receipts.Add(receipt);
            if (receipt.Documents.SingleOrDefault() is { Status: GraphRagDocumentStatus.Failed } failed)
                throw new InvalidOperationException($"'{document.Identifier}' wasn't written: {failed.ErrorCode}.");
        }
    }
}

/// <summary>
/// Places pipeline chunks in their document's text, in order (ADR 0052, section 8; spike d): the span from metadata when the
/// chunker recorded one; otherwise the content without a header chunker's heading-path prefix, byte-exact after the previous
/// chunk's start, then with whitespace runs normalised.
/// </summary>
internal static class SpanRecovery
{
    public static IReadOnlyList<(int Start, int End)> Place(string text, IReadOnlyList<IngestionChunk<string>> chunks, string identifier)
    {
        var spans = new List<(int Start, int End)>();
        var locator = new GraphRagTextLocator(text);
        var from = 0;
        for (var i = 0; i < chunks.Count; i++)
        {
            var chunk = chunks[i];
            if (chunk.HasMetadata && chunk.Metadata.TryGetValue(GraphRagIngestionText.StartKey, out var s) && s is int start
                && chunk.Metadata.TryGetValue(GraphRagIngestionText.EndKey, out var e) && e is int end && 0 <= start && start < end && end <= text.Length)
            {
                spans.Add((start, end));
                from = start;
                continue;
            }
            var content = Content(chunk);
            if (content.Length == 0) continue;
            var span = locator.Find(content, from) ?? throw new GraphRagChunkPlacementException(identifier, i);
            spans.Add(span);
            from = span.Start;
        }
        return spans;
    }

    // A header chunker starts the content with the heading path (the chunk's context) and a line break.
    private static string Content(IngestionChunk<string> chunk)
    {
        var content = (chunk.Content ?? "").ReplaceLineEndings("\n");
        if (!string.IsNullOrEmpty(chunk.Context) && content.StartsWith(chunk.Context + "\n", StringComparison.Ordinal))
            content = content[(chunk.Context.Length + 1)..];
        return content.Trim();
    }
}
