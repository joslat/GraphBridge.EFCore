using System.Text.Json;
using GraphBridge.GraphRag;

namespace GraphBridge.GraphRag.Tests;

/// <summary>GR-54 (ADR 0052, section 21) offline: a citation of derived text names where the text came from.</summary>
public sealed class GraphRagDerivedTextTests
{
    private static GraphRagEvidenceItem Item(string? origin)
        => new("c1", "d1", "The harvest was larger than last year.", 100, 138, 0.02, []) { TextOrigin = origin };

    [Fact]
    public void A_located_citation_carries_its_items_text_origin()
    {
        var citation = GraphRagCitationVerifier.Locate(Item("ocr: Tesseract 5.3"), "harvest was larger")!;
        Assert.Equal(("harvest was larger", "ocr: Tesseract 5.3"), (citation.Quote, citation.TextOrigin));
        Assert.Null(GraphRagCitationVerifier.Locate(Item(null), "harvest was larger")!.TextOrigin);
    }

    [Fact]
    public void The_origin_is_written_only_for_derived_text()
    {
        var derived = JsonSerializer.Serialize(GraphRagCitationVerifier.Locate(Item("ocr: Tesseract 5.3"), "harvest")!, JsonSerializerOptions.Web);
        Assert.Contains("\"textOrigin\":\"ocr: Tesseract 5.3\"", derived, StringComparison.Ordinal);
        var own = JsonSerializer.Serialize(GraphRagCitationVerifier.Locate(Item(null), "harvest")!, JsonSerializerOptions.Web);
        Assert.DoesNotContain("textOrigin", own, StringComparison.Ordinal);
        Assert.DoesNotContain("textOrigin", JsonSerializer.Serialize(Item(null), JsonSerializerOptions.Web), StringComparison.Ordinal);

        var tool = JsonSerializer.Serialize(new GraphRagToolCitation("d1", "c1", 104, 111, "harvest") { TextOrigin = "ocr: Tesseract 5.3" }, JsonSerializerOptions.Web);
        Assert.Contains("\"textOrigin\":\"ocr: Tesseract 5.3\"", tool, StringComparison.Ordinal);
    }

    [Fact]
    public void An_exported_answer_keeps_the_origin()
    {
        var item = Item("transcript: Whisper large-v3");
        var citation = GraphRagCitationVerifier.Locate(item, "larger than last year")!;
        var evidence = new GraphRagEvidence("q", [item], [], 38, 0, [], new("2026.09.0", "set", ["index"], "cosine"));
        var answer = new GraphRagAnswer(GraphRagAnswerDisposition.Answered, "Larger.", [citation], null, evidence);
        var read = GraphRagAnswerExport.FromJson(GraphRagAnswerExport.ToJson(answer, DateTimeOffset.UnixEpoch));
        Assert.Equal("transcript: Whisper large-v3", read.Citations[0].TextOrigin);
        Assert.Equal("transcript: Whisper large-v3", read.Evidence.Items[0].TextOrigin);
        Assert.Empty(GraphRagAnswerExport.Check(read));
    }
}
