# 10 · Associations (many-to-many)

**Goal:** store an EF many-to-many join entity as one directed relationship with a payload.
**Track:** ★ core · **Mode:** needs Neo4j · **Time:** ~15 min · **Builds on:** 08

## Concepts

- In a relational database, many-to-many needs a join table. In a graph it is just a relationship. `ToGraphRelationship("LIKES", source FK, target FK)` maps EF's join entity `Like` to `(:Person)-[:LIKES {stars}]->(:Movie)`.
- The join entity is still a normal EF entity: query it, change its payload, delete it. EF also maintains the skip navigations `Person.LikedMovies` and `Movie.LikedBy`.
- A **pair association** allows one relationship per (person, movie) pair.

## Run it

```powershell
dotnet run --project workshop/GraphBridge.Workshop -- run 10
```

## Walkthrough

1. **The mappings.** `LIKES` uses skip navigations; `FOLLOWS` (person to person) has none:

<!-- region: Domain/MoviesContext.cs#Associations -->
```csharp
// Association: an EF many-to-many join entity stored as one directed LIKES edge with a payload.
model.Entity<Person>()
    .HasMany(p => p.LikedMovies).WithMany(m => m.LikedBy)
    .UsingEntity<Like>(
        right => right.HasOne(l => l.Movie).WithMany(m => m.Likes).HasForeignKey(l => l.MovieId),
        left => left.HasOne(l => l.Person).WithMany(p => p.Likes).HasForeignKey(l => l.PersonId),
        join =>
        {
            join.HasKey(l => new { l.PersonId, l.MovieId });
            join.Property(l => l.Stars).HasGraphPropertyName("stars");
            join.ToGraphRelationship("LIKES", [nameof(Like.PersonId)], [nameof(Like.MovieId)]);
        });
// An association between two people, with no navigations: (:Person)-[:FOLLOWS]->(:Person).
model.Entity<Follow>(follows =>
{
    follows.HasKey(f => new { f.FollowerId, f.FolloweeId });
    follows.HasOne<Person>().WithMany().HasForeignKey(f => f.FollowerId);
    follows.HasOne<Person>().WithMany().HasForeignKey(f => f.FolloweeId);
    follows.ToGraphRelationship("FOLLOWS", [nameof(Follow.FollowerId)], [nameof(Follow.FolloweeId)]);
});
```

2. **Query the association.**

<!-- region: Query -->
```csharp
var query = db.Likes.Where(l => l.Stars >= 4).OrderByDescending(l => l.Stars);
var likes = await query.ToListAsync(cancellationToken);
```

   Each returned row also carries a check that exactly one `LIKES` relationship connects the pair.

3. **Add through the skip navigation.** EF creates the `Like` join entity; the provider creates one relationship.

<!-- region: Add -->
```csharp
var hugo = await db.People.SingleAsync(p => p.Id == SeedData.Hugo, cancellationToken);
var wick = await db.Movies.SingleAsync(m => m.Id == SeedData.JohnWick, cancellationToken);
hugo.LikedMovies.Add(wick);                          // EF creates the Like join entity
await db.SaveChangesAsync(cancellationToken);        // one LIKES relationship
```

4. **Change the payload, then delete.**

<!-- region: Payload -->
```csharp
var like = await db.Likes.SingleAsync(l => l.PersonId == SeedData.Hugo && l.MovieId == SeedData.JohnWick, cancellationToken);
like.Stars = 5;                                      // updates only the relationship property
await db.SaveChangesAsync(cancellationToken);
```

   Removing the `Like` deletes only the relationship; the person and the movie remain.

## What you should see

Four strong likes; the new `LIKES` relationship appears, gets five stars and is deleted; both endpoints remain.

## Try it yourself

1. Add a second `Like` for the same person and movie in one context. What does EF say before anything reaches Neo4j?
2. Query `db.Follows`, then load the people with those keys (the pattern from lesson 08, step 4) and print who follows whom by name.

## Limits

- One relationship per endpoint pair. For parallel relationships with their own identity, use lesson 11.
- Association `Count`/`Any` and `Include` of associations are not translated.

## Read more

[Native association relationships](../../../../docs/features/ASSOCIATIONS.md)
