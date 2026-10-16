![GraphBridge](https://raw.githubusercontent.com/joslat/GraphBridge.EFCore/main/docs/images/graphbridge-logo-small.png)

# GraphBridge for Entity Framework Core

[![NuGet](https://img.shields.io/nuget/vpre/GraphBridge.EntityFrameworkCore?label=nuget&color=0b6ae6)](https://www.nuget.org/packages/GraphBridge.EntityFrameworkCore)
![.NET 10](https://img.shields.io/badge/.NET-10-512bd4)
![EF Core 10](https://img.shields.io/badge/EF%20Core-10-512bd4)
![Neo4j 2026.09](https://img.shields.io/badge/Neo4j-2026.09-018bff)
[![License: MIT](https://img.shields.io/badge/license-MIT-0b6ae6)](https://github.com/joslat/GraphBridge.EFCore/blob/main/LICENSE)
![Developer preview](https://img.shields.io/badge/status-developer%20preview-e8a317)

![A graph database, no second ORM. A real Entity Framework Core provider for Neo4j.](https://raw.githubusercontent.com/joslat/GraphBridge.EFCore/main/docs/images/graphbridge-hero.svg)

GraphBridge is a real, non-relational Entity Framework Core 10 provider for Neo4j. EF Core owns your model, entity identity and change tracking; the official Neo4j .NET driver owns the connection; GraphBridge stores entities as nodes and relationships and translates queries into parameterized Cypher. No second ORM, no client-side query fallback, no hidden schema change.

> **Developer preview, not production-supported.** APIs and mappings may change between previews: pin the exact version. Use the async methods; synchronous `SaveChanges()` and `ToList()` are refused.

## Install

```shell
dotnet add package GraphBridge.EntityFrameworkCore --prerelease
```

Pin the EF Core and driver versions GraphBridge is tested with:

```xml
<PackageReference Include="Microsoft.EntityFrameworkCore" Version="[10.0.12]" />
<PackageReference Include="Neo4j.Driver" Version="[6.3.0]" />
```

## Quick start

Map an ordinary entity to a node and lend EF a driver your application owns:

```csharp
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

GraphBridge never creates schema: create the key constraint it proposes once, then save and query.

```csharp
await using (var db = new PeopleContext(driver, "neo4j"))
{
    db.People.Add(new Person { Id = Guid.NewGuid(), Name = "Keanu Reeves", Born = 1964 });
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

```cypher
MATCH (n:`Person`)
WHERE coalesce(n.`name` = $p0, (n.`name` IS NULL AND $p0 IS NULL))
RETURN n.`id` AS `c0`, n.`born` AS `c1`, n.`name` AS `c2`, n.`nickname` AS `c3`, n.`version` AS `c4`
ORDER BY n.`name` IS NULL DESC, n.`name` ASC
LIMIT $p1
```

Values are always parameters, and C# null semantics are kept. The full walkthrough, including the key constraint, is in [Getting started](https://joslat.github.io/GraphBridge.EFCore/GETTING_STARTED.html).

## Every EF concept has a native graph shape

![Four storage forms: node, native reference, association, identified relationship](https://raw.githubusercontent.com/joslat/GraphBridge.EFCore/main/docs/images/storage-forms.svg)

```csharp
model.Entity<Person>().ToGraphNode("Person");
model.Entity<Review>().HasOne(r => r.Movie).WithMany(m => m.Reviews)
    .HasForeignKey(r => r.MovieId).ToGraphReference("HAS_REVIEW");   // (:Movie)-[:HAS_REVIEW]->(:Review)
```

[Nodes](https://joslat.github.io/GraphBridge.EFCore/features/MAPPING_NODES.html) · [references](https://joslat.github.io/GraphBridge.EFCore/features/REFERENCES.html) · [associations](https://joslat.github.io/GraphBridge.EFCore/features/ASSOCIATIONS.html) · [identified relationships](https://joslat.github.io/GraphBridge.EFCore/features/IDENTIFIED_RELATIONSHIPS.html)

## Three ways to query

![Choosing a level: EF Core LINQ, Fluent Cypher with lambdas, Fluent Cypher with explicit methods, native Cypher](https://raw.githubusercontent.com/joslat/GraphBridge.EFCore/main/docs/images/query-surfaces.svg)

| Surface | Use it for |
|---|---|
| [EF Core LINQ](https://joslat.github.io/GraphBridge.EFCore/features/LINQ_QUERIES.html) | Tracked entities and DTOs with EF's C# semantics |
| [Fluent Cypher](https://joslat.github.io/GraphBridge.EFCore/features/FLUENT_CYPHER.html) | Typed graph patterns, optional matches, aggregates, EXISTS and paths |
| [Native Cypher](https://joslat.github.io/GraphBridge.EFCore/features/NATIVE_CYPHER.html) | Anything else, as reviewed parameterized text |

A query that cannot be translated faithfully is refused before it reaches the database, with a code (`GBQ` for LINQ, `GBFC` for Fluent Cypher) and a supported alternative. See [known limitations](https://joslat.github.io/GraphBridge.EFCore/reference/KNOWN_LIMITATIONS.html).

## Tested with

| | Version |
|---|---|
| .NET SDK | 10.0.401 |
| EF Core | 10.0.12 |
| Neo4j.Driver | 6.3.0 |
| Neo4j | 2026.09 Community; Enterprise reader/writer permissions verified separately |

Other versions, Enterprise schema deployment and clustering are not verified yet. Details: [compatibility](https://joslat.github.io/GraphBridge.EFCore/reference/COMPATIBILITY.html).

## Companion packages

| Package | What it adds |
|---|---|
| [GraphBridge.Tooling](https://www.nuget.org/packages/GraphBridge.Tooling) | Doctor, Verify, Query Lab, design-time commands, ontology compilation and scaffolding |
| [GraphBridge.Generators](https://www.nuget.org/packages/GraphBridge.Generators) | Compile-time mapping registration from attributes |
| [GraphBridge.Templates](https://www.nuget.org/packages/GraphBridge.Templates) | `dotnet new graphbridge`: a console app with offline query tests |

None of them is needed at runtime.

## Learn more

- [Visual introduction](https://joslat.github.io/GraphBridge.EFCore/WHAT_IS_GRAPHBRIDGE.html) and [documentation](https://joslat.github.io/GraphBridge.EFCore/)
- [Workshop](https://github.com/joslat/GraphBridge.EFCore/blob/main/workshop/README.md): 31 runnable lessons
- [Samples](https://github.com/joslat/GraphBridge.EFCore/tree/main/samples): code-first, ontology-first and database-first applications
- [Changelog](https://github.com/joslat/GraphBridge.EFCore/blob/main/CHANGELOG.md) and [verification summary](https://github.com/joslat/GraphBridge.EFCore/blob/main/tests/verification/VERIFICATION_SUMMARY.md)

Questions, bugs and failing query shapes are welcome in [GitHub issues](https://github.com/joslat/GraphBridge.EFCore/issues). MIT licensed.
