using GraphBridge.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore;

namespace GraphBridge.Workshop.Lessons;

public sealed class Lesson06FailureLab : ILesson
{
    public LessonInfo Info { get; } = new("06", 'A', "The failure lab", "06-FailureLab", LessonMode.Offline, Core: true,
        Requirement.None, "See which query shapes GraphBridge refuses, and why refusing beats silently running them on the client.");

    public async Task RunAsync(LessonContext lesson, CancellationToken cancellationToken)
    {
        await using var db = lesson.CreateContext();
        lesson.Note("Every shape below fails while the query is compiled. None of them reaches Neo4j, and none runs on the client.");

        lesson.Step("A predicate that only C# could evaluate");
        lesson.Code("ClientMethod");
        // workshop:start ClientMethod
        lesson.ExpectFailure<Neo4jQueryTranslationException>("a custom C# method in Where is rejected",
            () => db.People.Where(p => LooksFamous(p.Name)).ToQueryString());
        // workshop:end ClientMethod
        lesson.Check("the C# method was never called", _calls == 0);

        lesson.Step("Shapes outside the supported LINQ subset");
        lesson.Code("Shapes");
        // workshop:start Shapes
        lesson.ExpectFailure<Neo4jQueryTranslationException>("GroupBy is rejected",
            () => db.Movies.GroupBy(m => m.Released).Select(g => g.Key).ToQueryString());
        lesson.ExpectFailure<Neo4jQueryTranslationException>("filtering after paging is rejected",
            () => db.People.Take(5).Where(p => p.Born > 1960).ToQueryString());
        lesson.ExpectFailure<Neo4jQueryTranslationException>("Distinct is rejected",
            () => db.Movies.Select(m => m.Released).Distinct().ToQueryString());
        // workshop:end Shapes

        lesson.Step("What to do instead");
        lesson.Note("Move the filter before Take, use the integer Min/Max aggregates, or write the query in Fluent Cypher (lesson 16) or native Cypher (lesson 15).");
        lesson.Limit("Rejection lists are part of the contract: docs/reference/KNOWN_LIMITATIONS.md names each unsupported shape and its alternative.");
    }

    private static int _calls;
    private static bool LooksFamous(string name) { _calls++; return name.Length > 10; }
}
