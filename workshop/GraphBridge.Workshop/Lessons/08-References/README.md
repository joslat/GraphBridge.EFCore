# 08 · One-to-many references and Include

**Goal:** store an ordinary EF one-to-many as a native relationship and load it with Include.
**Track:** ★ core · **Mode:** needs Neo4j · **Time:** ~15 min · **Builds on:** 04

## Concepts

- `ToGraphReference("HAS_REVIEW")` stores an EF one-to-many as `(:Movie)-[:HAS_REVIEW]->(:Review)`. There is **no foreign-key property** on the review node; `Review.MovieId` is derived from the relationship when reading.
- `Include` loads one navigation, in either direction, with ordinary EF fixup and identity resolution, unless the target has its own native parent (step 3).
- When a request is outside the supported subset, GraphBridge refuses it and the lesson shows the supported alternative.

## Run it

```powershell
dotnet run --project workshop/GraphBridge.Workshop -- run 08
```

## Walkthrough

1. **The mapping.**

<!-- region: Domain/MoviesContext.cs#References -->
```csharp
// References: an ordinary EF one-to-many, stored as a relationship instead of a foreign-key property.
model.Entity<Review>()
    .HasOne(r => r.Movie).WithMany(m => m.Reviews).HasForeignKey(r => r.MovieId)
    .OnDelete(DeleteBehavior.Restrict)
    .ToGraphReference("HAS_REVIEW");          // (:Movie)-[:HAS_REVIEW]->(:Review)
model.Entity<Movie>()
    .HasOne(m => m.Director).WithMany(p => p.Directed).HasForeignKey(m => m.DirectorId)
    .IsRequired(false)
    .OnDelete(DeleteBehavior.Restrict)
    .ToGraphReference("DIRECTED");            // (:Person)-[:DIRECTED]->(:Movie), optional
```

2. **A movie with its reviews.**

<!-- region: Include -->
```csharp
var query = db.Movies
    .Where(m => m.Title == "The Matrix")
    .Include(m => m.Reviews);
var matrix = await query.SingleAsync(cancellationToken);
```

   The Cypher matches the movies first, then collects each movie's reviews in a `CALL { MATCH (n)-[:HAS_REVIEW]->(i) ... }` subquery. Both the movie and each review prove their single parent link before EF shapes them.

3. **The other direction meets a documented limit.**

<!-- region: Reverse -->
```csharp
lesson.ExpectFailure<Neo4jQueryTranslationException>("Include of a target that has its own native parent is refused",
    () => db.Reviews.Include(r => r.Movie).ToQueryString());
```

   `Movie` is both a parent (of reviews) and a child (of its optional director). Including it from a review would need a second membership check, so the provider refuses rather than guess.

4. **The supported alternative: load both sides, and EF connects them.**

<!-- region: Fixup -->
```csharp
var reviews = await db.Reviews.Where(r => r.Rating >= 4).OrderBy(r => r.Text).ToListAsync(cancellationToken);
Guid[] movieIds = [.. reviews.Select(r => r.MovieId).Distinct()];
await db.Movies.Where(m => movieIds.Contains(m.Id)).ToListAsync(cancellationToken);   // EF fixes up review.Movie
```

5. **Add a review through the navigation.** One EF entry is saved; the provider creates the node *and* its `HAS_REVIEW` relationship.

<!-- region: Add -->
```csharp
var movie = await db.Movies.Where(m => m.Id == SeedData.JohnWick).Include(m => m.Reviews).SingleAsync(cancellationToken);
movie.Reviews.Add(new Review { Id = reviewId, Rating = 5, Text = "Workshop review", CreatedAt = DateTimeOffset.UtcNow });
var saved = await db.SaveChangesAsync(cancellationToken);   // one node + one HAS_REVIEW relationship
```

   The lesson then checks in the database that the relationship exists and that no review node stores a movie key. It deletes the review, and its link goes with it.

## What you should see

Eleven passing checkpoints, including the refusal and the shared tracked movie after fixup.

## Try it yourself

1. In Neo4j Browser: `MATCH (m:Movie {title: 'The Matrix'})-[:HAS_REVIEW]->(r) RETURN m, r`.
2. Try deleting The Matrix in a context. Why is it refused?

## Limits

- One direct `Include` per query: no filtered, nested or string Includes.
- A review cannot be moved to another movie (required references keep their parent).
- Deleting a movie that still has reviews is refused; there is no cascade.

## Read more

[Native references](../../../../docs/features/REFERENCES.md) · [Include](../../../../docs/features/REFERENCES.md#include-and-tracking)
