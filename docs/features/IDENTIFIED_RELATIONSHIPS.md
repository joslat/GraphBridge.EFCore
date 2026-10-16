# Identified relationships

Map an entity to a Neo4j relationship that has its own application key with `ToGraphIdentifiedRelationship`. Use it when the same two nodes can be connected more than once, such as an actor with two roles in one movie, or when a relationship needs a stable ID of its own.

```csharp
public sealed class Credit
{
    public Guid Id { get; set; }
    public Guid PersonId { get; set; }
    public Guid MovieId { get; set; }
    public string Role { get; set; } = "";
    public int Billing { get; set; }
}

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

Each `Credit` is one `(:Person)-[:ACTED_IN {id, role, billing}]->(:Movie)` relationship. The key and the payload are stored on the relationship; `PersonId` and `MovieId` come from the endpoint nodes' keys. Hugo Weaving plays two roles in Cloud Atlas, so the graph holds two `ACTED_IN` relationships between the same two nodes, each with its own `id`.

```csharp
var query = db.Credits
    .Where(c => c.PersonId == SeedData.Hugo && c.MovieId == SeedData.CloudAtlas)
    .OrderBy(c => c.Billing);
var credits = await query.ToListAsync(cancellationToken);   // Bill Smoke, Nurse Noakes
```

```cypher
MATCH (s:`Person`)-[r:`ACTED_IN`]->(t:`Movie`)
CALL (r) {
  MATCH ()-[candidate:`ACTED_IN`]->()
  WHERE candidate.`id` = r.`id`
  WITH candidate LIMIT 2
  RETURN count(*) AS `__gb_identified_edge_count`
}
WITH s, r, t, `__gb_identified_edge_count`
WHERE (coalesce(s.`id` = $p0, (s.`id` IS NULL AND $p0 IS NULL)) AND coalesce(t.`id` = $p1, (t.`id` IS NULL AND $p1 IS NULL)))
RETURN r.`id` AS `c0`, t.`id` AS `c2`, s.`id` AS `c3`, r.`billing` AS `c1`, r.`role` AS `c4`, `__gb_identified_edge_count` AS `__gb_identified_edge_count`
ORDER BY r.`billing` IS NULL DESC, r.`billing` ASC
```

The `CALL` block checks that the relationship's key is unique across all `ACTED_IN` relationships, before filtering and paging. A row is shaped only when exactly one relationship has that key.

## Mapping contract

`ToGraphIdentifiedRelationship(relationshipType, keyProperty, sourceForeignKeyProperties, targetForeignKeyProperties)` takes the relationship type, the name of the key property and two arrays of foreign-key property names. Each array names one complete EF foreign key in its declared order. The relationship points from the source foreign key's node to the target foreign key's node.

The model is validated when it is built; a violation throws `InvalidOperationException` (`GraphBridge model '<Entity>': ...`).

- **Key:** exactly one primary-key property, named by `keyProperty` and separate from the endpoint foreign keys. It is required and stable (not a row version) and has type `Guid`, `string`, `int` or `long`. String keys cannot be blank. Assign it yourself, as with [node keys](MAPPING_NODES.md#application-keys). Composite relationship keys are not supported.
- **Endpoints:** two required, non-nullable, many-to-one foreign keys to the primary keys of [mapped nodes](MAPPING_NODES.md). Composite endpoint keys are supported. Endpoint foreign keys use the same type and converter instance as the node keys they reference.
- **Payload:** every other mapped property is stored on the relationship. `IsRowVersion()` on a `long` property and `IsConcurrencyToken()` work as on [nodes](MAPPING_NODES.md#row-versions-and-concurrency-tokens).
- **Navigations:** reference navigations from the relationship entity to its endpoints, with inverse collections on the nodes, are supported. Skip navigations through an identified relationship are rejected.
- **Ownership:** one EF entity type per identified relationship type. The type cannot be shared with an [association](ASSOCIATIONS.md) or a [native reference](REFERENCES.md), and `ToGraphIdentifiedRelationship` and `ToGraphRelationship` are mutually exclusive.
- An identified relationship cannot be the endpoint of another relationship. If something needs to point at it, model it as a node.

Saves and reads require existing uniqueness constraints for the relationship key and for both endpoint node keys:

```cypher
CREATE CONSTRAINT `<name>` FOR ()-[r:`ACTED_IN`]-() REQUIRE (r.`id`) IS UNIQUE
```

GraphBridge checks them and never creates them. A missing relationship constraint throws `InvalidOperationException` (`GraphBridge requires an existing relationship uniqueness constraint for :ACTED_IN (id)...`). Schema inspection proposes it; see [schema preflight](SCHEMA_AND_MIGRATIONS.md).

## Parallel relationships

Different keys can connect the same endpoint pair. Adding a third role is just another entity:

```csharp
db.Credits.Add(new Credit { Id = id, PersonId = SeedData.Hugo, MovieId = SeedData.CloudAtlas, Role = "Old Georgie", Billing = 9 });
await db.SaveChangesAsync(cancellationToken);          // a new relationship between the same two nodes
```

An add always uses `CREATE`, never `MERGE`, between existing endpoints. Endpoints added in the same save are created first. Updates write only the modified payload properties and check concurrency tokens; deletes remove only the relationship. Every operation must affect exactly one relationship, or the whole save rolls back. The save count is the number of EF entries.

## Endpoints are immutable

A relationship's endpoints are part of its identity. Changing `PersonId` or `MovieId` on a saved `Credit` throws `NotSupportedException` when you save (`Changing an identified-edge key or rebinding its endpoints is unsupported...`).

To move a credit, delete it and add a new one **with a new key**. Deleting and re-adding the same key in one save is also refused with `NotSupportedException`.

## Query

Identified-relationship roots support the same filter, ordering, paging and direct scalar projection subset as node roots, with EF identity resolution and reference and inverse fix-up. Scalar projections keep hidden key and endpoint values, so they are validated too. Validation covers returned rows only; it is not a whole-graph scan.

## Limits

- `Count`, `LongCount` and `Any` on an identified-relationship root are refused with GBQ170. `Include` of an identified relationship is refused with GBQ140.
- No endpoint rebinding, composite relationship keys or skip navigations.
- To match patterns over these relationships (co-actors, two roles in one movie), use [Fluent Cypher](FLUENT_CYPHER.md).

## Try it

- Workshop lesson [11 · Identified relationships](../../workshop/GraphBridge.Workshop/Lessons/11-IdentifiedRelationships/README.md).
- The [Organization sample](../../samples/02-Organization-OntologyFirst/README.md) generates parallel employment relationships; the [Movies sample](../../samples/03-Movies-DatabaseFirst/README.md) scaffolds identified `ACTED_IN` and `DIRECTED` relationships from an existing database.

Design: [ADR-0016](../architecture/adr/ADR-0016.md). Evidence: [EF behavior and conformance matrix](../../tests/verification/EF_CONFORMANCE_MATRIX.md#implemented-behavior-and-limits).
