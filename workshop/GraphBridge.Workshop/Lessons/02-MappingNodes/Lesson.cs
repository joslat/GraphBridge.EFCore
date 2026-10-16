using GraphBridge.EntityFrameworkCore.Schema;
using Microsoft.EntityFrameworkCore;

namespace GraphBridge.Workshop.Lessons;

public sealed class Lesson02MappingNodes : ILesson
{
    public LessonInfo Info { get; } = new("02", 'A', "Mapping nodes", "02-MappingNodes", LessonMode.Offline, Core: true,
        Requirement.None, "Map entities to labels, keys and property names, and read the storage manifest EF derives from them.");

    public async Task RunAsync(LessonContext lesson, CancellationToken cancellationToken)
    {
        lesson.Step("Map each entity to a node label, an application key and property names");
        lesson.Code("Nodes", "Domain/MoviesContext.cs");
        lesson.Note("Keys are application keys (here Guids you assign), not Neo4j internal element IDs. ValueGeneratedNever says EF must not invent them.");

        lesson.Step("Read the storage manifest: what EF will store, derived from the finalized model");
        await using var db = lesson.CreateContext();
        lesson.Code("Manifest");
        // workshop:start Manifest
        GraphSchemaManifest manifest = db.Database.GetGraphSchemaManifest();
        var nodes = manifest.Nodes.Select(n => new
        {
            n.Label,
            Key = string.Join(", ", n.KeyProperties),
            Properties = string.Join(", ", n.Properties.Select(p => p.Name))
        });
        var relationships = manifest.Relationships.Select(r => new
        {
            Type = r.RelationshipType,
            r.Identity,
            Pattern = $"({r.Source.NodeLabel})-[:{r.RelationshipType}]->({r.Target.NodeLabel})"
        });
        // workshop:end Manifest
        lesson.Table(nodes);
        lesson.Table(relationships);
        lesson.ExpectSequence("three node labels", ["Movie", "Person", "Review"], manifest.Nodes.Select(n => n.Label).Order(StringComparer.Ordinal));
        lesson.ExpectSequence("five relationship types", ["ACTED_IN", "DIRECTED", "FOLLOWS", "HAS_REVIEW", "LIKES"],
            manifest.Relationships.Select(r => r.RelationshipType).Order(StringComparer.Ordinal));
        var movie = manifest.Nodes.Single(n => n.Label == "Movie");
        lesson.Check("Movie.DirectorId is not a node property: it is derived from the DIRECTED relationship",
            movie.Properties.All(p => !p.Name.Contains("director", StringComparison.OrdinalIgnoreCase)));
        lesson.Check("CLR names map to camelCase graph names", movie.Properties.Any(p => p.Name == "released"));

        lesson.Step("A fingerprint identifies the storage shape");
        lesson.Value("Fingerprint", manifest.Fingerprint);
        lesson.Expect("the fingerprint is deterministic", manifest.Fingerprint, db.Database.GetGraphSchemaManifest().Fingerprint);
        lesson.Note("The fingerprint ignores CLR names and declaration order. Compare it across builds to detect storage changes.");
    }
}
