using GraphBridge.EntityFrameworkCore.FluentCypher;
using GraphBridge.Workshop.Domain;
using Microsoft.EntityFrameworkCore;
using Graph = GraphBridge.EntityFrameworkCore.FluentCypher.Cypher;

namespace GraphBridge.Workshop.Lessons;

public sealed record CastingRow(string Name, string? Title);
public sealed record CreditCount(string Name, long Movies, long Credits);
public sealed record Career(string Name, int? First, int? Last, IReadOnlyList<string> Titles);

public sealed class Lesson17FluentOptionalAndCounts : ILesson
{
    public LessonInfo Info { get; } = new("17", 'D', "Fluent Cypher II: optional matches and counts", "17-FluentOptionalAndCounts", LessonMode.Mixed, Core: true,
        Requirement.Seed, "Keep rows without a match with OPTIONAL MATCH, and count what you mean: rows or distinct things.");

    // workshop:start Optional
    /// <summary>Everyone, with any movie after 2010 they acted in. The predicate belongs to the OPTIONAL MATCH.</summary>
    public static CypherResultQuery<CastingRow> RecentWork(DbContext db)
    {
        var person = Graph.Node<Person>();
        var movie = Graph.Node<Movie>();
        return db.Cypher()
            .Match(person)
            .OptionalMatch(person.Out<Credit>(movie), where: () => movie.Value.Released > 2010)
            .ReturnDistinct(() => new CastingRow(person.Value.Name, movie.Value.Title))
            .OrderBy(() => person.Value.Name)
            .Limit(100);
    }
    // workshop:end Optional

    // workshop:start Filtered
    /// <summary>The same predicate after WITH filters rows instead: people without a recent movie disappear.</summary>
    public static CypherResultQuery<CastingRow> RecentWorkOnly(DbContext db)
    {
        var person = Graph.Node<Person>();
        var movie = Graph.Node<Movie>();
        return db.Cypher()
            .Match(person)
            .OptionalMatch(person.Out<Credit>(movie))
            .With(person, movie)
            .Where(() => movie.Value.Released > 2010)
            .ReturnDistinct(() => new CastingRow(person.Value.Name, movie.Value.Title))
            .OrderBy(() => person.Value.Name)
            .Limit(100);
    }
    // workshop:end Filtered

    // workshop:start Counts
    /// <summary>Distinct movies versus physical ACTED_IN rows per actor.</summary>
    public static CypherResultQuery<CreditCount> CreditCounts(DbContext db)
    {
        var person = Graph.Node<Person>();
        var movie = Graph.Node<Movie>();
        var movies = Graph.CountDistinct(movie);
        var credits = Graph.CountRows();
        return db.Cypher()
            .Match(person.Out<Credit>(movie))
            .With(person, movies, credits)
            .Return(() => new CreditCount(person.Value.Name, movies.Value, credits.Value))
            .OrderBy(() => person.Value.Name)
            .Limit(100);
    }
    // workshop:end Counts

    // workshop:start Aggregates
    /// <summary>First and last release year and the titles per person, including people without credits.</summary>
    public static CypherResultQuery<Career> Careers(DbContext db)
    {
        var person = Graph.Node<Person>();
        var movie = Graph.Node<Movie>();
        var first = Graph.Min(movie.Property(m => m.Released));
        var last = Graph.Max(movie.Property(m => m.Released));
        var titles = Graph.CollectDistinct(movie.Property(m => m.Title), max: 10);
        return db.Cypher()
            .Match(person)
            .OptionalMatch(person.Out<Credit>(movie))
            .With(person, first, last, titles)
            .Return(() => new Career(person.Value.Name, (int?)first.Value, (int?)last.Value, titles.Value))
            .OrderBy(() => person.Value.Name)
            .Limit(100);
    }
    // workshop:end Aggregates

    public async Task RunAsync(LessonContext lesson, CancellationToken cancellationToken)
    {
        await using var db = lesson.CreateContext();

        lesson.Step("OPTIONAL MATCH with its own predicate keeps everyone");
        lesson.Code("Optional");
        lesson.Cypher("optional", RecentWork(db).ToQueryString());

        lesson.Step("The same predicate after WITH filters rows");
        lesson.Code("Filtered");
        lesson.Cypher("filtered", RecentWorkOnly(db).ToQueryString());

        lesson.Step("Count rows or count distinct movies");
        lesson.Code("Counts");
        lesson.Cypher("counts", CreditCounts(db).ToQueryString());

        lesson.Step("Other aggregates: min, max and a bounded collect");
        lesson.Code("Aggregates");
        lesson.Cypher("aggregates", Careers(db).ToQueryString());
        lesson.Note("collect needs a maximum, applied on the server ([..$p0]), so one row cannot grow without bound. Its order is not guaranteed.");

        lesson.Step("A C# predicate that would mean something different in Cypher is refused");
        lesson.Code("Refused");
        // workshop:start Refused
        var person = Graph.Node<Person>();
        var movie = Graph.Node<Movie>();
        var afterOptional = db.Cypher().Match(person).OptionalMatch(person.Out<Credit>(movie)).With(person, movie);
        lesson.ExpectFailure<NotSupportedException>("'!=' on a symbol that OPTIONAL MATCH may leave null is refused (GBFC110)",
            () => afterOptional.Where(() => movie.Value.Title != "The Matrix").Return(() => person.Value.Name).ToQueryString());
        // workshop:end Refused
        lesson.Note("In C#, a missing movie is 'not The Matrix' and the row stays. In Cypher, null <> 'The Matrix' is unknown and the row is dropped. GraphBridge refuses rather than pick one meaning for you.");

        if (!lesson.NeedsDatabase("Running the optional matches and counts")) return;

        lesson.Step("Run them");
        var everyone = await RecentWork(db).ToListAsync(cancellationToken);
        lesson.Table(everyone);
        lesson.Expect("all nine people are kept", 9, everyone.Select(r => r.Name).Distinct().Count());
        lesson.Check("people without a recent movie have a null title", everyone.Any(r => r.Name == "Emil Eifrem" && r.Title is null));
        var filtered = await RecentWorkOnly(db).ToListAsync(cancellationToken);
        lesson.Table(filtered);
        lesson.ExpectSequence("only people with a recent movie remain", ["Hugo Weaving", "Keanu Reeves", "Tom Hanks"], filtered.Select(r => r.Name));
        var counts = await CreditCounts(db).ToListAsync(cancellationToken);
        lesson.Table(counts);
        lesson.Expect("Hugo: two distinct movies, three credits", new CreditCount("Hugo Weaving", 2, 3), counts.Single(c => c.Name == "Hugo Weaving"));
        lesson.Expect("Tom Hanks: one movie, two credits", new CreditCount("Tom Hanks", 1, 2), counts.Single(c => c.Name == "Tom Hanks"));
        var careers = await Careers(db).ToListAsync(cancellationToken);
        lesson.Table(careers.Select(c => new { c.Name, c.First, c.Last, Titles = string.Join(", ", c.Titles.Order(StringComparer.Ordinal)) }));
        var keanu = careers.Single(c => c.Name == "Keanu Reeves");
        lesson.Expect("Keanu: 1999 to 2014", (1999, 2014), (keanu.First, keanu.Last));
        lesson.ExpectSequence("Keanu's four titles", ["John Wick", "The Matrix", "The Matrix Reloaded", "The Matrix Revolutions"], keanu.Titles.Order(StringComparer.Ordinal));
        var emil = careers.Single(c => c.Name == "Emil Eifrem");
        lesson.Check("no credits: min and max are null, the list is empty", emil.First is null && emil.Last is null && emil.Titles.Count == 0);
    }
}
