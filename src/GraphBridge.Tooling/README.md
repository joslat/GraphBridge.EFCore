# GraphBridge.Tooling

Optional authoring library for GraphBridge's experimental Neo4j EF Core provider. It supports explicit ontology/policy compilation, separate SHACL validation, bounded database inspection and reviewed scaffolding proposals. Generation never implicitly deploys schema or chooses persistence semantics from arbitrary ontology axioms.

This library is not a runtime dependency of GraphBridge.EntityFrameworkCore. It references the provider plus its own RDF, YAML and Roslyn authoring dependencies. Its package does not bundle those dependency assemblies, a CLI executable or a Neo4j server.

**GraphBridge Doctor** is the application-hosted diagnostic component in this package (`GraphBridge.Tooling.Diagnostics.GraphDoctor`). It compiles registered queries offline and optionally reads selected-database schema/profile metadata, returning redacted JSON or plain text. Its results can be viewed in the Query Lab report and VS Code extension; it performs no automatic repair or registered result execution. See [Doctor usage](https://github.com/joslat/GraphBridge.EFCore/blob/main/docs/tools/DOCTOR.md) and the [package/component map](https://github.com/joslat/GraphBridge.EFCore/blob/main/docs/reference/PACKAGES_AND_COMPONENTS.md).

`GraphBridge.Tooling.DesignTime.GraphMigrationCommands` optionally hosts a declared migration catalog, offline plan preview and read-only status in your application. It does not add relational EF migration services or apply commands. See the [design-time contract and sample](https://github.com/joslat/GraphBridge.EFCore/blob/main/docs/tools/DESIGN_TIME.md).

**GraphBridge Verify** provides separate offline compilation, read-only planning and explicitly owned-fixture semantic checks. **GraphBridge Query Lab** renders the captured evidence as self-contained HTML with typed pattern/scope diagrams, source/Cypher panes and observed outcomes. Rendering performs no query execution and needs no server or JavaScript. These are components of this package, not standalone installed applications. See [Query Lab](https://github.com/joslat/GraphBridge.EFCore/blob/main/docs/tools/QUERY_LAB.md).

See the [repository samples and boundaries](https://github.com/joslat/GraphBridge.EFCore/tree/main/samples), [published verification](https://github.com/joslat/GraphBridge.EFCore/blob/main/tests/verification/VERIFICATION_SUMMARY.md) and included LICENSE/THIRD_PARTY_NOTICES.md. This is an unstable preview; public publication is a separate decision.

`GraphScaffoldReview.Compare` compares explicit snapshot/policy versions offline, retains actionable ambiguity diagnostics, verifies artifact round trips and reports actual finalized-model differences. The source CLI exposes `scaffold-review`; [GraphBridge.Tool command line](https://github.com/joslat/GraphBridge.EFCore/blob/main/docs/tools/GRAPHBRIDGE_TOOL.md) distinguish review from schema deployment or migration planning.
