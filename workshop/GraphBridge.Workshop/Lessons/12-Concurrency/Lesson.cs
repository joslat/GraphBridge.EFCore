using GraphBridge.Workshop.Domain;
using Microsoft.EntityFrameworkCore;

namespace GraphBridge.Workshop.Lessons;

public sealed class Lesson12Concurrency : ILesson
{
    public LessonInfo Info { get; } = new("12", 'C', "Optimistic concurrency", "12-Concurrency", LessonMode.Connected, Core: false,
        Requirement.Seed, "Detect a lost update with a provider-maintained row version, and recover by reading again.");

    public async Task RunAsync(LessonContext lesson, CancellationToken cancellationToken)
    {
        lesson.Step("Two users load the same person");
        await using var first = lesson.CreateContext();
        await using var second = lesson.CreateContext();
        var mine = await first.People.SingleAsync(p => p.Id == SeedData.Emil, cancellationToken);
        var theirs = await second.People.SingleAsync(p => p.Id == SeedData.Emil, cancellationToken);
        var loadedVersion = mine.Version;
        lesson.Value("Version both loaded", loadedVersion);

        lesson.Step("The first save wins and moves the version");
        lesson.Code("First");
        // workshop:start First
        mine.Nickname = "Neo4j co-founder";
        await first.SaveChangesAsync(cancellationToken);      // MATCH ... WHERE version = loaded → SET version = loaded + 1
        // workshop:end First
        lesson.Expect("the version moved by one", loadedVersion + 1, mine.Version);

        lesson.Step("The second save is based on a stale version and is refused");
        lesson.Code("Second");
        // workshop:start Second
        theirs.Nickname = "Graph person";
        await lesson.ExpectFailureAsync<DbUpdateConcurrencyException>("the stale save is refused",
            () => second.SaveChangesAsync(cancellationToken));
        // workshop:end Second
        await using (var check = lesson.CreateContext())
            lesson.Expect("the first writer's value survived", "Neo4j co-founder",
                (await check.People.SingleAsync(p => p.Id == SeedData.Emil, cancellationToken)).Nickname);

        lesson.Step("Recover: read again in a fresh context, decide, save");
        lesson.Code("Recover");
        // workshop:start Recover
        await using (var retry = lesson.CreateContext())
        {
            var current = await retry.People.SingleAsync(p => p.Id == SeedData.Emil, cancellationToken);
            current.Nickname = "Emil";                          // the workshop's seeded value
            await retry.SaveChangesAsync(cancellationToken);
        }
        // workshop:end Recover
        await using (var check = lesson.CreateContext())
        {
            var emil = await check.People.SingleAsync(p => p.Id == SeedData.Emil, cancellationToken);
            lesson.Expect("the nickname is back to its seeded value", "Emil", emil.Nickname);
            lesson.Expect("two successful saves, two versions", loadedVersion + 2, emil.Version);
        }
        lesson.Limit("There is no automatic merge or retry. Rollback does not rewind entities EF already accepted; use a fresh context (lesson 13).");
    }
}
