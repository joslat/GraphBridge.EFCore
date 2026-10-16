using GraphBridge.EntityFrameworkCore.Diagnostics;
using GraphBridge.Workshop.Domain;
using Microsoft.EntityFrameworkCore;

namespace GraphBridge.Workshop.Lessons;

public sealed class Lesson21BulkMutations : ILesson
{
    public LessonInfo Info { get; } = new("21", 'D', "Bulk update and delete", "21-BulkMutations", LessonMode.Connected, Core: false,
        Requirement.Seed, "Change or delete many nodes on the server without loading them, and see what that means for tracked entities.");

    public async Task RunAsync(LessonContext lesson, CancellationToken cancellationToken)
    {
        Guid[] ids = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];
        await using (var setup = lesson.CreateContext())
        {
            setup.People.AddRange(ids.Select((id, i) => new Person { Id = id, Name = $"Extra {i + 1}", Born = 1900 }));
            await setup.SaveChangesAsync(cancellationToken);
        }

        lesson.Step("Update three people on the server, in one statement");
        await using var db = lesson.CreateContext();
        var tracked = await db.People.SingleAsync(p => p.Id == ids[0], cancellationToken);
        lesson.Code("Update");
        // workshop:start Update
        var updated = await db.People
            .Where(p => ids.Contains(p.Id))
            .ExecuteUpdateAsync(set => set
                .SetProperty(p => p.Nickname, "Background actor")
                .SetProperty(p => p.Born, 2000), cancellationToken);
        // workshop:end Update
        lesson.Expect("three rows updated", 3, updated);
        lesson.Expect("the tracked copy is now stale: EF did not see the change", 1900, tracked.Born);
        await using (var fresh = lesson.CreateContext())
            lesson.Check("a fresh context sees the new values",
                (await fresh.People.Where(p => ids.Contains(p.Id)).ToListAsync(cancellationToken)).All(p => p.Born == 2000 && p.Nickname == "Background actor"));

        lesson.Step("Keys are identity: setting one is refused");
        await lesson.ExpectFailureAsync<Neo4jQueryTranslationException>("a key setter is rejected before any database work",
            () => db.People.Where(p => ids.Contains(p.Id)).ExecuteUpdateAsync(set => set.SetProperty(p => p.Id, Guid.NewGuid()), cancellationToken));

        lesson.Step("Delete them on the server");
        lesson.Code("Delete");
        // workshop:start Delete
        var deleted = await db.People.Where(p => ids.Contains(p.Id)).ExecuteDeleteAsync(cancellationToken);
        // workshop:end Delete
        lesson.Expect("three rows deleted", 3, deleted);
        await using (var fresh = lesson.CreateContext())
            lesson.Check("they are gone", !await fresh.People.AnyAsync(p => ids.Contains(p.Id), cancellationToken));
        lesson.Limit("At most 10,000 selected rows per call (more aborts the whole call). Deletes are restrictive: a node with relationships is not deleted. Tracked entities are never refreshed.");
    }
}
