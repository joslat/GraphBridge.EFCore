# GraphBridge.Tool command line

`GraphBridge.Tool` is the command-line front end for the authoring toolchains: generate an EF model from an ontology, validate RDF data with SHACL, inspect an existing graph, scaffold a reviewed EF model from that inspection, and compare two scaffold versions. It is source-only (`src/GraphBridge.Tool`, `IsPackable=false`): run it from a repository checkout with `dotnet run`. It is not an installable `dotnet tool`, and it does not include Doctor or Verify, which run inside your application.

```powershell
dotnet run --project src/GraphBridge.Tool -- --help

dotnet run --project src/GraphBridge.Tool -- ontology `
  --ontology samples/02-Organization-OntologyFirst/source/organization.ttl `
  --policy samples/02-Organization-OntologyFirst/source/graphbridge.policy.yaml `
  --shapes samples/02-Organization-OntologyFirst/source/organization.shacl.ttl `
  --output samples/02-Organization-OntologyFirst/generated --check
```

The second command regenerates the Organization sample in memory and prints `Generated artifacts match. Storage fingerprint: …` without writing files. After one build you can add `--no-build` after the project path to start faster.

| Command | Purpose | Database access |
|---|---|---|
| [`ontology`](#ontology) | Compile a Turtle vocabulary and YAML policy into EF source, IRI constants, a storage manifest and schema proposals. | None |
| [`shacl`](#shacl) | Validate RDF data against SHACL shapes. | None |
| [`inspect`](#inspect) | Save a bounded, read-only snapshot of an existing graph's schema and property types. | Read-only, always rolled back |
| [`scaffold`](#scaffold) | Compile a snapshot and a reviewed JSON policy into tracked EF source or read-only DTO proposals. | None |
| [`scaffold-review`](#review-database-first-changes) | Compare two snapshot/policy versions offline and report model and artifact changes as JSON. | None |

The tool reads input files of at most 4 MiB as strict UTF-8. Generation never deploys schema, and no command writes to the database.

## Exit codes

| Code | Meaning |
|---|---|
| `0` | Success: artifacts generated or matched, SHACL data conforms, snapshot saved, or review produced with two valid proposals. `--help`, `help` and no arguments also return `0`. |
| `2` | Invalid usage, unknown command or option, invalid input, failed generation, nonconforming or unvalidated SHACL data, an inspection failure, or a review with an invalid proposal. |
| `3` | `--check` found missing or different generated files; nothing was changed. |

Diagnostics go to standard error as `Severity Code Subject: Message`. Neo4j errors are reported generically, never with server messages or credentials.

## ontology

```text
ontology --ontology FILE --policy FILE [--shapes FILE] --output DIRECTORY [--check]
         [--target-version VERSION] [--target-edition community|enterprise]
```

| Option | Meaning |
|---|---|
| `--ontology` | A Turtle vocabulary. Named OWL classes and properties are read explicitly; imports are not fetched and there is no OWL/RDFS reasoning. |
| `--policy` | The versioned YAML persistence policy: CLR types, node labels or identified-edge relationship types, keys, property names, types and conversions, direction and deletion. Domain and range never imply requiredness; the policy decides. |
| `--shapes` | Optional SHACL shapes compiled alongside. Validation of data is the separate `shacl` command. |
| `--output` | Destination directory for the generated set. |
| `--check` | Compare instead of writing; exit `3` on any difference. |
| `--target-version`, `--target-edition` | Declared deployment target for schema proposals. Defaults: `2026.09.0`, `community`. A declared target does not certify a running server. |

The output directory receives `Entities.g.cs`, `Context.g.cs`, `GraphModelConfiguration.g.cs`, `GraphIris.g.cs`, `graphbridge.binding.json` with its `.sha256`, `schema-<edition>.cypher` and `generation-report.md`, plus a `.graphbridge-generated.json` ownership marker. The binding manifest comes from the actual finalized EF model compiled from the emitted C#, never directly from ontology metadata. Applications reference only the provider and compile the generated C#; RDF, YAML and Roslyn stay in the tool.

Publication validates the complete set first, then replaces only a directory the tool owns: it refuses unrelated files, manual edits and links. It is not crash-atomic; if the process dies mid-replace, inspect and recover the `.graphbridge-backup-*` sibling. Do not publish concurrently to the same directory. Unsupported semantic constructs produce diagnostics instead of mappings. Schema files are proposals; generation never runs them.

## shacl

```powershell
dotnet run --project src/GraphBridge.Tool -- shacl `
  --shapes samples/02-Organization-OntologyFirst/source/organization.shacl.ttl `
  --data samples/02-Organization-OntologyFirst/source/organization.invalid.ttl
```

Prints `SHACL conforms: True` or `SHACL conforms: False`, or `SHACL validation did not run.` when parsing or the supported-shape check fails. Only conforming data exits `0`. The supported subset covers class targets, direct property paths, cardinality, datatype or class, and node kind; other shapes are rejected. SHACL never becomes EF or database enforcement.

## inspect

```powershell
$env:GRAPHBRIDGE_URI = 'bolt://localhost:7687'
$env:GRAPHBRIDGE_DATABASE = 'neo4j'
$env:GRAPHBRIDGE_USERNAME = 'reader'
$env:GRAPHBRIDGE_PASSWORD = '…'   # load from your secret store; never a command argument
dotnet run --project src/GraphBridge.Tool -- inspect --labels Person,Movie --relationship-types ACTED_IN,DIRECTED --output movies.snapshot.json
```

| Setting | Meaning |
|---|---|
| `GRAPHBRIDGE_URI` | `bolt`, `bolt+s`, `bolt+ssc`, `neo4j`, `neo4j+s` or `neo4j+ssc`, with a host and no embedded credentials, path, query or fragment. |
| `GRAPHBRIDGE_DATABASE`, `GRAPHBRIDGE_USERNAME`, `GRAPHBRIDGE_PASSWORD` | Selected database and a read account. All four settings are required. |
| `--output` | A new file in an existing directory. Existing paths are never overwritten, and the path cannot traverse filesystem links. |
| `--labels`, `--relationship-types` | Optional comma-separated selections; without them, every discovered label and type is sampled. |

The inspector opens one read transaction on the selected database and always rolls it back. It reads server components, constraints, indexes, labels, relationship types and a bounded sample of property storage kinds (up to 32 samples per label or relationship type, a 30-second transaction timeout and a 10-second connection timeout). The snapshot keeps schema metadata, labels, relationship endpoint label sets and property kinds; it keeps no raw values, passwords or element IDs. A sample cannot prove requiredness, cardinality, every type or global uniqueness, and no key is ever chosen from names or sampled values. With `--labels` and `--relationship-types`, it reads only those names; without them, it reads every label and relationship type up to `maxTokens` (64 by default, up to 512 in the library). On Neo4j 2026 that list includes every label the database ever had, in use or not, so select what to inspect on a database that has been used for long.

The sample applications use `GRAPHBRIDGE_NEO4J_*` names. To reuse them, copy `GRAPHBRIDGE_NEO4J_URI` to `GRAPHBRIDGE_URI` and so on in the same process, without printing them, as the [Movies sample](../../samples/03-Movies-DatabaseFirst/README.md#inspection-and-the-read-only-proposal) shows.

## scaffold

```powershell
dotnet run --project src/GraphBridge.Tool -- scaffold `
  --snapshot samples/03-Movies-DatabaseFirst/snapshots/reviewed.snapshot.json `
  --policy samples/03-Movies-DatabaseFirst/source/graphbridge.reviewed.policy.json `
  --output samples/03-Movies-DatabaseFirst/generated --check
```

The JSON policy is where you make the decisions a sample cannot: which property is each node's and relationship's key, approved explicitly with `approveRequiredKey`, and how each property maps.

```json
{
  "formatVersion": 1,
  "namespace": "GraphBridge.Samples.Movies",
  "contextName": "MoviesContext",
  "nodes": [
    {
      "label": "GraphBridgeMoviesPerson",
      "clrName": "Person",
      "keyProperties": ["personId"],
      "approveRequiredKey": true,
      "properties": [
        { "storeName": "personId", "clrName": "PersonId", "scalarType": "Guid", "conversion": "GuidString", "required": true, "ignore": false }
      ]
    }
  ],
  "relationships": []
}
```

| Policy | Output |
|---|---|
| Reviewed keys backed by observed uniqueness or key constraints | A tracked model: the same files as `ontology`, plus read-only DTO proposals (`ScaffoldReadOnlyDtos.g.cs`) and `inference-report.md`. |
| Empty `nodes` and `relationships` (no reviewed keys) | Read-only DTO proposals (`ScaffoldReadOnlyDtos.g.cs`) and `inference-report.md`, with no tracked context or storage fingerprint. |

Tracked generation requires observed uniqueness or key constraints, an explicit key approval and supported property mappings. The policy cannot create constraints or convert data. Unsupported payload, such as a native string array, must be ignored explicitly and stays available through native DTO queries. Generated schema files describe an empty target, not a migration of the inspected database. `--output` and `--check` follow the same publication rules as `ontology`.

## Review database-first changes

`scaffold-review` compares two explicit snapshot/policy pairs entirely offline. It keeps ambiguity diagnostics, compiles each valid tracked proposal into a real finalized EF storage manifest, checks serialization and artifact round trips, and writes deterministic JSON to standard output. It never publishes generated files, connects to Neo4j, deploys schema or invents a migration.

```powershell
dotnet run --project src/GraphBridge.Tool -- scaffold-review `
  --baseline-snapshot samples/03-Movies-DatabaseFirst/snapshots/before-schema.snapshot.json `
  --baseline-policy samples/03-Movies-DatabaseFirst/source/graphbridge.reviewed.policy.json `
  --snapshot samples/03-Movies-DatabaseFirst/snapshots/reviewed.snapshot.json `
  --policy samples/03-Movies-DatabaseFirst/source/graphbridge.reviewed.policy.json
```

This comparison intentionally exits **2**: the baseline has no required uniqueness constraints, so it is not a valid tracked model. Its diagnostics stay in the JSON, next to the valid current proposal. Use the reviewed snapshot on both sides for an unchanged comparison. Each side needs its own explicit policy, so a current policy is never silently applied to historical observations.

| Report field | Meaning |
|---|---|
| `baseline` / `current` | Success, source fingerprint, optional compiled-model fingerprint, round-trip result, diagnostics with actions, and proposed artifact hashes. |
| `observationChanges` | Differences in bounded sample, type, schema and capability evidence. A missing observation does not prove deletion from the database. |
| `modelChanges` | Stable paths through the actual finalized EF storage manifests; null when either side has no valid tracked model. |
| `modelsEquivalent` | True only when both compiled storage manifests match. Null for invalid or DTO-only sides; never proof of equal database contents. |
| `artifactChanges` | Added, removed or changed generated-file hashes. CLR naming can change artifacts without changing storage semantics. |

Named collections use semantic paths such as `/nodes/Person/properties/name/isRequired`, escaping `~` and `/` as JSON Pointer tokens. Ordered key arrays remain ordered atomic values. Raw sample collections and ambiguous duplicate-name arrays are compared as canonical arrays, without invented identities. `before` and `after` hold serialized JSON values: an absent path is a null field, while an actual JSON null is the string `"null"`. Changes are descriptive and sorted; they are not a safety classification or deployment plan.

The report is format version 1 with LF line endings. Exit `0` means both proposals passed generation and round-trip checks, even if their models differ; exit `2` means invalid input or at least one invalid proposal. Malformed JSON or options are reported on standard error; well-formed but conflicting proposals produce a structured review with diagnostics on standard output. DTO-only proposals can succeed while `modelsEquivalent` stays null. `scaffold-review` does not accept `--output` or `--check`.

The compiler does not coerce mixed property kinds, infer uniqueness or cardinality, discard contradictory endpoints or substitute element IDs for application identity. Diagnostic actions tell you when to review keys or constraints, keep DTO access, correct endpoint policy or explicitly ignore unsupported tracked payload. Requiredness and stored-format conversions still need separate data verification beyond bounded samples. After review, use `scaffold --check` and then `scaffold` to compare and publish accepted files, and review constraints and data separately.

## Use the libraries directly

Every command wraps a public API in `GraphBridge.Tooling`, which you can call from your own code or tests:

| Command | API |
|---|---|
| `ontology` | `OntologyCompiler.Compile(ontology, policyYaml, shapes)`, `GraphArtifactGenerator.Generate(…)`, `GraphArtifactDirectory.Publish` / `Matches` |
| `shacl` | `ShaclValidator.Validate(shapes, data)` |
| `inspect` | `GraphDatabaseInspector.InspectAsync(driver, database, GraphInspectionOptions, ct)`, `GraphScaffoldJson.WriteSnapshot` |
| `scaffold` | `GraphScaffoldCompiler.Compile(snapshot, policy)`, `GraphScaffoldArtifacts.Generate(compiled, snapshot.Capabilities)` |
| `scaffold-review` | `GraphScaffoldReview.Compare(baseline, baselinePolicy, current, policy)` |

The library exposes limits the command fixes, such as `GraphInspectionOptions(sampleLimit, maxMetadataRows, …, transactionTimeout, labels, relationshipTypes)`. From workshop lesson 30:

```csharp
var snapshot = await GraphDatabaseInspector.InspectAsync(driver, database,
    new GraphInspectionOptions(sampleLimit: 32, labels: ["Person", "Movie"], relationshipTypes: ["ACTED_IN"]),
    cancellationToken);
var policy = new GraphScaffoldPolicy("Scaffolded.Workshop", "ScaffoldedContext",
    [new GraphScaffoldNodeMapping("Person", "Person", ["id"], approveRequiredKey: true),
     new GraphScaffoldNodeMapping("Movie", "Movie", ["id"], approveRequiredKey: true)],
    [new GraphScaffoldRelationshipMapping("ACTED_IN", "Credit", "Person", "Movie", "id", approveRequiredKey: true)]);
var compiled = GraphScaffoldCompiler.Compile(snapshot, policy);
var artifacts = GraphScaffoldArtifacts.Generate(compiled, snapshot.Capabilities);
```

## Limits

- Source-only: no packaged executable and no global tool. `GraphBridge.Tooling` is the package; it bundles no CLI.
- No command deploys schema, migrates data or infers identity, requiredness or cardinality from samples.
- Generated schema files are empty-target proposals; apply reviewed schema with [explicit migration plans](../features/SCHEMA_AND_MIGRATIONS.md).
- Enterprise schema proposals are generated but their execution is unverified.

## Try it

- Workshop lesson [30 · Authoring toolchains: database-first](../../workshop/GraphBridge.Workshop/Lessons/30-AuthoringToolchains/README.md) inspects the workshop graph and scaffolds a reviewed model with the library API.
- [Sample 02, Organization](../../samples/02-Organization-OntologyFirst/README.md): `ontology` and `shacl` from a Turtle vocabulary and two policy versions.
- [Sample 03, Movies](../../samples/03-Movies-DatabaseFirst/README.md): `inspect`, read-only and reviewed `scaffold`, saved-snapshot `--check` and `scaffold-review`.

Design decisions: [ADR-0019](../architecture/adr/ADR-0019.md) (ontology), [ADR-0020](../architecture/adr/ADR-0020.md) (database-first) and [ADR-0040](../architecture/adr/ADR-0040.md) (review). Evidence: [capability evidence, authoring row](../../tests/verification/CAPABILITY_EVIDENCE.md).
