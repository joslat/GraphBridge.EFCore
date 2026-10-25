# Changelog

All notable changes to GraphBridge. Every version so far is a developer preview; the [compatibility contract](docs/reference/COMPATIBILITY.md) describes what may change between previews.

## Unreleased: the GraphRAG toolkit

The toolkit's source, samples 07 to 09 and its docs are in the repository. Its three experimental packages, `GraphBridge.GraphRag`, `GraphBridge.GraphRag.DataIngestion` and `GraphBridge.GraphRag.AgentFramework`, ship with a later preview, once they have been verified with a real model. So far they have run only with stand-ins for the models.

### GraphRAG toolkit (experimental)

- **Added:** `GraphBridge.GraphRag`, building blocks for graph-based retrieval-augmented generation on GraphBridge ([ADR 0052](docs/architecture/adr/ADR-0052.md)). It is experimental: using it reports `GBRAG001`, and its API may change in any preview. Requires Neo4j 2026.09 or later.
  - **Store:** `modelBuilder.AddGraphRag(dimensions)` maps documents, sections and chunks with child-to-parent links that carry the run that wrote them. `GraphRagLayout.Neo4jGraphRag` writes the names Neo4j's Python `neo4j-graphrag` library reads. `GraphRagSchema` returns the constraints and indexes as steps for your own migration plan.
  - **Ingest:** text and Markdown loaders, a structure-aware chunker whose chunks are exactly their documents' UTF-8 bytes, and resumable ingestion: one transaction and one lease per document, unchanged documents skipped, embeddings in bounded parallel batches with transient-only retries.
  - **Retrieve and answer:** vector and full-text routes fused by reciprocal rank on chunk identity, with a per-item trace, a byte budget, neighbouring chunks and read-only Cypher expansions as labelled context. Answers come from any `IChatClient` with every quote located in its chunk, or a refusal with a reason.
  - **Maintain:** re-ingest only what changed, remove a source, reset by run or document, remove orphans, and a health report. Removal refuses nodes the toolkit didn't write.
  - **Ontologies and extraction:** `AddGraphRag(dimensions, o => o.Ontology = GraphRagOntologies.Article)` maps an ontology's classes as nodes, a mention relationship per class, its facts as associations and its who-said-it quotes. The standard ontologies are the generic document, the novel and the article; `GraphRagOntology.Custom(...)` takes your own classes, and `WithLabelPrefix(...)` keeps their labels apart from yours.
    - Ingestion then extracts each chunk through an `IChatClient` under a JSON schema read from the model (`GraphRagModelSchema`).
    - It prunes unknown kinds, disallowed relationships and quotes that aren't in the chunk, and records why in the receipt. Unreadable replies are quarantined, and an optional `IGraphRagExtractionVerifier` can refuse any candidate.
    - Things resolve by exact normalised name; placeholders such as "the man" never merge.
    - Facts list every document that states them, and are written so that concurrent runs don't lose each other's documents.
    - Removing a document takes it off its facts, and orphan clean-up removes things no mention points to.
  - **Sources:** `GraphRagSourceMetadata` sets a document's title, publication date, publisher and URL from the source itself.
  - **Agent tools:** `GraphRagTools.Create<TContext>(services)` returns `AIFunction`s:
    - `answer_from_documents`, with checked citations or a refusal;
    - `search_documents`, passages with exact spans;
    - with an ontology, `find_things` and `related_facts`.

    An optional `IGraphRagToolTrail` records each call with the evidence it returned.
  - **Re-embedding:** `GraphRagReembedding.Plan(layout, from, to, stagingProperty)` writes the new set to a staging property with its own index, then cuts over by layout and cleans up. Each stage previews first, and nothing is replaced in place.
  - **Chunked documents:** `IGraphRagIngestion.IngestChunkedAsync` ingests documents whose chunks were decided elsewhere, with the same lease, unchanged check, embeddings, extraction and transaction. `GraphRagChunkers.Create` exposes the core chunker.
  - **Tracing and receipts:** the `GraphBridge.GraphRag` activity source traces each stage: ingest, document, embed, extract, write, retrieve and answer. Tags hold IDs, counts, outcomes and timings only, never document text, questions, quotes or source URIs. Every answer carries a `Receipt` with its chat calls, tokens and the time spent retrieving and answering.
  - **Docs:** a GraphRAG section in `docs/graphrag`, marked experimental; it joins the docs site with the packages. It covers the overview with the provider gap list, the quick start, ontologies, ingestion, retrieval, answers with evidence ("GraphRAG you can trust"), agents and APIs, maintenance, evaluation and interop.
  - **Workshop:** lesson 31, "From documents to cited answers".
  - **Evaluation:** `tests/GraphBridge.GraphRag.Evaluation`, a test-only harness on AgentEval with deterministic metrics (citation validity, abstention and retrieval coverage), offline stand-ins and a labelled public-domain corpus. It isn't a package.
- **Added:** `GraphBridge.GraphRag.DataIngestion` (experimental), a `Microsoft.Extensions.DataIngestion` chunk writer that stores pipeline chunks with exact positions in their document's Markdown. Spans come from `GraphRagSpanChunker`'s metadata, or are recovered byte-exact or with whitespace runs normalised; a chunk that can't be placed fails its document and nothing of it is written.
- **Added:** `GraphBridge.GraphRag.AgentFramework` (experimental), a Microsoft Agent Framework `AIContextProvider`. It gives an agent the toolkit's tools and instructions to answer through `answer_from_documents`, so its citations are checked against the stored text.
- **Samples:** [08 GraphRAG novel](samples/08-GraphRagNovel/README.md), on *Alice's Adventures in Wonderland*, and [09 GraphRAG articles](samples/09-GraphRagArticles/README.md), on fictional articles. Both run offline with extraction replies written by hand; the real-model path through an OpenAI-compatible endpoint hasn't been run yet.

## 0.1.0-preview.5 (2026-10-26)

**Developer preview; not production-supported.** Five packages: the provider, the VectorData adapter, Tooling, Generators and Templates. This preview adds three provider features: embeddings as EF properties, vector and full-text index steps in migrations, and typed relationship upserts. The provider now declares EF Core below 11 and Neo4j.Driver below 7. The GraphRAG toolkit is in the repository; its packages come with a later preview (see "Unreleased" above).

### Relationship upserts

- **Added:** `ExecuteUpsertAsync` upserts associations (matched on their endpoint pair) and identified relationships (matched on their key) between existing nodes ([bulk mutations](docs/features/BULK_MUTATIONS.md#upserts), ADR 0051 amendment). A preflight in the same transaction locks both endpoints of every entity and refuses the call, writing nothing, when an endpoint is missing, an identified key belongs to a relationship between other endpoints, or a pair already has two associations. Earlier previews refused relationship types.

### Embeddings as EF properties

- **Added:** `Property(c => c.Embedding).IsGraphVector(dimensions)` maps a `float[]` or `ReadOnlyMemory<float>` property as an embedding, stored as a list of floats ([writing vectors through EF](docs/features/GRAPH_SEARCH.md#writing-vectors-through-ef)). SaveChanges, typed upserts and bulk updates write it with change tracking. A vector of another length, with a NaN or infinite value, or all zeros is refused before anything is sent.
- **Added:** queries return vectors and test them for null; other uses are refused with GBQ120, GBQ130, GBQ105, GBQ160 or the new Fluent Cypher code GBFC114. Doctor reports a declared vector index whose dimensions differ from the mapped property's (new finding GBD-SEARCH-012).
- The schema manifest records a vector property as `LIST<FLOAT>` with its dimensions. Manifests of models without vectors keep their fingerprints.

### Migrations: vector and full-text indexes

- **Added:** typed migration plans create and drop vector and full-text indexes, with the same preview, journal, exact-definition check and wait until `ONLINE` as range indexes ([schema and migrations](docs/features/SCHEMA_AND_MIGRATIONS.md#vector-and-full-text-indexes)). `GraphMigrationSchemaDefinition.VectorIndex(...)` takes the dimensions and similarity; `FullTextIndex(...)` takes up to 16 labels or types and properties, an analyzer and the consistency mode. A differently configured index under the step's name stops the step for review, and an equivalent index under another name refuses creation.
- **Added:** `GraphObservedIndex.IndexConfig`, the observed `options.indexConfig`, and the `VectorIndexes` and `FullTextIndexes` capabilities: supported on 2025 and 2026 releases, unknown on Neo4j 5.
- Checksums of plans made of the earlier step kinds are unchanged, so journals of applied migrations still match.

### LINQ

- **Instance `Equals` in predicates:** `Where(p => p.Name.Equals(name))` and the same on `Guid`, `int`, `long`, `bool` and enum properties now translate as `==`, with the same null handling and value checks as `object.Equals`. They were refused with GBQ120. Overloads that take a comparison, such as `string.Equals(value, StringComparison)`, are still refused.

### Fixes

- **Fixed:** a node or identified relationship deleted in a transaction can be added again under the same key in that transaction. GraphBridge took an element's write lock by writing its key to itself; on Neo4j 2026.09 that keeps the key taken until the transaction ends, so adding the element again failed with an index entry conflict. Locks now set and remove a transient property, `__graphbridge_lock`, which leaves nothing behind. Found when the GraphRAG toolkit re-ingested a document under new settings: its chunks are deleted and written again with the same IDs.

### Dependencies

- **Changed:** the provider package declares `Microsoft.EntityFrameworkCore` as `[10.0.13, 11.0.0)` and `Neo4j.Driver` as `[6.4.0, 7.0.0)`. Earlier previews declared minimums only, so NuGet could pair GraphBridge with EF Core 11, whose internal provider APIs it hasn't been built against. NuGet now flags EF Core 11 or driver 7 at restore ([compatibility](docs/reference/COMPATIBILITY.md#exact-stack)).
- **Changed:** Neo4j.Driver 6.3.0 → 6.4.0, which fixes two security issues: its certificate validator accepted a server that presented no certificate, and `CustomAuthToken.ToString()` printed credentials. GraphBridge's use of the driver is unchanged.

## 0.1.0-preview.4 (2026-10-23)

**Developer preview; not production-supported.** Five packages: the provider, the VectorData adapter, Tooling, Generators and Templates. This preview adopts what Neo4jClient users rely on: whole entities from Fluent Cypher, typed node upserts, query tags, command timeouts and write counters, with a [guide for Neo4jClient users](docs/COMING_FROM_NEO4JCLIENT.md). Doctor also warns early about search indexes. NuGet package validation against 0.1.0-preview.3 finds no binary-breaking API change in the provider, VectorData, Tooling or Generators packages; behavior changes are marked **Changed** below.

### Doctor: early warnings for search indexes

- **Declare the indexes your searches use** with `GraphSearchIndexExpectation.Vector(...)` or `.FullText(...)` in a `GraphQueryCatalog`. Connected Doctor then checks each one before the first search ([guide](docs/tools/DOCTOR.md#search-indexes)), using the new codes `GBD-SEARCH-000` to `GBD-SEARCH-011`. It reports:
  - **Failures:** a missing, not-yet-ONLINE or FAILED index; the wrong kind; different labels or properties; different vector dimensions.
  - **Warnings:** a different similarity function; stored values that Neo4j silently leaves out of the index (wrong dimensions, non-numeric values, zero vectors on cosine); an index with nothing to search.
  - **Other:** servers that can't run typed search, and eventually consistent full-text indexes.
- **No changes to the database, nothing retained:** Doctor reads metadata and a bounded sample in its own rolled-back read transaction and never creates or changes an index. The report holds only the declared ID, finding codes and counts: never index names, labels, properties or values.
- **Public API additions (preview):** `GraphQueryCatalog` takes an optional list of search index declarations, `GraphEvidenceReport` has `SearchIndexes`, and its constructor gains an optional `searchIndexes` parameter. The 0.1.0-preview.3 constructors of both types remain, so code compiled against preview.3 keeps working (checked with NuGet package validation). The Query Lab HTML report lists the declared indexes.

### Fluent Cypher: whole entities

- **Return a node or relationship as its entity:** `Return(() => movie.Value)` now gives the mapped `Movie`, alone or inside a DTO ([guide](docs/features/FLUENT_CYPHER.md#returning-whole-entities), [ADR 0049](docs/architecture/adr/ADR-0049.md)). Results are untracked: nothing is attached and there is no identity resolution. The query returns only the mapped properties, values are read as LINQ reads them, and EF creates the instance. Nodes with a native-reference foreign key and both relationship mappings are supported; an entity that a missing OPTIONAL MATCH didn't find is null.
- **Lists of entities:** `Graph.Collect(node or edge, max)` and `Graph.CollectDistinct(...)` return a bounded `IReadOnlyList<T>` of entities.
- **Ordering by a returned entity's member:** `OrderBy(() => movie.Value.Year)` when `movie.Value` is returned, including after `ReturnDistinct`.
- **Changed:** GBFC404 now refuses only constructing a mapped entity type in RETURN, such as `new Movie { ... }`; returning the symbol's `Value` was refused before and now works. New code GBFC408: an entity whose store property names start with the reserved `__gb_` prefix can't be returned as an entity.

### Query tags, timeouts and write counts

- **Query tags:** EF Core's `TagWith` and `TagWithCallSite` were accepted and silently ignored; they now lead the generated Cypher as `//` comment lines, one per line of the tag, on queries, Include queries and bulk mutations ([inspection](docs/features/QUERY_INSPECTION.md#what-toquerystring-shows), [ADR 0050](docs/architecture/adr/ADR-0050.md)). Fluent Cypher gains `TagWith`. Keep tags constant text.
- **Command timeouts:** `UseNeo4j(driver, database, neo4j => neo4j.CommandTimeout(...))`, `Database.SetGraphCommandTimeout(...)` and Fluent `WithTimeout(...)` set the timeout of the transactions GraphBridge begins ([guide](docs/features/TRANSACTIONS_AND_CONCURRENCY.md#command-timeouts)). A timeout keeps the driver's exception and code; trace spans get `error.type = timeout`.
- **Write counters:** `Database.ExecuteCypherAsync(...)` runs native Cypher for its effect and returns `GraphWriteCounters`; SaveChanges, bulk and native write spans carry `graphbridge.counters.*` tags ([tracing](docs/features/COMMAND_TRACING.md#tags)).
- **Changed:** a query with `TagWith` now sends different Cypher text, so it gets its own server plan-cache entry and statement fingerprint. Keep tags constant; never put user data in them.
- **Public API additions (preview):** `Neo4jDbContextOptionsBuilder`, a `UseNeo4j` overload taking it, `Neo4jDbContextOptionsExtension.CommandTimeout` and `WithCommandTimeout`, `Neo4jCommandTimeoutDatabaseFacadeExtensions`, `Neo4jDatabaseFacadeExtensions.ExecuteCypherAsync`, `GraphWriteCounters`, and `CypherResultQuery<T>.TagWith` and `WithTimeout`.

### Typed batch upserts

- **`ExecuteUpsertAsync`** creates the node for each entity whose key no node has, and updates the node that has it, in one statement and one transaction of at most 10,000 entities ([guide](docs/features/BULK_MUTATIONS.md#upserts), [ADR 0051](docs/architecture/adr/ADR-0051.md)). Values are converted as SaveChanges converts them, and a selector such as `p => new { p.Name, p.Email }` limits what an existing node receives.
- **Checked before writing:** the uniqueness constraint on the key, duplicate keys in one call, default key values and the 10,000-entity cap. Relationships, native-reference dependents, concurrency tokens and shadow properties are refused for now.
- **Result:** `GraphUpsertResult` with `Created`, `Matched` and the write counters; traced as `GraphBridge.Upsert`.
- **Public API additions (preview):** `Neo4jUpsertDbSetExtensions.ExecuteUpsertAsync`, `GraphUpsertResult` and `GraphBridgeDiagnostics.UpsertActivityName`.

### Repository

- **Issue forms:** new issues use forms with required version fields, and there is a new API proposal form. Blank issues are off; questions go to [Discussions](https://github.com/joslat/GraphBridge.EFCore/discussions).
- **[Roadmap](ROADMAP.md):** what's next, what's deliberately not planned, the proposed criteria for leaving preview, and how to influence it.
- **Release tooling:** public fetches use `--no-tags`, and the post-release live check waits until nuget.org search lists the new version.

## 0.1.0-preview.3 (2026-10-17)

**Developer preview; not production-supported.** Five packages: the provider, the new VectorData adapter, Tooling, Generators and Templates.

### Thanks

The VectorData adapter is Luis Quintanilla's idea. In [issue #1](https://github.com/joslat/GraphBridge.EFCore/issues/1) he proposed exposing GraphBridge's vector search through `Microsoft.Extensions.VectorData`, with a complete design and acceptance criteria, and the new package follows that design. Thank you, Luis!

### VectorData adapter (new optional package)

- **`GraphBridge.EntityFrameworkCore.VectorData`:** `AsNodeVectorSearchable` and `AsRelationshipVectorSearchable` expose an existing Neo4j vector index as `Microsoft.Extensions.VectorData`'s `IVectorSearchable<TRecord>` ([guide](docs/features/VECTOR_DATA.md), [sample 06](samples/06-VectorSearch/README.md)). The adapter accepts `ReadOnlyMemory<float>`, `float[]` and `Embedding<float>` and passes native scores through unchanged. It refuses every non-default search option before database work, and detects a pooled context that was leased to another scope. The core provider takes no VectorData dependency. Design proposed by Luis Quintanilla in [issue #1](https://github.com/joslat/GraphBridge.EFCore/issues/1).
- **Workshop:** [lesson 20](workshop/GraphBridge.Workshop/Lessons/20-Search/README.md) ends with the same vector search through `IVectorSearchable` and checks that it returns the same movies and scores.

### Fluent Cypher

- **Typed refusals:** a refused Fluent query now throws `FluentCypherException`, derived from `NotSupportedException`, with the `GBFC###` code in its `Code` property ([ADR 0047, Amendment 2](docs/architecture/adr/ADR-0047.md#amendment-2-a-typed-fluent-exception)). The message and `Exception.Data["GraphBridge.FluentDiagnostic"]` are unchanged, and `catch (NotSupportedException)` keeps working. Code that checks the exact type, such as xUnit's `Assert.Throws<NotSupportedException>`, should switch to `FluentCypherException`.

### LINQ refusals

- `Neo4jQueryTranslationException` exposes its `GBQ###` code as `Code`, alongside `Exception.Data["GraphBridge.QueryDiagnostic"]`.

### Dependencies

- **EF Core 10.0.13** (was 10.0.12). Between the two, EF's core library changed only complex-collection change tracking, which GraphBridge does not use ([source check](docs/architecture/EF_SERVICE_MAP.md)). Pin `Microsoft.EntityFrameworkCore` to `[10.0.13]` in your application; new projects from the template already do. Neo4j.Driver stays at 6.3.0.

### Documentation site

- The site's theme is updated to Material for MkDocs 9.7.7, which fixes a cross-site scripting flaw in search suggestions ([GHSA-xvg9-69gf-fjrf](https://github.com/advisories/GHSA-xvg9-69gf-fjrf)). No package is affected.

### Repository

- Pull requests to the public repository are imported into development with the contributor credited, then published in the next snapshot ([CONTRIBUTING](CONTRIBUTING.md)).

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

