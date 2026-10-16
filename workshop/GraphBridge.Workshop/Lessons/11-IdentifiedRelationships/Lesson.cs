using GraphBridge.Workshop.Domain;
using Microsoft.EntityFrameworkCore;

namespace GraphBridge.Workshop.Lessons;

public sealed class Lesson11IdentifiedRelationships : ILesson
{
    public LessonInfo Info { get; } = new("11", 'B', "Identified relationships", "11-IdentifiedRelationships", LessonMode.Connected, Core: false,
        Requirement.Seed, "Give relationships their own keys so one person can hold several roles in one movie.");

    public async Task RunAsync(LessonContext lesson, CancellationToken cancellationToken)
    {
        lesson.Step("Map ACTED_IN with its own key");
        lesson.Code("Credits", "Domain/MoviesContext.cs");

        lesson.Step("Hugo Weaving plays two roles in Cloud Atlas: two parallel relationships");
        await using (var db = lesson.CreateContext())
        {
            lesson.Code("Parallel");
            // workshop:start Parallel
            var query = db.Credits
                .Where(c => c.PersonId == SeedData.Hugo && c.MovieId == SeedData.CloudAtlas)
                .OrderBy(c => c.Billing);
            var credits = await query.ToListAsync(cancellationToken);
            // workshop:end Parallel
            lesson.Cypher("hugo-cloud-atlas", query.ToQueryString());
            lesson.Table(credits.Select(c => new { c.Role, c.Billing, c.Id }));
            lesson.ExpectSequence("two roles, each its own relationship", ["Bill Smoke", "Nurse Noakes"], credits.Select(c => c.Role));
            lesson.Check("the two credits have different keys", credits.Select(c => c.Id).Distinct().Count() == 2);
        }
        lesson.Expect("the database holds two physical ACTED_IN relationships", 2L, await Physical(lesson, cancellationToken));

        lesson.Step("Add a third role, update its payload, then delete it");
        var id = Guid.NewGuid();
        await using (var db = lesson.CreateContext())
        {
            lesson.Code("Add");
            // workshop:start Add
            db.Credits.Add(new Credit { Id = id, PersonId = SeedData.Hugo, MovieId = SeedData.CloudAtlas, Role = "Old Georgie", Billing = 9 });
            await db.SaveChangesAsync(cancellationToken);          // a new relationship between the same two nodes
            // workshop:end Add
        }
        lesson.Expect("now three parallel relationships", 3L, await Physical(lesson, cancellationToken));
        await using (var db = lesson.CreateContext())
        {
            var credit = await db.Credits.SingleAsync(c => c.Id == id, cancellationToken);
            credit.Billing = 5;
            await db.SaveChangesAsync(cancellationToken);
            db.Credits.Remove(credit);
            await db.SaveChangesAsync(cancellationToken);
        }
        lesson.Expect("back to two", 2L, await Physical(lesson, cancellationToken));
        lesson.Limit("A relationship's endpoints cannot be changed. To move a credit, delete it and create a new one with a new key.");
    }

    private static Task<long> Physical(LessonContext lesson, CancellationToken cancellationToken)
        => WorkshopData.ScalarAsync(lesson.Connection!,
            "MATCH (:Person {id: $person})-[r:ACTED_IN]->(:Movie {id: $movie}) RETURN count(r) AS value", cancellationToken,
            ("person", SeedData.Hugo.ToString("D")), ("movie", SeedData.CloudAtlas.ToString("D")));
}
