# 25 · Mapping source generator

**Goal:** declare a graph mapping with attributes, let the build generate it, and verify the finalized model matches.
**Track:** full · **Mode:** offline · **Time:** ~10 min · **Builds on:** 02

## Concepts

- The optional `GraphBridge.Generators` analyzer reads attributes such as `[GraphNode]` and `[GraphProperty]` and generates `Apply(modelBuilder)` and `Verify(model)` at build time.
- Mapping mistakes become **compiler errors** (`GBSG001` to `GBSG008`), for example a missing key or an unmapped property.
- The generator adds no runtime dependency. The finalized EF model stays the authority, and `Verify` proves they match.

## Run it

```powershell
dotnet run --project workshop/GraphBridge.Workshop -- run 25
```

## Walkthrough

1. **The attributed model** lives in its own small project, [Mapping](../../Mapping), which references the generator as an analyzer:

<!-- region: Mapping/StudioModel.cs#Model -->
```csharp
// The attributes are the whole mapping. The source generator turns them into StudioMapping.Apply and
// StudioMapping.Verify at build time; mistakes become compiler errors (GBSG001–GBSG008), not runtime surprises.
[GraphModel(typeof(Studio), typeof(Release), SchemaPolicy = GraphSchemaPolicy.ExistingConstraints)]
public static partial class StudioMapping { }

[GraphNode("Studio", nameof(Id))]
public sealed class Studio
{
    [GraphProperty("id", true, Conversion = GraphConversion.GuidString)] public Guid Id { get; set; }
    [GraphProperty("name", true)] public string Name { get; set; } = "";
    [GraphProperty("founded", false)] public int? Founded { get; set; }
    [GraphIgnore] public string Display => $"{Name} ({Founded})";
}

[GraphNode("Release", nameof(Id))]
public sealed class Release
{
    [GraphProperty("id", true, Conversion = GraphConversion.GuidString)] public Guid Id { get; set; }
    [GraphProperty("title", true)] public string Title { get; set; } = "";
    [GraphProperty("version", true, Concurrency = GraphConcurrency.IncrementingInt64)] public long Version { get; set; }
}
```

```xml
<ProjectReference Include="../../../src/GraphBridge.Generators/GraphBridge.Generators.csproj"
                  OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
```

2. **Use and verify** the generated mapping:

<!-- region: Verify -->
```csharp
await using var db = new StudioContext(lesson.Driver);
StudioMapping.Verify(db.Model);   // throws if the finalized model differs from what the attributes declared
var manifest = db.Database.GetGraphSchemaManifest();
```

   The generated fingerprint equals the runtime storage manifest's fingerprint (introduced in lesson 02).

## What you should see

Verification passes, the two fingerprints agree, and the manifest lists `Release` and `Studio`.

## Try it yourself

1. Remove `[GraphIgnore]` from `Studio.Display` and build the Mapping project. Read the GBSG diagnostic.
2. Remove `SchemaPolicy = ...` from `[GraphModel]`. Which diagnostic do you get, and why does GraphBridge insist on it?

## Limits

- Generated mappings cover nodes, identified edges and scalar properties; anything else is configured with the ordinary fluent API.

## Read more

[Mapping source generator](../../../../docs/tools/MAPPING_GENERATOR.md) · [Generator README](../../../../src/GraphBridge.Generators/README.md)
