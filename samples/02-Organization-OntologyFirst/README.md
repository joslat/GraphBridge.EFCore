# Organization: ontology and policy to the real EF provider

The Turtle vocabulary describes `Person`, `Organization` and `worksFor`. The policy chooses application GUID identities, graph names, required properties, physical direction and restrictive deletion. Generated `Employment` entities occupy native relationships with their own `EmploymentId`; two employment records may connect the same endpoints.

`Organization.App` references only the provider. It compiles the checked-in C# in `generated/` and `generated-v2/`; ontology parsing and Roslyn are optional authoring tools, not application runtime dependencies. The tooling compiles its emitted C#, finalizes the generated EF model and derives `graphbridge.binding.json` from that model. Source hashes and semantic diagnostics are recorded separately from the storage fingerprint.

Run these commands from the repository root to regenerate the artifacts:

```powershell
dotnet run --project src/GraphBridge.Tool -- ontology --ontology samples/02-Organization-OntologyFirst/source/organization.ttl --policy samples/02-Organization-OntologyFirst/source/graphbridge.policy.yaml --shapes samples/02-Organization-OntologyFirst/source/organization.shacl.ttl --output samples/02-Organization-OntologyFirst/generated
dotnet run --project src/GraphBridge.Tool -- ontology --ontology samples/02-Organization-OntologyFirst/source/organization.ttl --policy samples/02-Organization-OntologyFirst/source/graphbridge.policy.v2.yaml --shapes samples/02-Organization-OntologyFirst/source/organization.shacl.ttl --output samples/02-Organization-OntologyFirst/generated-v2
```

Append `--check` to either command to check deterministic regeneration without changing files. Review each `generation-report.md`. Offline capability profiles describe a declared deployment target; they do not certify a running server. Generated schema files are proposals and are never executed by generation or context startup. The sample's explicit typed schema plan uses its own stable constraint names; choose this plan for the demo instead of separately applying overlapping generated proposals.

RDFS domain and range describe inference semantics. They do not establish EF nullability or closed-world validation. Policy nullability is explicit, and SHACL validation remains a separate operation:

```powershell
dotnet run --project src/GraphBridge.Tool -- shacl --shapes samples/02-Organization-OntologyFirst/source/organization.shacl.ttl --data samples/02-Organization-OntologyFirst/source/organization.valid.ttl
dotnet run --project src/GraphBridge.Tool -- shacl --shapes samples/02-Organization-OntologyFirst/source/organization.shacl.ttl --data samples/02-Organization-OntologyFirst/source/organization.invalid.ttl
```

The second command should report nonconformance. No OWL reasoning or SHACL validation happens during EF reads or saves. Unsupported semantic constructs produce diagnostics rather than silently becoming persistence rules.

The executable has four explicit modes:

```powershell
dotnet run --project samples/02-Organization-OntologyFirst/Organization.App -- --preview
dotnet run --project samples/02-Organization-OntologyFirst/Organization.App -- --prepare-schema
dotnet run --project samples/02-Organization-OntologyFirst/Organization.App -- --run
dotnet run --project samples/02-Organization-OntologyFirst/Organization.App -- --migrate-demo
```

`--preview` is offline: it prints the journal bootstrap and exact typed migration plans, checksums, Cypher, parameter values and preconditions. Other modes require this repository's owned disposable fixture at `bolt://127.0.0.1:17687`, database `neo4j`, and its matching fixture marker. Use the environment values produced by the fixture scripts: `GRAPHBRIDGE_NEO4J_URI`, `GRAPHBRIDGE_NEO4J_DATABASE`, `GRAPHBRIDGE_NEO4J_USERNAME`, `GRAPHBRIDGE_NEO4J_PASSWORD` and `GRAPHBRIDGE_FIXTURE_ID`. Do not put credentials in source files. Follow the repository resource checks before starting or using a fixture.

[appsettings.example.json](appsettings.example.json) documents this environment variable mapping, offline mode and fixture guards without credential values. The executable does not load this file; load the owned fixture values into the process environment before invoking a database mode.

`--prepare-schema` explicitly initializes the migration journal and applies three uniqueness constraints: both node IDs and the relationship-wide employment ID. It requires the demo's journal predecessor chain; unrelated prior migrations are not silently bypassed. Uniqueness does not imply existence: required values are also enforced by the provider. Community is the baseline; there is no Enterprise Graph Type deployment in this sample.

`--run` adds two endpoints and two parallel employments through generated EF types, reads them in fresh contexts, updates one relationship and verifies reference fixup. It exercises required and nullable `DateOnly`, including setting then removing `ValidTo`. These dates use an explicit invariant `yyyy-MM-dd` string conversion: calendar dates only, without time-of-day or timezone semantics. Native read-only observations verify direction, relationship payloads, absence of stored endpoint-FK slots, and absence of synthetic `Employment` nodes. The run removes only its own randomly identified entities through EF.

Before CRUD writes, the workflow compares the checked-in v1 compiler fingerprint with the actual finalized v1 EF model. `OrganizationResult.SourceSchemaFingerprint` is the compiler artifact; `TargetSchemaFingerprint` is that same version's runtime model fingerprint. The command prints both. These values must agree and do not indicate that a v2 migration ran. The application copies both versions' binding JSON and hashes into `organization-schema/v1` and `organization-schema/v2`; integration assertions compare the full canonical JSON and SHA-256 with each actual generated model. Migration mode separately prints the source v1 and target v2 mapping fingerprints, each checked against its own compiler artifact; mapping fingerprints are not database observations.

The v2 policy preserves all labels, IDs and endpoints, adds optional `Department`, and changes `Role`/`role` to `Position`/`position`. Regeneration does not rename stored data. `--migrate-demo` seeds two fixed employment IDs, explicitly backfills their departments and then renames each `role` property to an absent `position` property. Every data step is restricted to one exact ID and at most one relationship. Reading the required v2 `Position` before migration fails clearly.

The migration validates its exact schema history and predecessor before seeding. If execution stopped after seed committed but before migration began, it recognizes only the complete expected four-entity dataset; partial or conflicting values cause an error without overwriting anything. This is a single-invocation fixture demo: do not run competing demo commands while preparing its dataset. Migration execution itself rechecks durable ownership and predecessor state.

The migration retains its dataset and durable journal; repeating the mode verifies the already-applied plan without reseeding. Plans commit step by step and declare no automatic rollback compensation. On failure, completed steps and durable ownership may remain. Inspect with `ValidateGraphMigrationAsync`; only after stopping/fencing the prior process and resolving its transactions should a caller use `RecoverGraphMigrationAsync` with the exact owner token and generation. The sample never steals ownership, retries uncertain writes, resets a journal, or deletes schema automatically. A failed CRUD seed acknowledgement also exposes its four exact IDs in the original exception's `Data` dictionary and leaves uncertain commit resolution explicit.

Every public async workflow entrypoint accepts an optional `CancellationToken`, and the executable forwards Ctrl+C to those operations. An already canceled token stops before driver access, including schema initialization and seeding. Cancellation is cooperative between awaited driver operations; it does not abandon an in-flight request or prove that a lost commit acknowledgement rolled back. After an acknowledged CRUD seed, failure/cancellation cleanup uses an uncancelled token and retains the original exception; a cleanup failure is attached separately. Once final cleanup begins it completes without the caller's token, and no cancellation check follows its acknowledged delete. Migration cancellation retains any acknowledged seed and durable journal for explicit validation/resume; it never resets the journal or compensates completed steps automatically.

The integration cases in `OrganizationSampleTests.cs` exercise the compiled generated models, raw graph storage, restrictive deletion and public migration APIs against the disposable fixture. Fixture-only test cleanup resets the sample data and migration journal between cases; that reset is not an application command. Current executed validation evidence is recorded in `docs/IMPLEMENTATION_STATUS.md` (maintainer-local record).

The cancellation/fingerprint increment passes the focused owned-fixture acceptance run, including both offline entrypoint/parity facts. Added cases cover all pre-canceled workflow entrypoints without transport, actual compiler/runtime parity, an empty database/journal after pre-canceled setup and CRUD, and preservation of an acknowledged seed/journal followed by explicit migration resume. These do not claim deterministic mid-flight network cancellation or unknown-commit recovery coverage at the sample layer.

See the [executed migration preview and recovery example](../../docs/features/SCHEMA_AND_MIGRATIONS.md#migration-recovery-example) for owner fencing and named failure-boundary tests.
