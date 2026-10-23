# Verification summary

What has been verified, against which source and how, with the latest checkpoint per area first. Feature pages state the supported contract; this file and the records beside it hold the evidence. To reproduce, see [tests/README](../README.md). What each feature's tests cover is in [feature evidence](FEATURE_EVIDENCE.md). Hosted run numbers and commit hashes from before 0.1.0-preview.2 refer to the private development history, which is not part of this repository.

## Latest checkpoints

**Neo4jClient adoptions (2026-10-23, `b01bf04`):** whole entities from Fluent Cypher ([ADR 0049](../../docs/architecture/adr/ADR-0049.md)), query tags, command timeouts and write counters ([ADR 0050](../../docs/architecture/adr/ADR-0050.md)), typed node upserts ([ADR 0051](../../docs/architecture/adr/ADR-0051.md)) and the [Neo4jClient guide](../../docs/COMING_FROM_NEO4JCLIENT.md). The solution builds with zero warnings and errors. **844 provider units** pass, and on the owned Neo4j 2026.09.0 Community fixture, serialized, with zero failures and skips: the **full integration suite (305)**, including the new entity-return, operational-control and upsert cases; the **specification suite (81)**; the **live tooling suite (23)**; and the **workshop suite (9)**, which runs all lessons live and checks the guide's compiled excerpts. Sample 05's nine compiled snapshots match. The first live run found two defects, both fixed with tests: relationship entity returns used a map projection on a function call, which Neo4j refuses, and SaveChanges and bulk mutations never consumed their summaries, so their spans had no counters. A [Neo4jClient interoperability](NEO4JCLIENT_INTEROP.md) check compared values written by each library and read by the other. The docs site builds strictly and the public-documents check passes (1,106 local links). Not repeated at this commit: the Enterprise permission suite, the Query Lab TypeScript and browser checks, and the benchmark matrix; the upsert timing was an indicative measurement outside the guarded harness.

**Release-readiness checks (2026-10-16, `5d6449a`):** on pinned Neo4j 2026.09.0 Community, zero failures and skips: **795 provider units**, **266 tooling units**, **47 generator tests**, the full **283-case integration suite**, **81 specification facts**, **22 tooling live cases** and the **workshop suite (8)**, which runs all 31 lessons live, checks that a full run leaves the seed graph intact, detects a drifted graph and allows one workshop process per database. Query Lab passes 28 evidence and 10 host tests, the editor adapter checks and the Chrome browser check on Node 24.21.0. An agent stress test of the workshop (bad settings, unreachable and foreign databases, interrupted and concurrent runs, reset paths, exercises) found seven defects, all fixed with tests. Not yet repeated at this commit: the Enterprise permission suite, the VS Code packaged-editor test and the full benchmark matrix (a run was stopped by its memory guard when other applications reduced free memory).

**Fluent Cypher v2, refusal codes and workshop (2026-10-16, working tree based on `89f7ff3`):** **793 provider units**, **266 tooling units** and the workshop offline suite pass. On the owned Community 2026.09.0 fixture, serialized: the **full integration suite (280)**, **specification suite (81)**, **live tooling suite (22)**, the Fluent live oracle and all 31 workshop lessons pass with zero skips. The Query Lab TypeScript suite passes on Node 24.21.0 (28 evidence, 10 host, adapter and Chrome browser checks). Samples 01–05 run as their READMEs describe. Details: [Fluent Cypher acceptance](FLUENT_CYPHER_ACCEPTANCE.md). Enterprise and benchmark suites were not rerun. **Release-candidate qualification:** commit `f9121c3` as local candidate `0.1.0-preview.2` produced all 14 receipts (locked build, provider units and live, specification, tooling, generator, template, combined-package, Windows and Linux consumers, fresh sample, secret scan, dependency audit, public documents) and a finalized manifest ready for maintainer review. Nothing was published.

Use the [capability × evidence map](CAPABILITY_EVIDENCE.md) to connect supported scenarios, actual sample source, named verification cases and exclusions. Checkpoint counts below retain their original revision and coverage.

**5.26 read-only profile probe (2026-10-12, working tree based on `0d8b49d`):** one focused live case passes on Neo4j **5.26.18 Community**, immutable image `sha256:ef6f1cb1a5594cbfddb82876a9dc67171941eabca3a38100f2bef0b3fc6c05e9`. It verifies Doctor/schema inspection, three Verify plans, bounded LINQ projection, Fluent results against an independent native control, unchanged graph/schema and redacted authentication rejection. Actual EXPLAIN reports dialect 5; the server omits a separate Cypher advertisement row. Typed search now classifies absent advertisement as unsupported instead of ambiguous, without looking up indexes or executing a fallback. Three new cases fail before that correction; all **18 focused search units** then pass, including duplicate-observation rejection. Builds have zero warnings/errors; the owned fixture/network/credentials were removed. The built-in account is administrative; this is not least-privilege, general CRUD, cross-version or final candidate acceptance.

## Baseline suite inventory (2026-10-11)

The first complete public baseline, executed in hosted CI; later checkpoints above add to it.

| Suite | Passed | Source / run |
|---|---:|---|
| Provider unit | 566 | `6b5c59d`, baseline 38123338761 |
| Tooling unit | 175 | Same baseline |
| Generator | 36 | Same baseline |
| Benchmark offline validation | 76 | Same baseline |
| Provider integration | 184 | Same baseline |
| Upstream EF specification facts | 12 | Same baseline; exact names below |
| Database-first suite | 14 | Same baseline; includes one offline precancellation fact |
| **Baseline total** | **1,063** | Zero failures/skips; build zero warnings/errors |
| Enterprise permission acceptance | 7 | `0731ca7`, 38125489524; zero failures/skips |

Both actual package-consumer checks, generated-artifact regeneration and migration preview pass in baseline CI. Resource assertions and PowerShell policy checks are additional checks, not included as xUnit tests in the 1,063 total. The Enterprise effective grants (maintainer-local record) retain exact nonsecret evidence. Hosted fixture cleanup succeeded.

## The twelve upstream facts

These derive from EF **10.0.12** and preserve the inherited assertions, then verify full Cypher baselines:

- `ComparisonOperatorTranslationsNeo4jTest`: `Equal`, `NotEqual`, `GreaterThan`, `GreaterThanOrEqual`, `LessThan`, `LessThanOrEqual`.
- `LogicalOperatorTranslationsNeo4jTest`: `And`, `And_with_bool_property`, `Or`, `Or_with_bool_property`, `Not`, `Not_with_bool_property`.

See the [adapter contract](../GraphBridge.SpecificationTests/README.md), [comparison overrides](../GraphBridge.SpecificationTests/ComparisonOperatorTranslationsNeo4jTest.cs) and [logical overrides](../GraphBridge.SpecificationTests/LogicalOperatorTranslationsNeo4jTest.cs). Custom provider tests are separate evidence and do not become upstream tests by testing a similar behavior. The baseline does not establish upstream nullable/tracking/update/relationship suites.

## Current public checkout

The [manual public workflow](../../.github/workflows/build.yml) restores, builds and packs the public solution, including compilation of restored tests and benchmark source. It does not execute live database tests or benchmarks. Historical hosted runs above remain evidence for their recorded commits; they are not a fresh acceptance run of the current tree.

```powershell
dotnet restore GraphBridge.sln --locked-mode --disable-parallel
dotnet build GraphBridge.sln --no-restore -m:1 -p:UseSharedCompilation=false -nodeReuse:false
```

The public solution includes acceptance suites and benchmark projects; engineering guards and package-consumer checks live in tracked `eng/`. See [testing instructions](../README.md) for offline and explicitly enabled live entry points. Live acceptance still requires an explicitly selected disposable fixture and the memory/ownership rules; moving tests does not remove those validation requirements.

## Performance evidence

Run 38123594691 at `6b5c59d` covers **128 cases**, **3,072 observations**, and **152 EXPLAIN/PROFILE plans**. The results report (maintainer-local record) includes the environment, methodology and SHA-256 of the retained raw archive (maintainer-local record).

For the 100,000-node full-read case, reported p50 allocation was approximately **324.21 MiB EF no-tracking vs 86.35 MiB direct driver**. This is process-wide allocation measured for those specific paths, not retained heap, an application memory requirement or a diagnosis of the allocating stack. Twenty measured observations per case do not establish a production tail-latency SLO. Different result-shaping/API work contributes to the comparison; profile before attributing or optimizing the difference. The new strategy tracks that profiling separately.
