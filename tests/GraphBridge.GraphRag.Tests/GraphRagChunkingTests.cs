using System.Text;
using GraphBridge.GraphRag;

namespace GraphBridge.GraphRag.Tests;

/// <summary>Wave 1 (GR-03): loading and chunking with exact UTF-8 byte positions.</summary>
public sealed class GraphRagChunkingTests
{
    private const string Markdown = """
        # Graphs — an overview 🌍

        Graphs connect facts. Ünïcödé text keeps its bytes.

        ## Nodes

        A node holds properties.
        It can span lines.

        ```cypher
        # not a heading

        MATCH (n) RETURN n
        ```

        ## Edges

        Edges link nodes. 🔗

        # Appendix

        The end.
        """;

    [Fact]
    public void Every_chunk_is_exactly_its_bytes_and_together_they_cover_the_document()
    {
        var document = Load(Markdown, "text/markdown");
        var plan = new GraphRagChunker(new GraphRagChunkerOptions { MaxUtf8Bytes = 64, OverlapParagraphs = 1 }).Chunk(document);
        var bytes = document.Utf8.ToArray();
        Assert.NotEmpty(plan.Chunks);
        var covered = new bool[bytes.Length];
        foreach (var chunk in plan.Chunks)
        {
            Assert.True(chunk.EndByte - chunk.StartByte <= 64);
            Assert.True(Boundary(bytes, chunk.StartByte) && Boundary(bytes, chunk.EndByte));
            // Text decoded from the span re-encodes to the same bytes: nothing was normalised.
            var text = Encoding.UTF8.GetString(bytes, chunk.StartByte, chunk.EndByte - chunk.StartByte);
            Assert.Equal(bytes[chunk.StartByte..chunk.EndByte], Encoding.UTF8.GetBytes(text));
            for (var i = chunk.StartByte; i < chunk.EndByte; i++) covered[i] = true;
        }
        Assert.All(covered, Assert.True);
        Assert.Equal(Enumerable.Range(0, plan.Chunks.Count), plan.Chunks.Select(c => c.Order));
    }

    [Fact]
    public void Headings_outside_fences_make_nested_sections_and_chunks_never_cross_them()
    {
        var document = Load(Markdown, "text/markdown");
        var plan = new GraphRagChunker(new GraphRagChunkerOptions { MaxUtf8Bytes = 2048 }).Chunk(document);
        Assert.Equal(["Graphs — an overview 🌍", "Nodes", "Edges", "Appendix"], plan.Sections.Select(s => s.Title));
        Assert.Equal([1, 2, 2, 1], plan.Sections.Select(s => s.Level));
        Assert.Equal([null, 0, 0, null], plan.Sections.Select(s => s.ParentOrder));
        Assert.Equal(plan.Sections[3].StartByte, plan.Sections[0].EndByte);
        Assert.Equal(document.Utf8.Length, plan.Sections[3].EndByte);
        // One chunk per heading segment at this size, each in the innermost section.
        Assert.Equal([0, 1, 2, 3], plan.Chunks.Select(c => c.SectionOrder));
        foreach (var chunk in plan.Chunks)
        {
            var section = plan.Sections[chunk.SectionOrder!.Value];
            Assert.True(chunk.StartByte >= section.StartByte && chunk.EndByte <= section.EndByte);
        }
        var nodes = Text(document, plan.Chunks[1]);
        Assert.Contains("# not a heading", nodes);
        Assert.Contains("MATCH (n) RETURN n", nodes);
    }

    [Fact]
    public void Paragraphs_pack_whole_overlap_by_whole_paragraphs_and_long_ones_split_at_whitespace()
    {
        var paragraphs = Enumerable.Range(1, 6).Select(i => $"Paragraph {i} has some words.").ToArray();
        var document = Load(string.Join("\n\n", paragraphs), "text/plain");
        var plan = new GraphRagChunker(new GraphRagChunkerOptions { MaxUtf8Bytes = 70, OverlapParagraphs = 1 }).Chunk(document);
        Assert.Empty(plan.Sections);
        Assert.All(plan.Chunks, c => Assert.Null(c.SectionOrder));
        var texts = plan.Chunks.Select(c => Text(document, c)).ToArray();
        Assert.StartsWith("Paragraph 1", texts[0]);
        Assert.StartsWith("Paragraph 2", texts[1]);          // the overlap repeats the previous chunk's last paragraph
        Assert.All(texts, t => Assert.True(t.TrimEnd().EndsWith('.')));

        var none = new GraphRagChunker(new GraphRagChunkerOptions { MaxUtf8Bytes = 70, OverlapParagraphs = 0 }).Chunk(document);
        Assert.All(none.Chunks.Zip(none.Chunks.Skip(1)), pair => Assert.Equal(pair.First.EndByte, pair.Second.StartByte));

        var words = Load(string.Join(' ', Enumerable.Repeat("word", 60)), "text/plain");
        var split = new GraphRagChunker(new GraphRagChunkerOptions { MaxUtf8Bytes = 64 }).Chunk(words);
        Assert.True(split.Chunks.Count > 1);
        Assert.All(split.Chunks.Take(split.Chunks.Count - 1), c => Assert.EndsWith(" ", Text(words, c)));
    }

    [Fact]
    public void A_run_of_multibyte_characters_splits_only_at_code_point_boundaries()
    {
        var document = Load(string.Concat(Enumerable.Repeat("🌍é", 40)), "text/plain");
        var plan = new GraphRagChunker(new GraphRagChunkerOptions { MaxUtf8Bytes = 64, OverlapParagraphs = 0 }).Chunk(document);
        var bytes = document.Utf8.ToArray();
        Assert.True(plan.Chunks.Count > 1);
        Assert.All(plan.Chunks, c => Assert.True(Boundary(bytes, c.StartByte) && Boundary(bytes, c.EndByte) && c.EndByte - c.StartByte <= 64));
        Assert.Equal(bytes.Length, plan.Chunks[^1].EndByte);
    }

    [Fact]
    public void A_token_counter_adds_a_budget_and_counts_and_the_fingerprint_names_the_settings()
    {
        var document = Load(string.Join("\n\n", Enumerable.Range(1, 8).Select(i => $"one two three four {i}")), "text/plain");
        var counter = new WordCounter();
        var plan = new GraphRagChunker(new GraphRagChunkerOptions { MaxUtf8Bytes = 2048, MaxTokens = 12, OverlapParagraphs = 0 }, counter).Chunk(document);
        Assert.True(plan.Chunks.Count > 1);
        Assert.All(plan.Chunks, c => Assert.True(c.TokenCount <= 12));
        Assert.All(plan.Chunks, c => Assert.Equal(counter.Count(Text(document, c)), c.TokenCount));
        Assert.Contains("tokens=12/", plan.Fingerprint);
        Assert.NotEqual(plan.Fingerprint, new GraphRagChunker(new GraphRagChunkerOptions()).Chunk(document).Fingerprint);
        Assert.Null(new GraphRagChunker(new GraphRagChunkerOptions()).Chunk(document).Chunks[0].TokenCount);
        Assert.Empty(new GraphRagChunker(new GraphRagChunkerOptions()).Chunk(Load("", "text/markdown")).Chunks);
        Assert.Throws<ArgumentOutOfRangeException>(() => new GraphRagChunker(new GraphRagChunkerOptions { MaxUtf8Bytes = 63 }).Chunk(document));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GraphRagChunker(new GraphRagChunkerOptions { OverlapParagraphs = 5 }).Chunk(document));
    }

    [Fact]
    public async Task The_loader_removes_a_byte_order_mark_refuses_invalid_UTF8_and_names_the_document()
    {
        var folder = Directory.CreateTempSubdirectory("graphrag-loader-");
        try
        {
            var markdown = Path.Combine(folder.FullName, "guide.md");
            await File.WriteAllBytesAsync(markdown, [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("# Guide\n\nText.")]);
            var source = GraphRagSources.File(markdown);
            Assert.Equal(new Uri(markdown).AbsoluteUri, source.SourceUri);
            Assert.Equal("text/markdown", source.MediaType);
            var loader = new GraphRagTextLoader(1 << 20);
            var loaded = await loader.LoadAsync(source);
            Assert.Equal("# Guide\n\nText."u8.ToArray(), loaded.Utf8.ToArray());
            Assert.Equal("Guide", loaded.Title);

            var invalid = Path.Combine(folder.FullName, "broken.txt");
            await File.WriteAllBytesAsync(invalid, [0x61, 0xC3, 0x28]);
            Assert.Equal("invalid-utf8", (await Assert.ThrowsAsync<GraphRagLoadException>(() => loader.LoadAsync(GraphRagSources.File(invalid)))).ErrorCode);
            Assert.Equal("document-too-large", (await Assert.ThrowsAsync<GraphRagLoadException>(() => new GraphRagTextLoader(4).LoadAsync(source))).ErrorCode);
            var pdf = GraphRagSources.File(Path.Combine(folder.FullName, "paper.pdf"));
            Assert.False(loader.CanLoad(pdf));
            Assert.Equal("unsupported-media-type", (await Assert.ThrowsAsync<GraphRagLoadException>(() => loader.LoadAsync(pdf))).ErrorCode);
            Assert.Equal(["broken.txt", "guide.md"], GraphRagSources.Folder(folder.FullName, "*.*").Select(s => Path.GetFileName(s.Path!)));

            var text = GraphRagSources.Text("note-1", "Plain text.");
            Assert.Equal("text:note-1", text.SourceUri);
            Assert.Equal("note-1", (await loader.LoadAsync(text)).Title);
            Assert.Equal("invalid-utf8", (await Assert.ThrowsAsync<GraphRagLoadException>(() => loader.LoadAsync(GraphRagSources.Text("bad", "\ud800")))).ErrorCode);
        }
        finally { folder.Delete(recursive: true); }
    }

    private static GraphRagLoadedDocument Load(string text, string mediaType)
        => new("text:test", mediaType, null, Encoding.UTF8.GetBytes(text.ReplaceLineEndings("\n")));

    private static string Text(GraphRagLoadedDocument document, GraphRagChunkSpan chunk)
        => Encoding.UTF8.GetString(document.Utf8.Span[chunk.StartByte..chunk.EndByte]);

    private static bool Boundary(byte[] bytes, int position) => position == 0 || position == bytes.Length || (bytes[position] & 0xC0) != 0x80;

    private sealed class WordCounter : IGraphRagTokenCounter
    {
        public int Count(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
    }
}
