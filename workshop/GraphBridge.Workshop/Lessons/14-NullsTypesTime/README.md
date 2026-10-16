# 14 · Nulls, types and time

**Goal:** see where C# and Cypher disagree about null, how Guids are stored, and how timestamps keep their offsets.
**Track:** full · **Mode:** offline + Neo4j · **Time:** ~15 min · **Builds on:** 05 (it previews Fluent Cypher, introduced in lesson 16)

## Concepts

- **LINQ** reproduces C# semantics: `x != "Larry"` is true when `x` is null. **Fluent Cypher** keeps native semantics: `null <> 'Larry'` is unknown, so the row is dropped.
- `DateTimeOffset` is stored as a native zoned datetime, so offsets survive. LINQ compares timestamps as UTC instants.
- Guids are stored as lowercase canonical strings; enums as integers.

## Run it

```powershell
dotnet run --project workshop/GraphBridge.Workshop -- run 14
```

## Walkthrough

1. **One question, two semantics.**

<!-- region: Questions -->
```csharp
// LINQ keeps C# semantics: a missing nickname is "not Larry".
var linq = db.People.Where(p => p.Nickname != "Larry").OrderBy(p => p.Name).Select(p => p.Name);
// Fluent with an explicit predicate keeps native semantics: null <> 'Larry' is unknown, so the row drops.
var person = Graph.Node<Person>();
var fluent = db.Cypher().Match(person).Where(person.Property(p => p.Nickname).Ne("Larry"))
    .Return(() => person.Value.Name).OrderBy(() => person.Value.Name).Limit(100);
```

   LINQ:

<!-- snapshot: 14-linq-not-larry -->
```cypher
MATCH (n:`Person`)
WHERE (NOT coalesce(n.`nickname` = $p0, (n.`nickname` IS NULL AND $p0 IS NULL)))
RETURN n.`name` AS `c2`
ORDER BY n.`name` IS NULL DESC, n.`name` ASC
```

   Fluent:

<!-- snapshot: 14-fluent-not-larry -->
```cypher
MATCH (n0:`Person`)
WHERE n0.`nickname` <> $p0
RETURN n0.`name` AS c0
ORDER BY c0 ASC
LIMIT $p1
```

   LINQ returns eight people (everyone except Laurence, including those without a nickname). Fluent returns only Emil Eifrem. The lambda form `person.Value.Nickname != "Larry"` is refused with `GBFC110` because it would hide that choice.

2. **Time.**

<!-- region: Time -->
```csharp
var since = new DateTimeOffset(2023, 7, 1, 18, 0, 0, TimeSpan.Zero);
var recentReviews = db.Reviews.Where(r => r.CreatedAt >= since).OrderBy(r => r.CreatedAt).Select(r => r.CreatedAt);
```

   LINQ normalizes both sides to UTC before comparing (`datetime({datetime: ..., timezone: '+00:00'})`), so the 20:30 +02:00 review counts as 18:30 UTC. The value read back still carries its `+02:00` offset.

3. **Guids.** A native read of `p.id` returns the canonical string `00000000-0000-0000-0001-000000000001` for Keanu Reeves.

## What you should see

Eight names from LINQ, one from Fluent, a refusal, two timestamps with their original offsets, and the canonical Guid string.

## Try it yourself

1. Write the C# meaning in Fluent explicitly: `person.Property(p => p.Nickname).IsNull().Or(person.Property(p => p.Nickname).Ne("Larry"))`. Do you get eight names?
2. Compare `CreatedAt` in Fluent with `Gt(since)`. Is it an instant comparison?

## Limits

- Ordering and range comparisons over properties with value converters are not translated; equality is.
- Date parts and arithmetic on temporal values are not translated.

## Read more

[Null and type semantics](../../../../docs/features/NULLS_TYPES_AND_TIME.md) · [Fluent Cypher predicates](../../../../docs/features/FLUENT_CYPHER.md#predicates-as-c-lambdas)
