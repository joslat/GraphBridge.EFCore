using GraphBridge.Workshop.Domain;
using Microsoft.EntityFrameworkCore;

namespace GraphBridge.Workshop.Lessons;

public sealed class Lesson01FirstContext : ILesson
{
    public LessonInfo Info { get; } = new("01", 'A', "Your first context", "01-FirstContext", LessonMode.Offline, Core: true,
        Requirement.None, "Configure an EF Core context for Neo4j with a driver your application owns, and see your first query.");

    public async Task RunAsync(LessonContext lesson, CancellationToken cancellationToken)
    {
        lesson.Step("Lend the application's Neo4j driver to EF Core");
        lesson.Code("Configure", "Domain/MoviesContext.cs");
        lesson.Note("Your application creates and disposes the IDriver (usually once, as a singleton). EF borrows it; disposing a context never closes the driver.");

        lesson.Step("EF Core builds and validates the model");
        await using var db = lesson.CreateContext();
        lesson.Value("Provider", db.Database.ProviderName);
        lesson.Value("Mapped entity types", string.Join(", ", db.Model.GetEntityTypes().Select(e => e.ClrType.Name).Order()));
        lesson.Expect("GraphBridge is the active EF provider", "GraphBridge.EntityFrameworkCore", db.Database.ProviderName);
        lesson.Expect("six entity types are mapped", 6, db.Model.GetEntityTypes().Count());

        lesson.Step("Your first query, compiled to Cypher without touching the database");
        lesson.Code("FirstQuery");
        // workshop:start FirstQuery
        var name = "Keanu Reeves";
        var query = db.People.Where(p => p.Name == name).OrderBy(p => p.Name).Take(5);
        var cypher = query.ToQueryString();   // parameter placeholders only, never the value
        // workshop:end FirstQuery
        lesson.Cypher("first-query", cypher);
        lesson.Check("the value is a parameter, not part of the Cypher text", !cypher.Contains(name, StringComparison.Ordinal));

        lesson.Step("Async only: synchronous calls are rejected before any database work");
        lesson.Code("AsyncOnly");
        // workshop:start AsyncOnly
        lesson.ExpectFailure<NotSupportedException>("synchronous enumeration is rejected", () => query.ToList());
        db.People.Add(new Person { Id = Guid.NewGuid(), Name = "Not saved" });
        lesson.ExpectFailure<NotSupportedException>("synchronous SaveChanges is rejected", () => db.SaveChanges());
        // workshop:end AsyncOnly
        lesson.Note("Use ToListAsync, SingleAsync, SaveChangesAsync and await using. Nothing above contacted Neo4j.");
    }
}
