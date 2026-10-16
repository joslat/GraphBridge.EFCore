using GraphBridge.Workshop.Mapping;
using Microsoft.EntityFrameworkCore;

namespace GraphBridge.Workshop.Lessons;

public sealed class Lesson25MappingGenerator : ILesson
{
    public LessonInfo Info { get; } = new("25", 'F', "Mapping source generator", "25-MappingGenerator", LessonMode.Offline, Core: false,
        Requirement.None, "Declare a graph mapping with attributes, let the build generate it, and verify the finalized model matches.");

    public async Task RunAsync(LessonContext lesson, CancellationToken cancellationToken)
    {
        lesson.Step("Declare the mapping with attributes");
        lesson.Code("Model", "Mapping/StudioModel.cs");
        lesson.Note("The Mapping project references GraphBridge.Generators as an analyzer. The generator, not this lesson, writes StudioMapping.Apply and StudioMapping.Verify.");

        lesson.Step("Use the generated mapping, then verify the finalized EF model");
        lesson.Code("Verify");
        // workshop:start Verify
        await using var db = new StudioContext(lesson.Driver);
        StudioMapping.Verify(db.Model);   // throws if the finalized model differs from what the attributes declared
        var manifest = db.Database.GetGraphSchemaManifest();
        // workshop:end Verify
        lesson.Check("the finalized model matches the generated declaration", true);
        lesson.Value("Generated fingerprint", StudioMapping.Fingerprint);
        lesson.Value("Runtime manifest fingerprint", manifest.Fingerprint);
        lesson.Expect("the generated and runtime storage fingerprints agree", StudioMapping.Fingerprint, manifest.Fingerprint);
        lesson.ExpectSequence("two labels, as declared", ["Release", "Studio"], manifest.Nodes.Select(n => n.Label).Order(StringComparer.Ordinal));
        lesson.Limit("The generator is optional and build-time only: it adds no runtime dependency. The finalized EF model remains the authority; Verify checks it on startup.");
    }
}
