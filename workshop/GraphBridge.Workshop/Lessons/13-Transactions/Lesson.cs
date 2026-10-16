using GraphBridge.Workshop.Domain;
using Microsoft.EntityFrameworkCore;
using Neo4j.Driver;

namespace GraphBridge.Workshop.Lessons;

public sealed class Lesson13Transactions : ILesson
{
    public LessonInfo Info { get; } = new("13", 'C', "Transactions", "13-Transactions", LessonMode.Connected, Core: false,
        Requirement.Schema, "Group saves and native queries in one explicit transaction, and see what rollback does and does not undo.");

    public async Task RunAsync(LessonContext lesson, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        lesson.Step("Save inside an explicit transaction, read your own write, then roll back");
        Person draft;
        await using (var db = lesson.CreateContext())
        {
            lesson.Code("Rollback");
            // workshop:start Rollback
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            draft = new Person { Id = id, Name = "Draft Person" };
            db.People.Add(draft);
            await db.SaveChangesAsync(cancellationToken);
            // A native query borrows the same transaction, so it sees the uncommitted node.
            var inside = await db.Database.QueryCypherAsync("MATCH (p:Person {id: $id}) RETURN count(p) AS value",
                new Dictionary<string, object?> { ["id"] = id.ToString("D") }, r => r["value"].As<long>(), cancellationToken: cancellationToken);
            await transaction.RollbackAsync(cancellationToken);
            // workshop:end Rollback
            lesson.Expect("the transaction saw its own write", 1L, inside.Single());
            lesson.Expect("EF already accepted the save: the entry is Unchanged, not Added", EntityState.Unchanged, db.Entry(draft).State);
        }
        await using (var fresh = lesson.CreateContext())
            lesson.Check("after rollback the node does not exist", !await fresh.People.AnyAsync(p => p.Id == id, cancellationToken));
        lesson.Note("Rollback undoes the database work, not EF's in-memory state. After a rollback, discard the context and start fresh.");

        lesson.Step("Two saves, one commit");
        lesson.Code("Commit");
        // workshop:start Commit
        await using (var db = lesson.CreateContext())
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var person = new Person { Id = id, Name = "Committed Person" };
            db.People.Add(person);
            await db.SaveChangesAsync(cancellationToken);
            person.Nickname = "Two saves";
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        // workshop:end Commit
        await using (var fresh = lesson.CreateContext())
        {
            var committed = await fresh.People.SingleAsync(p => p.Id == id, cancellationToken);
            lesson.Expect("both saves committed together", "Two saves", committed.Nickname);
            fresh.People.Remove(committed);
            await fresh.SaveChangesAsync(cancellationToken);
        }
        lesson.Limit("No savepoints, nested or ambient transactions, and no automatic retry. If a commit's outcome is unknown, check durable state before retrying (docs/features/TRANSACTIONS_AND_CONCURRENCY.md).");
    }
}
