using GraphBridge.GraphRag;
using GraphBridge.Samples.GraphRagShared;
using Microsoft.Extensions.DependencyInjection;

namespace GraphBridge.Workshop.Lessons;

public sealed class Lesson33RetrievalAndStreaming : ILesson
{
    public LessonInfo Info { get; } = new("33", 'G', "Retrieval, scopes and streamed answers", "33-RetrievalAndStreaming", LessonMode.Mixed, Core: false,
        Requirement.Guarded, "Retrieve passages by meaning and by words, keep retrieval to chosen documents, and stream an answer whose text counts only once its citations are checked.");

    private const string Matrix = "The Matrix was released in 1999. Keanu Reeves plays Neo, a programmer who learns that the world is a simulation. Hugo Weaving plays Agent Smith.";
    private const string Atlas = "Cloud Atlas tells six stories across five centuries. Tom Hanks and Hugo Weaving each play several roles.";

    public async Task RunAsync(LessonContext lesson, CancellationToken cancellationToken)
    {
        lesson.Step("Retrieval options: two routes, every list bounded");
        lesson.Code("Options");
        // workshop:start Options
        var options = new GraphRagRetrievalOptions { MaxItems = 4 };
        // workshop:end Options
        lesson.Expect("both routes by default", GraphRagRoutes.Vector | GraphRagRoutes.FullText, options.Routes);
        lesson.Value("Candidates per route", options.CandidatesPerRoute);
        lesson.Value("Evidence budget", $"{options.MaxItems} passages, {options.MaxUtf8Bytes} bytes");
        lesson.Note("The vector route ranks passages by meaning, the full-text route by the question's words. The two lists are fused by rank, so a passage both routes find comes first.");

        if (!await GraphRagWorkshop.ReadyAsync(lesson, "retrieval and answers", cancellationToken)) return;
        await using var services = GraphRagWorkshop.Services(lesson, new ExtractiveChatClient(), new GraphRagWorkshop.WrittenReply(GraphRagWorkshop.NothingExtracted));
        try
        {
            await GraphRagWorkshop.ProvisionAsync(lesson.Connection!, cancellationToken);
            var receipt = await services.GetRequiredService<IGraphRagIngestion>()
                .IngestAsync([GraphRagSources.Text("matrix", Matrix), GraphRagSources.Text("atlas", Atlas)], cancellationToken);
            var atlas = receipt.Documents.Single(d => d.SourceUri == "text:atlas").DocumentId;
            string Name(string documentId) => documentId == atlas ? "atlas" : "matrix";

            lesson.Step("Retrieve: each passage with its rank in each route");
            lesson.Code("Retrieve");
            // workshop:start Retrieve
            var retriever = services.GetRequiredService<IGraphRagRetriever>();
            var evidence = await retriever.RetrieveAsync("Which films does Hugo Weaving play in?", options, cancellationToken);
            // workshop:end Retrieve
            lesson.Table(evidence.Items.Select(i => new
            {
                Document = Name(i.DocumentId), Bytes = $"{i.StartByte}-{i.EndByte}", Routes = string.Join(", ", i.Routes.Select(r => $"{r.Route} #{r.Rank}"))
            }));
            lesson.Expect("both films' passages are found", 2, evidence.Items.Select(i => i.DocumentId).Distinct().Count());

            lesson.Step("Scope: keep retrieval to chosen documents");
            lesson.Code("Scope");
            // workshop:start Scope
            var scoped = await retriever.RetrieveAsync("Which films does Hugo Weaving play in?",
                new GraphRagRetrievalOptions { MaxItems = 4, Scope = GraphRagScope.Documents(atlas) }, cancellationToken);
            // workshop:end Scope
            lesson.Check("only the chosen document's passages", scoped.Items.Count > 0 && scoped.Items.All(i => i.DocumentId == atlas));
            lesson.Value("How the scope was kept", string.Join("; ", scoped.Scope!.Routes.Select(r => $"{r.Route}: {r.Strategy}, kept {r.Kept} of {r.Fetched}")));

            lesson.Step("Stream an answer: provisional text first, then the checked answer");
            lesson.Code("Stream");
            // workshop:start Stream
            var answers = services.GetRequiredService<IGraphRagAnswers>();
            var updates = new List<GraphRagAnswerUpdate>();
            await foreach (var update in answers.AnswerStreamingAsync("Which roles do Tom Hanks and Hugo Weaving play?", cancellationToken: cancellationToken))
                updates.Add(update);
            // workshop:end Stream
            lesson.Expect("retrieval is reported first", GraphRagAnswerUpdateKind.Retrieved, updates[0].Kind);
            lesson.Check("the model's text arrives in pieces, not yet checked", updates.Any(u => u.Kind == GraphRagAnswerUpdateKind.Delta));
            var completed = updates[^1];
            lesson.Expect("the last update completes it", GraphRagAnswerUpdateKind.Completed, completed.Kind);
            lesson.Expect("with an answer whose citation was checked", GraphRagAnswerDisposition.Answered, completed.Answer!.Disposition);
            lesson.Value("Cited", completed.Answer.Citations.Single().Quote);

            GraphRagAnswer? refused = null;
            await foreach (var update in answers.AnswerStreamingAsync("Who directed Jaws?", cancellationToken: cancellationToken))
                refused = update.Answer ?? refused;
            lesson.Expect("a question the documents don't answer completes as a refusal, which withdraws any provisional text",
                GraphRagAnswerDisposition.InsufficientEvidence, refused?.Disposition);
        }
        finally
        {
            await GraphRagWorkshop.CleanAsync(lesson.Connection!, services);
        }
        lesson.Limit("Show the pieces as unverified: only the completed update says whether they hold. In a web application, MapGraphRag from GraphBridge.GraphRag.AspNetCore sends these updates as server-sent events; sample 09's explorer shows them.");
    }
}
