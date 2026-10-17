# Changelog

All notable changes to GraphBridge. Every version so far is a developer preview; the [compatibility contract](docs/reference/COMPATIBILITY.md) describes what may change between previews.

## Unreleased

### VectorData adapter (new optional package)

- **`GraphBridge.EntityFrameworkCore.VectorData`:** `AsNodeVectorSearchable` and `AsRelationshipVectorSearchable` expose an existing Neo4j vector index as `Microsoft.Extensions.VectorData`'s `IVectorSearchable<TRecord>` ([guide](docs/features/VECTOR_DATA.md), [sample 06](samples/06-VectorSearch/README.md)). The adapter accepts `ReadOnlyMemory<float>`, `float[]` and `Embedding<float>` and passes native scores through unchanged. It refuses every non-default search option before database work, and detects a pooled context that was leased to another scope. The core provider takes no VectorData dependency. Design proposed by Luis Quintanilla in [issue #1](https://github.com/joslat/GraphBridge.EFCore/issues/1).

### Fluent Cypher

- **Typed refusals:** a refused Fluent query now throws `FluentCypherException`, derived from `NotSupportedException`, with the `GBFC###` code in its `Code` property ([ADR 0047, Amendment 2](docs/architecture/adr/ADR-0047.md#amendment-2-a-typed-fluent-exception)). The message and `Exception.Data["GraphBridge.FluentDiagnostic"]` are unchanged, and `catch (NotSupportedException)` keeps working. Code that checks the exact type, such as xUnit's `Assert.Throws<NotSupportedException>`, should switch to `FluentCypherException`.

### LINQ refusals

- `Neo4jQueryTranslationException` exposes its `GBQ###` code as `Code`, alongside `Exception.Data["GraphBridge.QueryDiagnostic"]`.

## 0.1.0-preview.2 (2026-10-16)

**Developer preview; not production-supported.** The first published preview: [nuget.org](https://www.nuget.org/packages/GraphBridge.EntityFrameworkCore/0.1.0-preview.2) and the [GitHub release](https://github.com/joslat/GraphBridge.EFCore/releases/tag/v0.1.0-preview.2), 2026-10-16.

Changes since the unpublished [0.1.0-preview.1](#010-preview1-draft):

### Fluent Cypher v2

- **C# lambda predicates** under an agree-or-reject rule ([ADR 0047](docs/architecture/adr/ADR-0047.md)): `Where(() => ...)`, `OptionalMatch(pattern, where: () => ...)` and lambda ordering. A lambda is accepted only where native Cypher keeps exactly the rows its C# meaning keeps; anything else is refused before transport with a code.
- **Language depth:** `values.Contains(x)` becomes `IN`; ordinal `StartsWith`/`EndsWith`/`Contains` become `STARTS WITH`/`ENDS WITH`/`CONTAINS`; `!` over operands that can never be null; ordering over enum members. Explicit native twins: `.In(...)`, `.StartsWith(...)`, `.EndsWith(...)`, `.Contains(...)`.
- **`Graph.Is(predicate)`** embeds an explicit predicate such as `Graph.Exists(...)` in a lambda, or returns it as a Boolean column.
- **Aggregates** ([ADR 0048](docs/architecture/adr/ADR-0048.md)): `Graph.Min`, `Max`, `Sum`, `Avg` and server-bounded `Collect`/`CollectDistinct`, introduced by `With(...)` or returned directly with native implicit grouping.
- **Anonymous edges:** `actor.Out<Credit>(movie)` without declaring an edge symbol.
- **Coded diagnostics:** every Fluent refusal carries a `GBFC###` code in its message and in `Exception.Data["GraphBridge.FluentDiagnostic"]`.
- **Readable predicates:** generated WHERE clauses are flat (`a AND b AND c`), with parentheses only where AND and OR mix and around a `NOT` operand.

### LINQ refusals

Every refused LINQ shape names itself in LINQ terms, carries a `GBQ###` code (also in `Exception.Data["GraphBridge.QueryDiagnostic"]`) and suggests a supported alternative, for example `GraphBridge cannot translate GroupBy [GBQ101]. ...`. See [refusal codes](docs/reference/KNOWN_LIMITATIONS.md#refusal-codes). The supported subset itself is unchanged.

### Tooling

Doctor reports the query code as the `subcode` of `GBD-QUERY-001` (LINQ) and `GBD-QUERY-002` (Fluent). The JSON field is omitted when absent, and exception text is still never retained.

### Samples and documentation

A [guided workshop](workshop/README.md) in its own root folder, `workshop/`, with 31 runnable lessons, each with a script, checkpoints and reviewed Cypher snapshots, from a first context to Doctor, Verify, Query Lab, the mapping generator and database-first scaffolding. The documentation adds a [docs index](docs/README.md), a [concepts page](docs/CONCEPTS.md) and links from feature guides to the lessons that exercise them.

The repository layout changed: the workshop moved to `workshop/` (project `GraphBridge.Workshop`, test suite `-Suite Workshop`); `docs/` now holds only user documentation and architecture, organized into [features](docs/README.md#features), [tools](docs/README.md#tools), [reference](docs/README.md#reference) and [architecture](docs/architecture/ARCHITECTURE.md) pages with a visual introduction; verification records moved to `tests/verification/`, the dependency inventory to `eng/dependencies/` and these release notes to this file. A documentation site builds locally from `docs/` with `eng/scripts/Build-DocsSite.ps1`. The workshop refuses a second concurrent run against the same database and reports a graph that drifted from its seed. The Fluent Cypher gallery (sample 05) adds list, text and aggregate queries.

### Upgrade notes

- **Verify baselines:** readable predicates change the text, and therefore the statement fingerprint, of every Fluent query with a WHERE clause. A baseline recorded with preview.1 reports those statements as changed; review and re-record it once.
- **Exception messages:** Fluent messages now read `GraphBridge fluent Cypher [GBFC###]: ...`, and LINQ refusal messages are reworded. The exception types are unchanged. Use the codes in `Exception.Data` instead of matching message text.
- **Source compatibility:** `Where(null)` and `OptionalMatch(pattern, null)` with a literal `null` are now ambiguous at compile time, because lambda overloads were added. Pass a typed value instead.
- Pin the exact preview, as for every `0.x` release.

### Verification

Evidence is recorded in the [Fluent Cypher acceptance record](tests/verification/FLUENT_CYPHER_ACCEPTANCE.md) and the [verification summary](tests/verification/VERIFICATION_SUMMARY.md). The release workflow built, tested and packed the published packages from the tagged commit, with trusted publishing and attached checksums.

## 0.1.0-preview.1 (draft)

**Developer preview; not production-supported.** Prepared 2026-10-11. This is a reviewable release draft, not an announcement of a published NuGet package or GitHub release.

GraphBridge is a real non-relational EF Core provider for Neo4j. EF owns the finalized model, identity and change tracker; Neo4j.Driver owns transport. The verified subset includes async node and native-edge queries/writes, optimistic concurrency, atomic saves, explicit transactions, direct required/optional-reference Include, native DTO/path queries, schema drift and typed migration plans. The three authoring workflows and optional generator are working within their recorded scope.

Start with the [sample index](samples/README.md) or [actual-package quickstart](docs/GETTING_STARTED.md). Repository setup requires SDK 10.0.401, the pinned dependency restore and the owned disposable fixture; each sample documents its commands. The package quickstart uses a local feed and unique verification prerelease; do not assume `dotnet add package` from nuget.org can install this unreleased version.

Read the [compact compatibility matrix](docs/reference/COMPATIBILITY.md), [known limitations](docs/reference/KNOWN_LIMITATIONS.md) and [exact verification inventory](tests/verification/VERIFICATION_SUMMARY.md). In particular, use `SaveChangesAsync()` and async entity enumeration. Broader LINQ, required-reference reassignment, automatic retries, full Enterprise schema deployment and `dotnet ef` migrations are not supported in this preview.

Optional native references now support atomic attach/remove/replace with nullable FK slots; see [the contract](docs/features/REFERENCES.md). Models using this feature emit schema manifest version 2; required-only manifests remain version 1. [Commit recovery](docs/architecture/adr/ADR-0026.md) remains explicit and evidence-driven, with no automatic replay.

### Changes and upgrade policy

During `0.x` previews, public APIs, generated source, supported model shapes and persisted mapping contracts may change. Pin the exact preview and tested EF/driver versions. Each subsequent preview must identify changed APIs/mappings, affected applications, required regeneration or explicit data migration, and actual acceptance evidence. A breaking mapping change must never silently rewrite an existing database. Review the diff and migration preview, back up affected data and verify on a disposable copy before applying a reviewed plan.

Once published, an existing version's package contents must not be replaced. A changed artifact receives a new version. A 1.0 designation requires a separately declared stable support contract and operational evidence; completing the original private roadmap did not supply that promise.

### Feedback

Use this repository's Issues tab when available to your account: [bug report](https://github.com/joslat/GraphBridge.EFCore/issues/new?template=bug_report.md), [feature request](https://github.com/joslat/GraphBridge.EFCore/issues/new?template=feature_request.md), or [EF behavior mismatch](https://github.com/joslat/GraphBridge.EFCore/issues/new?template=ef_mismatch.md). Include a minimal synthetic repro, exact package/server versions, relevant LINQ and sanitized generated Cypher. Do not attach credentials, connection environment files, private data or unreviewed raw logs. No external announcement has been sent.

### Generator authoring change

`GraphModel` now requires `SchemaPolicy = GraphSchemaPolicy.ExistingConstraints`. Existing generator consumers must add it and regenerate. This declares separate reviewed constraint provisioning; it does not inspect or change database schema. Runtime-only fluent models are unaffected. Property/key errors now have specific GBSG006/GBSG007 codes; GBSG008 identifies missing/invalid policy. [Migration details and evidence](docs/tools/MAPPING_GENERATOR.md). Any future published package containing this change must use a new version under the policy above.

