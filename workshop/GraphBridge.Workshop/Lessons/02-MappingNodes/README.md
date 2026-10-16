# 02 · Mapping nodes

**Goal:** map entities to labels, keys and property names, and read the storage manifest EF derives from them.
**Track:** ★ core · **Mode:** offline · **Time:** ~10 min · **Builds on:** 01

## Concepts

- An entity becomes a **node** with `ToGraphNode("Label")`. Its EF primary key is an **application key** you assign, not Neo4j's internal element ID.
- `HasGraphPropertyName` maps C# names to the graph's names. The workshop uses Neo4j's camelCase convention: `Released` is stored as `released`.
- The **storage manifest** is computed from the finalized EF model without a database. Its fingerprint identifies the storage shape.

## Run it

```powershell
dotnet run --project workshop/GraphBridge.Workshop -- run 02
```

## Walkthrough

1. **The node mappings.**

<!-- region: Domain/MoviesContext.cs#Nodes -->
```csharp
// Nodes: a label per entity, an application key, and camelCase property names (Neo4j's convention).
model.Entity<Person>(person =>
{
    person.ToGraphNode("Person");
    person.Property(p => p.Id).ValueGeneratedNever().HasGraphPropertyName("id");
    person.Property(p => p.Name).HasGraphPropertyName("name");
    person.Property(p => p.Born).HasGraphPropertyName("born");
    person.Property(p => p.Nickname).HasGraphPropertyName("nickname");
    person.Property(p => p.Version).HasGraphPropertyName("version").IsRowVersion();
});
model.Entity<Movie>(movie =>
{
    movie.ToGraphNode("Movie");
    movie.Property(m => m.Id).ValueGeneratedNever().HasGraphPropertyName("id");
    movie.Property(m => m.Title).HasGraphPropertyName("title");
    movie.Property(m => m.Released).HasGraphPropertyName("released");
    movie.Property(m => m.Tagline).HasGraphPropertyName("tagline");
});
model.Entity<Review>(review =>
{
    review.ToGraphNode("Review");
    review.Property(r => r.Id).ValueGeneratedNever().HasGraphPropertyName("id");
    review.Property(r => r.Rating).HasGraphPropertyName("rating");
    review.Property(r => r.Text).HasGraphPropertyName("text");
    review.Property(r => r.CreatedAt).HasGraphPropertyName("createdAt");
});
```

   `ValueGeneratedNever()` tells EF that you assign the keys. `IsRowVersion()` on `Person.Version` lets the provider maintain an optimistic-concurrency version (lesson 12).

2. **Read the manifest.**

<!-- region: Manifest -->
```csharp
GraphSchemaManifest manifest = db.Database.GetGraphSchemaManifest();
var nodes = manifest.Nodes.Select(n => new
{
    n.Label,
    Key = string.Join(", ", n.KeyProperties),
    Properties = string.Join(", ", n.Properties.Select(p => p.Name))
});
var relationships = manifest.Relationships.Select(r => new
{
    Type = r.RelationshipType,
    r.Identity,
    Pattern = $"({r.Source.NodeLabel})-[:{r.RelationshipType}]->({r.Target.NodeLabel})"
});
```

   It lists three labels and five relationship types. Look at the Movie node: there is no `directorId` property. Lesson 08 shows why: references are stored as relationships.

3. **The fingerprint** is a SHA-256 over canonical JSON. It ignores CLR names and declaration order, so renaming a C# class does not change it but renaming a stored property does.

## What you should see

Two tables (nodes, relationships), five passing checkpoints and a 64-character fingerprint.

## Try it yourself

1. In `MoviesContext`, change `HasGraphPropertyName("tagline")` to `"tag_line"` and run lesson 02 only. Does the fingerprint change? Why do the checkpoints still pass? Change it back before continuing: the seeded database stores `tagline`.
2. Find the relationship whose identity is `ApplicationId`. Why does it need its own key? (Lesson 11.)

## Limits

- Owned, complex and inherited entity types are not mapped. Keys must be stable application values, and their uniqueness must be enforced by constraints (lesson 03).

## Read more

[Mapping diagnostics](../../../../docs/tools/MAPPING_GENERATOR.md) · [Schema preflight](../../../../docs/features/SCHEMA_AND_MIGRATIONS.md)
