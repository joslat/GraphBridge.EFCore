using GraphBridge.Tooling.Scaffolding;

namespace GraphBridge.Workshop.Lessons;

public sealed class Lesson30AuthoringToolchains : ILesson
{
    public LessonInfo Info { get; } = new("30", 'F', "Authoring toolchains: database-first", "30-AuthoringToolchains", LessonMode.Connected, Core: false,
        Requirement.Seed, "Inspect the graph you built in this workshop and scaffold a reviewed EF model from it.");

    public async Task RunAsync(LessonContext lesson, CancellationToken cancellationToken)
    {
        lesson.Step("Inspect a bounded sample of the database (a read-only transaction that is always rolled back)");
        lesson.Code("Inspect");
        // workshop:start Inspect
        var snapshot = await GraphDatabaseInspector.InspectAsync(lesson.Driver, lesson.Database,
            new GraphInspectionOptions(sampleLimit: 32, labels: ["Person", "Movie"], relationshipTypes: ["ACTED_IN"]),
            cancellationToken);
        // workshop:end Inspect
        lesson.Value("Labels", string.Join(", ", snapshot.Labels));
        lesson.Value("Relationship types", string.Join(", ", snapshot.RelationshipTypes));
        lesson.Check("the inspected labels include Person and Movie", snapshot.Labels.Contains("Person") && snapshot.Labels.Contains("Movie"));

        lesson.Step("Review the keys, then compile a tracked EF model");
        lesson.Code("Scaffold");
        // workshop:start Scaffold
        var policy = new GraphScaffoldPolicy("Scaffolded.Workshop", "ScaffoldedContext",
            [new GraphScaffoldNodeMapping("Person", "Person", ["id"], approveRequiredKey: true),
             new GraphScaffoldNodeMapping("Movie", "Movie", ["id"], approveRequiredKey: true)],
            [new GraphScaffoldRelationshipMapping("ACTED_IN", "Credit", "Person", "Movie", "id", approveRequiredKey: true)]);
        var compiled = GraphScaffoldCompiler.Compile(snapshot, policy);
        // workshop:end Scaffold
        lesson.Table(compiled.Diagnostics.Select(d => new { d.Code, d.Severity, d.Subject }));
        lesson.Check("the reviewed policy compiles to a tracked model", compiled.Success && compiled.Model is not null);
        lesson.Note("Every warning is a review prompt: a sample cannot prove that keys always exist, and ObservedParallelEdges is Hugo Weaving's two Cloud Atlas roles.");

        lesson.Step("Generate the EF source");
        lesson.Code("Generate");
        // workshop:start Generate
        var artifacts = GraphScaffoldArtifacts.Generate(compiled, snapshot.Capabilities);
        // workshop:end Generate
        lesson.Table(artifacts.Files.Select(f => new { f.RelativePath, Lines = f.Content.Split('\n').Length }));
        lesson.Check("generation succeeds", artifacts.Success);
        lesson.Check("entities and a context are generated", artifacts.Files.Any(f => f.RelativePath.EndsWith("Entities.g.cs", StringComparison.Ordinal))
            && artifacts.Files.Any(f => f.RelativePath.EndsWith("Context.g.cs", StringComparison.Ordinal)));
        var entities = artifacts.Files.First(f => f.RelativePath.EndsWith("Entities.g.cs", StringComparison.Ordinal)).Content;
        lesson.Note("First lines of the generated entities:\n" + string.Join("\n", entities.Split('\n').Take(12)));
        lesson.Limit("A sample cannot prove requiredness, cardinality or every type, and keys are your decision: approveRequiredKey is an explicit review step. Nothing is written to the database or to disk here.");
    }
}
