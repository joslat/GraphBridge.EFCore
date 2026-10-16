# 09 · Optional references

**Goal:** attach, replace and remove an optional parent by changing an ordinary EF foreign key.
**Track:** full · **Mode:** needs Neo4j · **Time:** ~10 min · **Builds on:** 08

## Concepts

- An optional reference uses a nullable foreign key (`Guid? DirectorId`) and `.IsRequired(false)`.
- Changing the foreign key changes the **relationship**: set it to create a link, change it to move the link, set null to delete it. Both nodes always stay.
- Every change is checked against the original membership in the same transaction as the save, so a concurrent change to the same link fails instead of being overwritten.

## Run it

```powershell
dotnet run --project workshop/GraphBridge.Workshop -- run 09
```

The lesson changes John Wick's director and finishes with no director, as seeded.

## Walkthrough

1. **Movies and their directors**, with a reference Include:

<!-- region: Directors -->
```csharp
var movies = await db.Movies.Include(m => m.Director).OrderBy(m => m.Released).ThenBy(m => m.Title).ToListAsync(cancellationToken);
```

2. **Attach.**

<!-- region: Attach -->
```csharp
var wick = await db.Movies.SingleAsync(m => m.Id == SeedData.JohnWick, cancellationToken);
wick.DirectorId = SeedData.Lana;                       // creates (Lana)-[:DIRECTED]->(John Wick)
await db.SaveChangesAsync(cancellationToken);
```

3. **Replace.** Setting `DirectorId = SeedData.Lilly` deletes Lana's link and creates Lilly's, in one save.

4. **Remove.**

<!-- region: Remove -->
```csharp
var wick = await db.Movies.SingleAsync(m => m.Id == SeedData.JohnWick, cancellationToken);
wick.DirectorId = null;                                // deletes the link; both nodes stay
await db.SaveChangesAsync(cancellationToken);
```

After each step, the lesson counts `DIRECTED` relationships in the database with native Cypher.

## What you should see

The director table (John Wick without one), then link counts 1 → 0 and 1 → 0 as the reference moves and is removed.

## Try it yourself

1. Set `wick.Director = await db.People.SingleAsync(p => p.Name == "Hugo Weaving")` instead of setting the key. Same result?
2. Delete Lana while she still directs The Matrix. Read the refusal.

## Limits

- No cascade delete or automatic set-null when a parent is deleted.
- Native reference links carry no properties. Use an identified relationship (lesson 11) for a link with data.

## Read more

[Optional references](../../../../docs/features/REFERENCES.md#optional-references)
