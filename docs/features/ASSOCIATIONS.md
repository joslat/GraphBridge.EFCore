# Associations

Store an EF many-to-many join entity as one directed Neo4j relationship with `ToGraphRelationship`. Use it for links between things, optionally with properties on the link: a person likes a movie with a number of stars, a person follows another person.

```csharp
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

Each `Like` entity is exactly one `(:Person)-[:LIKES {stars: 4}]->(:Movie)` relationship. There is no join node, and the endpoint keys are not copied onto the relationship: `Like.PersonId` and `Like.MovieId` come from the endpoint nodes' keys. The join is still a normal EF entity that you can query, change and delete, and EF maintains the skip navigations `Person.LikedMovies` and `Movie.LikedBy`.

Query the association through its `DbSet`:

```csharp
var query = db.Likes.Where(l => l.Stars >= 4).OrderByDescending(l => l.Stars);
var likes = await query.ToListAsync(cancellationToken);
```

```cypher
MATCH (s:`Person`)-[r:`LIKES`]->(t:`Movie`)
CALL (s, t) {
  MATCH (s)-[candidate:`LIKES`]->(t)
  WITH candidate LIMIT 2
  RETURN count(*) AS `__gb_association_count`
}
WITH s, r, t, `__gb_association_count`
WHERE coalesce(r.`stars` >= $p0, false)
RETURN s.`id` AS `c0`, t.`id` AS `c1`, r.`stars` AS `c2`, `__gb_association_count` AS `__gb_association_count`
ORDER BY r.`stars` IS NULL ASC, r.`stars` DESC
```

The `CALL` block counts the `LIKES` relationships between each returned pair, independently of the filter. A row is shaped only when exactly one exists.

## Mapping contract

`ToGraphRelationship(relationshipType, sourceForeignKeyProperties, targetForeignKeyProperties)` takes the relationship type and two arrays of foreign-key property names. Each array names one complete EF foreign key in its declared order (composite foreign keys are supported). The relationship always points from the source foreign key's node to the target foreign key's node; nothing is inferred from names.

The model is validated when it is built; a violation throws `InvalidOperationException` (`GraphBridge model '<Entity>': ...`).

- The join entity has exactly two foreign keys. Both are required, non-nullable, non-generated, many-to-one, and target the primary key of a [mapped node](MAPPING_NODES.md).
- The join's primary key is exactly the union of the two foreign keys. This is what makes it a pair association.
- Every other mapped property is a payload property stored on the relationship. `HasGraphPropertyName` applies as usual.
- Endpoint foreign keys use the same type and the same converter instance as the node keys they reference.
- Supported navigations are references from the join to its endpoints (`Like.Person`) with their inverse collections (`Person.Likes`), and skip navigations (`Person.LikedMovies`, `Movie.LikedBy`) that use the two mapped roles. Associations without navigations, like `Follow`, and self-associations (person to person) are supported.
- EF shared-type dictionary joins (`UsingEntity<Dictionary<string, object>>("Favorite", ...)`) work when mapped the same way; query them with `db.Set<Dictionary<string, object>>("Favorite")`.
- Two association entities cannot map the same source label, relationship type and target label. A relationship type cannot be shared with a [native reference](REFERENCES.md) or an [identified relationship](IDENTIFIED_RELATIONSHIPS.md).
- Not supported: alternate keys on the join, a join that is itself the endpoint of another relationship, `ToGraphNode` or `ToGraphReference` on the same entity. `ToGraphRelationship` and `ToGraphIdentifiedRelationship` are mutually exclusive.

Both endpoint labels need a reviewed uniqueness constraint on their keys. Saves and association reads check that the constraints exist in the selected database; GraphBridge never creates them. See [schema preflight](SCHEMA_AND_MIGRATIONS.md).

## One relationship per pair

A pair association allows one relationship of its type per (source, target) pair. The pair is the identity:

- EF itself refuses to track two `Like` entities with the same key, so a second like for the same person and movie fails before anything reaches Neo4j.
- If the database holds two physical `LIKES` relationships for one pair (written outside GraphBridge), reads of that pair throw before EF shapes a row, and updates and deletes of it fail. GraphBridge never picks one of them.

For several relationships between the same two nodes (two roles in one movie), use an [identified relationship](IDENTIFIED_RELATIONSHIPS.md).

## Save changes

```csharp
var hugo = await db.People.SingleAsync(p => p.Id == SeedData.Hugo, cancellationToken);
var wick = await db.Movies.SingleAsync(m => m.Id == SeedData.JohnWick, cancellationToken);
hugo.LikedMovies.Add(wick);                          // EF creates the Like join entity
await db.SaveChangesAsync(cancellationToken);        // one LIKES relationship
```

- **Add** creates the relationship between existing endpoints with `MERGE` and sets its payload on creation. Endpoints added in the same save are created first. If the relationship already exists, the add succeeds only when its stored payload matches; otherwise the save throws `DbUpdateConcurrencyException` and nothing is overwritten. A missing endpoint also throws `DbUpdateConcurrencyException`.
- **Update** writes only the modified payload properties. `IsRowVersion()` on a `long` payload property and `IsConcurrencyToken()` give optimistic concurrency, as on [nodes](MAPPING_NODES.md#row-versions-and-concurrency-tokens).
- **Delete** removes only the relationship; both nodes stay. In one save, relationship deletions run before node deletions, so you can delete a node together with its associations.
- **Move** an association by removing the old join and adding a new one; you cannot change its endpoint keys. Removing and re-adding the same pair in one save is refused; save the removal first.

Every operation locks its endpoints and must affect exactly one relationship, or the whole save rolls back. The save count is the number of EF entries. A writer outside GraphBridge that locks in a different order can make Neo4j abort a save with a deadlock error; GraphBridge does not retry automatically.

## Query

Association roots support the same filter, ordering, paging and direct scalar projection subset as node roots. EF performs tracking and fixes up reference, inverse and skip navigations as endpoints and joins are tracked. Querying an association does not mark a whole navigation as loaded.

Each returned row is validated (one physical relationship, well-formed endpoint keys) before it is shaped, including scalar projections. This covers returned rows only; it is not a whole-graph scan, and rows shaped before a failing row may already be tracked.

## Limits

- `Count`, `LongCount` and `Any` on an association root are refused with GBQ170. `Include` of an association or skip navigation is refused with GBQ140. Navigation predicates such as `p.LikedMovies.Any()` are not translated.
- One relationship per endpoint pair; endpoints cannot be changed in place.
- No automatic retry after a deadlock abort or an uncertain commit; see [transactions and concurrency](TRANSACTIONS_AND_CONCURRENCY.md).
- To match relationship patterns (co-actors, friends of friends), use [Fluent Cypher](FLUENT_CYPHER.md).

## Try it

- Workshop lesson [10 · Associations (many-to-many)](../../workshop/GraphBridge.Workshop/Lessons/10-Associations/README.md).
- The [Fluent Cypher sample](../../samples/05-FluentCypher/README.md) maps a `Friendship` association and matches it in `EXISTS` patterns and bounded paths.

Design: [ADR-0015](../architecture/adr/ADR-0015.md). Evidence: [EF behavior and conformance matrix](../../tests/verification/EF_CONFORMANCE_MATRIX.md#implemented-behavior-and-limits).
