using System.Text;

namespace GraphBridge.GraphRag;

/// <summary>
/// A document whose chunks were decided elsewhere, such as by a DataIngestion pipeline (ADR 0052, section 8): its text and
/// each chunk's exact span. Ingest it with <see cref="IGraphRagIngestion.IngestChunkedAsync"/>; it is leased, embedded,
/// extracted and written like a loaded file. Sections come from the text's Markdown headings.
/// </summary>
public sealed class GraphRagChunkedDocument
{
    private GraphRagChunkedDocument(GraphRagSource source, string? title, ReadOnlyMemory<byte> utf8, GraphRagChunkPlan plan)
        => (Source, Title, Utf8, Plan) = (source, title, utf8, plan);

    public GraphRagSource Source { get; }
    public string? Title { get; }
    /// <summary>The text as UTF-8; chunk positions count these bytes.</summary>
    public ReadOnlyMemory<byte> Utf8 { get; }
    public GraphRagChunkPlan Plan { get; }

    /// <param name="source">Where the text came from; a text source made with <see cref="GraphRagSources.Text"/>.</param>
    /// <param name="spans">Each chunk's UTF-16 range [Start, End) in <paramref name="text"/>, in reading order; spans may overlap.</param>
    /// <param name="chunkerFingerprint">Names the chunker and its settings; it joins chunk IDs and the run's settings fingerprint.</param>
    public static GraphRagChunkedDocument Create(GraphRagSource source, string text, IReadOnlyList<(int Start, int End)> spans, string chunkerFingerprint,
        string? title = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(spans);
        ArgumentException.ThrowIfNullOrWhiteSpace(chunkerFingerprint);
        if (spans.Count == 0 && text.Length > 0) throw new ArgumentException("A document with text needs at least one chunk.", nameof(spans));
        var previous = 0;
        foreach (var (start, end) in spans)
        {
            if (start < 0 || end > text.Length || start >= end)
                throw new ArgumentException($"The span [{start}, {end}) isn't a non-empty range of the text.", nameof(spans));
            if (start < previous) throw new ArgumentException("Spans must be in reading order.", nameof(spans));
            if (char.IsLowSurrogate(text[start]) || end < text.Length && char.IsLowSurrogate(text[end]))
                throw new ArgumentException($"The span [{start}, {end}) splits a surrogate pair.", nameof(spans));
            previous = start;
        }

        byte[] utf8;
        try { utf8 = new UTF8Encoding(false, true).GetBytes(text); }
        catch (EncoderFallbackException) { throw new ArgumentException("The text contains an unpaired surrogate, so it has no UTF-8 form.", nameof(text)); }
        // Byte offset of every UTF-16 index, so spans keep code point boundaries.
        var offsets = new int[text.Length + 1];
        for (var i = 0; i < text.Length; i++)
            offsets[i + 1] = offsets[i] + (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]) ? 4
                : char.IsLowSurrogate(text[i]) ? 0 : text[i] < 0x80 ? 1 : text[i] < 0x800 ? 2 : 3);

        var loaded = new GraphRagLoadedDocument(source.SourceUri, source.MediaType, title, utf8);
        var sections = new GraphRagChunker(new GraphRagChunkerOptions()).Chunk(loaded).Sections;
        var chunks = spans.Select((span, order) =>
        {
            var (start, end) = (offsets[span.Start], offsets[span.End]);
            // The innermost section holding the chunk's start: the deepest one that contains it.
            var section = sections.Where(s => s.StartByte <= start && start < s.EndByte).OrderByDescending(s => s.Level).FirstOrDefault();
            return new GraphRagChunkSpan(order, start, end, section?.Order, null);
        }).ToArray();
        return new(source, title, utf8, new GraphRagChunkPlan(sections, chunks, chunkerFingerprint));
    }
}
