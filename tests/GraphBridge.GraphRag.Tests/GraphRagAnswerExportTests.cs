using System.Text;
using System.Text.Json.Nodes;
using GraphBridge.GraphRag;

namespace GraphBridge.GraphRag.Tests;

/// <summary>GR-14 (ADR 0052, section 17): one answer as one JSON document, read back exactly and checked without the database.</summary>
public sealed class GraphRagAnswerExportTests
{
    private const string Passage = "Alice was beginning to get very tired of sitting by her sister on the bank. “What is the use of a book,” thought Alice.";
    private static readonly DateTimeOffset Exported = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    private static GraphRagAnswer Answer(string? quote = null)
    {
        var start = 1_000;
        var item = new GraphRagEvidenceItem("chunk-1", "doc-1", Passage, start, start + Encoding.UTF8.GetByteCount(Passage), 0.031,
            [new(GraphRagRoutes.Vector, 1, 0.92, 0.0164), new(GraphRagRoutes.FullText, 2, 3.5, 0.0161)])
        {
            Pages = [new(3, 900), new(4, start + 40)]
        };
        var adopted = new GraphRagEvidenceItem("chunk-2", "doc-2", "a passage another tool wrote", 0, 28, 0.016, [new(GraphRagRoutes.Vector, 2, 0.88, 0.0161)])
        {
            Grounding = GraphRagGrounding.Chunk
        };
        var offset = Encoding.UTF8.GetByteCount(Passage[..Passage.IndexOf("What is", StringComparison.Ordinal)]);
        var words = "What is the use of a book";
        var evidence = new GraphRagEvidence("What did Alice think of her sister's book?", [item, adopted],
            [new("chunk-0", "Down the Rabbit-Hole", "neighbour")], 512, 3, [new("MaxEvidenceItems", 2)],
            new("2026.09.0", "bge-m3-1024", ["graphrag_chunk_embedding", "graphrag_chunk_text"], "cosine"))
        {
            Scope = new("documents", 2, 40, [new(GraphRagRoutes.Vector, "exact", 40, 2), new(GraphRagRoutes.FullText, "over-fetch", 30, 1)])
        };
        return new(GraphRagAnswerDisposition.Answered, "She wondered what use a book without pictures was.",
            [new("doc-1", "chunk-1", start + offset, start + offset + Encoding.UTF8.GetByteCount(words), quote ?? words) { Page = 4, EndPage = 4 }],
            null, evidence, new(2, 1_234, 56, TimeSpan.FromMilliseconds(120), TimeSpan.FromSeconds(1.5)));
    }

    [Fact]
    public void An_export_reads_back_to_the_same_answer_and_writes_the_same_document_again()
    {
        var json = GraphRagAnswerExport.ToJson(Answer(), Exported);
        var read = GraphRagAnswerExport.FromJson(json);
        Assert.Equal(json, GraphRagAnswerExport.ToJson(read, Exported));

        Assert.Equal((GraphRagAnswerDisposition.Answered, "She wondered what use a book without pictures was."), (read.Disposition, read.Text));
        var citation = Assert.Single(read.Citations);
        Assert.Equal(("chunk-1", "What is the use of a book", 4, 4, GraphRagGrounding.Source), (citation.ChunkId, citation.Quote, citation.Page, citation.EndPage, citation.Grounding));
        Assert.Equal(Passage, read.Evidence.Items[0].Text);
        Assert.Equal([3, 4], read.Evidence.Items[0].Pages!.Select(p => p.Number));
        Assert.Equal(GraphRagGrounding.Chunk, read.Evidence.Items[1].Grounding);
        Assert.Equal([GraphRagRoutes.Vector, GraphRagRoutes.FullText], read.Evidence.Items[0].Routes.Select(r => r.Route));
        Assert.Equal("over-fetch", read.Evidence.Scope!.Routes[1].Strategy);
        Assert.Equal(("MaxEvidenceItems", 2), (read.Evidence.LimitsReached[0].Name, read.Evidence.LimitsReached[0].Value));
        Assert.Equal(TimeSpan.FromSeconds(1.5), read.Receipt!.Answer);
        Assert.Empty(GraphRagAnswerExport.Check(read));
    }

    [Fact]
    public void The_document_names_its_format_toolkit_and_time_and_writes_enums_as_names()
    {
        var document = JsonNode.Parse(GraphRagAnswerExport.ToJson(Answer(), Exported))!;
        Assert.Equal("graphbridge-graphrag-answer/1", document["format"]!.GetValue<string>());
        Assert.Equal("2026-10-26T12:00:00.0000000+00:00", document["exportedUtc"]!.GetValue<string>());
        Assert.False(string.IsNullOrEmpty(document["toolkit"]!.GetValue<string>()));
        var answer = document["answer"]!;
        Assert.Equal("Answered", answer["disposition"]!.GetValue<string>());
        Assert.Equal("FullText", answer["evidence"]!["items"]![0]!["routes"]![1]!["route"]!.GetValue<string>());
        Assert.Equal("Chunk", answer["evidence"]!["items"]![1]!["grounding"]!.GetValue<string>());
        Assert.Null(answer["evidence"]!["items"]![1]!["pages"]);
    }

    [Fact]
    public void A_refusal_round_trips_and_checks_clean()
    {
        var answer = Answer() with { Disposition = GraphRagAnswerDisposition.InsufficientEvidence, Text = null, Citations = [], Reason = "model-declined" };
        var read = GraphRagAnswerExport.FromJson(GraphRagAnswerExport.ToJson(answer, Exported));
        Assert.Equal((GraphRagAnswerDisposition.InsufficientEvidence, "model-declined"), (read.Disposition, read.Reason));
        Assert.Empty(GraphRagAnswerExport.Check(read));
    }

    [Fact]
    public void Check_finds_an_edited_quote_a_citation_outside_its_evidence_and_an_uncited_answer()
    {
        Assert.Equal(["citation 1: the quote is not the evidence's bytes 1079-1104"], GraphRagAnswerExport.Check(Answer("What is the use of a cook")));

        var outside = Answer();
        outside = outside with { Citations = [outside.Citations[0] with { ChunkId = "chunk-9" }] };
        Assert.Equal(["citation 1: no evidence item of chunk 'chunk-9' holds bytes 1079-1104"], GraphRagAnswerExport.Check(outside));

        Assert.Equal(["an answer without citations"], GraphRagAnswerExport.Check(Answer() with { Citations = [] }));

        // GR-69: what a citation says about its bytes must be what its evidence item says.
        var cited = Answer().Citations[0];
        Assert.Equal(["citation 1: its pages are not the evidence's pages of bytes 1079-1104"],
            GraphRagAnswerExport.Check(Answer() with { Citations = [cited with { Page = 3 }] }));
        var derived = Answer();
        derived = derived with { Evidence = derived.Evidence with { Items = [derived.Evidence.Items[0] with { TextOrigin = "ocr" }, derived.Evidence.Items[1]] } };
        Assert.Equal(["citation 1: its grounding or text origin is not its evidence item's"], GraphRagAnswerExport.Check(derived));
        Assert.Equal(["citation 1: its grounding or text origin is not its evidence item's"],
            GraphRagAnswerExport.Check(Answer() with { Citations = [cited with { Grounding = GraphRagGrounding.Chunk }] }));
        // A span that splits the opening quotation mark's bytes doesn't pass as the replacement character.
        var mark = 1_000 + Encoding.UTF8.GetByteCount(Passage[..Passage.IndexOf('“', StringComparison.Ordinal)]);
        Assert.Equal([$"citation 1: the quote is not the evidence's bytes {mark}-{mark + 1}"],
            GraphRagAnswerExport.Check(Answer() with { Citations = [cited with { StartByte = mark, EndByte = mark + 1, Quote = "\uFFFD", Page = 4, EndPage = 4 }] }));
        // An inverted span, or missing lists, is reported rather than thrown.
        Assert.Equal(["citation 1: no quote, or not a span"], GraphRagAnswerExport.Check(Answer() with { Citations = [cited with { EndByte = cited.StartByte - 1 }] }));
        Assert.Equal(["an answer without its citations or evidence"], GraphRagAnswerExport.Check(Answer() with { Citations = null! }));
        Assert.Equal(["a refusal needs a reason and no text"],
            GraphRagAnswerExport.Check(Answer() with { Disposition = GraphRagAnswerDisposition.InsufficientEvidence, Citations = [], Reason = "no-evidence" }));
    }

    [Theory]
    [InlineData("""{"format": "graphbridge-graphrag-answer/2", "answer": {}}""")]
    [InlineData("""{"answer": {}}""")]
    [InlineData("""[1, 2]""")]
    [InlineData("""{"format": "graphbridge-graphrag-answer/1"}""")]
    [InlineData("""{"format": "graphbridge-graphrag-answer/1", "answer": {}}""")]
    public void Another_format_or_a_document_without_an_answer_is_refused(string json)
        => Assert.Throws<FormatException>(() => GraphRagAnswerExport.FromJson(json));
}
