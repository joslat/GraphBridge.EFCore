# Native references

Store an ordinary EF one-to-many relationship as a native Neo4j relationship from the parent to the child with `ToGraphReference`. Use it for ownership and membership: a review always belongs to one movie; a movie may have a director.

```csharp
// Required: every Review has exactly one Movie.
model.Entity<Review>()
    .HasOne(r => r.Movie).WithMany(m => m.Reviews).HasForeignKey(r => r.MovieId)
    .OnDelete(DeleteBehavior.Restrict)
    .ToGraphReference("HAS_REVIEW");          // (:Movie)-[:HAS_REVIEW]->(:Review)

// Optional: Movie.DirectorId is Guid?, so a Movie has zero or one director.
model.Entity<Movie>()
    .HasOne(m => m.Director).WithMany(p => p.Directed).HasForeignKey(m => m.DirectorId)
    .IsRequired(false)
    .OnDelete(DeleteBehavior.Restrict)
    .ToGraphReference("DIRECTED");            // (:Person)-[:DIRECTED]->(:Movie)
```

The graph stores `(:Movie)-[:HAS_REVIEW]->(:Review)`. There is **no foreign-key property** on the review node: `Review.MovieId` is derived from the parent's key when you read. The link carries no mapped properties and has no EF identity of its own. EF still owns the foreign-key values, navigation fixup and change tracking.

Adding a review through the navigation saves one EF entry and creates the node and its link:

```csharp
var movie = await db.Movies.Where(m => m.Id == SeedData.JohnWick).Include(m => m.Reviews).SingleAsync(cancellationToken);
movie.Reviews.Add(new Review { Id = reviewId, Rating = 5, Text = "Workshop review", CreatedAt = DateTimeOffset.UtcNow });
var saved = await db.SaveChangesAsync(cancellationToken);   // one node + one HAS_REVIEW relationship
```

## Mapping rules

The model is validated when it is built; a violation throws `InvalidOperationException` (`GraphBridge model '<Entity>': ...`).

- Both entity types are [mapped nodes](MAPPING_NODES.md) with `ToGraphNode` and stable application keys.
- The relationship is a non-owning many-to-one whose foreign key targets the parent's primary key. Alternate-key references are not supported.
- Delete behavior is `Restrict` or `NoAction`. Cascade and set-null policies are rejected.
- A required reference has all foreign-key properties non-nullable; an optional reference uses `.IsRequired(false)` and all nullable foreign-key properties.
- Foreign-key properties are not part of the child's key or a concurrency token, are not generated, and cannot have `HasGraphPropertyName` (they are never stored). Their type and converter must match the parent key.
- A child entity type has at most one native parent reference. Cycles, including self-references, are rejected.
- Each relationship type belongs to one reference mapping and cannot be reused by an [association](ASSOCIATIONS.md) or [identified relationship](IDENTIFIED_RELATIONSHIPS.md).

## Required references

- A required child cannot move to another parent. Changing `Review.MovieId` on a saved review throws `NotSupportedException` when you save; delete the review and add a new one instead.
- Deleting a parent that still has children fails; see [delete is restrictive](MAPPING_NODES.md#delete-is-restrictive).

## Optional references

An optional reference extends the same mapping to zero or one parent, with a nullable foreign key and `.IsRequired(false)`. Change the foreign key or the navigation to change the relationship; both nodes always stay.

```csharp
var wick = await db.Movies.SingleAsync(m => m.Id == SeedData.JohnWick, cancellationToken);
wick.DirectorId = SeedData.Lana;                       // creates (Lana)-[:DIRECTED]->(John Wick)
await db.SaveChangesAsync(cancellationToken);
```

Setting `DirectorId` to another person later deletes the old link and creates the new one in one save. Setting it to `null` deletes the link. Assigning the `Director` navigation works the same way.

A composite optional foreign key must be entirely null or entirely specified. Custom key equality is not supported; converter semantics must match the parent keys.

## Save and read contract

- Inserting a child with no parent creates only its node. Adding a parent and its children in the same save creates all nodes before their membership links. Returned save counts count EF entries, not links.
- An optional foreign key or navigation can attach, remove or replace the parent. Membership checks use the original foreign key, lock the child and the relevant old and new parents, and share the node's transaction and concurrency check. A stale scalar version or a changed original membership rejects and rolls back the transition.
- An unchanged membership keeps its physical link and any external link properties. A changed membership deletes the old link and creates a new one; external properties on the old link are not copied.
- Deleting a child removes its validated owned link. Other relationships still make the restrictive delete fail, and rollback restores that link. Deleting a referenced parent is restrictive; database or client cascade and automatic null-setting are not supported.
- Zero incoming links read as a null foreign key. One incoming link requires the mapped parent label and every key value. Duplicate links, wrong labels and missing or invalid parent keys throw before the returned row is shaped, including scalar projections, which carry hidden identity checks.

Checks apply to returned rows and mutations. Filtering or paging can exclude malformed data; queries are not whole-graph integrity audits. Arbitrary external Cypher does not take part in the provider's lock and version conventions. An explicit transaction rollback restores the database but does not rewind EF entries already accepted by an earlier save; use a fresh context afterward.

## Include and tracking

One direct `Include` of a native reference is supported, in either direction:

```csharp
var query = db.Movies
    .Where(m => m.Title == "The Matrix")
    .Include(m => m.Reviews);
var matrix = await query.SingleAsync(cancellationToken);
```

Lesson 08 prints the generated Cypher. Each movie first proves its own optional `DIRECTED` parent; a `CALL` subquery then collects its reviews, and each review proves its single `HAS_REVIEW` parent before EF shapes it:

```cypher
MATCH (n:`Movie`)
CALL (n) {
  OPTIONAL MATCH (parent)-[candidate:`DIRECTED`]->(n)
  WITH parent LIMIT 2
  RETURN collect(parent) AS parents
}
WITH n, size(parents) AS `__gb_node_reference_count`, CASE WHEN size(parents) = 1 THEN parents[0] ELSE null END AS parent
WITH n, `__gb_node_reference_count`, coalesce(parent:`Person`, false) AS `__gb_node_reference_principal`, CASE WHEN parent:`Person` THEN parent ELSE null END AS s
WHERE coalesce(n.`title` = $p0, (n.`title` IS NULL AND $p0 IS NULL))
WITH n, s, `__gb_node_reference_count`, `__gb_node_reference_principal`
CALL (n) {
  MATCH (n)-[:`HAS_REVIEW`]->(i)
CALL (i) {
  OPTIONAL MATCH (parent)-[candidate:`HAS_REVIEW`]->(i)
  WITH parent LIMIT 2
  RETURN collect(parent) AS parents
}
WITH i, size(parents) AS `__gb_node_reference_count`, CASE WHEN size(parents) = 1 THEN parents[0] ELSE null END AS parent
WITH i, `__gb_node_reference_count`, coalesce(parent:`Movie`, false) AS `__gb_node_reference_principal`, CASE WHEN parent:`Movie` THEN parent ELSE null END AS ip
  RETURN collect({`c0`: i.`id`, `c2`: ip.`id`, `c1`: i.`createdAt`, `c3`: i.`rating`, `c4`: i.`text`, `__gb_include_label`: coalesce(i:`Review`, false), `__gb_node_reference_count`: `__gb_node_reference_count`, `__gb_node_reference_principal`: `__gb_node_reference_principal`}) AS `__gb_include_rows`
}
RETURN n.`id` AS `c0`, s.`id` AS `c1`, n.`released` AS `c2`, n.`tagline` AS `c3`, n.`title` AS `c4`, `__gb_node_reference_count` AS `__gb_node_reference_count`, `__gb_node_reference_principal` AS `__gb_node_reference_principal`, `__gb_include_rows` AS `__gb_include_rows`
```

`s.id AS c1` is how `Movie.DirectorId` is read: from the parent's key, not from a property.

An absent reference produces an empty related list. A collection Include keeps every returned child, and root paging (`Skip`/`Take`) happens before expansion. Each query uses one command and buffers the complete related collection per root, so this bounds depth and query count, not fan-out memory.

Tracked results reuse EF instances and fix up inverse navigations. An absent reference is marked loaded only if the current tracked foreign key is still null; pending foreign-key changes to an unresolved parent stay unresolved. Existing tracked values are not overwritten. `AsNoTracking` and `AsNoTrackingWithIdentityResolution` keep their distinct identity semantics and do not populate the context tracker.

### Refused Include shapes

A refused Include throws `Neo4jQueryTranslationException` before any database work, with the code in its `Code` property, its message and `Exception.Data["GraphBridge.QueryDiagnostic"]`.

| Shape | Example | Code |
|---|---|---|
| Filtered Include | `Include(b => b.Posts.Where(...))` | GBQ140 |
| Nested Include | `.Include(b => b.Posts).ThenInclude(...)` | GBQ140 |
| More than one Include | `.Include(...).Include(...)` | GBQ140 |
| String Include | `Include("Posts")` | GBQ140 |
| Association, identified-relationship or skip navigation | `db.People.Include(p => p.Likes)`, `Include(p => p.LikedMovies)` | GBQ140 |
| Reference Include whose target has its own native parent | `db.Reviews.Include(r => r.Movie)` while `Movie` has a `DIRECTED` parent | GBQ140 |
| Target with a query filter or an `AutoInclude` navigation | | GBQ140 |

Combining Include with a non-entity `Select` or an aggregate is refused as well. Lazy and explicit loading are not supported.

The supported alternative is a second query by key. Load both sides and EF connects them:

```csharp
var reviews = await db.Reviews.Where(r => r.Rating >= 4).OrderBy(r => r.Text).ToListAsync(cancellationToken);
Guid[] movieIds = [.. reviews.Select(r => r.MovieId).Distinct()];
await db.Movies.Where(m => movieIds.Contains(m.Id)).ToListAsync(cancellationToken);   // EF fixes up review.Movie
```

## Schema witness and verification

Both endpoint labels need a reviewed uniqueness constraint on their keys. Saves and reads of children check that the constraints exist; GraphBridge never creates them. Cardinality (one parent per child) is an application rule that GraphBridge verifies on returned rows and mutations; Neo4j key uniqueness does not prove it.

The [storage manifest](MAPPING_NODES.md#the-storage-manifest) lists each reference with identity `DependentNode`. Models containing optional references emit schema manifest version 2, with `requiredIncomingLinks: 0` and `maximumIncomingLinks: 1`. Required-only models keep version 1 and its existing serialized contract. Consumers of manifest JSON must inspect its version.

## Limits

- A required child cannot be reassigned to another parent.
- `Count`, `LongCount` and `Any` on a child's root (for example `db.Reviews.CountAsync()`) are refused with GBQ170. Count the parents' related node set instead, or use a reviewed native query.
- Reference links carry no properties. For a link with data, use an [association](ASSOCIATIONS.md) or an [identified relationship](IDENTIFIED_RELATIONSHIPS.md).
- No cascade delete and no automatic set-null when a parent is deleted.
- Existing data that stores a scalar foreign-key property instead of a link is not upgraded: a required child without its link fails to read. Migrate such data explicitly.

## Try it

- Workshop lessons [08 · One-to-many references and Include](../../workshop/GraphBridge.Workshop/Lessons/08-References/README.md) and [09 · Optional references](../../workshop/GraphBridge.Workshop/Lessons/09-OptionalReferences/README.md).
- The [Blogging sample](../../samples/01-Blogging-CodeFirst/README.md): a required `HAS_POST` reference with Include in both directions.

Design: [ADR-0021](../architecture/adr/ADR-0021.md) (references and Include) and [ADR-0032](../architecture/adr/ADR-0032.md) (optional references). Evidence: [EF behavior and conformance matrix](../../tests/verification/EF_CONFORMANCE_MATRIX.md#implemented-behavior-and-limits).
