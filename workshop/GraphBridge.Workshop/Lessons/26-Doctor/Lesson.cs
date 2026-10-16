using GraphBridge.Tooling.Diagnostics;

namespace GraphBridge.Workshop.Lessons;

/// <summary>Workshop queries registered as reviewable contracts for Doctor, Verify and Query Lab.</summary>
public static class WorkshopContracts
{
    // workshop:start Catalog
    public static GraphQueryCatalog Create()
    {
        var budget = new GraphQueryBudget(maxRows: 100, deadline: TimeSpan.FromSeconds(10));
        return new([
            GraphQueryContract.Fluent(new GraphQueryDefinition("workshop.coactors.v1",
                "Distinct co-actor names ordered by name.", "Lessons/16-FluentPatterns#Coactors",
                [new("name", GraphResultKind.String)], GraphResultComparison.Ordered, GraphDuplicatePolicy.Eliminate, budget),
                db => Lesson16FluentPatterns.Coactors(db, "Keanu Reeves")),
            GraphQueryContract.Fluent(new GraphQueryDefinition("workshop.recent-work.v1",
                "Everyone, with any movie after 2010 they acted in; people without one keep a null title.", "Lessons/17-FluentOptionalAndCounts#Optional",
                [new("name", GraphResultKind.String), new("title", GraphResultKind.String, nullable: true)],
                GraphResultComparison.Multiset, GraphDuplicatePolicy.Eliminate, budget),
                db => Lesson17FluentOptionalAndCounts.RecentWork(db))
        ]);
    }
    // workshop:end Catalog

    /// <summary>The exact C# of each registered query, for Query Lab's source view.</summary>
    public static IReadOnlyList<GraphQuerySource> Sources() =>
    [
        new("workshop.coactors.v1", SourceRegions.Get("Lessons/16-FluentPatterns/Lesson.cs", "Coactors")),
        new("workshop.recent-work.v1", SourceRegions.Get("Lessons/17-FluentOptionalAndCounts/Lesson.cs", "Optional"))
    ];
}

public sealed class Lesson26Doctor : ILesson
{
    public LessonInfo Info { get; } = new("26", 'F', "GraphBridge Doctor", "26-Doctor", LessonMode.Mixed, Core: true,
        Requirement.Schema, "Check that your model and registered queries compile, and what the database schema actually provides.");

    public async Task RunAsync(LessonContext lesson, CancellationToken cancellationToken)
    {
        await using var db = lesson.CreateContext();

        lesson.Step("Register queries as contracts");
        lesson.Code("Catalog", "Lessons/26-Doctor/Lesson.cs");
        lesson.Note("A contract is a public declaration: id, meaning, columns, duplicate policy and budget. Doctor compiles it; it does not run it.");

        lesson.Step("Offline Doctor: model and query compilation, no database");
        lesson.Code("Offline");
        // workshop:start Offline
        var offline = GraphDoctor.InspectOffline(db, WorkshopContracts.Create());
        // workshop:end Offline
        lesson.Value("Status", offline.Status);
        lesson.Table(offline.Diagnostics.Select(d => new { d.Code, d.Severity, d.Component }));
        lesson.Expect("offline evidence passes", GraphEvidenceStatus.Pass, offline.Status);
        lesson.Check("the JSON report contains no Cypher text by default", !offline.ToJson().Contains("MATCH", StringComparison.Ordinal));

        if (!lesson.NeedsDatabase("Connected Doctor (read-only schema inspection)")) return;

        lesson.Step("Connected Doctor: read-only schema inspection against this database");
        lesson.Code("Connected");
        // workshop:start Connected
        var connected = await GraphDoctor.InspectAsync(db, WorkshopContracts.Create(), "workshop.local.v1", cancellationToken: cancellationToken);
        // workshop:end Connected
        lesson.Value("Status", connected.Status);
        lesson.Table(connected.Diagnostics.Select(d => new { d.Code, d.Severity, d.Component }));
        lesson.Expect("connected Doctor passes; warnings are allowed and listed", GraphEvidenceStatus.Pass, connected.Status);
        lesson.Note("\"workshop.local.v1\" is a profile id: your own name for the environment this evidence describes, such as staging.v3.");
        lesson.Limit("Doctor observes metadata. It never repairs schema, runs registered query results or certifies production readiness.");
    }
}
