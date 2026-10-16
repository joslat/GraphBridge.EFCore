using GraphBridge.Tooling.Diagnostics;

namespace GraphBridge.Workshop.Lessons;

public sealed class Lesson27Verify : ILesson
{
    public LessonInfo Info { get; } = new("27", 'F', "GraphBridge Verify", "27-Verify", LessonMode.Mixed, Core: false,
        Requirement.Schema, "Turn registered query contracts into pass/fail evidence: compile offline, then plan against a real database.");

    public async Task RunAsync(LessonContext lesson, CancellationToken cancellationToken)
    {
        await using var db = lesson.CreateContext();

        lesson.Step("V0: compile every contract offline");
        lesson.Code("Offline");
        // workshop:start Offline
        var offline = GraphVerify.Offline(db, WorkshopContracts.Create());
        // workshop:end Offline
        lesson.Value("Status", offline.Status);
        lesson.Expect("exit code 0: every contract compiles", 0, offline.ExitCode);

        if (!lesson.NeedsDatabase("V1 planning against the database")) return;

        lesson.Step("V1: inspect the schema and EXPLAIN every contract");
        lesson.Code("Connected");
        // workshop:start Connected
        var connected = await GraphVerify.InspectAsync(db, WorkshopContracts.Create(), "workshop.local.v1", cancellationToken: cancellationToken);
        // workshop:end Connected
        lesson.Value("Status", connected.Status);
        lesson.Expect("exit code 0: schema and plans satisfy the contracts", 0, connected.ExitCode);
        lesson.Note("EXPLAIN plans a query without running it. V1 never executes registered results, PROFILE or DDL.");
        lesson.Limit("V2 compares results with independent native references on an explicitly provisioned disposable fixture (docs/tools/VERIFY.md#graphbridge-verify-v2-semantic-comparison); it is a maintainer tool and not part of this workshop.");
    }
}
