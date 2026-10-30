using System.Text;
using GraphBridge.GraphRag;
using GraphBridge.GraphRag.Ontologies;
using GraphBridge.Samples.GraphRagShared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GraphBridge.Workshop.Lessons;

public sealed class Lesson32ThingsFactsQuotes : ILesson
{
    public LessonInfo Info { get; } = new("32", 'G', "Things, facts and quotes", "32-ThingsFactsQuotes", LessonMode.Mixed, Core: false,
        Requirement.Guarded, "Extract the characters and places of a passage, a fact between them and who says what, each checked against the passage before anything is written, and see what the checks prune.");

    // Written for the workshop; the characters are from the public-domain novel.
    internal const string Passage = "Alice sat by the river with her sister. “What is the use of a book without pictures?” Alice asked her sister. "
        + "Then the White Rabbit ran past, and Alice followed it all the way to Wonderland.";

    // A model's reply for the passage, written by hand. Three items are wrong on purpose: the Cheshire Cat isn't in the passage,
    // TAKES_PLACE_IN joins an event to a place, not a character, and the Rabbit says nothing here.
    internal const string Reply = """
        {"entities": [
          {"kind": "Character", "name": "Alice", "mentions": ["Alice"]},
          {"kind": "Character", "name": "Alice's sister", "mentions": ["her sister"]},
          {"kind": "Character", "name": "White Rabbit", "mentions": ["the White Rabbit"]},
          {"kind": "Character", "name": "Cheshire Cat", "mentions": ["the Cheshire Cat"]},
          {"kind": "Place", "name": "the river", "mentions": ["the river"]},
          {"kind": "Place", "name": "Wonderland", "mentions": ["Wonderland"]}
        ],
        "relationships": [
          {"type": "KIN_OF", "from": "Alice", "fromKind": "Character", "to": "Alice's sister", "toKind": "Character", "kind": "sibling"},
          {"type": "TAKES_PLACE_IN", "from": "Alice", "fromKind": "Character", "to": "Wonderland", "toKind": "Place"}
        ],
        "quotes": [
          {"type": "SAYS", "speaker": "Alice", "words": "What is the use of a book without pictures?", "addressee": "her sister"},
          {"type": "SAYS", "speaker": "White Rabbit", "words": "Oh my ears and whiskers!"}
        ]}
        """;

    public async Task RunAsync(LessonContext lesson, CancellationToken cancellationToken)
    {
        lesson.Step("The ontology: what a model may extract");
        lesson.Code("Ontology");
        // workshop:start Ontology
        var ontology = GraphRagOntologies.Novel.WithLabelPrefix("Workshop");
        // workshop:end Ontology
        lesson.Table(ontology.Classes.Select(c => new { c.Kind, c.Label, Aliases = c.Aliases }));
        lesson.Table(ontology.Relationships.Select(r => new { r.Type, From = r.FromKind, To = r.ToKind }));
        lesson.Expect("the kinds stay; only the labels carry the prefix", "WorkshopCharacter", ontology.Classes.Single(c => c.Kind == "Character").Label);
        lesson.Note("The model is shown the kinds, facts and quote types, and asked for JSON: each thing with the exact words that mention it, each fact by its endpoints' names, each quote with its speaker.");

        if (!await GraphRagWorkshop.ReadyAsync(lesson, "extraction", cancellationToken)) return;
        var extractor = new GraphRagWorkshop.WrittenReply(Reply);
        await using var services = GraphRagWorkshop.Services(lesson, new ExtractiveChatClient(), extractor);
        try
        {
            await GraphRagWorkshop.ProvisionAsync(lesson.Connection!, cancellationToken);

            lesson.Step("Ingest: the reply is checked against the passage before anything is written");
            lesson.Code("Extract");
            // workshop:start Extract
            var receipt = await services.GetRequiredService<IGraphRagIngestion>()
                .IngestAsync([GraphRagSources.Text("riverbank", Passage)], cancellationToken);
            var extraction = receipt.Extraction!;
            // workshop:end Extract
            lesson.Expect("one model call for the passage", 1, extractor.Calls);
            lesson.Value("Written", $"{extraction.Entities} things, {extraction.Mentions} mentions, {extraction.Facts} fact, {extraction.Quotes} quote");
            lesson.Table(extraction.Pruned.Select(p => new { p.Item, p.Label, p.Name, p.Reason }));
            lesson.ExpectSequence("four items pruned, each with its reason",
                ["Entity ungrounded", "Mention quote-not-found", "Quote quote-not-found", "Relationship pattern-not-allowed"],
                extraction.Pruned.Select(p => $"{p.Item} {p.Reason}").Order(StringComparer.Ordinal));

            lesson.Step("Read the graph back with LINQ: every mention and quote is the passage's own bytes");
            lesson.Code("Read");
            // workshop:start Read
            await using var scope = services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<GraphRagWorkshop.RagContext>();
            var characters = await db.Set<Character>().OrderBy(c => c.Name).Select(c => c.Name).ToListAsync(cancellationToken);
            var mentions = await db.Set<GraphRagMention<Character>>().ToListAsync(cancellationToken);
            var kin = await db.Set<KinOf>().SingleAsync(cancellationToken);
            var says = await db.Set<Says>().SingleAsync(cancellationToken);
            // workshop:end Read
            lesson.ExpectSequence("the characters in the passage, and only those", ["Alice", "Alice's sister", "White Rabbit"], characters);
            var bytes = Encoding.UTF8.GetBytes(Passage);
            lesson.Table(mentions.OrderBy(m => m.StartByte).Select(m => new { m.StartByte, m.EndByte, m.Quote }));
            lesson.Check("each mention's quote is exactly the bytes at its span",
                mentions.All(m => m.Quote == Encoding.UTF8.GetString(bytes, m.StartByte, m.EndByte - m.StartByte)));
            lesson.Expect("the fact keeps its property", "sibling", kin.Kind);
            lesson.Expect("the quote is the spoken words, at their bytes", says.Words, Encoding.UTF8.GetString(bytes, says.StartByte, says.EndByte - says.StartByte));
            lesson.Value("Addressed to", says.Addressee);
        }
        finally
        {
            await GraphRagWorkshop.CleanAsync(lesson.Connection!, services);
        }
        lesson.Limit("A check proves that the words are in the passage, not that the model read them right: a wrong alias or relation that quotes real words passes. Merges (lesson 34) are a person's decision.");
    }
}
