# Mapping source generator

`GraphBridge.Generators` is an optional Roslyn source generator. You declare the graph mapping with attributes on your entity classes; the build generates the EF registration (`Apply`), a startup check that the finalized model still matches (`Verify`), the expected storage `Fingerprint` and identifier constants. Mapping mistakes become located compiler errors (GBSG001–GBSG008) instead of runtime surprises. Use it when you want a closed, reviewable mapping contract; ordinary fluent mappings keep working without it.

```csharp
using GraphBridge.Generated;
using Microsoft.EntityFrameworkCore;
using Neo4j.Driver;

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

public sealed class StudioContext(IDriver driver) : DbContext
{
    protected override void OnConfiguring(DbContextOptionsBuilder options) => options.UseNeo4j(driver);
    protected override void OnModelCreating(ModelBuilder modelBuilder) => StudioMapping.Apply(modelBuilder);
}
```

At startup, check the finalized model once before you use the context:

```csharp
await using var db = new StudioContext(driver);
StudioMapping.Verify(db.Model);   // throws if the finalized model differs from what the attributes declared
```

`StudioMapping.Fingerprint` then equals `db.Database.GetGraphSchemaManifest().Fingerprint`.

## Reference the generator

Reference the provider as usual and the generator as an analyzer, so no generator assembly ships with your application:

```xml
<!-- From source -->
<ProjectReference Include="../GraphBridge.Generators/GraphBridge.Generators.csproj"
                  OutputItemType="Analyzer" ReferenceOutputAssembly="false" />

<!-- From nuget.org -->
<PackageReference Include="GraphBridge.Generators" Version="0.1.0-preview.5" PrivateAssets="all" />
```

The generator adds the attribute types to your compilation as internal types in the `GraphBridge.Generated` namespace (`GraphBridge.Attributes.g.cs`), so you need no attribute package. It targets `netstandard2.0` and is verified with Roslyn 5.9.0 from the pinned .NET SDK 10.0.401; older compiler hosts are not verified. It never loads your assembly, constructs a context, runs `OnModelCreating` or opens a database.

## Declare the model

| Attribute | Where | Meaning |
|---|---|---|
| `[GraphModel(params Type[] entities, SchemaPolicy = …)]` | A `static partial class` | Lists every entity in the model. `SchemaPolicy = GraphSchemaPolicy.ExistingConstraints` is required. |
| `[GraphNode(label, params string[] keys, Iri = …)]` | Entity class | A node label and its ordered key properties (up to 16). |
| `[GraphIdentifiedEdge(type, key, Iri = …)]` | Entity class | A native relationship with its own key property. |
| `[GraphProperty(name, required, Conversion = …, Concurrency = …, Iri = …)]` | Property | A stored property: graph name and requiredness. |
| `[GraphSource(params string[] foreignKeys)]`, `[GraphTarget(…)]` | Navigation on an edge | The source and target node, with foreign-key properties matching the principal key in order. |
| `[GraphIgnore]` | Property | Not persisted. |

| Option | Values |
|---|---|
| `Conversion` | `Native` (default), `GuidString` (canonical Guid string), `DateOnlyIsoString` (invariant `yyyy-MM-dd`). |
| `Concurrency` | `None` (default), `OriginalValue` (concurrency token), `IncrementingInt64` (row version on a required `long`). |

Every entity property must be a `[GraphProperty]`, an endpoint navigation or `[GraphIgnore]`. Native scalars are `string`, `bool`, `int`, `long` and `double`, plus their nullable forms; `Guid` and `DateOnly` need their explicit conversion. Keys must be non-null `Guid`, `string`, `int` or `long`. All properties use `ValueGeneratedNever`, so your application supplies keys. Nullability and requiredness must agree.

An identified edge declares exactly one source and one target navigation. Direction is source to target and endpoint deletion is `Restrict`. Endpoint foreign-key properties stay EF properties but are not stored on the relationship:

```csharp
[GraphIdentifiedEdge("GENERATOR_WORKS_FOR", nameof(Id))]
public sealed class Employment
{
    [GraphProperty("id", true, Conversion = GraphConversion.GuidString)] public Guid Id { get; set; }
    [GraphProperty("personTenant", true)] public string PersonTenant { get; set; } = "";
    [GraphProperty("personId", true, Conversion = GraphConversion.GuidString)] public Guid PersonId { get; set; }
    [GraphProperty("companyId", true)] public long CompanyId { get; set; }
    [GraphProperty("since", true, Conversion = GraphConversion.DateOnlyIsoString)] public DateOnly Since { get; set; }
    [GraphSource(nameof(PersonTenant), nameof(PersonId))] public Person Person { get; set; } = null!;
    [GraphTarget(nameof(CompanyId))] public Company Company { get; set; } = null!;
}
```

Here `Person` has the composite key `[GraphNode("GeneratorPerson", nameof(Tenant), nameof(Id))]`. The complete model is the [generator consumer project](../../tests/GraphBridge.Generators.Tests/Consumer/ConsumerModel.cs).

## What is generated

For a `[GraphModel]` class named `StudioMapping`, the generator completes the partial class with:

| Member | Purpose |
|---|---|
| `Apply(ModelBuilder)` | Ordinary EF fluent registration: `ToGraphNode`, properties, conversions, keys and identified relationships. Call it explicitly from `OnModelCreating`; the generator never injects calls. |
| `Verify(IModel)` | Builds the storage manifest of the finalized model and throws `InvalidOperationException`, naming both fingerprints, when it differs from `Fingerprint`. Catches an omitted `Apply` call and later configuration that changes storage. |
| `Fingerprint` | The expected storage fingerprint, the same value `GetGraphSchemaManifest().Fingerprint` reports. |
| `ExpectedCanonicalJson` | The canonical storage description behind the fingerprint. |
| `Identifiers.Entity_<hash>` | Per entity: `Label` or `RelationshipType`, optional `Iri`, `Properties.P_<member>` and `Iris.P_<member>` constants. |

Output is deterministic: declaration order does not change it. Renaming a CLR member or an IRI changes generated source but not the storage fingerprint. `DateOnly` conversions appear as opaque in the fingerprint, as in the runtime manifest, so equal fingerprints do not prove two converters behave alike.

## Optional compile-time mapping diagnostics

The generator's analyzer checks the attributed contract and reports located build errors before emitting registration for an invalid model:

| Code | Problem | Action |
|---|---|---|
| GBSG001 | Unsupported model declaration or closed-contract shape | Follow the reported declaration requirement; explicitly map or ignore properties. Arbitrary fluent configuration is not analyzed. |
| GBSG002 | Missing, nullable, unsupported or invalid ordered application key | Declare required stable Guid/string/int/long key properties, in explicit order. |
| GBSG003 | Unsupported or inconsistent edge endpoint | Use one nonnullable source and target node, both registered in the model, with disjoint ordered FK groups matching principal keys and conversions. |
| GBSG004 | Invalid or ambiguous storage identifier | Use unique validated labels/types/property tokens; the diagnostic identifies the declaration. |
| GBSG005 | Property/endpoint annotation without a graph entity owner | Add the intended GraphNode/GraphIdentifiedEdge declaration or remove the misplaced annotation. This analyzer works without GraphModel registration. |
| GBSG006 | Unsupported scalar/conversion, nullability, concurrency or accessor contract | Follow the member-specific repair message. Native scalars are string/bool/int/long/double; Guid and DateOnly need explicit supported conversions. Use GraphIgnore for nonpersisted members. |
| GBSG007 | Conflicting key authority or key/version roles | Remove conflicting EF/data-annotation key or generation attributes. Keep graph key declarations authoritative; use a separate non-key version/token property. |
| GBSG008 | Missing or unrecognized schema policy | Explicitly declare ExistingConstraints and provision reviewed constraints separately. |

The schema policy is an **authoring acknowledgement**, not a database inspection result:

```csharp
[GraphModel(typeof(Person), SchemaPolicy = GraphSchemaPolicy.ExistingConstraints)]
public static partial class PeopleMapping { }
```

The generator never opens a database, creates constraints, infers keys from data or executes application model code. Generated registration describes an EF model; runtime operations keep their own constraint checks. Use [schema preflight](../features/SCHEMA_AND_MIGRATIONS.md) and a reviewed provisioning step for the database. There is deliberately no automatic-deployment policy value. Suppressing a generator error does not create the missing registration.

### Upgrading from earlier previews

`GraphModel` declarations written before `SchemaPolicy` existed must add `SchemaPolicy = GraphSchemaPolicy.ExistingConstraints` and rebuild. Fingerprints of existing EF and manual models are unchanged for the same storage mapping, and no database rewrite is implied. Failures that earlier reported GBSG001 or GBSG002 now use the specific GBSG006 and GBSG007 codes; update compiler-log consumers that match codes.

## Evidence and limits

- The closed contract covers nodes, identified edges and scalar properties. It excludes inheritance, records, generic or nested entities, instance fields, init-only mapped members, shadow properties, alternate keys, explicit indexes, association edges, native one-to-many references and custom conversion code. Configure those with the ordinary fluent API; `Verify` still detects storage differences.
- EF and data-annotation mapping attributes on generator entities are diagnosed, to avoid conflicting convention inputs. Dynamic conventions and later fluent configuration cannot be inspected at compile time.
- The finalized EF `IModel` remains the runtime authority. Unsupported general EF models get runtime provider diagnostics; these checks cover only the generator's documented contract.

Evidence: [EF conformance matrix, optional generator row](../../tests/verification/EF_CONFORMANCE_MATRIX.md#implemented-behavior-and-limits). Design decisions: [ADR-0022](../architecture/adr/ADR-0022.md) and [ADR-0039](../architecture/adr/ADR-0039.md).

## Try it

- Workshop lesson [25 · Mapping source generator](../../workshop/GraphBridge.Workshop/Lessons/25-MappingGenerator/README.md) builds the Studio model above in its own [Mapping project](../../workshop/GraphBridge.Workshop/Mapping/GraphBridge.Workshop.Mapping.csproj), then compares the generated and runtime fingerprints. Its exercises trigger GBSG diagnostics on purpose.
- The [generator README](../../src/GraphBridge.Generators/README.md) lists the package and test commands.
