# AGENTS.md — GraphBridge agent rules

When the local strategy pack is present, read `strategy/design/START_HERE.md` and its four specification files before writing provider code. In a clean clone without that pack, use the public supported contracts, `docs/architecture`, `tests/README.md` and source. Do not require ignored files for builds or contribution. This file controls autonomous coding behavior; the design pack describes intended scope, while the implementation tracker records verified behavior.

## Priorities

1. Correctness against actual EF Core + Neo4j, then simple design, then performance, then optional tooling.
2. Implement milestone tickets in `strategy/design/04_IMPLEMENTATION.md` in dependency order; M0 + source-map before query provider.
3. Keep GraphBridge a real EF Core **non-relational provider**. Don't substitute a hand-written `IQueryable` or a second ORM.
4. EF Core is the single entity state owner and finalized EF `IModel` is runtime model authority. Neo4j.Driver owns transport.
5. Node and relationship storage are explicitly different; join-entity→edge lowering is an unproven hypothesis until tested.
6. One package initially. Ontology-first, database-first and Roslyn generator are deferred toolchains, not runtime dependencies.

## Adaptive task planning

- The user authorizes reordering, modifying, removing or adding tasks when evidence or sound engineering judgment justifies it. Routine plan adjustments do not require renewed permission.
- Prioritize finishing incomplete earlier phases whenever feasible before advancing later phases. Audit the earliest open acceptance gates first, close missing verification or implementation, and record any external blocker explicitly. Passing a supported subset does not close all remaining work in its phase; keep partial and full completion distinct.
- Preserve the intended outcome, architectural constraints and real prerequisites. The milestone dependency order is the default; adjust it only with a recorded reason and revised dependencies. Never bypass a correctness gate or remove work merely to claim completion.
- Keep `strategy/design/04_IMPLEMENTATION.md` and `strategy/docs/implementation/IMPLEMENTATION_STATUS.md` consistent when the roadmap changes. Record what changed, why, dependency impacts and any effect on scope or validation in the tracking table. Mark removed or superseded tasks explicitly so their history remains reviewable.

## Engineering rules

- Pin **.NET 10 / EF Core 10 / Neo4j.Driver 6.x** exact compatible patch releases; validate signatures against the matching EF source tag.
- Write tests **before/with** each increment. Live integration against disposable Neo4j for DB behavior.
- No production/destructive DB access; no committed secrets; no silent `DETACH DELETE`.
- Query values parameterized and Cypher identifiers from validated metadata only. Unsupported translation throws.
- No unbounded client-side predicate execution. Do not make up EF provider API method signatures.
- Treat internal EF APIs as version-risky and track usage in `docs/architecture/EF_SERVICE_MAP.md`.
- Each PR/ticket reports commands/tests actually run, precise evidence and limitations; if toolchain absent, state the blocker.
- Update the local implementation ledger when available and the relevant local ADR with changed assumptions.
- Goal execution requires a tracking-table update whenever any task, subtask or milestone is completed. Use `strategy/docs/implementation/IMPLEMENTATION_STATUS.md` as the canonical local ledger when the strategy pack is available; the public root status file links only to published verification and compatibility documentation. Create the local table if missing; reconcile stale entries before continuing. Record each completed item, its status, actual validation evidence and remaining limitations immediately, rather than waiting for a milestone or final report. Keep unfinished and unverified work explicit; delegated completions must also be recorded by the primary agent.
- Organize tracking by milestone/phase and ticket, with tasks beneath their parent. Include priority and notes columns; use ✅ for completed scope, ⏳ for doing or awaiting verification, and ○ for not started. Never give an incomplete parent a completion icon merely because some subtasks passed.
- When the local strategy pack is available, keep the visual progress artifact synchronized with each tracking update: run `python strategy/tools/progress/Update-ProgressDashboard.py`, then `python strategy/tools/progress/Update-ProgressDashboard.py --check`. The generated `strategy/design/features/GraphBridge_Implementation_Progress.html` reads the canonical ledger and strategy; edit those sources rather than changing completion in the HTML. Keep quick-win and strategic-theme totals separate because their scope overlaps.
- Maintain `strategy/design/features/GraphBridge_Future_Implementation_Ideas.html` as the separate visual register of future implementation ideas discovered during work. Whenever improvement potential is identified, capture it promptly. Search existing entries first: enrich the matching idea with concrete evidence, use cases, benefits, tradeoffs, dependencies or sharper acceptance criteria rather than duplicating it; preserve its FUT identifier. Create a new entry only for a distinct improvement. Record stable FUT identifiers, value, scope boundaries, dependencies and proposed acceptance criteria; update its review date and inventory when changed. Reconcile overlaps when current themes close. Future ideas do not count as unfinished roadmap work or expand the active goal automatically. Preserve promotion, supersession or rejection history and link any accepted implementation ticket.

## Local strategy boundary

- `strategy/` is intentionally ignored. It contains maintainer-local design, planning, raw benchmark results, internal verification records and launch drafts. Never force-add it or introduce public build, package or CI dependencies on it. `GraphBridge.sln` owns the public source, samples, tests and benchmarks.
- Keep implementation architecture/ADRs, conformance contracts, tests, executable benchmarks and reusable engineering/verification tools tracked alongside public source and guides (user restoration approval, 2026-10-12). Keep raw media, traces, installers, generated results, credentials and editorial/progress tooling private or ignored. Public documentation must not link to ignored strategy files. Restoring source does not authorize publishing private evidence or enabling automatic CI.

## Shared machine and resource limits

- There is no machine-wide container or graph-database count limit, including for tests (user revision, 2026-10-11). Other containers or databases do not block GraphBridge work and no session exception is required. Continue implementation within the memory and ownership rules below.
- Another developer agent may be working in another repository on this machine. Treat Docker, databases, CPU and memory as shared resources, including tests and benchmarks. Never stop or modify another developer's resources.
- Before starting or using a fixture, inspect available memory and verify the selected fixture's ownership. Reuse existing resources where practical; additional workloads must fit the available memory budget.
- Reuse this repository's existing disposable fixture for compatible tests. Serialize database test runs and other memory-intensive builds or benchmarks; delegated agents must coordinate resource use through the primary agent.
- Reserve at least **5 GB of available host RAM** for the other developer agent. Use only memory above that reserve; check available memory before resource-heavy work and monitor it during execution, allowing headroom for peak usage.
- Fresh GitHub-hosted CI VMs do not share this developer machine. Their explicitly opted-in fixture policy reserves 2 GiB plus workload headroom; local and self-hosted execution retain the 5 GiB reserve. Container ownership checks still apply in CI.
- Containers may remain running between tasks while the 5 GB reserve is maintained.
- Release idle GraphBridge-owned test fixtures, profiling helpers and isolated verification browsers promptly when another agent needs memory. Verify ownership before stopping anything; never use machine-wide Docker/WSL shutdown, blanket process termination or shared build-server shutdown as cleanup. Do not launch local builds, database tests or verification browsers while available RAM is below the reserve; continue lightweight work only.
- If available memory approaches or falls below the reserve, defer memory-intensive work and release this repository's idle resources as needed. Continue lightweight work that fits the available budget; never stop another developer's resources. If memory cannot be measured, do not assume sufficient headroom.

## Verification cost and notification limits

- Build/live and security workflows are manual-only during incubation (user revision, 2026-10-11). Do not restore push/PR triggers without a user request.
- Batch coherent changes and use focused local checks when memory permits. Do not dispatch full hosted workflows for each edit, push, or speculative fix; reserve hosted acceptance for prepared milestones or checks that require that environment.
- After a failed hosted run, inspect its evidence and resolve the cause before considering another run. Avoid repeated full-suite trial-and-error; retain unverified status until the relevant checks pass. Keep existing test and release gates intact.
- Keep tool output, polling and AI work focused on decisions and implementation; avoid redundant reads, unnecessary delegation and repeated status-only turns. Continue within the shared-memory reserve when choosing local verification.

## Definition of done

Compile + relevant unit tests + live integration tests (if task requires DB) + reviewed generated Cypher + error-path tests + explicit limitations + status report. A design-only result never counts as a working provider feature.
