using System.Text;
using GraphBridge.GraphRag;
using Microsoft.Extensions.AI;

namespace GraphBridge.GraphRag.Tests;

/// <summary>Wave 2 (GR-05): answers with verified citations or a refusal, with a scripted chat client and fixed evidence.</summary>
public sealed class GraphRagAnswerTests
{
    private static readonly GraphRagEvidence Evidence = new("What do graphs connect?",
    [
        Item("c1", "Graphs connect facts — not rows.", 0),
        Item("c2", "Edges have a type and a direction.", 100)
    ], [new GraphRagContextItem("c0", "A neighbouring paragraph.", "neighbour of c1")], 70, 0, [], new("2026.09.0", "set", ["index"], "cosine"));

    [Fact]
    public async Task An_answer_with_quotes_found_in_their_evidence_is_answered_with_the_stored_text()
    {
        var chat = new ScriptedChat("""Here you go: ```json {"answer": "Facts.", "citations": [{"evidence": 1, "quote": "Graphs connect facts - not rows."}]} ```""");
        var answer = await new GraphRagAnswerer(new FixedRetriever(Evidence), () => chat).AnswerAsync("What do graphs connect?");
        Assert.Equal(GraphRagAnswerDisposition.Answered, answer.Disposition);
        Assert.Equal("Facts.", answer.Text);
        var citation = Assert.Single(answer.Citations);
        Assert.Equal(("c1", "Graphs connect facts — not rows.", 0, Encoding.UTF8.GetByteCount("Graphs connect facts — not rows.")),
            (citation.ChunkId, citation.Quote, citation.StartByte, citation.EndByte));
        Assert.Null(answer.Reason);
        // The prompt numbers the evidence and labels context as never cited; the reply format is JSON.
        Assert.Contains("[1] Graphs connect facts", chat.Prompt);
        Assert.Contains("Context (never cite):\n- A neighbouring paragraph.", chat.Prompt.ReplaceLineEndings("\n"));
        Assert.Equal(ChatResponseFormat.Json, chat.Options!.ResponseFormat);
    }

    [Theory]
    [InlineData("""{"answer": "Facts.", "citations": [{"evidence": 1, "quote": "Graphs connect FACTS"}]}""", "citation-not-found")]
    [InlineData("""{"answer": "Facts.", "citations": [{"evidence": 2, "quote": "Graphs connect facts"}]}""", "citation-not-found")]
    [InlineData("""{"answer": "Facts.", "citations": [{"evidence": 1, "quote": "Graphs connect facts"}, {"evidence": 2, "quote": "made up"}]}""", "citation-not-found")]
    [InlineData("""{"answer": "Facts.", "citations": [{"evidence": 3, "quote": "Graphs"}]}""", "citation-not-found")]
    [InlineData("""{"answer": "Facts.", "citations": [{"evidence": 0, "quote": "A neighbouring paragraph."}]}""", "citation-not-found")]
    [InlineData("""{"answer": "Facts.", "citations": []}""", "no-citations")]
    [InlineData("""{"answer": "Facts."}""", "no-citations")]
    [InlineData("""{"insufficient": "The evidence is about edges."}""", "model-declined")]
    [InlineData("I think graphs connect facts.", "unreadable-response")]
    [InlineData("""{"answer": 42, "citations": []}""", "unreadable-response")]
    [InlineData("""{"answer": "Facts.", "citations": [ """, "unreadable-response")]
    public async Task Any_failed_citation_or_missing_support_is_a_refusal_without_answer_text(string reply, string reason)
    {
        var answer = await new GraphRagAnswerer(new FixedRetriever(Evidence), () => new ScriptedChat(reply)).AnswerAsync("q");
        Assert.Equal((GraphRagAnswerDisposition.InsufficientEvidence, reason), (answer.Disposition, answer.Reason));
        Assert.Null(answer.Text);
        Assert.Empty(answer.Citations);
        Assert.Same(Evidence, answer.Evidence);
    }

    [Fact]
    public async Task No_evidence_is_refused_without_calling_the_model_and_a_missing_client_is_an_error()
    {
        var chat = new ScriptedChat("{}");
        var empty = Evidence with { Items = [] };
        var answer = await new GraphRagAnswerer(new FixedRetriever(empty), () => chat).AnswerAsync("q");
        Assert.Equal("no-evidence", answer.Reason);
        Assert.Equal(0, chat.Calls);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new GraphRagAnswerer(new FixedRetriever(Evidence), () => null).AnswerAsync("q"));
    }

    [Theory]
    [InlineData("What is (a+b)?", "What is \\(a\\+b\\)\\?")]
    [InlineData("graphs AND rows OR NOT tables", "graphs and rows or not tables")]
    [InlineData("  spaced\tout \n words ", "spaced out words")]
    [InlineData("title:secret* ~fuzzy \"phrase\"", "title\\:secret\\* \\~fuzzy \\\"phrase\\\"")]
    public void Questions_become_plain_Lucene_terms(string question, string terms)
        => Assert.Equal(terms, GraphRagRetriever<Microsoft.EntityFrameworkCore.DbContext>.LuceneTerms(question));

    private static GraphRagEvidenceItem Item(string id, string text, int start)
        => new(id, "doc", text, start, start + Encoding.UTF8.GetByteCount(text), 0.03, []);

    private sealed class FixedRetriever(GraphRagEvidence evidence) : IGraphRagRetriever
    {
        public Task<GraphRagEvidence> RetrieveAsync(string question, GraphRagRetrievalOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(evidence);
    }

    private sealed class ScriptedChat(string reply) : IChatClient
    {
        public int Calls { get; private set; }
        public string Prompt { get; private set; } = "";
        public ChatOptions? Options { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            Prompt = messages.Last().Text;
            Options = options;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, reply)));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
