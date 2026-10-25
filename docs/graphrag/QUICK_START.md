# Quick start

[Sample 07](../../samples/07-GraphRagQuickStart/README.md) ingests GraphBridge's own documentation, then answers questions with checked citations. It runs offline: a hashing embedder and an extractive chat client stand in for models.

## 1. Map the model

```csharp
public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.AddGraphRag(embeddingDimensions: 1536);
}
```

`AddGraphRag` maps documents, sections, chunks, their links, ingestion runs and leases. The dimensions must equal your embedding model's.

## 2. Register the toolkit

```csharp
var set = new GraphRagEmbeddingSet("text-embedding-3-small", 1536);
services.AddDbContext<AppDbContext>(o => o.UseNeo4j(driver, "neo4j"))
    .AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(embeddings)
    .AddSingleton<IChatClient>(chat)
    .AddGraphRag<AppDbContext>(set);
```

## 3. Apply the schema as a migration

```csharp
var plan = new GraphMigrationPlan("app-002-graphrag", "app-001", GraphRagSchema.CreateSteps(GraphRagLayout.Default, set),
    GraphMigrationRollbackPolicy.NoRollback("Dropping the indexes is a separate, reviewed migration."));
await db.Database.ApplyGraphMigrationAsync(plan);
```

The toolkit never creates schema itself. The plan holds:
- a uniqueness constraint on every key;
- the chunk vector index, named after the embedding set;
- a full-text index on chunk text.

Preview it, review it, and apply it like any other [migration plan](../features/SCHEMA_AND_MIGRATIONS.md).

## 4. Ingest and ask

```csharp
var receipt = await services.GetRequiredService<IGraphRagIngestion>().IngestAsync(GraphRagSources.Folder("docs"));
var answer = await services.GetRequiredService<IGraphRagAnswers>().AnswerAsync("How are vector indexes created?");
if (answer.Disposition == GraphRagAnswerDisposition.Answered)
    foreach (var citation in answer.Citations) Console.WriteLine($"{citation.Quote} [{citation.StartByte}-{citation.EndByte}]");
else Console.WriteLine($"Not answered: {answer.Reason}");
```

Run `IngestAsync` again and unchanged files are skipped. Samples [08](../../samples/08-GraphRagNovel/README.md) and [09](../../samples/09-GraphRagArticles/README.md) add the novel and article ontologies.
