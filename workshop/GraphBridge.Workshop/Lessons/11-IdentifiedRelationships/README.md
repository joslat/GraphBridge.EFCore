# 11 · Identified relationships

**Goal:** give relationships their own keys so one person can hold several roles in one movie.
**Track:** full · **Mode:** needs Neo4j · **Time:** ~10 min · **Builds on:** 10

## Concepts

- An **identified relationship** has an application key of its own, separate from its endpoints: `ToGraphIdentifiedRelationship("ACTED_IN", nameof(Credit.Id), ...)`.
- Different keys can connect the same two nodes, so **parallel relationships** are allowed. Hugo Weaving plays two roles in Cloud Atlas: two `ACTED_IN` relationships between the same two nodes.
- Payload properties (`role`, `billing`) live on the relationship and update like any EF property.

## Run it

```powershell
dotnet run --project workshop/GraphBridge.Workshop -- run 11
```

## Walkthrough

1. **The mapping.**

<!-- region: Domain/MoviesContext.cs#Credits -->
```csharp
// Identified relationship: each ACTED_IN edge has its own key, so parallel edges are allowed.
model.Entity<Credit>(credit =>
{
    credit.HasOne<Person>().WithMany().HasForeignKey(c => c.PersonId);
    credit.HasOne<Movie>().WithMany().HasForeignKey(c => c.MovieId);
    credit.Property(c => c.Id).ValueGeneratedNever().HasGraphPropertyName("id");
    credit.Property(c => c.Role).HasGraphPropertyName("role");
    credit.Property(c => c.Billing).HasGraphPropertyName("billing");
    credit.ToGraphIdentifiedRelationship("ACTED_IN", nameof(Credit.Id), [nameof(Credit.PersonId)], [nameof(Credit.MovieId)]);
});
```

2. **Two roles, two relationships.**

<!-- region: Parallel -->
```csharp
var query = db.Credits
    .Where(c => c.PersonId == SeedData.Hugo && c.MovieId == SeedData.CloudAtlas)
    .OrderBy(c => c.Billing);
var credits = await query.ToListAsync(cancellationToken);
```

   Each row's Cypher also checks that its relationship key is unique: the `CALL { MATCH ()-[candidate:ACTED_IN]->() WHERE candidate.id = r.id ... }` block.

3. **A third role** is just another entity:

<!-- region: Add -->
```csharp
db.Credits.Add(new Credit { Id = id, PersonId = SeedData.Hugo, MovieId = SeedData.CloudAtlas, Role = "Old Georgie", Billing = 9 });
await db.SaveChangesAsync(cancellationToken);          // a new relationship between the same two nodes
```

   The lesson then changes its billing and deletes it, counting the physical relationships after each step: 2 → 3 → 2.

## What you should see

Bill Smoke and Nurse Noakes with different keys, and the physical relationship count going 2 → 3 → 2.

## Try it yourself

1. In Neo4j Browser: `MATCH (p:Person {name: 'Tom Hanks'})-[r:ACTED_IN]->(m) RETURN m.title, r.role`.
2. Change a credit's `PersonId` and save. Read the refusal: endpoints cannot move.

## Limits

- Endpoints are fixed. To move a credit, delete it and create a new one with a new key.
- Composite relationship keys and identified-relationship skip navigations are not supported.

## Read more

[Identified relationships](../../../../docs/features/IDENTIFIED_RELATIONSHIPS.md)
