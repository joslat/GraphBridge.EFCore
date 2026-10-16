using GraphBridge.EntityFrameworkCore.FluentCypher;
using GraphBridge.Workshop.Domain;
using Microsoft.EntityFrameworkCore;
using Graph = GraphBridge.EntityFrameworkCore.FluentCypher.Cypher;

namespace GraphBridge.Workshop.Lessons;

public sealed class Lesson18FluentExists : ILesson
{
    public LessonInfo Info { get; } = new("18", 'D', "Fluent Cypher III: existence", "18-FluentExists", LessonMode.Mixed, Core: false,
        Requirement.Seed, "Ask whether a pattern exists, or does not, without adding rows to the result.");

    // workshop:start LikedNotActed
    /// <summary>People who like a movie they did not act in.</summary>
    public static CypherResultQuery<string> LikedButNotActedIn(DbContext db)
    {
        var person = Graph.Node<Person>();
        var movie = Graph.Node<Movie>();
        return db.Cypher()
            .Match(person.Out<Like>(movie))
            .Where(Graph.Exists(person.Out<Credit>(movie)).Not())
            .ReturnDistinct(() => person.Value.Name)
            .OrderBy(() => person.Value.Name)
            .Limit(25);
    }
    // workshop:end LikedNotActed

    // workshop:start LambdaExists
    public static CypherResultQuery<string> LikedButNotActedInLambda(DbContext db)
    {
        var person = Graph.Node<Person>();
        var movie = Graph.Node<Movie>();
        return db.Cypher()
            .Match(person.Out<Like>(movie))
            .Where(() => !Graph.Is(Graph.Exists(person.Out<Credit>(movie))))
            .ReturnDistinct(() => person.Value.Name)
            .OrderBy(() => person.Value.Name)
            .Limit(25);
    }
    // workshop:end LambdaExists

    // workshop:start Suggestions
    /// <summary>Follow suggestions: people two FOLLOWS hops away whom the person does not already follow.</summary>
    public static CypherResultQuery<string> FollowSuggestions(DbContext db, string name)
    {
        var person = Graph.Node<Person>();
        var friend = Graph.Node<Person>();
        var candidate = Graph.Node<Person>();
        return db.Cypher()
            .Match(person.Out<Follow>(friend).Out<Follow>(candidate))
            .Where(() => person.Value.Name == name && candidate.Value.Id != person.Value.Id)
            .Where(Graph.Exists(person.Out<Follow>(candidate)).Not())
            .ReturnDistinct(() => candidate.Value.Name)
            .OrderBy(() => candidate.Value.Name)
            .Limit(25);
    }
    // workshop:end Suggestions

    public async Task RunAsync(LessonContext lesson, CancellationToken cancellationToken)
    {
        await using var db = lesson.CreateContext();

        lesson.Step("NOT EXISTS: likes without acting");
        lesson.Code("LikedNotActed");
        lesson.Cypher("liked-not-acted", LikedButNotActedIn(db).ToQueryString());
        lesson.Note("person and movie inside EXISTS are the outer symbols, so the check is correlated. The anonymous Credit edge stays local to EXISTS.");

        lesson.Step("The same EXISTS inside a lambda, with Graph.Is");
        lesson.Code("LambdaExists");
        lesson.Expect("Graph.Is embeds the explicit predicate unchanged", LikedButNotActedIn(db).ToQueryString(), LikedButNotActedInLambda(db).ToQueryString());
        lesson.Note("EXISTS is always true or false, never null, so C# ! and native NOT agree. Graph.Is is translated, never called.");

        lesson.Step("A lambda and an explicit EXISTS predicate combine with AND");
        lesson.Code("Suggestions");
        lesson.Cypher("suggestions", FollowSuggestions(db, "Emil Eifrem").ToQueryString());

        if (!lesson.NeedsDatabase("Running the existence checks")) return;

        lesson.Step("Run them");
        var liked = await LikedButNotActedIn(db).ToListAsync(cancellationToken);
        lesson.Table(liked);
        lesson.ExpectSequence("people who like a movie they were not in", ["Emil Eifrem", "Keanu Reeves", "Laurence Fishburne", "Tom Hanks"], liked);
        var suggestions = await FollowSuggestions(db, "Emil Eifrem").ToListAsync(cancellationToken);
        lesson.Table(suggestions);
        lesson.ExpectSequence("Emil follows Keanu, Keanu follows Carrie-Anne: suggest Carrie-Anne", ["Carrie-Anne Moss"], suggestions);
    }
}
