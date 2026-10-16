using Microsoft.EntityFrameworkCore;
using Neo4j.Driver;

namespace GraphBridge.Workshop.Lessons;

public sealed record MovieStats(string Title, long Reviews, double? AverageRating);

public sealed class Lesson15NativeCypher : ILesson
{
    public LessonInfo Info { get; } = new("15", 'D', "Native Cypher with a projector", "15-NativeCypher", LessonMode.Connected, Core: false,
        Requirement.Seed, "Run reviewed, parameterized Cypher through the same context, transaction and driver, with an explicit row cap.");

    // workshop:start Statement
    public const string ReviewStats = """
        MATCH (m:Movie)
        WHERE m.released >= $from
        OPTIONAL MATCH (m)-[:HAS_REVIEW]->(r:Review)
        RETURN m.title AS title, count(r) AS reviews, avg(r.rating) AS average
        ORDER BY reviews DESC, title
        """;
    // workshop:end Statement

    public async Task RunAsync(LessonContext lesson, CancellationToken cancellationToken)
    {
        await using var db = lesson.CreateContext();

        lesson.Step("A report LINQ cannot express: reviews per movie, with an average");
        lesson.Code("Statement");
        lesson.Note("Aggregates over dependents such as Movie and Review, OPTIONAL MATCH and avg() are outside the LINQ subset (lesson 07). Native Cypher covers them.");

        lesson.Step("Run it with parameters and an explicit projector");
        lesson.Code("Run");
        // workshop:start Run
        var rows = await db.Database.QueryCypherAsync(ReviewStats,
            new Dictionary<string, object?> { ["from"] = 1999L },
            record => new MovieStats(record["title"].As<string>(), record["reviews"].As<long>(),
                record["average"] is null ? null : record["average"].As<double>()),
            maxRows: 50, cancellationToken: cancellationToken);
        // workshop:end Run
        lesson.Table(rows);
        lesson.Expect("one row per movie", 5, rows.Count);
        lesson.Expect("The Matrix: two reviews averaging 4.5", new MovieStats("The Matrix", 2, 4.5), rows[0]);
        lesson.Check("movies without reviews keep a null average", rows.Any(r => r.Title == "John Wick" && r.AverageRating is null));
        lesson.Expect("native results are never tracked", 0, db.ChangeTracker.Entries().Count());
        lesson.Limit("GraphBridge does not inspect native Cypher; it only enforces maxRows. Keeping values in parameters is your job: review native Cypher like any SQL you write by hand.");
    }
}
