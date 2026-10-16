using GraphBridge.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore;

namespace GraphBridge.Workshop.Lessons;

public sealed class Lesson23MigrationPlans : ILesson
{
    public LessonInfo Info { get; } = new("23", 'E', "Migration plans", "23-MigrationPlans", LessonMode.Mixed, Core: false,
        Requirement.Schema, "Change schema deliberately: preview a typed plan offline, validate it, apply it once, and see that reapplying is a no-op.");

    // workshop:start Plan
    public static GraphMigrationPlan Plan { get; } = new(
        "workshop-001-movie-released", predecessorId: null,
        [GraphMigrationStep.CreateIndex("released-index",
            new GraphMigrationSchemaDefinition("workshop_movie_released", GraphMigrationSchemaKind.RangeIndex, "NODE", "Movie", ["released"]))],
        GraphMigrationRollbackPolicy.NoRollback("Dropping the index is a separate, reviewed migration."));
    // workshop:end Plan

    public async Task RunAsync(LessonContext lesson, CancellationToken cancellationToken)
    {
        lesson.Step("A typed plan: one step, a checksum, an explicit rollback policy");
        lesson.Code("Plan");

        lesson.Step("Preview it offline: the exact Cypher, impact and preconditions");
        lesson.Code("Preview");
        // workshop:start Preview
        var preview = Plan.Preview();
        var journal = GraphMigrationJournal.PreviewInitialization();
        // workshop:end Preview
        lesson.Value("Checksum", preview.Checksum);
        lesson.Table(preview.Steps.Select(s => new { s.Id, s.Kind, s.Impact }));
        lesson.Cypher("step", preview.Steps.Single().Command.Cypher);
        lesson.Cypher("journal", string.Join("\n", journal));
        lesson.Note("The journal records which plans ran. Initializing it is itself an explicit, reviewable step: three uniqueness constraints.");

        if (!lesson.NeedsDatabase("Validating and applying the plan")) return;

        lesson.Step("Initialize the journal, validate, apply");
        await using var db = lesson.CreateContext();
        // A database can keep the index after its journal was cleared (a shared test fixture, for example). GraphBridge
        // refuses to adopt schema its journal did not record, so the lesson removes its own index first, visibly.
        var before = await db.Database.ValidateGraphMigrationAsync(Plan, cancellationToken: cancellationToken);
        if (!before.AlreadyApplied && await WorkshopData.ScalarAsync(lesson.Connection!,
                "SHOW INDEXES YIELD name WHERE name = 'workshop_movie_released' RETURN count(*) AS value", cancellationToken) > 0)
        {
            lesson.Note("The index exists, but the migration journal has no record of creating it. GraphBridge will not adopt unrecorded schema, so the lesson drops its own index, workshop_movie_released, and applies the plan again.");
            await WorkshopData.ExecuteAsync(lesson.Connection!, "DROP INDEX workshop_movie_released", cancellationToken);
        }
        lesson.Code("Apply");
        // workshop:start Apply
        await db.Database.InitializeGraphMigrationsAsync(cancellationToken: cancellationToken);
        var validation = await db.Database.ValidateGraphMigrationAsync(Plan, cancellationToken: cancellationToken);
        var applied = await db.Database.ApplyGraphMigrationAsync(Plan, cancellationToken: cancellationToken);
        // workshop:end Apply
        lesson.Value("Can apply", validation.CanApply);
        lesson.Value("Already applied before this run", validation.AlreadyApplied);
        lesson.Check("the journal is initialized", validation.IsInitialized);
        lesson.Check("the plan applied now, or had already been applied", applied.CompletedSteps == 1 || applied.AlreadyApplied);

        lesson.Step("Apply again: same checksum, nothing to do");
        var again = await db.Database.ApplyGraphMigrationAsync(Plan, cancellationToken: cancellationToken);
        lesson.Check("reapplying the same plan is a no-op", again.AlreadyApplied);
        lesson.Expect("the index exists", 1L, await WorkshopData.ScalarAsync(lesson.Connection!,
            "SHOW INDEXES YIELD name WHERE name = 'workshop_movie_released' RETURN count(*) AS value", cancellationToken));
        lesson.Limit("A plan with schema steps is not atomic as a whole; each step records its own completion. There is no automatic Down; dropping the index is another reviewed plan.");
    }
}
