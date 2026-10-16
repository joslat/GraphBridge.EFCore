using GraphBridge.EntityFrameworkCore.FluentCypher;
using GraphBridge.Workshop.Domain;
using Microsoft.EntityFrameworkCore;
using Neo4j.Driver;
using Graph = GraphBridge.EntityFrameworkCore.FluentCypher.Cypher;

namespace GraphBridge.Workshop.Lessons;

public sealed record RoleCount(string Name, long Roles);

public sealed class Lesson22ThreeSurfaces : ILesson
{
    public LessonInfo Info { get; } = new("22", 'D', "Capstone: one question, three query surfaces", "22-ThreeSurfaces", LessonMode.Mixed, Core: true,
        Requirement.Seed, "Answer 'who acted in Cloud Atlas, in how many roles?' with LINQ, Fluent Cypher and native Cypher, and compare.");

    private const string Title = "Cloud Atlas";

    // workshop:start Fluent
    public static CypherResultQuery<RoleCount> FluentRoles(DbContext db, string title)
    {
        var person = Graph.Node<Person>();
        var movie = Graph.Node<Movie>();
        var roles = Graph.CountRows();
        return db.Cypher()
            .Match(person.Out<Credit>(movie))
            .Where(() => movie.Value.Title == title)
            .With(person, roles)
            .Return(() => new RoleCount(person.Value.Name, roles.Value))
            .OrderBy(() => person.Value.Name)
            .Limit(100);
    }
    // workshop:end Fluent

    // workshop:start NativeText
    public const string NativeRoles = """
        MATCH (p:Person)-[r:ACTED_IN]->(:Movie {title: $title})
        RETURN p.name AS name, count(r) AS roles
        ORDER BY name
        """;
    // workshop:end NativeText

    public async Task RunAsync(LessonContext lesson, CancellationToken cancellationToken)
    {
        await using var db = lesson.CreateContext();
        var movieId = SeedData.CloudAtlas;
        var creditQuery = db.Credits.Where(c => c.MovieId == movieId);

        lesson.Step("Surface 1 · EF Core LINQ: tracked entities, EF semantics");
        lesson.Cypher("linq", creditQuery.ToQueryString());
        lesson.Step("Surface 2 · Fluent Cypher: native pattern, native semantics, untracked rows");
        lesson.Code("Fluent");
        lesson.Cypher("fluent", FluentRoles(db, Title).ToQueryString());
        lesson.Step("Surface 3 · Native Cypher: full control, you own correctness");
        lesson.Code("NativeText");

        if (!lesson.NeedsDatabase("Comparing the three answers")) return;

        lesson.Step("Run LINQ: GroupBy is not translated (lesson 06), so count the bounded credit list in memory");
        lesson.Code("Linq");
        // workshop:start Linq
        var credits = await db.Credits.Where(c => c.MovieId == movieId).ToListAsync(cancellationToken);
        Guid[] personIds = [.. credits.Select(c => c.PersonId).Distinct()];
        var names = await db.People.Where(p => personIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Name, cancellationToken);
        var linq = credits.GroupBy(c => c.PersonId)
            .Select(g => new RoleCount(names[g.Key], g.Count()))
            .OrderBy(r => r.Name, StringComparer.Ordinal).ToList();
        // workshop:end Linq
        lesson.Table(linq);

        lesson.Step("Run Fluent Cypher");
        var fluent = await FluentRoles(db, Title).ToListAsync(cancellationToken);
        lesson.Table(fluent);

        lesson.Step("Run native Cypher with an explicit projector");
        lesson.Code("Native");
        // workshop:start Native
        var native = await db.Database.QueryCypherAsync(NativeRoles,
            new Dictionary<string, object?> { ["title"] = Title },
            record => new RoleCount(record["name"].As<string>(), record["roles"].As<long>()),
            maxRows: 100, cancellationToken: cancellationToken);
        // workshop:end Native
        lesson.Table(native);

        lesson.Step("Compare");
        lesson.ExpectSequence("LINQ and Fluent agree", linq, fluent);
        lesson.ExpectSequence("Fluent and native agree", native, fluent);
        lesson.ExpectSequence("two actors, two roles each", [new RoleCount("Hugo Weaving", 2), new RoleCount("Tom Hanks", 2)], fluent);
        lesson.Expect("only LINQ tracked anything: four credits and two people", 6, db.ChangeTracker.Entries().Count());
        lesson.Note("Same answer, different contracts. LINQ tracked entities and checked edge identity. Fluent and native returned untracked rows; native Cypher is checked only by you.");
    }
}
