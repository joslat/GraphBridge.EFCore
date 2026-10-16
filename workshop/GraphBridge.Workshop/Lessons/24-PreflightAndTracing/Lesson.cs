using System.Collections.Concurrent;
using System.Diagnostics;
using GraphBridge.EntityFrameworkCore.Diagnostics;
using GraphBridge.EntityFrameworkCore.Schema;
using GraphBridge.Workshop.Domain;
using Microsoft.EntityFrameworkCore;

namespace GraphBridge.Workshop.Lessons;

public sealed class Lesson24PreflightAndTracing : ILesson
{
    public LessonInfo Info { get; } = new("24", 'E', "Deployment preflight and command tracing", "24-PreflightAndTracing", LessonMode.Connected, Core: false,
        Requirement.Seed, "Gate a deployment on the schema the model needs, and observe every database command without leaking data.");

    public async Task RunAsync(LessonContext lesson, CancellationToken cancellationToken)
    {
        await using var db = lesson.CreateContext();

        lesson.Step("A deployment gate: does this database have the schema the model needs?");
        lesson.Code("Preflight");
        // workshop:start Preflight
        var inspection = await db.Database.InspectGraphSchemaAsync(cancellationToken: cancellationToken);
        var deploymentExitCode = inspection.Comparison.Status == GraphSchemaStatus.Fail ? 1 : 0;   // warnings do not block
        // workshop:end Preflight
        lesson.Value("Status", inspection.Comparison.Status);
        lesson.Value("Warnings", inspection.Comparison.Issues.Count(i => i.Status == GraphSchemaStatus.Warn));
        lesson.Expect("the gate passes", 0, deploymentExitCode);
        lesson.Note("Run the same check in a release pipeline with read-only credentials. It reads metadata only and never repairs anything.");

        lesson.Step("Listen to GraphBridge's activities");
        lesson.Code("Listener");
        // workshop:start Listener
        var activities = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == GraphBridgeDiagnostics.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activities.Enqueue
        };
        ActivitySource.AddActivityListener(listener);
        // workshop:end Listener

        var secret = "Keanu Reeves";
        var people = await db.People.Where(p => p.Name == secret).ToListAsync(cancellationToken);
        var id = Guid.NewGuid();
        db.People.Add(new Person { Id = id, Name = "Traced Person" });
        await db.SaveChangesAsync(cancellationToken);
        db.People.Remove(await db.People.SingleAsync(p => p.Id == id, cancellationToken));
        await db.SaveChangesAsync(cancellationToken);

        var seen = activities.ToArray();
        lesson.Table(seen.Select(a => new
        {
            Operation = a.OperationName,
            Kind = a.GetTagItem("graphbridge.command.kind"),
            Fingerprint = (a.GetTagItem("graphbridge.statement.fingerprint") as string)?[..12]
        }));
        lesson.Expect("the query found Keanu", 1, people.Count);
        lesson.Check("the LINQ query produced a GraphBridge.Query activity", seen.Any(a => a.OperationName == GraphBridgeDiagnostics.QueryActivityName));
        lesson.Check("the saves produced GraphBridge.SaveChanges activities", seen.Count(a => a.OperationName == GraphBridgeDiagnostics.SaveChangesActivityName) >= 2);
        lesson.Check("statements carry a fingerprint, not their text", seen.Any(a => a.GetTagItem("graphbridge.statement.fingerprint") is string));
        lesson.Check("no tag contains the parameter value",
            seen.SelectMany(a => a.TagObjects).All(t => t.Value?.ToString()?.Contains(secret, StringComparison.Ordinal) != true));
        lesson.Limit("Activities carry kinds, counts, fingerprints and outcomes. Statement text, parameter values, database names and exception messages are never recorded.");
    }
}
