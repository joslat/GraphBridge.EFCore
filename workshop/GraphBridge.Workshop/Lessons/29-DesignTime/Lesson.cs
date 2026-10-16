using System.Text.Json;
using GraphBridge.EntityFrameworkCore.Migrations;
using GraphBridge.Tooling.DesignTime;

namespace GraphBridge.Workshop.Lessons;

public sealed class Lesson29DesignTime : ILesson
{
    public LessonInfo Info { get; } = new("29", 'F', "Design-time migration commands", "29-DesignTime", LessonMode.Mixed, Core: false,
        Requirement.Schema, "Give your application list, preview and status commands for its migration plans, with machine-readable output.");

    public async Task RunAsync(LessonContext lesson, CancellationToken cancellationToken)
    {
        lesson.Step("Host the commands in your application, over your own plan catalog");
        lesson.Code("Commands");
        // workshop:start Commands
        IReadOnlyList<GraphMigrationPlan> catalog = [Lesson23MigrationPlans.Plan];
        async Task<(int ExitCode, string Json)> Run(params string[] args)
        {
            var output = new StringWriter();
            var errors = new StringWriter();
            var exitCode = await GraphMigrationCommands.RunAsync(args, catalog, () => lesson.CreateContext(), output, errors, cancellationToken);
            if (errors.ToString().Trim() is { Length: > 0 } message) lesson.Note($"{args[0]} reported: {message}");
            return (exitCode, output.ToString());
        }
        // workshop:end Commands

        lesson.Step("list and preview need no database");
        var list = await Run("list");
        var preview = await Run("preview", Lesson23MigrationPlans.Plan.Id);
        lesson.Expect("list exits 0", 0, list.ExitCode);
        lesson.Expect("preview exits 0", 0, preview.ExitCode);
        lesson.Check("list reports the catalog as JSON", Parses(list.Json) && list.Json.Contains(Lesson23MigrationPlans.Plan.Id, StringComparison.Ordinal));
        lesson.Check("preview includes the plan checksum", preview.Json.Contains(Lesson23MigrationPlans.Plan.Preview().Checksum, StringComparison.Ordinal));
        lesson.Note("A build step or reviewer can diff this JSON. Values in parameters are hidden unless --include-values is passed.");

        if (!lesson.NeedsDatabase("The read-only status command")) return;

        lesson.Step("status reads the journal, read-only");
        var status = await Run("status", Lesson23MigrationPlans.Plan.Id);
        lesson.Value("Exit code", status.ExitCode);
        lesson.Check("status is 0 (ready or applied) or 3 (needs attention, such as an uninitialized journal)", status.ExitCode is 0 or 3);
        lesson.Limit("These commands never initialize or apply plans; applying is an explicit deployment step (lesson 23). Relational `dotnet ef migrations` commands are not supported.");
    }

    private static bool Parses(string json)
    {
        try { using var _ = JsonDocument.Parse(json); return true; }
        catch (JsonException) { return false; }
    }
}
