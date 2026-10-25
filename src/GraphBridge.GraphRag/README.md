# GraphBridge.GraphRag

**Experimental.** GraphRAG building blocks for [GraphBridge](https://github.com/joslat/GraphBridge.EFCore), the EF Core provider for Neo4j. The API may change in any preview; using it reports `GBRAG001`, which you acknowledge once with `<NoWarn>$(NoWarn);GBRAG001</NoWarn>`.

Everything is stored through EF Core as typed entities, and every quote keeps the exact bytes it came from.

- **Documents, sections and chunks:**
  - EF entities linked child to parent (`IN_DOCUMENT`, `SECTION_OF`, `IN_SECTION`, `NEXT_CHUNK`, `SUBSECTION_OF`);
  - each chunk's text is exactly its document's UTF-8 bytes `[StartByte, EndByte)`.
- **Embeddings:** a mapped `float[]` property, written by any `Microsoft.Extensions.AI` `IEmbeddingGenerator`.
- **Resumable ingestion:**
  - one transaction per document, and a lease per document;
  - unchanged documents are skipped;
  - transient model failures are retried at most twice.
- **Retrieval and answers:**
  - vector and full-text search fused by rank;
  - an evidence budget and a trace of where each item came from;
  - answers whose every citation is located in the stored text, or a refusal with a reason.
- **Ontologies and extraction:** the standard generic document, novel and article ontologies, or your own classes.
  - Extraction prunes what the ontology doesn't allow and records why.
  - Every mention and quote is located in its chunk.
  - Things resolve by exact name; placeholders such as "the man" never merge.
  - Facts list every document that states them.
- **Maintenance:** remove a source, reset what the toolkit wrote, clean up orphans, and a health report.
- **Agent tools:** `GraphRagTools` gives any chat client or agent `answer_from_documents`, `search_documents` and, with an ontology, `find_things` and `related_facts`. Every result carries IDs and byte spans, and an optional trail records each call.
- **Re-embedding:** `GraphRagReembedding.Plan(...)` moves to a new embedding set through a staging property, with preview, backfill, cut-over and clean-up, never replacing vectors in place.
- **Adapters:** `GraphBridge.GraphRag.DataIngestion` for DataIngestion pipelines, and `GraphBridge.GraphRag.AgentFramework` for Agent Framework agents.
- **Layouts:** `GraphRagLayout.Default`, or `GraphRagLayout.Neo4jGraphRag` for the names Neo4j's Python `neo4j-graphrag` library reads.

```csharp
// The model: dimensions must equal the embedding set's; the ontology is optional.
protected override void OnModelCreating(ModelBuilder modelBuilder)
    => modelBuilder.AddGraphRag(1536, o => o.Ontology = GraphRagOntologies.Article);

// The services: an IEmbeddingGenerator<string, Embedding<float>>, and an IChatClient for answers and extraction.
var set = new GraphRagEmbeddingSet("text-embedding-3-small", 1536);
services.AddGraphRag<AppDbContext>(set);

// The schema: constraints and indexes as typed migration steps, in your own reviewed plan.
var plan = new GraphMigrationPlan("app-002-graphrag", "app-001", GraphRagSchema.CreateSteps(GraphRagLayout.Default, set, GraphRagOntologies.Article),
    GraphMigrationRollbackPolicy.NoRollback("Dropping the indexes is a separate migration."));
await db.Database.ApplyGraphMigrationAsync(plan);

// Ingest Markdown and plain text, then ask, or query the graph with LINQ.
var receipt = await ingestion.IngestAsync(GraphRagSources.Folder("articles"));
var answer = await answers.AnswerAsync("Who works for the library?");
var quotes = await db.Set<Quoted>().ToListAsync();
```

Samples 07 to 09 in the repository run offline. Requires Neo4j 2026.09 or later. Design: ADR 0052 in the repository's `docs/architecture/adr`.
