using GraphBridge.EntityFrameworkCore.FluentCypher;
using GraphBridge.Workshop.Domain;
using Microsoft.EntityFrameworkCore;
using Graph = GraphBridge.EntityFrameworkCore.FluentCypher.Cypher;

namespace GraphBridge.Workshop.Lessons;

public sealed class Lesson16FluentPatterns : ILesson
{
    public LessonInfo Info { get; } = new("16", 'D', "Fluent Cypher I: patterns", "16-FluentPatterns", LessonMode.Mixed, Core: true,
        Requirement.Seed, "Write a graph pattern in C#: typed node and edge symbols, direction, and a C# predicate.");

    // workshop:start Coactors
    public static CypherResultQuery<string> Coactors(DbContext db, string actorName)
    {
        var actor = Graph.Node<Person>();
        var movie = Graph.Node<Movie>();
        var coactor = Graph.Node<Person>();
        return db.Cypher()
            .Match(actor.Out<Credit>(movie).In<Credit>(coactor))
            .Where(() => actor.Value.Name == actorName && coactor.Value.Id != actor.Value.Id)
            .ReturnDistinct(() => coactor.Value.Name)
            .OrderBy(() => coactor.Value.Name)
            .Limit(25);
    }
    // workshop:end Coactors

    // workshop:start Explicit
    public static CypherResultQuery<string> CoactorsExplicit(DbContext db, string actorName)
    {
        var actor = Graph.Node<Person>();
        var movie = Graph.Node<Movie>();
        var coactor = Graph.Node<Person>();
        return db.Cypher()
            .Match(actor.Out(Graph.Edge<Credit>(), movie).In(Graph.Edge<Credit>(), coactor))
            .Where(actor.Property(p => p.Name).Eq(actorName)
                .And(coactor.Property(p => p.Id).Ne(actor.Property(p => p.Id))))
            .ReturnDistinct(() => coactor.Value.Name)
            .OrderBy(coactor.Property(p => p.Name))
            .Limit(25);
    }
    // workshop:end Explicit

    // workshop:start ListsAndText
    /// <summary>Co-actors of any listed actor, except the listed actors and names starting with a prefix.</summary>
    public static CypherResultQuery<string> CoactorsOfAny(DbContext db, string[] actorNames, string skipPrefix)
    {
        var actor = Graph.Node<Person>();
        var movie = Graph.Node<Movie>();
        var coactor = Graph.Node<Person>();
        return db.Cypher()
            .Match(actor.Out<Credit>(movie).In<Credit>(coactor))
            .Where(() => actorNames.Contains(actor.Value.Name)
                && !actorNames.Contains(coactor.Value.Name)
                && !coactor.Value.Name.StartsWith(skipPrefix, StringComparison.Ordinal))
            .ReturnDistinct(() => coactor.Value.Name)
            .OrderBy(() => coactor.Value.Name)
            .Limit(25);
    }
    // workshop:end ListsAndText

    // workshop:start SameMovieTwice
    /// <summary>One MATCH never uses the same physical relationship twice, so this finds people with two credits in one movie.</summary>
    public static CypherResultQuery<string> TwoRolesInOneMovie(DbContext db)
    {
        var person = Graph.Node<Person>();
        var movie = Graph.Node<Movie>();
        return db.Cypher()
            .Match(person.Out<Credit>(movie).In<Credit>(person))
            .ReturnDistinct(() => person.Value.Name)
            .OrderBy(() => person.Value.Name)
            .Limit(25);
    }
    // workshop:end SameMovieTwice

    // workshop:start Entities
    /// <summary>Whole entities: the movies themselves, untracked, instead of scalar columns.</summary>
    public static CypherResultQuery<Movie> MoviesOf(DbContext db, string actorName)
    {
        var actor = Graph.Node<Person>();
        var movie = Graph.Node<Movie>();
        return db.Cypher()
            .Match(actor.Out<Credit>(movie))
            .Where(() => actor.Value.Name == actorName)
            .ReturnDistinct(() => movie.Value)
            .OrderBy(() => movie.Value.Released)
            .ThenBy(() => movie.Value.Title)
            .Limit(25);
    }
    // workshop:end Entities

    public async Task RunAsync(LessonContext lesson, CancellationToken cancellationToken)
    {
        await using var db = lesson.CreateContext();

        lesson.Step("A pattern: who acted in a movie with Keanu Reeves?");
        lesson.Code("Coactors");
        var coactors = Coactors(db, "Keanu Reeves");
        lesson.Cypher("coactors", coactors.ToQueryString());
        lesson.Note("actor and coactor are both Person symbols, yet different variables (n0, n3). The arrow directions come from Out and In.");

        lesson.Step("The same query with explicit predicate methods compiles to identical Cypher");
        lesson.Code("Explicit");
        lesson.Expect("lambda and explicit forms produce the same Cypher", coactors.ToQueryString(), CoactorsExplicit(db, "Keanu Reeves").ToQueryString());

        lesson.Step("Lists and text: Contains, ordinal StartsWith and !");
        lesson.Code("ListsAndText");
        string[] stars = ["Keanu Reeves", "Tom Hanks"];
        var ofAny = CoactorsOfAny(db, stars, "L");
        lesson.Cypher("lists-and-text", ofAny.ToQueryString());
        lesson.Note("actorNames.Contains(...) becomes IN with one list parameter, copied when Where runs. ! is accepted here because a person's name is required, so the operand can never be null.");
        var someone = Graph.Node<Person>();
        lesson.ExpectFailure<NotSupportedException>("StartsWith without StringComparison.Ordinal is refused",
            () => db.Cypher().Match(someone).Where(() => someone.Value.Name.StartsWith("K")));

        lesson.Step("Relationship uniqueness: one MATCH never reuses a relationship");
        lesson.Code("SameMovieTwice");
        lesson.Cypher("two-roles", TwoRolesInOneMovie(db).ToQueryString());

        lesson.Step("Whole entities: return the movie itself");
        lesson.Code("Entities");
        var moviesOfKeanu = MoviesOf(db, "Keanu Reeves");
        lesson.Cypher("entities", moviesOfKeanu.ToQueryString());
        lesson.Note("Only the mapped properties travel. DirectorId is not stored on the movie, so it is read from the incoming DIRECTED relationship, exactly as LINQ reads it.");

        if (!lesson.NeedsDatabase("Running the patterns")) return;

        lesson.Step("Run them");
        var names = await coactors.ToListAsync(cancellationToken);
        lesson.Table(names);
        lesson.ExpectSequence("Keanu's co-actors", ["Carrie-Anne Moss", "Hugo Weaving", "Laurence Fishburne"], names);
        var listed = await ofAny.ToListAsync(cancellationToken);
        lesson.Table(listed);
        lesson.ExpectSequence("co-actors of Keanu or Tom, without the stars and without L", ["Carrie-Anne Moss", "Hugo Weaving"], listed);
        var twoRoles = await TwoRolesInOneMovie(db).ToListAsync(cancellationToken);
        lesson.Table(twoRoles);
        lesson.ExpectSequence("only people with two credits in one movie match themselves", ["Hugo Weaving", "Tom Hanks"], twoRoles);
        var movies = await moviesOfKeanu.ToListAsync(cancellationToken);
        lesson.Table(movies.Select(m => new { m.Title, m.Released, m.Tagline, m.DirectorId }));
        lesson.ExpectSequence("Keanu's movies as Movie entities, by year then title",
            ["The Matrix", "The Matrix Reloaded", "The Matrix Revolutions", "John Wick"], movies.Select(m => m.Title));
        lesson.ExpectSequence("directors read through DIRECTED; John Wick has none",
            [SeedData.Lana, SeedData.Lilly, SeedData.Lana, (Guid?)null], movies.Select(m => m.DirectorId));
        lesson.Expect("Fluent results are never tracked, entities included", 0, db.ChangeTracker.Entries().Count());
        lesson.Limit("Fluent Cypher is read-only, and its entities are untracked. To change and save them, load tracked entities from returned keys (guide: From Fluent results to tracked entities).");
    }
}
