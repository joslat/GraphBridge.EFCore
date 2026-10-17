# Getting started

This page takes you from an empty console application to a saved node and the Cypher GraphBridge generates for it: install the packages, configure `UseNeo4j` with a driver your application owns, map one entity, create its key constraint, then save and query. Read [Concepts](CONCEPTS.md) first if you want the ideas behind each step.

## What you need

- .NET SDK 10 and a `net10.0` project. GraphBridge is built and tested with SDK 10.0.401.
- A Neo4j server you can write to and create a constraint in. GraphBridge is tested on Neo4j 2026.09 Community; see [compatibility](reference/COMPATIBILITY.md). For a disposable local server with Docker, follow the workshop's [Get a database](../workshop/GraphBridge.Workshop/README.md#get-a-database).
- The GraphBridge packages, from [nuget.org](https://www.nuget.org/packages/GraphBridge.EntityFrameworkCore). To try changes that are not released yet, build them from source instead: see [Install from a local feed](#install-from-a-local-feed).

## 1. Install the packages

Reference the provider and pin the EF Core and driver versions it is tested with:

```xml
<ItemGroup>
  <PackageReference Include="GraphBridge.EntityFrameworkCore" Version="0.1.0-preview.3" />
  <PackageReference Include="Microsoft.EntityFrameworkCore" Version="[10.0.13]" />
  <PackageReference Include="Neo4j.Driver" Version="[6.3.0]" />
</ItemGroup>
```

`0.1.0-preview.3` is the current preview on nuget.org; pin it exactly, because previews may change. The provider declares only minimum versions of EF Core and Neo4j.Driver, so the explicit pins keep NuGet from resolving untested newer ones.

## 2. Map an entity and configure the context

An ordinary EF Core entity and context. `UseNeo4j` lends EF a driver your application owns; `ToGraphNode` maps the entity to a label, and `HasGraphPropertyName` sets the stored property names:

```csharp
using Microsoft.EntityFrameworkCore;
using Neo4j.Driver;

public sealed class Person
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public int? Born { get; set; }
    public string? Nickname { get; set; }
    public long Version { get; set; }
}

public sealed class PeopleContext(IDriver driver, string database) : DbContext
{
    public DbSet<Person> People => Set<Person>();

    protected override void OnConfiguring(DbContextOptionsBuilder options)
        => options.UseNeo4j(driver, database);

    protected override void OnModelCreating(ModelBuilder model)
        => model.Entity<Person>(person =>
        {
            person.ToGraphNode("Person");
            person.Property(p => p.Id).ValueGeneratedNever().HasGraphPropertyName("id");
            person.Property(p => p.Name).HasGraphPropertyName("name");
            person.Property(p => p.Born).HasGraphPropertyName("born");
            person.Property(p => p.Nickname).HasGraphPropertyName("nickname");
            person.Property(p => p.Version).HasGraphPropertyName("version").IsRowVersion();
        });
}
```

`Id` is an application key that you assign. `IsRowVersion()` makes GraphBridge maintain `Version` for [optimistic concurrency](features/TRANSACTIONS_AND_CONCURRENCY.md#optimistic-concurrency). [Mapping nodes](features/MAPPING_NODES.md) covers keys, names and types in full.

Create the driver once, keep it for the application's lifetime and dispose it at shutdown. Contexts are short-lived and never close it. Read the credentials from configuration, never from source:

```csharp
await using var driver = GraphDatabase.Driver(
    Environment.GetEnvironmentVariable("NEO4J_URI") ?? "bolt://localhost:7687",
    AuthTokens.Basic(Environment.GetEnvironmentVariable("NEO4J_USERNAME") ?? "neo4j",
        Environment.GetEnvironmentVariable("NEO4J_PASSWORD")!));
```

## 3. Create the key constraint

GraphBridge never creates schema. `SaveChangesAsync` refuses to write a `Person` until a uniqueness constraint covers its key. Ask GraphBridge what the model needs:

```csharp
await using (var db = new PeopleContext(driver, "neo4j"))
{
    var inspection = await db.Database.InspectGraphSchemaAsync();   // reads metadata only
    foreach (var proposal in inspection.Comparison.Proposals.Where(p => p.IsRequired))
        Console.WriteLine(proposal.Cypher);
}
```

```cypher
CREATE CONSTRAINT `<name>` FOR (n:`Person`) REQUIRE (n.`id`) IS UNIQUE
```

`<name>` is a generated constraint name. Review the statement and run it once with an administrative account, for example in Neo4j Browser or `cypher-shell`. In a real deployment this belongs in a reviewed step or a [migration plan](features/SCHEMA_AND_MIGRATIONS.md), not in application startup.

## 4. Save and query

```csharp
var id = Guid.NewGuid();
await using (var db = new PeopleContext(driver, "neo4j"))
{
    db.People.Add(new Person { Id = id, Name = "Keanu Reeves", Born = 1964 });
    await db.SaveChangesAsync();                  // one CREATE in one transaction; Version becomes 1
}

await using (var db = new PeopleContext(driver, "neo4j"))
{
    var name = "Keanu Reeves";
    var query = db.People.Where(p => p.Name == name).OrderBy(p => p.Name).Take(5);
    Console.WriteLine(query.ToQueryString());     // the Cypher, without running it
    var people = await query.ToListAsync();
}
```

Use the async methods: `SaveChangesAsync`, `ToListAsync`, `SingleAsync` and `await using`. Synchronous `SaveChanges()` and `ToList()` throw `NotSupportedException` before any database work.

## 5. See the Cypher

`ToQueryString()` prints the Cypher without contacting the database:

```cypher
MATCH (n:`Person`)
WHERE coalesce(n.`name` = $p0, (n.`name` IS NULL AND $p0 IS NULL))
RETURN n.`id` AS `c0`, n.`born` AS `c1`, n.`name` AS `c2`, n.`nickname` AS `c3`, n.`version` AS `c4`
ORDER BY n.`name` IS NULL DESC, n.`name` ASC
LIMIT $p1
```

The name is a parameter (`$p0`), never part of the text. The `coalesce(...)` and `IS NULL` parts keep C# null semantics, where `null == null` is true. [Query inspection](features/QUERY_INSPECTION.md) shows how to get the server's plan for a query.

## Where to go next

- **[Workshop](WORKSHOP.md):** 31 runnable lessons on a small movie graph, from this first context to relationships, Fluent Cypher and the tools. Lessons 01–04 extend this page.
- **[Samples](SAMPLES.md):** complete applications to copy from: code-first, ontology-first, database-first, design-time commands and a Fluent Cypher gallery.
- **Feature guides:** [mapping nodes](features/MAPPING_NODES.md), [references](features/REFERENCES.md), [associations](features/ASSOCIATIONS.md), [identified relationships](features/IDENTIFIED_RELATIONSHIPS.md), [LINQ queries](features/LINQ_QUERIES.md), [Fluent Cypher](features/FLUENT_CYPHER.md), [native Cypher](features/NATIVE_CYPHER.md), [transactions and concurrency](features/TRANSACTIONS_AND_CONCURRENCY.md), [schema and migrations](features/SCHEMA_AND_MIGRATIONS.md).
- **Before you build on it:** [compatibility](reference/COMPATIBILITY.md), [known limitations](reference/KNOWN_LIMITATIONS.md) and [troubleshooting](reference/TROUBLESHOOTING.md).

To see a complete application run without writing code, `pwsh eng/scripts/Run-BloggingQuickstart.ps1` (PowerShell 7 and Docker) restores and builds the Blogging sample, starts a disposable Neo4j container, runs the sample and removes the container again. It refuses to start when a repository fixture is already running, and checks that enough memory is free first.

## Install from a local feed

> **Only for unreleased changes.** Published previews install from nuget.org, as in step 1. To try changes that are not released yet, build the packages from a clone of this repository into a folder, and use that folder as a NuGet source.

From the repository root, with .NET SDK 10.0.401, pack the provider with a version of your own. Use a new version every time you pack: NuGet caches packages by version, so repacking the same version can keep giving you the old bytes.

```powershell
$version = '0.1.0-local.' + (Get-Date -Format 'yyyyMMddHHmmss')
$feed = Join-Path $HOME 'graphbridge-feed'
dotnet restore src/GraphBridge.EntityFrameworkCore/GraphBridge.EntityFrameworkCore.csproj --locked-mode
dotnet pack src/GraphBridge.EntityFrameworkCore/GraphBridge.EntityFrameworkCore.csproj --no-restore "-p:PackageVersion=$version" -o $feed
$version   # use this version in your PackageReference
```

Pack the optional packages the same way, with the same version, when you need them: `src/GraphBridge.Tooling/GraphBridge.Tooling.csproj`, `src/GraphBridge.Generators/GraphBridge.Generators.csproj` and `templates/GraphBridge.Templates.csproj` (see [packages and components](reference/PACKAGES_AND_COMPONENTS.md)).

Then add a `NuGet.Config` next to your application that lists the feed and nuget.org:

```xml
<configuration>
  <packageSources>
    <clear />
    <add key="graphbridge-local" value="C:\Users\you\graphbridge-feed" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
```

and add the packages:

```powershell
dotnet add package GraphBridge.EntityFrameworkCore --version $version
dotnet add package Microsoft.EntityFrameworkCore --version '[10.0.13]'
dotnet add package Neo4j.Driver --version '[6.3.0]'
```

The driver package 6.3.0 reports assembly version 6.3.11.0; its informational version identifies 6.3.0. To check packed bytes end to end, `eng/scripts/Test-ProviderPackage.ps1` packs the provider with a unique version and builds an isolated PackageReference-only consumer from it. The [`dotnet new` template](tools/TEMPLATE.md) produces the same kind of package-only application with two offline query tests.
