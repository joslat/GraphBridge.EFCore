using Microsoft.EntityFrameworkCore;

namespace GraphBridge.Workshop.Lessons;

public sealed record PersonCard(string Name, int? Born);
public sealed record MovieCard(string Title, int Released, string? Tagline);

public sealed class Lesson07Projections : ILesson
{
    public LessonInfo Info { get; } = new("07", 'A', "Projections and aggregates", "07-Projections", LessonMode.Mixed, Core: false,
        Requirement.Seed, "Return DTOs instead of tracked entities, and compute integer aggregates on the server.");

    public async Task RunAsync(LessonContext lesson, CancellationToken cancellationToken)
    {
        await using var db = lesson.CreateContext();

        lesson.Step("Project people into a DTO: only the selected properties are returned");
        lesson.Code("People");
        // workshop:start People
        var people = db.People
            .Where(p => p.Born >= 1965)
            .OrderBy(p => p.Born).ThenBy(p => p.Name)
            .Select(p => new PersonCard(p.Name, p.Born));
        // workshop:end People
        lesson.Cypher("person-cards", people.ToQueryString());

        lesson.Step("Project movies: a dependent still proves its parent link");
        lesson.Code("Movies");
        // workshop:start Movies
        var movies = db.Movies
            .Where(m => m.Released >= 2003)
            .OrderBy(m => m.Released).ThenBy(m => m.Title)
            .Select(m => new MovieCard(m.Title, m.Released, m.Tagline));
        // workshop:end Movies
        lesson.Cypher("movie-cards", movies.ToQueryString());
        lesson.Note("Movie has an optional DIRECTED parent (lesson 09). Before shaping any Movie row, even into a DTO, the provider reads its key and checks that it has at most one valid director link. That is why the Cypher is longer.");

        if (!lesson.NeedsDatabase("Running the projections")) return;

        lesson.Step("Run them: DTOs are never tracked");
        var personRows = await people.ToListAsync(cancellationToken);
        var movieRows = await movies.ToListAsync(cancellationToken);
        lesson.Table(personRows);
        lesson.Table(movieRows);
        lesson.Expect("five people born in 1965 or later", 5, personRows.Count);
        lesson.Expect("four movies from 2003 on", 4, movieRows.Count);
        lesson.Expect("nothing was tracked", 0, db.ChangeTracker.Entries().Count());

        lesson.Step("Integer Min and Max run on the server");
        lesson.Code("MinMax");
        // workshop:start MinMax
        var earliest = await db.People.MinAsync(p => p.Born, cancellationToken);
        var latest = await db.People.MaxAsync(p => p.Born, cancellationToken);
        // workshop:end MinMax
        lesson.Expect("earliest birth year", 1956, earliest);
        lesson.Expect("latest birth year", 1978, latest);
        lesson.Limit("Aggregates run on ordinary node roots such as Person. Sum, Average, GroupBy and aggregates over dependents such as Movie are not translated; use Fluent or native Cypher for those reports.");
    }
}
