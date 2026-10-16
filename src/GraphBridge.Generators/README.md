# Optional compiled mapping registration

This optional Roslyn incremental generator emits ordinary EF fluent registration from an explicit C# contract. It also emits static identifiers, optional IRI constants and an expected storage fingerprint. The finalized EF `IModel` remains the runtime authority. Generation never loads an application assembly, constructs a context, runs `OnModelCreating` or accesses a database.

The original generator passed project/package consumer and finalized-model parity acceptance. FS-21 adds specific diagnostics and an explicit schema responsibility policy; 47 generator tests now pass. The refreshed package consumer and build-cost evidence (maintainer-local record) also pass; see the [specific diagnostic contract](../../docs/tools/MAPPING_GENERATOR.md). See [ADR-0022](../../docs/architecture/adr/ADR-0022.md) and the implementation tracker (maintainer-local record).

For a project reference, include the provider normally and the generator as an analyzer:

```xml
<ProjectReference Include="../GraphBridge.Generators/GraphBridge.Generators.csproj"
                  OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
```

The generator supplies internal authoring attributes directly to the consuming compilation. There is no generator assembly dependency in the compiled application. It targets `netstandard2.0`; the repository's SDK 10.0.401 contains Roslyn 5.9.0, matching the exact 5.9.0 analyzer package reference. Earlier compiler hosts are not verified.

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

Call `PeopleMapping.Apply(modelBuilder)` explicitly in `OnModelCreating`. After the context model has finalized, call `PeopleMapping.Verify(context.Model)` before using that context. Verification compares the complete storage model with `PeopleMapping.Fingerprint` and fails if registration was omitted or later configuration changes the storage contract. The generator does not inject calls into a user's context or prevent later configuration.

Every entity property must be mapped, an endpoint or explicitly `[GraphIgnore]`d. Nullability and requiredness must agree. GraphModel must declare `SchemaPolicy = GraphSchemaPolicy.ExistingConstraints`; constraints are provisioned separately and never checked or deployed by the generator. Earlier consumers must add this policy and regenerate. See [diagnostic codes and repair steps](../../docs/tools/MAPPING_GENERATOR.md). Node keys can contain up to sixteen ordered properties. Identified edges declare `[GraphIdentifiedEdge("TYPE", nameof(Id))]`, exactly one `[GraphSource(nameof(SourceId))]` navigation and one `[GraphTarget(nameof(TargetId))]` navigation. Endpoint attributes accept ordered FK arrays for composite principal keys. Direction is source to target; endpoint deletion is explicitly `Restrict`. Endpoint FK slots remain EF properties but are not stored edge payloads.

Supported scalars are string, bool, int, long, double and their nullable forms, plus explicit Guid-to-string and invariant DateOnly-to-ISO-string conversion. Keys require non-null Guid/string/int/long values. All ordinary properties use `ValueGeneratedNever`; applications supply keys. `Concurrency = GraphConcurrency.OriginalValue` enables ordinary concurrency checking. `IncrementingInt64` emits `IsRowVersion` for required native long properties. Date conversion is represented as opaque in the storage witness, matching the runtime contract; equal hashes do not establish arbitrary converter semantic equivalence.

`Identifiers` contains one deterministic `Entity_<hash>` container per fully qualified entity type, with `Label` or `RelationshipType`, optional `Iri`, `Properties.P_<member>` and optional `Iris.P_<member>` constants. Property names in endpoint slots identify logical mappings; they do not imply edge storage. Generated registration and identifier names remain deterministic when declaration order changes. CLR or IRI renaming can change generated source while preserving the storage fingerprint.

The closed contract currently excludes inheritance, records, generic/nested entities, instance fields, init-only mapped members, shadow properties, alternate keys, explicit indexes, association edges, native one-to-many references and custom conversion code. EF/data-annotation mapping attributes are diagnosed to avoid conflicting convention inputs. Dynamic conventions and arbitrary later fluent configuration cannot be inspected at compile time; finalized-model verification detects storage differences. Unsupported declarations produce located errors rather than guessed mappings.

The real [consumer project](../../tests/GraphBridge.Generators.Tests/Consumer/GraphBridge.Generators.Consumer.csproj) uses analyzer-only project wiring. Its model covers composite node keys, composite edge endpoints, nullable payloads, original-value concurrency, generated versions and date conversion. Generator tests compare exact canonical JSON and SHA-256 with both that finalized model and a separately written EF model, check runtime mismatch detection, and exercise diagnostics, incremental reuse and deterministic output.

Verification commands (local and hosted run 38120282034 pass all 36 tests and the packaged consumer):

```powershell
dotnet test tests/GraphBridge.Generators.Tests --no-restore -m:1
pwsh -File eng/scripts/Test-GeneratorPackage.ps1
```

No database is needed for these tests. Their driver proxy throws on every attempted transport operation. The package check uses a unique local version and verifies a real PackageReference consumer without publishing. Broader compiler-host compatibility remains outside the pinned SDK scope.
