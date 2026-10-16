using GraphBridge.Workshop.Domain;
using Microsoft.EntityFrameworkCore;
using Neo4j.Driver;
using Graph = GraphBridge.EntityFrameworkCore.FluentCypher.Cypher;

namespace GraphBridge.Workshop.Lessons;

public sealed class Lesson14NullsTypesTime : ILesson
{
    public LessonInfo Info { get; } = new("14", 'C', "Nulls, types and time", "14-NullsTypesTime", LessonMode.Mixed, Core: false,
        Requirement.Seed, "See where C# and Cypher disagree about null, how Guids are stored, and how timestamps keep their offsets.");

    public async Task RunAsync(LessonContext lesson, CancellationToken cancellationToken)
    {
        await using var db = lesson.CreateContext();

        lesson.Step("One question, two semantics: whose nickname is not 'Larry'?");
        lesson.Code("Questions");
        // workshop:start Questions
        // LINQ keeps C# semantics: a missing nickname is "not Larry".
        var linq = db.People.Where(p => p.Nickname != "Larry").OrderBy(p => p.Name).Select(p => p.Name);
        // Fluent with an explicit predicate keeps native semantics: null <> 'Larry' is unknown, so the row drops.
        var person = Graph.Node<Person>();
        var fluent = db.Cypher().Match(person).Where(person.Property(p => p.Nickname).Ne("Larry"))
            .Return(() => person.Value.Name).OrderBy(() => person.Value.Name).Limit(100);
        // workshop:end Questions
        lesson.Cypher("linq-not-larry", linq.ToQueryString());
        lesson.Cypher("fluent-not-larry", fluent.ToQueryString());
        lesson.ExpectFailure<NotSupportedException>("the C# lambda form is refused because it would be ambiguous (GBFC110)",
            () => db.Cypher().Match(person).Where(() => person.Value.Nickname != "Larry").Return(() => person.Value.Name).ToQueryString());

        lesson.Step("Instants in LINQ, offsets preserved in storage");
        lesson.Code("Time");
        // workshop:start Time
        var since = new DateTimeOffset(2023, 7, 1, 18, 0, 0, TimeSpan.Zero);
        var recentReviews = db.Reviews.Where(r => r.CreatedAt >= since).OrderBy(r => r.CreatedAt).Select(r => r.CreatedAt);
        // workshop:end Time
        lesson.Cypher("reviews-since", recentReviews.ToQueryString());

        if (!lesson.NeedsDatabase("Running the comparisons")) return;

        lesson.Step("Run them");
        var linqNames = await linq.ToListAsync(cancellationToken);
        var fluentNames = await fluent.ToListAsync(cancellationToken);
        lesson.Value("LINQ", string.Join(", ", linqNames));
        lesson.Value("Fluent", string.Join(", ", fluentNames));
        lesson.Expect("LINQ keeps everyone except Laurence", 8, linqNames.Count);
        lesson.ExpectSequence("native Cypher keeps only non-null nicknames that differ", ["Emil Eifrem"], fluentNames);

        var times = await recentReviews.ToListAsync(cancellationToken);
        lesson.Table(times.Select(t => new { Stored = t, Utc = t.UtcDateTime }));
        lesson.Expect("two reviews on or after 18:00 UTC on 1 July 2023", 2, times.Count);
        lesson.Expect("the +02:00 review kept its offset", TimeSpan.FromHours(2), times[0].Offset);

        lesson.Step("Guids are stored as canonical strings");
        var stored = await WorkshopData.ReadAsync(lesson.Connection!, "MATCH (p:Person {name: 'Keanu Reeves'}) RETURN p.id AS id",
            r => r["id"].As<string>(), cancellationToken);
        lesson.Expect("the stored key is the lowercase D-format string", SeedData.Keanu.ToString("D"), stored.Single());
        lesson.Limit("LINQ compares DateTimeOffset as instants (UTC); Fluent Cypher keeps native zoned semantics. Choose the surface deliberately for timestamp comparisons.");
    }
}
