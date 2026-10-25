using System.Globalization;
using System.Text;

namespace GraphBridge.GraphRag;

/// <summary>Chunk sizes. Byte budgets are exact and always enforced; token budgets need an <see cref="IGraphRagTokenCounter"/>.</summary>
public sealed class GraphRagChunkerOptions
{
    /// <summary>64 to 16,384 bytes of UTF-8 per chunk.</summary>
    public int MaxUtf8Bytes { get; set; } = 2_048;
    /// <summary>Enforced only when an <see cref="IGraphRagTokenCounter"/> is registered.</summary>
    public int? MaxTokens { get; set; }
    /// <summary>0 to 4 whole paragraphs that a chunk repeats from the end of the previous one.</summary>
    public int OverlapParagraphs { get; set; } = 1;

    internal void Validate()
    {
        if (MaxUtf8Bytes is < 64 or > 16_384) throw new ArgumentOutOfRangeException(nameof(MaxUtf8Bytes), "MaxUtf8Bytes must be 64 to 16,384.");
        if (MaxTokens is < 1) throw new ArgumentOutOfRangeException(nameof(MaxTokens), "MaxTokens must be positive.");
        if (OverlapParagraphs is < 0 or > 4) throw new ArgumentOutOfRangeException(nameof(OverlapParagraphs), "OverlapParagraphs must be 0 to 4.");
    }
}

/// <summary>Counts tokens for <see cref="GraphRagChunkerOptions.MaxTokens"/>; tokenizers are model-specific, so the application supplies one.</summary>
public interface IGraphRagTokenCounter
{
    int Count(string text);
}

/// <summary>Splits a loaded document into sections and chunks with exact byte positions.</summary>
public interface IGraphRagChunker
{
    GraphRagChunkPlan Chunk(GraphRagLoadedDocument document);
}

/// <summary>The core chunker, for adapters and applications that chunk text themselves, such as the DataIngestion adapter.</summary>
public static class GraphRagChunkers
{
    /// <summary>The structure-aware chunker ingestion uses: headings start sections, paragraphs are packed whole, positions are exact.</summary>
    public static IGraphRagChunker Create(GraphRagChunkerOptions? options = null, IGraphRagTokenCounter? counter = null)
    {
        options ??= new GraphRagChunkerOptions();
        options.Validate();
        return new GraphRagChunker(options, counter);
    }

    /// <summary>Names a chunker and its settings, as plans and settings fingerprints record it.</summary>
    public static string FingerprintOf(IGraphRagChunker chunker)
    {
        ArgumentNullException.ThrowIfNull(chunker);
        return chunker is GraphRagChunker core ? core.Fingerprint : chunker.GetType().FullName ?? "custom";
    }
}

/// <summary>What a chunker produced. <see cref="Fingerprint"/> names the chunker and its settings; chunk IDs and the run's
/// settings fingerprint include it, so a different chunking never reuses chunk IDs.</summary>
public sealed record GraphRagChunkPlan(IReadOnlyList<GraphRagSectionSpan> Sections, IReadOnlyList<GraphRagChunkSpan> Chunks, string Fingerprint);

/// <summary>A heading's section: from the heading to the next heading of the same or a higher level. <see cref="ParentOrder"/>
/// is the enclosing section's order, if any.</summary>
public sealed record GraphRagSectionSpan(int Order, int Level, string Title, int StartByte, int EndByte, int? ParentOrder);

/// <summary>A chunk's span, end exclusive, and the innermost section that contains it (null before the first heading).</summary>
public sealed record GraphRagChunkSpan(int Order, int StartByte, int EndByte, int? SectionOrder, int? TokenCount);

/// <summary>
/// The core chunker. Markdown headings (outside fenced code) start sections; chunks never cross a heading. Paragraphs are
/// packed whole up to the byte and token budgets; a paragraph that doesn't fit is split at line breaks, then at whitespace,
/// then at a code point boundary. Every byte of the document is in at least one chunk.
/// </summary>
internal sealed class GraphRagChunker(GraphRagChunkerOptions options, IGraphRagTokenCounter? counter = null) : IGraphRagChunker
{
    internal const string Version = "graphbridge-chunker-1";

    public GraphRagChunkPlan Chunk(GraphRagLoadedDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        options.Validate();
        var bytes = document.Utf8.Span;
        var lines = Lines(bytes);
        var markdown = document.MediaType == "text/markdown";
        var headings = markdown ? Headings(bytes, lines) : [];
        var sections = Sections(headings, bytes.Length);

        // Segments run from one heading line to the next, whatever its level; a chunk belongs to exactly one.
        var chunks = new List<GraphRagChunkSpan>();
        var starts = new List<int> { 0 };
        starts.AddRange(headings.Select(h => h.Start).Where(s => s > 0));
        starts.Add(bytes.Length);
        for (var i = 0; i + 1 < starts.Count; i++)
        {
            var (start, end) = (starts[i], starts[i + 1]);
            if (start == end) continue;
            var section = sections.LastOrDefault(s => s.StartByte <= start && start < s.EndByte);
            Pack(document.Utf8, Blocks(bytes, lines, start, end, markdown), section?.Order, chunks);
        }
        return new(sections, chunks, Fingerprint);
    }

    internal string Fingerprint => string.Join(';', Version, "bytes=" + options.MaxUtf8Bytes.ToString(CultureInfo.InvariantCulture),
        "tokens=" + (counter is null || options.MaxTokens is null ? "none" : options.MaxTokens.Value.ToString(CultureInfo.InvariantCulture) + "/" + counter.GetType().FullName),
        "overlap=" + options.OverlapParagraphs.ToString(CultureInfo.InvariantCulture));

    private void Pack(ReadOnlyMemory<byte> document, List<(int Start, int End)> blocks, int? section, List<GraphRagChunkSpan> chunks)
    {
        var pieces = blocks.SelectMany(block => Fit(document, block.Start, block.End)).ToList();
        var first = 0;
        while (first < pieces.Count)
        {
            var last = first;
            while (last + 1 < pieces.Count && Fits(document, pieces[first].Start, pieces[last + 1].End)) last++;
            var (start, end) = (pieces[first].Start, pieces[last].End);
            chunks.Add(new(chunks.Count, start, end, section, counter is null ? null : counter.Count(Text(document, start, end))));
            if (last + 1 >= pieces.Count) break;
            // Repeat up to OverlapParagraphs pieces, always moving forward and only while the overlap still fits a chunk.
            var next = last + 1;
            var overlap = Math.Min(options.OverlapParagraphs, last - first);
            while (overlap > 0 && !Fits(document, pieces[next - overlap].Start, pieces[next].End)) overlap--;
            first = next - overlap;
        }
    }

    // A piece that alone exceeds the budget is split at line breaks, then whitespace, then any code point boundary.
    private IEnumerable<(int Start, int End)> Fit(ReadOnlyMemory<byte> document, int start, int end)
    {
        if (Fits(document, start, end)) { yield return (start, end); yield break; }
        var bytes = document.Span;
        var cut = Cut(bytes, start, end, b => b == (byte)'\n') ?? Cut(bytes, start, end, b => b is (byte)' ' or (byte)'\t') ?? Boundary(bytes, start, end);
        // A single code point over the token budget can't be split further; it stays one piece.
        if (cut <= start || cut >= end) { yield return (start, end); yield break; }
        foreach (var piece in Fit(document, start, cut)) yield return piece;
        foreach (var piece in Fit(document, cut, end)) yield return piece;
    }

    // The last separator that leaves a fitting prefix, cutting after it.
    private int? Cut(ReadOnlySpan<byte> bytes, int start, int end, Func<byte, bool> separator)
    {
        for (var i = Math.Min(end - 1, start + options.MaxUtf8Bytes - 1); i > start; i--)
            if (separator(bytes[i - 1]) && Prefix(bytes, start, i)) return i;
        return null;
    }

    // The largest code point boundary within the budget, and never an empty piece.
    private int Boundary(ReadOnlySpan<byte> bytes, int start, int end)
    {
        var i = Math.Min(end - 1, start + options.MaxUtf8Bytes);
        while (i > start + 1 && ((bytes[i] & 0xC0) == 0x80 || !Prefix(bytes, start, i))) i--;
        while (i < end && (bytes[i] & 0xC0) == 0x80) i++;
        return i;
    }

    private bool Prefix(ReadOnlySpan<byte> bytes, int start, int end)
        => end - start <= options.MaxUtf8Bytes && (counter is null || options.MaxTokens is not { } max
            || counter.Count(Encoding.UTF8.GetString(bytes[start..end])) <= max);

    private bool Fits(ReadOnlyMemory<byte> document, int start, int end) => Prefix(document.Span, start, end);

    private static string Text(ReadOnlyMemory<byte> document, int start, int end) => Encoding.UTF8.GetString(document.Span[start..end]);

    private static List<(int Start, int End)> Lines(ReadOnlySpan<byte> bytes)
    {
        var lines = new List<(int, int)>();
        var start = 0;
        for (var i = 0; i < bytes.Length; i++)
            if (bytes[i] == (byte)'\n') { lines.Add((start, i + 1)); start = i + 1; }
        if (start < bytes.Length) lines.Add((start, bytes.Length));
        return lines;
    }

    // Paragraphs are runs of non-blank lines; the blank lines after one belong to it, so blocks cover the segment.
    // A fenced code block is one paragraph, blank lines included.
    private static List<(int Start, int End)> Blocks(ReadOnlySpan<byte> bytes, List<(int Start, int End)> lines, int start, int end, bool markdown)
    {
        var blocks = new List<(int Start, int End)>();
        var blockStart = start;
        var sawText = false;
        var afterBlank = false;
        var fenced = false;
        foreach (var (lineStart, lineEnd) in lines)
        {
            if (lineEnd <= start || lineStart >= end) continue;
            var line = bytes[lineStart..lineEnd];
            var blank = IsBlank(line);
            if (!blank && !fenced && afterBlank)
            {
                blocks.Add((blockStart, lineStart));
                blockStart = lineStart;
                afterBlank = false;
            }
            if (markdown && IsFence(line)) fenced = !fenced;
            if (blank) afterBlank = sawText && !fenced;
            else sawText = true;
        }
        if (blockStart < end) blocks.Add((blockStart, end));
        return blocks;
    }

    private static bool IsBlank(ReadOnlySpan<byte> line)
    {
        foreach (var b in line) if (b is not ((byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')) return false;
        return true;
    }

    private static bool IsFence(ReadOnlySpan<byte> line)
    {
        var trimmed = line.TrimStart((byte)' ');
        return line.Length - trimmed.Length <= 3 && (trimmed.StartsWith("```"u8) || trimmed.StartsWith("~~~"u8));
    }

    private static List<(int Start, int Level, string Title)> Headings(ReadOnlySpan<byte> bytes, List<(int Start, int End)> lines)
    {
        var headings = new List<(int, int, string)>();
        var fenced = false;
        foreach (var (start, end) in lines)
        {
            var line = bytes[start..end];
            if (IsFence(line)) { fenced = !fenced; continue; }
            if (fenced) continue;
            var trimmed = line.TrimStart((byte)' ');
            if (line.Length - trimmed.Length > 3) continue;
            var level = 0;
            while (level < trimmed.Length && trimmed[level] == (byte)'#') level++;
            if (level is < 1 or > 6 || level < trimmed.Length && trimmed[level] is not ((byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')) continue;
            var title = Encoding.UTF8.GetString(trimmed[level..]).Trim().TrimEnd('#').Trim();
            headings.Add((start, level, title));
        }
        return headings;
    }

    private static List<GraphRagSectionSpan> Sections(List<(int Start, int Level, string Title)> headings, int length)
    {
        var sections = new List<GraphRagSectionSpan>();
        for (var i = 0; i < headings.Count; i++)
        {
            var (start, level, title) = headings[i];
            var end = length;
            for (var j = i + 1; j < headings.Count; j++)
                if (headings[j].Level <= level) { end = headings[j].Start; break; }
            int? parent = null;
            for (var j = i - 1; j >= 0; j--)
                if (headings[j].Level < level && sections[j].EndByte >= end) { parent = j; break; }
            sections.Add(new(i, level, title, start, end, parent));
        }
        return sections;
    }
}
