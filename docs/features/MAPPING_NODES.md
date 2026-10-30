# Mapping entities to nodes

Map each EF entity that represents a thing (a person, a movie, a review) to a Neo4j node with `ToGraphNode`. You assign its key, choose its stored property names, and save it with ordinary EF code and `SaveChangesAsync`.

```csharp
public sealed class Person
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public int? Born { get; set; }
    public string? Nickname { get; set; }
    public long Version { get; set; }
}

public sealed class MoviesContext(IDriver driver, string database) : DbContext
{
    public DbSet<Person> People => Set<Person>();

    protected override void OnConfiguring(DbContextOptionsBuilder options)
        => options.UseNeo4j(driver, database);

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<Person>(person =>
        {
            person.ToGraphNode("Person");
            person.Property(p => p.Id).ValueGeneratedNever().HasGraphPropertyName("id");
            person.Property(p => p.Name).HasGraphPropertyName("name");
            person.Property(p => p.Born).HasGraphPropertyName("born");
            person.Property(p => p.Nickname).HasGraphPropertyName("nickname");
            person.Property(p => p.Version).HasGraphPropertyName("version").IsRowVersion();
        });
    }
}
```

Adding `new Person { Id = id, Name = "Ada Lovelace", Born = 1815 }` and calling `SaveChangesAsync` stores one node:

```text
(:Person {id: "<id as a lowercase Guid string>", name: "Ada Lovelace", born: 1815, version: 1})
```

`Nickname` is null, so the node has no `nickname` property. Your application owns the `IDriver` and lends it to EF with `UseNeo4j`; contexts never dispose it.

## Labels and property names

- Every entity that is not mapped as a relationship needs an explicit `ToGraphNode(label)`. Each entity type has one label, and two entity types cannot share a label.
- A property is stored under its CLR name unless you call `HasGraphPropertyName(name)`. The workshop uses Neo4j's camelCase convention (`Released` is stored as `released`). Each stored name can be used once per entity.
- Labels, relationship types and property names must match `[A-Za-z_][A-Za-z0-9_]*`. Other names throw `ArgumentException` when you configure them. Generated Cypher always quotes these identifiers with backticks, and values are always parameters.
- Graph properties that your model does not map are left alone. An update writes only the modified mapped properties, so a property written by another application survives.

## Additional labels

Some tools mark nodes with labels of their own besides the type's: neo4j-graphrag writes every entity as `(:__Entity__:__KGBuilder__:Person)`. Give a type those labels with `HasAdditionalGraphLabels`:

```csharp
modelBuilder.Entity<Person>().ToGraphNode("Person").HasAdditionalGraphLabels("__Entity__", "__KGBuilder__");
```

- **Written:** `SaveChanges` creates the node with every label. `ExecuteUpsertAsync` merges on the type's own label and key, then sets the others, so an existing node with the key gains any it lacks.
- **Matched:** queries, counts, relationship endpoints, includes, `SaveChanges` updates and deletes, `ExecuteUpdate`, `ExecuteDelete` and Fluent Cypher all require every label.
- **A node without them is outside the type.** Queries of the type skip it. A tracked entity whose node lost a label fails to save with `DbUpdateConcurrencyException`. Reaching it through a native reference or an Include fails the read with `InvalidOperationException`, rather than hide a principal or a child. It still holds its key, so adding an entity with that key fails on the constraint; an upsert by key takes it back.
- **Reserved:** labels starting with `__GraphBridge` belong to GraphBridge's migration journal and are refused.
- **One call:** `HasAdditionalGraphLabels` sets the type's additional labels; calling it again replaces them, so pass them all at once.
- **Identity:** the type's own label keeps its key constraints, indexes and manifest entry. Several types can share an additional label, but an additional label can't be any type's own label.
- **Doctor** reports nodes that carry a type's label without all its additional labels ([`GBD-LABEL-001`](../tools/DOCTOR.md#additional-labels)).

See [ADR 0055](../architecture/adr/ADR-0055.md).

Relationships between nodes are configured on the relationship, not the node: see [native references](REFERENCES.md), [associations](ASSOCIATIONS.md) and [identified relationships](IDENTIFIED_RELATIONSHIPS.md).

## Application keys

Every node entity needs an EF primary key whose values you control. Single and composite keys are supported; keyless entities are rejected.

GraphBridge does not use database-generated keys. Neo4j has no identity column or sequence, and its internal element IDs are not stable identities: Neo4j can reuse them after a node is deleted. Each save, lock and concurrency check therefore matches a node by its application key.

| Key type | Configuration |
|---|---|
| `Guid` | Use EF's default client-generated Guids, or `ValueGeneratedNever()` and assign them yourself. Stored as a lowercase canonical `D` string. |
| `int`, `long` | Call `ValueGeneratedNever()` and assign the value. EF otherwise treats a single integer key as store-generated and gives it a temporary value. |
| `string`, composite keys | Assign the values. |

A temporary key fails the save with `InvalidOperationException` (`GraphBridge key 'Id' is temporary...`). Any other store-generated property fails with `NotSupportedException`; the only provider-generated value is a `long` row version.

Every node key needs a reviewed uniqueness constraint in the database before `SaveChangesAsync` writes, for example:

```cypher
CREATE CONSTRAINT `<name>` FOR (n:`Person`) REQUIRE (n.`id`) IS UNIQUE
```

GraphBridge never creates schema. Without the constraint the save throws `InvalidOperationException` (`GraphBridge requires an existing uniqueness constraint for :Person (id)...`) before any data is written. Schema inspection proposes the missing constraints; see [schema preflight](SCHEMA_AND_MIGRATIONS.md).

Key conversions must round-trip exactly: a written key must preserve the model value, and a read key must reproduce the stored representation. Lossy key converters, custom key comparers that differ from the type mapping's comparer, and noncanonical stored Guid keys are rejected before persistence or EF shaping. `DateTimeOffset` cannot be a key.

## Property types

Mapped properties use `string`, `int`, `long`, `bool`, `double`, `DateTimeOffset`, `Guid`, enums and their nullable forms. Any other type needs an explicit `HasConversion` to one of the native types; collections, owned types and complex properties are not mapped. Setting an optional property to null removes it from the node. See [supported scalar types](NULLS_TYPES_AND_TIME.md#supported-scalar-types) for storage and comparison semantics.

## Row versions and concurrency tokens

| Configuration | Behavior |
|---|---|
| `IsRowVersion()` on an unconverted, non-nullable `long` | The provider maintains the value: 1 on insert, plus 1 on every update. Updates and deletes check the loaded value. |
| `IsConcurrencyToken()` | Your application sets the value. Updates and deletes check its original value. |

A missing node or a changed token makes the save throw `DbUpdateConcurrencyException`, and nothing is overwritten. Recovery is an application decision; see [optimistic concurrency](TRANSACTIONS_AND_CONCURRENCY.md#optimistic-concurrency).

## Save changes

`SaveChangesAsync` writes every pending change (nodes and relationships) in one transaction and returns the number of EF entries saved, not the number of graph commands. Each changed entry becomes one parameterized command:

```cypher
// Added
CREATE (n:`Person`)
SET n = $properties
RETURN count(n) AS count

// Modified: $changes holds only modified properties plus the new version
MATCH (n:`Person` {`id`: $k0})
WHERE coalesce(n.`version` = $o0, (n.`version` IS NULL AND $o0 IS NULL))
SET n += $changes
RETURN count(n) AS count

// Deleted
MATCH (n:`Person` {`id`: $k0})
WHERE coalesce(n.`version` = $o0, (n.`version` IS NULL AND $o0 IS NULL))
DELETE n
RETURN count(n) AS count
```

Updates and deletes first lock the node by its key. Every command must affect exactly one element, or the whole save rolls back.

Persistence and query enumeration are **async only**. `SaveChanges()`, `ToList()` and other synchronous enumeration throw `NotSupportedException` before any database work. An unchanged context can still return `0` from `SaveChanges()` through EF's own no-op shortcut. Use `SaveChangesAsync`, `ToListAsync` and `await using`; see the [async-only decision](../architecture/adr/ADR-0037.md).

## Delete is restrictive

GraphBridge deletes a node with `DELETE`, never `DETACH DELETE`. If the node still has any relationship, Neo4j refuses, `SaveChangesAsync` throws `DbUpdateException` and the whole save rolls back. Nothing is cascaded.

To delete a node, remove its relationships in the same save or an earlier one: delete its dependents, its association entities and its identified relationships. Within one save, relationship deletions run before node deletions. A dependent's own reference link is removed with it (see [native references](REFERENCES.md)).

## Limits

- Inheritance, owned entities, complex properties, keyless entities and property-bag entities (except association joins) are rejected when the model is built, with `InvalidOperationException` (`GraphBridge model '<Entity>': ...`).
- One label identifies each entity type; [additional labels](#additional-labels) are fixed per type, so a type can't be chosen by a node's labels, and inheritance isn't supported.
- Store-generated values other than a `long` row version, and `Ignore` save behaviors, are not supported.
- `ReloadAsync` and `GetDatabaseValuesAsync` are not supported; read again in a fresh context.
- Explicit transactions, rollback and uncertain commits are covered in [transactions and concurrency](TRANSACTIONS_AND_CONCURRENCY.md).

## The storage manifest

EF derives a storage manifest from the finalized model, without a database:

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

`GraphSchemaManifest` is in `GraphBridge.EntityFrameworkCore.Schema`. For the workshop model it lists three labels (`Movie`, `Person`, `Review`) and five relationship types with their identity: `DependentNode` for references (`DIRECTED`, `HAS_REVIEW`), `EndpointPair` for associations (`FOLLOWS`, `LIKES`) and `ApplicationId` for identified relationships (`ACTED_IN`). Derived foreign keys such as `Movie.DirectorId` are not node properties.

| Member | Use |
|---|---|
| `Nodes`, `Relationships` | Labels, types, keys, stored properties, indexes and delete behavior |
| `CanonicalJson` | The storage contract as canonical JSON |
| `Fingerprint` | Lowercase SHA-256 of the canonical JSON. It ignores CLR names and declaration order, so renaming a C# class does not change it but renaming a stored property does |
| `SchemaVersion` | `1`, or `2` when the model contains optional references |
| `Caveats` | What the fingerprint cannot prove, such as the semantics of opaque value converters |

Compare the fingerprint across builds to detect storage changes. `InspectGraphSchemaAsync` compares the manifest with a live database; see [schema preflight](SCHEMA_AND_MIGRATIONS.md).

## Try it

- Workshop lessons [01 · Your first context](../../workshop/GraphBridge.Workshop/Lessons/01-FirstContext/README.md), [02 · Mapping nodes](../../workshop/GraphBridge.Workshop/Lessons/02-MappingNodes/README.md), [03 · Schema before data](../../workshop/GraphBridge.Workshop/Lessons/03-SchemaFirst/README.md), [04 · Create, read, update, delete](../../workshop/GraphBridge.Workshop/Lessons/04-Crud/README.md) and [12 · Optimistic concurrency](../../workshop/GraphBridge.Workshop/Lessons/12-Concurrency/README.md).
- The [Blogging sample](../../samples/01-Blogging-CodeFirst/README.md): code-first nodes with client-generated Guid keys and row versions.
- To generate these mappings from attributes, see the [mapping generator](../tools/MAPPING_GENERATOR.md).
- Additional labels: the [live tests](../../tests/GraphBridge.IntegrationTests/AdditionalLabelsTests.cs) write, read, upsert and skip nodes with neo4j-graphrag's labels, and GraphRAG's [entity shape](../graphrag/INTEROP.md#one-graph-two-stacks-entities) uses them.

Evidence: [EF behavior and conformance matrix](../../tests/verification/EF_CONFORMANCE_MATRIX.md#implemented-behavior-and-limits).
