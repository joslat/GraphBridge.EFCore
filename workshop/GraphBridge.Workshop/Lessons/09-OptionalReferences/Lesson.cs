using GraphBridge.Workshop.Domain;
using Microsoft.EntityFrameworkCore;

namespace GraphBridge.Workshop.Lessons;

public sealed class Lesson09OptionalReferences : ILesson
{
    public LessonInfo Info { get; } = new("09", 'B', "Optional references", "09-OptionalReferences", LessonMode.Connected, Core: false,
        Requirement.Seed, "Attach, replace and remove an optional parent by changing an ordinary EF foreign key.");

    public async Task RunAsync(LessonContext lesson, CancellationToken cancellationToken)
    {
        lesson.Step("Movies and their optional directors");
        await using (var db = lesson.CreateContext())
        {
            lesson.Code("Directors");
            // workshop:start Directors
            var movies = await db.Movies.Include(m => m.Director).OrderBy(m => m.Released).ThenBy(m => m.Title).ToListAsync(cancellationToken);
            // workshop:end Directors
            lesson.Table(movies.Select(m => new { m.Title, Director = m.Director?.Name }));
            lesson.Expect("John Wick has no director yet", null, movies.Single(m => m.Id == SeedData.JohnWick).DirectorId);
        }

        lesson.Step("Attach: set the foreign key and save");
        await using (var db = lesson.CreateContext())
        {
            lesson.Code("Attach");
            // workshop:start Attach
            var wick = await db.Movies.SingleAsync(m => m.Id == SeedData.JohnWick, cancellationToken);
            wick.DirectorId = SeedData.Lana;                       // creates (Lana)-[:DIRECTED]->(John Wick)
            await db.SaveChangesAsync(cancellationToken);
            // workshop:end Attach
        }
        lesson.Expect("one DIRECTED link from Lana", 1L, await Links(lesson, SeedData.Lana, cancellationToken));

        lesson.Step("Replace: point the foreign key at another person");
        await using (var db = lesson.CreateContext())
        {
            var wick = await db.Movies.SingleAsync(m => m.Id == SeedData.JohnWick, cancellationToken);
            wick.DirectorId = SeedData.Lilly;                      // deletes the old link, creates a new one
            await db.SaveChangesAsync(cancellationToken);
        }
        lesson.Expect("Lana's link is gone", 0L, await Links(lesson, SeedData.Lana, cancellationToken));
        lesson.Expect("Lilly's link exists", 1L, await Links(lesson, SeedData.Lilly, cancellationToken));

        lesson.Step("Remove: set the foreign key back to null");
        await using (var db = lesson.CreateContext())
        {
            lesson.Code("Remove");
            // workshop:start Remove
            var wick = await db.Movies.SingleAsync(m => m.Id == SeedData.JohnWick, cancellationToken);
            wick.DirectorId = null;                                // deletes the link; both nodes stay
            await db.SaveChangesAsync(cancellationToken);
            // workshop:end Remove
        }
        lesson.Expect("John Wick has no DIRECTED link again", 0L, await Links(lesson, null, cancellationToken));
        await using (var db = lesson.CreateContext())
            lesson.Expect("a fresh read sees no director", null, (await db.Movies.SingleAsync(m => m.Id == SeedData.JohnWick, cancellationToken)).DirectorId);
        lesson.Limit("Deleting a person who still directs a movie is refused; there is no cascade or automatic set-null.");
    }

    private static Task<long> Links(LessonContext lesson, Guid? director, CancellationToken cancellationToken)
        => WorkshopData.ScalarAsync(lesson.Connection!, director is null
                ? "MATCH ()-[d:DIRECTED]->(:Movie {id: $movie}) RETURN count(d) AS value"
                : "MATCH (:Person {id: $person})-[d:DIRECTED]->(:Movie {id: $movie}) RETURN count(d) AS value",
            cancellationToken, ("movie", SeedData.JohnWick.ToString("D")), ("person", director?.ToString("D")));
}
