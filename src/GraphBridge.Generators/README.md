![GraphBridge](https://raw.githubusercontent.com/joslat/GraphBridge.EFCore/main/docs/images/graphbridge-logo-small.png)

# GraphBridge.Generators

[![NuGet](https://img.shields.io/nuget/vpre/GraphBridge.Generators?label=nuget&color=0b6ae6)](https://www.nuget.org/packages/GraphBridge.Generators)
![Roslyn incremental generator](https://img.shields.io/badge/Roslyn-incremental%20generator-512bd4)
[![License: MIT](https://img.shields.io/badge/license-MIT-0b6ae6)](https://github.com/joslat/GraphBridge.EFCore/blob/main/LICENSE)
![Developer preview](https://img.shields.io/badge/status-developer%20preview-e8a317)

Optional compile-time mapping for [GraphBridge](https://www.nuget.org/packages/GraphBridge.EntityFrameworkCore), the Entity Framework Core provider for Neo4j. Declare the graph mapping with attributes; the generator writes ordinary EF fluent registration, static identifiers and a storage fingerprint. The finalized EF model stays the runtime authority, and no generator assembly ships with your application.

```xml
<PackageReference Include="GraphBridge.Generators" Version="<version on this page>" PrivateAssets="all" />
```

## Declare the model

```csharp
using GraphBridge.Generated;

[GraphModel(typeof(Person), SchemaPolicy = GraphSchemaPolicy.ExistingConstraints)]
public static partial class PeopleMapping { }

[GraphNode("Person", nameof(Id))]
public sealed class Person
{
    [GraphProperty("id", true, Conversion = GraphConversion.GuidString)]
    public Guid Id { get; set; }

    [GraphProperty("name", false)]
    public string? Name { get; set; }
}
```

Call `PeopleMapping.Apply(modelBuilder)` in `OnModelCreating`. Once the context model has finalized, call `PeopleMapping.Verify(context.Model)`: it compares the whole storage model with `PeopleMapping.Fingerprint` and fails if registration was skipped or later configuration changed the storage contract.

## What you get

| | |
|---|---|
| **Registration** | Plain EF fluent code for nodes, composite keys up to sixteen properties, and identified relationships with typed source and target |
| **Identifiers** | Deterministic label, relationship-type and property-name constants, with optional IRIs |
| **Fingerprint** | A storage fingerprint that `Verify` checks against the finalized model |
| **Diagnostics** | Located compile errors with codes and repair steps instead of guessed mappings |

## Boundaries

- The generator never loads your assembly, constructs a context, runs `OnModelCreating` or opens a database.
- Constraints are provisioned separately; the generator never checks or deploys them.
- Not covered yet, among others: inheritance, records, generic or nested entities, shadow properties, alternate keys, association edges, native one-to-many references and custom conversions.
- Targets `netstandard2.0`; verified with Roslyn 5.9.0 from .NET SDK 10.0.401.

Read the [mapping generator guide](https://joslat.github.io/GraphBridge.EFCore/tools/MAPPING_GENERATOR.html) for every attribute and diagnostic code, and the [documentation](https://joslat.github.io/GraphBridge.EFCore/) for GraphBridge itself. Feedback is welcome in [GitHub issues](https://github.com/joslat/GraphBridge.EFCore/issues). MIT licensed.
