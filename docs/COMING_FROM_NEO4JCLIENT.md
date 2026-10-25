# Coming from Neo4jClient

[Neo4jClient](https://github.com/DotNet4Neo4j/Neo4jClient) has served .NET developers on Neo4j for more than a decade, and many applications run on it today. This page is for teams that use it and want to try GraphBridge, or to run both during a move. It maps Neo4jClient's idioms to GraphBridge's, shows how to read a graph Neo4jClient wrote, and lists what GraphBridge doesn't do yet.

Checked against Neo4jClient 5.1.20 (its source at commit [`a18f7a7`](https://github.com/DotNet4Neo4j/Neo4jClient/tree/a18f7a74d1b424251895796479c13ab94c5e7cac)), GraphBridge 0.1.0-preview.4, Neo4j 2026.09 and .NET 10, on 23 October 2026. The GraphBridge code below is compiled with the [workshop](../workshop/GraphBridge.Workshop/Guides/Neo4jClientGuide.cs), and the interoperability claims come from one live run recorded in [Neo4jClient interoperability](../tests/verification/NEO4JCLIENT_INTEROP.md).

## Two tools, two layers

Both libraries sit on the official [Neo4j .NET driver](https://github.com/neo4j/neo4j-dotnet-driver).

- **Neo4jClient** is a Cypher query builder: you write the clauses, and it serializes your objects into parameters and results back into objects with Newtonsoft.Json.
- **GraphBridge** is an Entity Framework Core provider: you map entity types to nodes and relationships, query them with LINQ, and save changes through EF's change tracker. When you want to write the pattern yourself, Fluent Cypher builds typed Cypher from the same mapping, and native Cypher runs any statement.

What changes most is where the model lives. In Neo4jClient the model is in your Cypher strings and your classes; in GraphBridge it is EF's model, so keys, property names, value conversions and the uniqueness constraints a key needs are declared once and checked.

## Idiom map

| Neo4jClient | GraphBridge |
|---|---|
| `new BoltGraphClient(...)`, `ConnectAsync()` | `UseNeo4j(driver, database)` on a `DbContext`, registered in DI |
| `Match(...).Where(...).Return(n => n.As<T>())` | A LINQ query on the `DbSet<T>`, or Fluent Cypher returning `node.Value` |
| `CollectAs<T>()` | `Graph.Collect(node, max)` or `Graph.CollectDistinct(node, max)` |
| `OptionalMatch(...)` | Fluent `OptionalMatch(...)`; LINQ `Include` for modeled relationships |
| `Create(...)` | `Add(entity)` and `SaveChangesAsync()` |
| `Merge(...).OnCreate().Set(...).OnMatch().Set(...)` | `ExecuteUpsertAsync(entities, updateProperties)` for nodes, and for relationships between existing nodes |
| `Set(...)`, `Delete(...)` | Tracked changes and `SaveChangesAsync()`, or `ExecuteUpdateAsync` and `ExecuteDeleteAsync` |
| `Unwind`, `Call ... Yield`, `LoadCsv`, `Union` | Native Cypher: `QueryCypherAsync` and `ExecuteCypherAsync` |
| `BeginTransaction()` | `Database.BeginTransactionAsync()` |
| `WithDatabase(name)` | The context's database, set in `UseNeo4j` |
| `WithIdentifier(...)` | `TagWith(...)`, written as a `//` comment the server shows in `SHOW TRANSACTIONS` |
| `MaxExecutionTime(...)` | `CommandTimeout` in `UseNeo4j`, `Database.SetGraphCommandTimeout`, or Fluent `WithTimeout` |
| `WithQueryStats`, `OperationCompleted` | `GraphWriteCounters` from `ExecuteCypherAsync`, and [command tracing](features/COMMAND_TRACING.md) |
| `Query.DebugQueryText` | `ToQueryString()`; parameter values are never inlined |

Over Bolt, Neo4jClient passes `WithIdentifier` to its [`OperationCompleted` event](https://github.com/DotNet4Neo4j/Neo4jClient/blob/a18f7a74d1b424251895796479c13ab94c5e7cac/Neo4jClient/BoltGraphClient.cs#L198-L241) rather than to the server, and `MaxExecutionTime` applies to its HTTP client, where it is sent as a [header](https://github.com/DotNet4Neo4j/Neo4jClient/blob/a18f7a74d1b424251895796479c13ab94c5e7cac/Neo4jClient/Execution/ResponseBuilder.cs#L91). GraphBridge sends tags and timeouts to the server over Bolt.

### Connecting

Register the driver once and the context per scope, as with any EF Core provider:

<!-- region: Guides/Neo4jClientGuide.cs#Register -->
```csharp
public static void Register(IServiceCollection services, Uri uri, string user, string password)
{
    services.AddSingleton(_ => GraphDatabase.Driver(uri, AuthTokens.Basic(user, password)));
    services.AddDbContext<CatalogContext>((provider, options) =>
        options.UseNeo4j(provider.GetRequiredService<IDriver>(), "neo4j"));
}
```

### Reading

A LINQ query replaces most `Match/Where/Return` chains over one node type:

<!-- region: Guides/Neo4jClientGuide.cs#Linq -->
```csharp
public static Task<List<Movie>> MoviesSince(MoviesContext db, int year)
    => db.Movies.Where(m => m.Released >= year).OrderBy(m => m.Title).ToListAsync();
```

When the question is a pattern, Fluent Cypher writes it with typed symbols and returns whole entities, as `Return(m => m.As<Movie>())` does:

<!-- region: Guides/Neo4jClientGuide.cs#Entities -->
```csharp
public static CypherResultQuery<Movie> MoviesOf(DbContext db, string actorName)
{
    var actor = Graph.Node<Person>();
    var movie = Graph.Node<Movie>();
    return db.Cypher()
        .Match(actor.Out<Credit>(movie))
        .Where(() => actor.Value.Name == actorName)
        .ReturnDistinct(() => movie.Value)
        .OrderBy(() => movie.Value.Title)
        .Limit(50);
}
```

`CollectAs<T>()` becomes a bounded collect. Every list has a maximum, so one row can't grow without limit:

<!-- region: Guides/Neo4jClientGuide.cs#Collect -->
```csharp
public static CypherResultQuery<Filmography> Filmographies(DbContext db)
{
    var actor = Graph.Node<Person>();
    var movie = Graph.Node<Movie>();
    var movies = Graph.CollectDistinct(movie, max: 100);
    return db.Cypher()
        .Match(actor)
        .OptionalMatch(actor.Out<Credit>(movie))
        .Return(() => new Filmography(actor.Value, movies.Value))
        .OrderBy(() => actor.Value.Name)
        .Limit(100);
}
```

Entities returned by Fluent Cypher are not tracked. To change one, load it with LINQ, or use the bulk operations below. See [Fluent Cypher](features/FLUENT_CYPHER.md#returning-whole-entities).

### Writing

Creating a node is adding an entity:

<!-- region: Guides/Neo4jClientGuide.cs#Create -->
```csharp
public static async Task AddPerson(MoviesContext db, string name, int born)
{
    db.People.Add(new Person { Id = Guid.NewGuid(), Name = name, Born = born });
    await db.SaveChangesAsync();
}
```

`MERGE` with `ON CREATE` and `ON MATCH` becomes an upsert. It needs a uniqueness constraint on the key and refuses the call before writing if there is none; see [upserts](features/BULK_MUTATIONS.md#upserts):

<!-- region: Guides/Neo4jClientGuide.cs#Upsert -->
```csharp
public static Task<GraphUpsertResult> SaveGenres(CatalogContext db, IEnumerable<Genre> genres)
    => db.Genres.ExecuteUpsertAsync(genres, updateProperties: g => new { g.Name });
```

`SET` and `DELETE` over many nodes become EF's bulk operations, which run on the server without loading the entities:

<!-- region: Guides/Neo4jClientGuide.cs#SetAndDelete -->
```csharp
public static async Task RenameAndPrune(MoviesContext db, Guid personId, int minimumRating)
{
    await db.People.Where(p => p.Id == personId)
        .ExecuteUpdateAsync(set => set.SetProperty(p => p.Nickname, "The One"));
    await db.Reviews.Where(r => r.Rating < minimumRating)
        .ExecuteDeleteAsync();
}
```

Anything else stays Cypher. `ExecuteCypherAsync` runs a statement for its effect and returns what it changed, as `WithQueryStats` does:

<!-- region: Guides/Neo4jClientGuide.cs#Native -->
```csharp
public static async Task<GraphWriteCounters> TagMovies(DbContext db, IReadOnlyList<string> titles, string tag)
{
    var counters = await db.Database.ExecuteCypherAsync(
        "UNWIND $titles AS title MATCH (m:Movie {title: title}) SET m.tag = $tag",
        new Dictionary<string, object?> { ["titles"] = titles, ["tag"] = tag });
    return counters; // counters.PropertiesSet, counters.NodesCreated, ...
}
```

### Transactions, tags and timeouts

<!-- region: Guides/Neo4jClientGuide.cs#Operations -->
```csharp
public static async Task<List<string>> Titles(MoviesContext db)
{
    db.Database.SetGraphCommandTimeout(TimeSpan.FromSeconds(10));
    await using var transaction = await db.Database.BeginTransactionAsync();
    var query = db.Movies.TagWith("catalog: titles").Select(m => m.Title);
    Console.WriteLine(query.ToQueryString());
    var titles = await query.ToListAsync();
    await transaction.CommitAsync();
    return titles;
}
```

`ToQueryString()` shows the Cypher with its tag comment and `$p0`-style parameters; values stay out of logs. See [transactions](features/TRANSACTIONS_AND_CONCURRENCY.md#command-timeouts) and [query inspection](features/QUERY_INSPECTION.md).

## Moving an existing graph

GraphBridge reads the graph your application already has. Three things decide how much mapping that takes.

1. **Keys.** Every GraphBridge node entity has a key stored as a property, covered by a uniqueness constraint. If your nodes already carry an identifier such as `Id`, create the constraint for it (`CREATE CONSTRAINT person_id FOR (p:Person) REQUIRE p.Id IS UNIQUE`). If they don't, add one first. GraphBridge never creates schema at runtime; see [schema and migrations](features/SCHEMA_AND_MIGRATIONS.md).
2. **Names.** Neo4jClient stores a property under its C# name by [default](https://github.com/DotNet4Neo4j/Neo4jClient/blob/a18f7a74d1b424251895796479c13ab94c5e7cac/Neo4jClient/BoltGraphClient.cs#L48), or under a `[JsonProperty]` name. GraphBridge also defaults to the C# name; map any other name with `HasGraphPropertyName`. Labels are whatever your Cypher wrote; map them with `ToGraphNode`.
3. **Stored types.** This is where the two differ. With its default settings, Neo4jClient 5.1.20 writes:

| C# type | Neo4jClient stores | GraphBridge by default | To match Neo4jClient |
|---|---|---|---|
| `Guid` | Its lowercase "D" string | The same | Nothing |
| Enum | The member name, as a string ([converters](https://github.com/DotNet4Neo4j/Neo4jClient/blob/a18f7a74d1b424251895796479c13ab94c5e7cac/Neo4jClient/BoltGraphClient.cs#L38-L46)) | An integer | `HasConversion<string>()` |
| `DateTime` | Text such as `1990-05-17T08:30:15.1234567Z` ([format](https://github.com/DotNet4Neo4j/Neo4jClient/blob/a18f7a74d1b424251895796479c13ab94c5e7cac/Neo4jClient/Neo4jDriverExtensions.cs#L18)) | Needs an explicit converter | The converter below |
| `DateTimeOffset` | Text such as `2026-10-23T14:05:06.7654321+02:00` | A native zoned datetime | The converter below |
| A null value | No property ([unless `serializeNullValues`](https://github.com/DotNet4Neo4j/Neo4jClient/blob/a18f7a74d1b424251895796479c13ab94c5e7cac/Neo4jClient/Neo4jDriverExtensions.cs#L126)) | The same: a missing property reads as null | Nothing |

To read and write such a graph unchanged, give GraphBridge the same representations:

<!-- region: Guides/Neo4jClientGuide.cs#ExistingGraph -->
```csharp
/// <summary>Reads and writes nodes as Neo4jClient 5.1.20 stores them with its default settings.</summary>
public sealed class CatalogContext(DbContextOptions<CatalogContext> options) : DbContext(options)
{
    // Neo4jClient's own DateTime and DateTimeOffset text format.
    private const string Neo4jClientDates = "yyyy'-'MM'-'dd'T'HH':'mm':'ss.FFFFFFFK";

    public DbSet<Genre> Genres => Set<Genre>();
    public DbSet<LegacyPerson> People => Set<LegacyPerson>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<Genre>().ToGraphNode("Genre").Property(g => g.Id).ValueGeneratedNever();

        var person = model.Entity<LegacyPerson>();
        person.ToGraphNode("Person");                                   // the label your Cypher used
        person.Property(p => p.Id).ValueGeneratedNever();               // Guid: the same "D" string
        person.Property(p => p.Status).HasConversion<string>();         // enums: stored by name
        person.Property(p => p.Born).HasConversion(                     // DateTime: ISO text
            v => v.ToString(Neo4jClientDates, CultureInfo.InvariantCulture),
            s => DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
        person.Property(p => p.LastSeen).HasConversion(                 // DateTimeOffset: ISO text
            v => v.ToString(Neo4jClientDates, CultureInfo.InvariantCulture),
            s => DateTimeOffset.Parse(s, CultureInfo.InvariantCulture));
    }
}
```

With `useDriverDateTypes: true`, Neo4jClient writes native temporal values instead. `DateTimeOffset` values are then zoned datetimes, which GraphBridge reads and writes by default, so leave out that converter. `DateTime` values are stored as zoned datetimes too, and GraphBridge has no mapping for a `DateTime` property stored that way: keep such properties out of the GraphBridge model, or keep Neo4jClient's default text format for them. Enums and `Guid` values are stored as above.

For a larger graph, let the tools do the first pass: [GraphBridge.Tool](tools/GRAPHBRIDGE_TOOL.md) scaffolds a model from the existing graph, as the [database-first sample](../samples/03-Movies-DatabaseFirst/README.md) shows, and [Doctor](tools/DOCTOR.md) checks the model against the database before you run it.

## Running both side by side

Two applications, or two services of one application, can use the same database at the same time. With the mapping above, GraphBridge reads what Neo4jClient writes and Neo4jClient reads what GraphBridge writes; the [interoperability check](../tests/verification/NEO4JCLIENT_INTEROP.md) compared every value both ways. The uniqueness constraint GraphBridge needs on a key is enforced by the server, so it also stops Neo4jClient from writing a second node with the same key.

They can't share one process. Neo4jClient 5.1.20 depends on the 5.x driver, [`Neo4j.Driver.Signed` 5.27.0](https://www.nuget.org/packages/Neo4jClient/5.1.20#dependencies-body-tab), and GraphBridge on Neo4j.Driver 6. Both packages contain `Neo4j.Driver.dll`, so only one of them loads. In the check, the 6.x driver loaded and Neo4jClient failed with a `TypeLoadException` as soon as it was given that driver, even though the project compiled. Move one application or service at a time.

## What has no equivalent yet

- **Upserts that create endpoints.** `ExecuteUpsertAsync` merges nodes, and relationships between nodes that already exist. A single MERGE that creates both a node and its relationship stays native Cypher; upsert the nodes first, then the relationships.
- **Fluent writes.** Fluent Cypher reads only; writes go through EF or native Cypher.
- **Tracked entities from Fluent Cypher.** Returned entities are untracked.
- **`LoadCsv`, `Union`, procedure calls and APOC.** Native Cypher.
- **The HTTP endpoint.** GraphBridge uses Bolt only.

The [roadmap](../ROADMAP.md) lists what is planned and how to ask for something.
