# 30 · Authoring toolchains: database-first

**Goal:** inspect the graph you built in this workshop and scaffold a reviewed EF model from it.
**Track:** full · **Mode:** needs Neo4j · **Time:** ~15 min · **Builds on:** 02, 11

## Concepts

- **Database-first:** `GraphDatabaseInspector` samples labels, relationship types and property types in a read-only transaction that is always rolled back.
- **You** decide the keys. A scaffold policy names each node's and relationship's key and explicitly approves it (`approveRequiredKey`). Without reviewed keys you get read-only DTOs, not a tracked model.
- **Ontology-first** starts from a Turtle vocabulary and a persistence policy instead; see the [Organization sample](../../../../samples/02-Organization-OntologyFirst/README.md).

## Run it

```powershell
dotnet run --project workshop/GraphBridge.Workshop -- run 30
```

## Walkthrough

1. **Inspect.**

<!-- region: Inspect -->
```csharp
var snapshot = await GraphDatabaseInspector.InspectAsync(lesson.Driver, lesson.Database,
    new GraphInspectionOptions(sampleLimit: 32, labels: ["Person", "Movie"], relationshipTypes: ["ACTED_IN"]),
    cancellationToken);
```

2. **Review and scaffold.**

<!-- region: Scaffold -->
```csharp
var policy = new GraphScaffoldPolicy("Scaffolded.Workshop", "ScaffoldedContext",
    [new GraphScaffoldNodeMapping("Person", "Person", ["id"], approveRequiredKey: true),
     new GraphScaffoldNodeMapping("Movie", "Movie", ["id"], approveRequiredKey: true)],
    [new GraphScaffoldRelationshipMapping("ACTED_IN", "Credit", "Person", "Movie", "id", approveRequiredKey: true)]);
var compiled = GraphScaffoldCompiler.Compile(snapshot, policy);
```

   Every diagnostic is a review prompt. `KeyExistenceUnproven` says a sample cannot prove every node has a key; `ObservedParallelEdges` comes from Hugo Weaving and Tom Hanks, who each have two Cloud Atlas roles.

3. **Generate the EF source.**

<!-- region: Generate -->
```csharp
var artifacts = GraphScaffoldArtifacts.Generate(compiled, snapshot.Capabilities);
```

   The result lists the generated files: entities, a context, the mapping configuration and reviewable schema proposals. The lesson prints the first lines of the generated entities. Nothing is written to disk or to the database.

## What you should see

The labels Person and Movie, the ACTED_IN relationship type, a list of review warnings, a successful compilation and the generated file list.

## Try it yourself

1. Set `approveRequiredKey: false` for Movie. What does the compiler produce instead?
2. Use the command-line equivalent: `dotnet run --project src/GraphBridge.Tool -- inspect` followed by `scaffold` (see [its environment settings](../../../../docs/tools/GRAPHBRIDGE_TOOL.md#inspect)).

## Limits

- A sample cannot prove requiredness, cardinality or every type. Treat inferred types as proposals to review.
- Scaffolding never deploys schema or changes data.

## Read more

[GraphBridge.Tool command line](../../../../docs/tools/GRAPHBRIDGE_TOOL.md) · [Movies database-first sample](../../../../samples/03-Movies-DatabaseFirst/README.md)
