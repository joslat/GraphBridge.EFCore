# Blogging: code-first nodes and required native references

This executable uses the real GraphBridge EF provider for every application query and write. A Blog owns two Posts through ordinary EF navigations and required `Post.BlogId` metadata, persisted as native `(blog)-[:HAS_POST]->(post)` relationships. `BlogId` is derived from the principal's stored key and is not a property on the Post node or the relationship. The workflow eagerly loads each navigation with ordinary EF `Include`.

BLOG-01–08 pass live acceptance in hosted run 38118649416: the updated workflow, native storage, restrictive deletion, legacy rejection and both Include directions are verified. Provider tests also cover malformed relationships, paging, all tracking modes and transaction cleanup. See implementation status (maintainer-local record) for precise scope and remaining roadmap work.

From the repository root, using the pinned SDK and Docker:

For a fresh owned fixture, PowerShell 7 offers one command that restores/builds serially, starts Neo4j, explicitly prepares schema, runs the workflow and removes its fixture in `finally`:

```powershell
pwsh eng/scripts/Run-BloggingQuickstart.ps1
```

It refuses an existing fixture environment; use the individual commands below to reuse one. The quickstart monitors available RAM and its child-process timeout, retains ignored stage logs and timing under `.artifacts/blogging-quickstart/`, and stops only its selected GraphBridge project. Coordinate with other GraphBridge commands; its lock only serializes quickstart invocations. Review logs before sharing. A forced process/host termination can prevent cleanup; the retained environment supports explicit owned-fixture teardown.

For separate steps:

```powershell
dotnet restore GraphBridge.sln --locked-mode
powershell -File eng/scripts/Start-Neo4jFixture.ps1
powershell -File eng/scripts/Run-BloggingSample.ps1 -PrepareSchema
powershell -File eng/scripts/Stop-Neo4jFixture.ps1
```

Use `pwsh` on Linux. The resource preflight supports Windows and Linux; other hosts are not yet supported by these scripts. Reuse an already running repository fixture without calling Start again. Credentials come from its ignored environment file. The application verifies the disposable fixture marker before any setup or demo writes.

[appsettings.example.json](appsettings.example.json) documents the required environment variable names and fixture guards without credential values. The executable does not load this file; the runner supplies process environment variables from the owned fixture.

`-PrepareSchema` explicitly creates reviewed uniqueness constraints for the two sample labels. Subsequent runs can omit it. The provider requires constraints before SaveChanges; it never creates them implicitly. The official driver is used only for this explicit schema setup and fixture verification.

The workflow adds two Posts to `Blog.Posts` and calls `Add(blog)`. One save persists three EF entries, three nodes and two owned links; graph operation counts do not replace EF entry counts. All three row versions begin at 1. A fresh context queries `Blogs.Include(blog => blog.Posts)` with parameterized LINQ, verifies two children, shared parent identity and loaded navigation flags, and tracks exactly three entries.

It then updates the Blog URL (one entry, version 2), rolls back a Post content edit in an explicit transaction, and verifies the original content and versions in a fresh context through `Posts.Include(post => post.Blog)`. Reference Include shares one tracked Blog across both Posts and marks the references loaded; inverse fixup does not claim that the Blog's collection is eagerly loaded. Finally the workflow removes both dependents before their principal (three entries). Provider deletion removes only the Posts' owned links before deleting those nodes; Blog deletion remains restrictive. Only this invocation's randomly identified data is cleaned up. The command prints generated Cypher, EF counts, rollback, fixup and Include results.

Long `IsRowVersion` properties are provider-generated concurrency versions; use a fresh context after an explicit transaction rollback. Required node references use `DeleteBehavior.Restrict`, principal-to-dependent direction and stable application keys. Include supports one unfiltered navigation in either direction, including root ordering/paging and no-tracking reads. Reassigning a persisted Post to another Blog, optional references, client cascade, filtered/nested Include, `ReloadAsync` and `GetDatabaseValuesAsync` remain unsupported in this checkpoint.

The existing BLOG-03–06 cases retain fresh-context identity behavior, preservation of unrelated native properties, atomic rollback after a second insert fails, and stale-version conflicts. BLOG-04 verifies that the related Post remains unchanged. [Relationship acceptance tests](../../tests/GraphBridge.IntegrationTests/BloggingRelationshipAcceptanceTests.cs) inspect the real graph separately from the application: exactly two directed native links, empty link properties, no stored `BlogId`, three saved/deleted EF entries, fresh-context fixup, restrictive principal deletion and rejection of a legacy orphan. The compiled workflow is covered by [BloggingSampleTests](../../tests/GraphBridge.IntegrationTests/BloggingSampleTests.cs). These original cases pass the hosted acceptance linked above.

The verified BLOG-08 acceptance case uses this actual sample context to exercise collection and reference Include, complete child collections after root `Skip`/`Take`, empty collections, loaded flags, shared tracked identity and `AsNoTracking` in both directions. The separate BLOG-07 explicit root-query assertions distinguish ordinary EF fixup from eager loading.

The current checklist increment prints source and final runtime storage fingerprints and forwards Ctrl+C cancellation through the workflow. Cleanup uses an uncanceled token and removes only this invocation's owned IDs, preserving the original failure if cleanup also fails. Two new tests cover cancellation before transport and immediately after an acknowledged create commit. These additions pass the focused owned-fixture acceptance run recorded in the implementation status. Random per-invocation IDs isolate concurrent runs; payloads, relationships and expected counts remain deterministic.

Existing data from the earlier node-only sample is not silently upgraded. A Post with a scalar `BlogId` and no owned incoming `HAS_POST` link fails required-reference materialization. Review and execute a separate explicit migration to validate principal keys, create exactly one correct link per Post, handle missing/conflicting principals, and remove obsolete scalar copies if appropriate. Schema preparation only creates the two application-key uniqueness constraints; it performs no link backfill, cardinality repair or destructive reset. Those constraints do not themselves enforce one incoming parent link per Post.

Read-only deployment checks: [schema drift CLI and CI recipe](../../docs/features/SCHEMA_AND_MIGRATIONS.md).
