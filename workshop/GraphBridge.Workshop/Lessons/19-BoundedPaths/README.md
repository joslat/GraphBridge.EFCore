# 19 · Bounded paths

**Goal:** follow relationships for a bounded number of hops, and see how cycles, limits and missing anchors behave.
**Track:** full · **Mode:** offline + Neo4j · **Time:** ~15 min · **Builds on:** 16, 18

## Concepts

- A **bounded path** repeats one relationship pattern between a minimum and maximum number of hops (at most 64). Results are native paths: ordered nodes and relationships.
- On a path, nodes may repeat but a physical relationship may not. Cycles therefore end on their own.
- Two APIs: Fluent `Repeat(min, max)` inside a pattern, and `QueryPathsAsync` with an exact anchor, a result limit and a `HasMorePaths` flag.

## Run it

```powershell
dotnet run --project workshop/GraphBridge.Workshop -- run 19
```

## Walkthrough

1. **Fluent Cypher.**

<!-- region: Fluent -->
```csharp
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
```

   From Keanu Reeves, following `FOLLOWS`, there are three paths: one, two and three hops, although the bound allows five. The longest returns to Keanu: Keanu → Carrie-Anne → Laurence → Keanu is a cycle, and it ends there because a path never reuses a physical relationship.

2. **QueryPathsAsync.**

<!-- region: Native -->
```csharp
var pattern = new GraphPathPattern("Person", ["FOLLOWS"]);
var result = await db.Database.QueryPathsAsync(pattern,
    new Dictionary<string, object?> { ["id"] = SeedData.Emil.ToString("D") },   // stored names and values
    GraphTraversalDirection.Outgoing, maxDepth: 3, resultLimit: 2,
    projectNode: node => node["name"].As<string>(), projectRelationship: edge => edge.Type,
    cancellationToken: cancellationToken);
```

   The anchor uses stored names and values (`id`, the Guid as a string). Emil has three outgoing paths, the limit returns two, and `HasMorePaths` says the result was cut. An unknown anchor returns `StartFound = false`, not an error.

## What you should see

The three Keanu routes in hop order, two of Emil's paths with `HasMorePaths` true, and a clean "not found" for an unknown anchor.

## Try it yourself

1. Change the Fluent pattern to `start.In<Follow>(end)`. Whose followers do you see?
2. Raise `resultLimit` to 10. What happens to `HasMorePaths`?

## Limits

- One relationship type per repeated step, the same node label at both ends, and no shortest or weighted paths.
- Depth and row limits bound what is returned, not the server's work. Anchor on an indexed key.

## Read more

[Bounded paths](../../../../docs/features/BOUNDED_PATHS.md) · [Fluent Cypher: bounded paths](../../../../docs/features/FLUENT_CYPHER.md#bounded-paths)
