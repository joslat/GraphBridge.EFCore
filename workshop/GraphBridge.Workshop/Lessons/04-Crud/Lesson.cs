using GraphBridge.Workshop.Domain;
using Neo4j.Driver;
using Microsoft.EntityFrameworkCore;

namespace GraphBridge.Workshop.Lessons;

public sealed class Lesson04Crud : ILesson
{
    public LessonInfo Info { get; } = new("04", 'A', "Create, read, update, delete", "04-Crud", LessonMode.Connected, Core: true,
        Requirement.Schema, "Save, query, change and delete a node with ordinary EF Core code and SaveChangesAsync.");

    public async Task RunAsync(LessonContext lesson, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        lesson.Step("Create: add an entity and save");
        lesson.Code("Create");
        // workshop:start Create
        var ada = new Person { Id = id, Name = "Ada Lovelace", Born = 1815 };
        await using (var db = lesson.CreateContext())
        {
            db.People.Add(ada);
            var saved = await db.SaveChangesAsync(cancellationToken);   // one EF entry → one CREATE
            lesson.Expect("one entry saved", 1, saved);
        }
        // workshop:end Create
        lesson.Expect("the provider set the row version to 1", 1L, ada.Version);

        lesson.Step("Read in a fresh context: tracked and untracked");
        await using (var db = lesson.CreateContext())
        {
            lesson.Code("Read");
            // workshop:start Read
            var query = db.People.Where(p => p.Id == id);
            var tracked = await query.SingleAsync(cancellationToken);
            var detached = await db.People.AsNoTracking().Where(p => p.Id == id).SingleAsync(cancellationToken);
            // workshop:end Read
            lesson.Cypher("read-by-key", query.ToQueryString());
            lesson.Expect("the tracked read finds Ada", "Ada Lovelace", tracked.Name);
            lesson.Expect("only the tracked read is in the change tracker", 1, db.ChangeTracker.Entries().Count());
            lesson.Check("AsNoTracking returns a separate, untracked object", !ReferenceEquals(tracked, detached));
        }

        lesson.Step("Update: change a property and save");
        // Another application writes a property this model does not map. A partial update must leave it alone.
        await WorkshopData.ExecuteAsync(lesson.Connection!, "MATCH (p:Person {id: $id}) SET p.source = 'archive'", cancellationToken,
            ("id", id.ToString("D")));
        await using (var db = lesson.CreateContext())
        {
            lesson.Code("Update");
            // workshop:start Update
            var person = await db.People.SingleAsync(p => p.Id == id, cancellationToken);
            person.Nickname = "Enchantress of Numbers";
            await db.SaveChangesAsync(cancellationToken);              // writes only the changed property
            // workshop:end Update
            lesson.Expect("the row version moved to 2", 2L, person.Version);
        }
        var stored = await WorkshopData.ReadAsync(lesson.Connection!, "MATCH (p:Person {id: $id}) RETURN p.nickname AS nickname, p.source AS source",
            r => (Nickname: r["nickname"].As<string>(), Source: r["source"].As<string>()), cancellationToken, ("id", id.ToString("D")));
        lesson.Expect("the nickname was written", "Enchantress of Numbers", stored.Single().Nickname);
        lesson.Expect("a property EF does not map survived the update", "archive", stored.Single().Source);

        lesson.Step("Delete and confirm in a fresh context");
        await using (var db = lesson.CreateContext())
        {
            lesson.Code("Delete");
            // workshop:start Delete
            var person = await db.People.SingleAsync(p => p.Id == id, cancellationToken);
            db.People.Remove(person);
            await db.SaveChangesAsync(cancellationToken);
            // workshop:end Delete
        }
        await using (var db = lesson.CreateContext())
            lesson.Check("Ada is gone", !await db.People.AnyAsync(p => p.Id == id, cancellationToken));
        lesson.Limit("A node that still has relationships cannot be deleted: deletion is restrictive, never a silent DETACH DELETE.");
    }
}
