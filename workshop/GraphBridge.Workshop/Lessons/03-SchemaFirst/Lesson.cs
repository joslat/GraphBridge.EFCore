using GraphBridge.EntityFrameworkCore.Schema;
using Microsoft.EntityFrameworkCore;

namespace GraphBridge.Workshop.Lessons;

public sealed class Lesson03SchemaFirst : ILesson
{
    public LessonInfo Info { get; } = new("03", 'A', "Schema before data", "03-SchemaFirst", LessonMode.Connected, Core: true,
        Requirement.Guarded, "Inspect the database schema, review the provider's proposals and apply them explicitly.");

    public async Task RunAsync(LessonContext lesson, CancellationToken cancellationToken)
    {
        lesson.Step("Compare the model with the database schema (metadata reads only)");
        await using var db = lesson.CreateContext();
        lesson.Code("Inspect");
        // workshop:start Inspect
        var inspection = await db.Database.InspectGraphSchemaAsync(cancellationToken: cancellationToken);
        var issues = inspection.Comparison.Issues
            .GroupBy(i => (i.Status, i.Code))
            .Select(g => new { g.Key.Status, g.Key.Code, Count = g.Count() });
        var proposals = inspection.Comparison.Proposals.Select(p => new { p.Name, p.Kind, p.LabelOrType, p.IsRequired });
        // workshop:end Inspect
        lesson.Value("Status", inspection.Comparison.Status);
        lesson.Table(issues.OrderBy(i => i.Status).ThenBy(i => i.Code));
        lesson.Table(proposals);
        lesson.Note("Fail issues block writes (missing key constraints). Warn issues do not: on Community edition, requiredness and property types cannot be enforced by constraints, so the application enforces them.");
        lesson.Note("Proposals are text for you to review. Inspection never runs DDL, and SaveChangesAsync refuses to write without the key constraints.");

        lesson.Step("Apply the required uniqueness constraints, deliberately");
        foreach (var proposal in inspection.Comparison.Proposals.Where(p => p.IsRequired).Take(1))
            lesson.Cypher("first-proposal-shape", Shape(proposal));
        var applied = await WorkshopData.EnsureSchemaAsync(lesson.Connection!, cancellationToken);
        lesson.Value("Statements applied now", applied.Count);
        lesson.Note(applied.Count == 0 ? "Nothing to apply: the constraints already exist from an earlier run." : "The workshop host ran each reviewed statement through the driver.");

        lesson.Step("Inspect again");
        var after = await db.Database.InspectGraphSchemaAsync(cancellationToken: cancellationToken);
        lesson.Value("Status", after.Comparison.Status);
        lesson.Check("no required proposals remain", after.Comparison.Proposals.All(p => !p.IsRequired));
        lesson.Check("the schema comparison does not fail", after.Comparison.Status != GraphSchemaStatus.Fail);
        lesson.Limit("Uniqueness constraints do not make a property required, and inspection does not scan data for duplicates or gaps.");
    }

    // Constraint names include the database's own naming; the snapshot keeps the reviewed statement shape.
    private static string Shape(GraphSchemaProposal proposal) => proposal.Cypher.Replace(proposal.Name, "<name>");
}
