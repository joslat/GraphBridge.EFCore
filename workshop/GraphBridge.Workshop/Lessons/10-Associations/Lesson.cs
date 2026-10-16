using GraphBridge.Workshop.Domain;
using Microsoft.EntityFrameworkCore;

namespace GraphBridge.Workshop.Lessons;

public sealed class Lesson10Associations : ILesson
{
    public LessonInfo Info { get; } = new("10", 'B', "Associations (many-to-many)", "10-Associations", LessonMode.Connected, Core: true,
        Requirement.Seed, "Store an EF many-to-many join entity as one directed relationship with a payload.");

    public async Task RunAsync(LessonContext lesson, CancellationToken cancellationToken)
    {
        lesson.Step("Map Person ↔ Movie likes as a LIKES relationship");
        lesson.Code("Associations", "Domain/MoviesContext.cs");
        lesson.Note("There is no join node: each Like entity is exactly one (:Person)-[:LIKES {stars}]->(:Movie) relationship.");

        lesson.Step("Query the association like any entity set");
        await using (var db = lesson.CreateContext())
        {
            lesson.Code("Query");
            // workshop:start Query
            var query = db.Likes.Where(l => l.Stars >= 4).OrderByDescending(l => l.Stars);
            var likes = await query.ToListAsync(cancellationToken);
            // workshop:end Query
            lesson.Cypher("strong-likes", query.ToQueryString());
            lesson.Table(likes.Select(l => new { Person = Name(l.PersonId), Movie = Title(l.MovieId), l.Stars }));
            lesson.Expect("four likes with four or five stars", 4, likes.Count);
        }

        lesson.Step("Add a like through the skip navigation");
        await using (var db = lesson.CreateContext())
        {
            lesson.Code("Add");
            // workshop:start Add
            var hugo = await db.People.SingleAsync(p => p.Id == SeedData.Hugo, cancellationToken);
            var wick = await db.Movies.SingleAsync(m => m.Id == SeedData.JohnWick, cancellationToken);
            hugo.LikedMovies.Add(wick);                          // EF creates the Like join entity
            await db.SaveChangesAsync(cancellationToken);        // one LIKES relationship
            // workshop:end Add
        }
        lesson.Expect("the LIKES relationship exists", 1L, await Likes(lesson, cancellationToken));

        lesson.Step("Change the payload, then delete the association");
        await using (var db = lesson.CreateContext())
        {
            lesson.Code("Payload");
            // workshop:start Payload
            var like = await db.Likes.SingleAsync(l => l.PersonId == SeedData.Hugo && l.MovieId == SeedData.JohnWick, cancellationToken);
            like.Stars = 5;                                      // updates only the relationship property
            await db.SaveChangesAsync(cancellationToken);
            // workshop:end Payload
        }
        lesson.Expect("the relationship now has five stars", 5L, await WorkshopData.ScalarAsync(lesson.Connection!,
            "MATCH (:Person {id: $person})-[l:LIKES]->(:Movie {id: $movie}) RETURN l.stars AS value", cancellationToken, Ids()));
        await using (var db = lesson.CreateContext())
        {
            db.Likes.Remove(await db.Likes.SingleAsync(l => l.PersonId == SeedData.Hugo && l.MovieId == SeedData.JohnWick, cancellationToken));
            await db.SaveChangesAsync(cancellationToken);
        }
        lesson.Expect("deleting the Like deleted only the relationship", 0L, await Likes(lesson, cancellationToken));
        lesson.Expect("both people and movie remain", 2L, await WorkshopData.ScalarAsync(lesson.Connection!,
            "MATCH (n) WHERE (n:Person AND n.id = $person) OR (n:Movie AND n.id = $movie) RETURN count(n) AS value", cancellationToken, Ids()));
        lesson.Limit("A pair association allows one relationship per person and movie. For parallel relationships, use an identified relationship (lesson 11).");
    }

    private static (string, object?)[] Ids() => [("person", SeedData.Hugo.ToString("D")), ("movie", SeedData.JohnWick.ToString("D"))];
    private static Task<long> Likes(LessonContext lesson, CancellationToken cancellationToken)
        => WorkshopData.ScalarAsync(lesson.Connection!, "MATCH (:Person {id: $person})-[l:LIKES]->(:Movie {id: $movie}) RETURN count(l) AS value", cancellationToken, Ids());
    private static string Name(Guid id) => SeedData.People().Single(p => p.Id == id).Name;
    private static string Title(Guid id) => SeedData.Movies().Single(m => m.Id == id).Title;
}
