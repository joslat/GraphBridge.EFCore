using Microsoft.EntityFrameworkCore;

namespace GraphBridge.Workshop.Lessons;

public sealed class Lesson05LinqQueries : ILesson
{
    public LessonInfo Info { get; } = new("05", 'A', "LINQ queries and visible Cypher", "05-LinqQueries", LessonMode.Mixed, Core: true,
        Requirement.Seed, "Filter, order, page and count with LINQ, and read the Cypher each query becomes.");

    public async Task RunAsync(LessonContext lesson, CancellationToken cancellationToken)
    {
        await using var db = lesson.CreateContext();

        lesson.Step("Filter, order and select one column");
        lesson.Code("Born");
        // workshop:start Born
        var bornBefore = 1965;
        var veterans = db.People
            .Where(p => p.Born < bornBefore)
            .OrderBy(p => p.Born)
            .Select(p => p.Name);
        // workshop:end Born
        lesson.Cypher("born-before", veterans.ToQueryString());

        lesson.Step("Count, Any and First");
        lesson.Code("Aggregates");
        // workshop:start Aggregates
        var nicknamed = db.People.Where(p => p.Nickname != null);
        var bornLate = db.People.Where(p => p.Born > 1975);
        var youngest = db.People.OrderByDescending(p => p.Born).Select(p => p.Name);
        // workshop:end Aggregates
        lesson.Cypher("nicknamed", nicknamed.ToQueryString());

        lesson.Step("Membership in a local list becomes one list parameter");
        lesson.Code("Contains");
        // workshop:start Contains
        string[] names = ["Keanu Reeves", "Tom Hanks", "Nobody"];
        var cast = db.People.Where(p => names.Contains(p.Name)).OrderBy(p => p.Name).Select(p => p.Name);
        // workshop:end Contains
        lesson.Cypher("contains", cast.ToQueryString());

        if (!lesson.NeedsDatabase("Running the queries")) return;

        lesson.Step("Run them");
        var veteranNames = await veterans.ToListAsync(cancellationToken);
        lesson.Table(veteranNames);
        lesson.ExpectSequence("people born before 1965, oldest first", ["Tom Hanks", "Hugo Weaving", "Laurence Fishburne", "Keanu Reeves"], veteranNames);
        var count = await nicknamed.CountAsync(cancellationToken);
        var any = await bornLate.AnyAsync(cancellationToken);
        var first = await youngest.FirstAsync(cancellationToken);
        var found = await cast.ToListAsync(cancellationToken);
        lesson.Value("People with a nickname (CountAsync)", count);
        lesson.Value("Anyone born after 1975 (AnyAsync)", any);
        lesson.Value("Youngest person (FirstAsync)", first);
        lesson.Value("Names found from the list (Contains)", string.Join(", ", found));
        lesson.Expect("two people have a nickname", 2, count);
        lesson.Check("someone was born after 1975", any);
        lesson.Expect("the youngest person", "Emil Eifrem", first);
        lesson.ExpectSequence("two of the three names exist", ["Keanu Reeves", "Tom Hanks"], found);
        lesson.Note("Every value above travelled as a parameter. The query text is the same whatever the values are.");
    }
}
