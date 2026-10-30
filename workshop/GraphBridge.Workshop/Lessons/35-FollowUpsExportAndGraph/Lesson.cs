using System.Text.Json;
using GraphBridge.GraphRag;
using GraphBridge.Samples.GraphRagShared;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace GraphBridge.Workshop.Lessons;

public sealed class Lesson35FollowUpsExportAndGraph : ILesson
{
    public LessonInfo Info { get; } = new("35", 'G', "Follow-ups, answer export and graph questions", "35-FollowUpsExportAndGraph", LessonMode.Mixed, Core: false,
        Requirement.Guarded, "Answer a follow-up that only makes sense with its conversation, export an answer as evidence a check can trust, and ask the graph a question through a model-written query that may only read.");

    private const string Matrix = "The Matrix was released in 1999. Keanu Reeves plays Neo, a programmer who learns that the world is a simulation.";
    private const string Atlas = "Cloud Atlas tells six stories across five centuries. Tom Hanks and Hugo Weaving each play several roles.";

    public async Task RunAsync(LessonContext lesson, CancellationToken cancellationToken)
    {
        lesson.Step("The export format, checked when it is read");
        lesson.Value("Format", GraphRagAnswerExport.Format);
        lesson.ExpectFailure<FormatException>("a document in another format is refused",
            () => GraphRagAnswerExport.FromJson("""{"format": "someone-else/1", "answer": {}}"""));

        if (!await GraphRagWorkshop.ReadyAsync(lesson, "follow-ups, export and graph questions", cancellationToken)) return;
        await using var services = GraphRagWorkshop.Services(lesson, new StandIn(), new GraphRagWorkshop.WrittenReply(GraphRagWorkshop.NothingExtracted),
            more => more.AddGraphRagText2Cypher<GraphRagWorkshop.RagContext>());
        try
        {
            await GraphRagWorkshop.ProvisionAsync(lesson.Connection!, cancellationToken);
            await services.GetRequiredService<IGraphRagIngestion>()
                .IngestAsync([GraphRagSources.Text("matrix", Matrix), GraphRagSources.Text("atlas", Atlas)], cancellationToken);

            lesson.Step("A follow-up: rewritten with its conversation, then answered");
            lesson.Code("FollowUp");
            // workshop:start FollowUp
            var answers = services.GetRequiredService<IGraphRagAnswers>();
            var first = await answers.AnswerAsync("Who does Keanu Reeves play?", cancellationToken: cancellationToken);
            var followUp = await answers.AnswerAsync("When was it released?",
                new GraphRagRetrievalOptions { History = [new GraphRagTurn("Who does Keanu Reeves play?", first.Text)] }, cancellationToken);
            // workshop:end FollowUp
            var rewrite = followUp.Evidence.Rewrite!;
            lesson.Value("Asked", rewrite.Asked);
            lesson.Value("Answered as", rewrite.Standalone);
            lesson.Expect("the follow-up was rewritten", "rewritten", rewrite.Outcome);
            lesson.Expect("and answered from the passage it needs", GraphRagAnswerDisposition.Answered, followUp.Disposition);
            lesson.Check("the cited passage gives the year", followUp.Citations.Single().Quote.Contains("1999", StringComparison.Ordinal));
            lesson.Note("The application keeps the conversation and sends it with each question; the toolkit stores none of it.");

            lesson.Step("Export the answer, read it back, and check it without the database");
            lesson.Code("Export");
            // workshop:start Export
            var json = GraphRagAnswerExport.ToJson(followUp);
            var read = GraphRagAnswerExport.FromJson(json);
            var problems = GraphRagAnswerExport.Check(read);
            var forged = read with { Citations = [read.Citations[0] with { Quote = "The Matrix was released in 2001." }] };
            // workshop:end Export
            lesson.Value("Exported", $"{json.Length} characters: the question, the answer, its citations, the evidence and the receipt");
            lesson.Check("every citation is the bytes of the evidence it names", problems.Count == 0, string.Join("; ", problems));
            lesson.Check("an edited quote is caught", GraphRagAnswerExport.Check(forged).Count > 0);

            lesson.Step("Ask the graph: a model writes one query, and only a read runs");
            lesson.Code("Graph");
            // workshop:start Graph
            var graph = services.GetRequiredService<IGraphRagGraphQuestions>();
            var titles = await graph.AskAsync("Which documents are stored?", cancellationToken);
            var write = await graph.AskAsync("Remove every document.", cancellationToken);
            // workshop:end Graph
            lesson.Value("Query", titles.Cypher);
            lesson.Table(titles.Rows.Select(r => new { Title = r["title"] }));
            lesson.Expect("the read is answered with its rows", GraphRagGraphAnswerDisposition.Answered, titles.Disposition);
            lesson.Value("Refused query", write.Cypher);
            lesson.Expect("a query that writes is refused before it runs: EXPLAIN plans it, and the plan says it writes",
                "not-read-only", write.Reason);
            lesson.Expect("both documents are still there", 2, (await graph.AskAsync("Which documents are stored?", cancellationToken)).Rows.Count);
        }
        finally
        {
            await GraphRagWorkshop.CleanAsync(lesson.Connection!, services);
        }
        lesson.Limit("Text2Cypher is experimental and off until you register it. Its answer is rows, not citations: read the query, since the rows are what it returned whether or not it asks what the question asks.");
    }

    // Stands in for a model: rewrites the follow-up, writes the graph queries, and answers with the extractive client.
    private sealed class StandIn : IChatClient
    {
        private readonly ExtractiveChatClient _answers = new();

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var prompt = messages.Last().Text;
            if (prompt.StartsWith("Conversation:", StringComparison.Ordinal))
                return Reply(JsonSerializer.Serialize(new { question = "When was The Matrix released?" }));
            if (prompt.Contains("\nGraph:\n", StringComparison.Ordinal))
                return Reply(JsonSerializer.Serialize(new
                {
                    cypher = prompt.StartsWith("Question: Remove", StringComparison.Ordinal)
                        ? $"MATCH (d:{GraphRagWorkshop.Layout.DocumentLabel}) DETACH DELETE d"
                        : $"MATCH (d:{GraphRagWorkshop.Layout.DocumentLabel}) RETURN d.Title AS title ORDER BY title"
                }));
            return _answers.GetResponseAsync(messages, options, cancellationToken);
        }

        private static Task<ChatResponse> Reply(string text) => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text)));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => _answers.GetStreamingResponseAsync(messages, options, cancellationToken);

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
