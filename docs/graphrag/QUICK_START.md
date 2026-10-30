# Quick start

[Sample 07](../../samples/07-GraphRagQuickStart/README.md) ingests GraphBridge's own documentation, then answers questions with checked citations. It runs offline: a hashing embedder and an extractive chat client stand in for models. To start a project of your own with the same steps, use the [GraphRAG starter template](../tools/TEMPLATE.md#the-graphrag-starter-experimental): `dotnet new graphbridge-graphrag`.

## 0. Get the packages and a database

Reference the provider and the toolkit at one version, and acknowledge the experimental API once in your project file:

```xml
<PropertyGroup>
  <NoWarn>$(NoWarn);GBRAG001</NoWarn>
</PropertyGroup>
<ItemGroup>
  <PackageReference Include="GraphBridge.EntityFrameworkCore" Version="0.2.0-preview.1" />
  <PackageReference Include="GraphBridge.GraphRag" Version="0.2.0-preview.1" />
</ItemGroup>
```

Add `GraphBridge.GraphRag.Pdf`, `.DataIngestion`, `.AgentFramework` or `.AspNetCore` at the same version if you use them: the toolkit needs the provider built from the same source.

To try changes that aren't released yet, pack them from a clone into a local feed instead, with one version for every package. Inside the clone you need the .NET SDK 10.0.401, which the repository's `global.json` pins exactly:

```powershell
git clone https://github.com/joslat/GraphBridge.EFCore
cd GraphBridge.EFCore
$version = "0.1.0-local.1"
$feed = (New-Item -ItemType Directory -Force ../graphbridge-feed).FullName
# Add GraphBridge.GraphRag.Pdf, .DataIngestion, .AgentFramework or .AspNetCore if you use them.
foreach ($project in "GraphBridge.EntityFrameworkCore", "GraphBridge.GraphRag") {
    dotnet pack "src/$project/$project.csproj" -c Release "-p:PackageVersion=$version" -o $feed
}
dotnet nuget add source $feed --name graphbridge-local
```

Add the feed by its full path: NuGet reads a relative path in its configuration from the configuration file's folder, not from where you ran the command. Then reference the packages at `0.1.0-local.1`.

The toolkit needs Neo4j 2026.09 or later. The [tutorial](TUTORIAL.md#1-start-a-disposable-database) starts a disposable one in Docker with one command. The [project template](../tools/TEMPLATE.md#the-graphrag-starter-experimental) sets all of this up for you.

## 1. Map the model

The context, in a file of its own or after the program's statements:

```csharp
using GraphBridge.GraphRag;
using Microsoft.EntityFrameworkCore;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.AddGraphRag(embeddingDimensions: 1536);
}
```

`AddGraphRag` maps documents, sections, chunks, their links, ingestion runs and leases. The dimensions must equal your embedding model's.

## 2. Register the toolkit

```csharp
using GraphBridge.EntityFrameworkCore.Migrations;
using GraphBridge.GraphRag;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Neo4j.Driver;

await using var driver = GraphDatabase.Driver("neo4j://localhost:7687",
    AuthTokens.Basic("neo4j", Environment.GetEnvironmentVariable("GRAPHBRIDGE_NEO4J_PASSWORD")));
var set = new GraphRagEmbeddingSet("text-embedding-3-small", 1536);
await using var services = new ServiceCollection()
    .AddDbContext<AppDbContext>(o => o.UseNeo4j(driver, "neo4j"))
    .AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(embeddings)
    .AddSingleton<IChatClient>(chat)
    .AddGraphRag<AppDbContext>(set)
    .BuildServiceProvider();
await using var scope = services.CreateAsyncScope();
```

The `GraphRagEmbeddingSet` names your embedding model and its dimensions; its `Id`, a hash of them, names the vector index, so another model gets another index.

`embeddings` and `chat` are your model clients, any `Microsoft.Extensions.AI` implementation; [sample 07](../../samples/07-GraphRagQuickStart/README.md) has offline stand-ins. In an ASP.NET Core application, register the same on `builder.Services`.

## 3. Apply the schema as a migration

```csharp
var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
await db.Database.InitializeGraphMigrationsAsync();
var plan = new GraphMigrationPlan("app-001-graphrag", null, GraphRagSchema.CreateSteps(GraphRagLayout.Default, set),
    GraphMigrationRollbackPolicy.NoRollback("Dropping the indexes is a separate, reviewed migration."));
await db.Database.ApplyGraphMigrationAsync(plan);
```

`InitializeGraphMigrationsAsync` creates the migration journal the first time and checks it after that. A plan's second argument is the plan before it: `null` here, because this is the database's first migration. If your application has migrations of its own, give the ID of its last one instead.

The toolkit never creates schema itself. The plan holds:
- a uniqueness constraint on every key;
- the chunk vector index, named after the embedding set;
- a full-text index on chunk text.

Preview it, review it, and apply it like any other [migration plan](../features/SCHEMA_AND_MIGRATIONS.md).

## 4. Ingest and ask

```csharp
var receipt = await scope.ServiceProvider.GetRequiredService<IGraphRagIngestion>().IngestAsync(GraphRagSources.Folder("docs"));
var answer = await scope.ServiceProvider.GetRequiredService<IGraphRagAnswers>().AnswerAsync("How are vector indexes created?");
if (answer.Disposition == GraphRagAnswerDisposition.Answered)
    foreach (var citation in answer.Citations) Console.WriteLine($"{citation.Quote} [{citation.StartByte}-{citation.EndByte}]");
else Console.WriteLine($"Not answered: {answer.Reason}");
```

Run `IngestAsync` again and unchanged files are skipped. Samples [08](../../samples/08-GraphRagNovel/README.md) and [09](../../samples/09-GraphRagArticles/README.md) add the novel and article ontologies.
