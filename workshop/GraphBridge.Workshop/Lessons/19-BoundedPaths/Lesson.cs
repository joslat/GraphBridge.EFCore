using GraphBridge.EntityFrameworkCore.FluentCypher;
using GraphBridge.EntityFrameworkCore.Paths;
using GraphBridge.Workshop.Domain;
using Microsoft.EntityFrameworkCore;
using Neo4j.Driver;
using Graph = GraphBridge.EntityFrameworkCore.FluentCypher.Cypher;

namespace GraphBridge.Workshop.Lessons;

public sealed class Lesson19BoundedPaths : ILesson
{
    public LessonInfo Info { get; } = new("19", 'D', "Bounded paths", "19-BoundedPaths", LessonMode.Mixed, Core: false,
        Requirement.Seed, "Follow relationships for a bounded number of hops, and see how cycles, limits and missing anchors behave.");

    // workshop:start Fluent
    /// <summary>Every FOLLOWS path of one to five hops from a named person.</summary>
    public static CypherResultQuery<GraphPath<INode, IRelationship>> FollowPaths(DbContext db, string name)
    {
        var start = Graph.Node<Person>();
        var end = Graph.Node<Person>();
        var path = Graph.Path();
        return db.Cypher()
            .Match(path, start.Out<Follow>(end).Repeat(minDepth: 1, maxDepth: 5))
            .Where(() => start.Value.Name == name)
            .Return(() => path.Value)
            .Limit(25);
    }
    // workshop:end Fluent

    public async Task RunAsync(LessonContext lesson, CancellationToken cancellationToken)
    {
        await using var db = lesson.CreateContext();

        lesson.Step("Fluent Cypher: a bounded, named path");
        lesson.Code("Fluent");
        lesson.Cypher("follow-paths", FollowPaths(db, "Keanu Reeves").ToQueryString());

        if (!lesson.NeedsDatabase("Following the paths")) return;

        lesson.Step("Run it: a cycle comes back to the start");
        var paths = (await FollowPaths(db, "Keanu Reeves").ToListAsync(cancellationToken))
            .OrderBy(p => p.Relationships.Count).ToList();
        var routes = paths.Select(p => string.Join(" → ", p.Nodes.Select(n => n["name"].As<string>()))).ToList();
        lesson.Table(routes);
        lesson.ExpectSequence("three paths, one per length",
            ["Keanu Reeves → Carrie-Anne Moss", "Keanu Reeves → Carrie-Anne Moss → Laurence Fishburne",
             "Keanu Reeves → Carrie-Anne Moss → Laurence Fishburne → Keanu Reeves"], routes);
        lesson.Note("The bound allows five hops, yet the longest path has three. Nodes may repeat on a path (the cycle returns to Keanu); a physical relationship may not, so the cycle ends on its own.");

        lesson.Step("QueryPathsAsync: an exact anchor, a result limit and an honest 'there is more'");
        lesson.Code("Native");
        // workshop:start Native
        var pattern = new GraphPathPattern("Person", ["FOLLOWS"]);
        var result = await db.Database.QueryPathsAsync(pattern,
            new Dictionary<string, object?> { ["id"] = SeedData.Emil.ToString("D") },   // stored names and values
            GraphTraversalDirection.Outgoing, maxDepth: 3, resultLimit: 2,
            projectNode: node => node["name"].As<string>(), projectRelationship: edge => edge.Type,
            cancellationToken: cancellationToken);
        // workshop:end Native
        lesson.Table(result.Paths.Select(p => string.Join(" → ", p.Nodes)));
        lesson.Check("the anchor was found", result.StartFound);
        lesson.Expect("two paths returned, as limited", 2, result.Paths.Count);
        lesson.Check("HasMorePaths says the limit cut the result", result.HasMorePaths);

        var missing = await db.Database.QueryPathsAsync(pattern, new Dictionary<string, object?> { ["id"] = "no-such-person" },
            GraphTraversalDirection.Outgoing, maxDepth: 3, resultLimit: 2, node => node["name"].As<string>(), edge => edge.Type,
            cancellationToken: cancellationToken);
        lesson.Check("an unknown anchor is reported, not an error", !missing.StartFound && missing.Paths.Count == 0);
        lesson.Limit("Depth (at most 64) and result limits bound the rows returned, not the server's work. Anchor on an indexed key, and no shortest-path ordering is implied.");
    }
}
