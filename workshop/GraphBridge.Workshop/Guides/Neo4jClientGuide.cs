using System.Globalization;
using GraphBridge.EntityFrameworkCore;
using GraphBridge.EntityFrameworkCore.Diagnostics;
using GraphBridge.EntityFrameworkCore.FluentCypher;
using GraphBridge.Workshop.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Neo4j.Driver;
using Graph = GraphBridge.EntityFrameworkCore.FluentCypher.Cypher;

namespace GraphBridge.Workshop.Guides;

// Compiled excerpts for docs/COMING_FROM_NEO4JCLIENT.md. WorkshopTests checks that the page shows exactly these regions.
// The claims about values written by Neo4jClient rest on tests/verification/NEO4JCLIENT_INTEROP.md.
public static class Neo4jClientGuide
{
    // workshop:start Register
    public static void Register(IServiceCollection services, Uri uri, string user, string password)
    {
        services.AddSingleton(_ => GraphDatabase.Driver(uri, AuthTokens.Basic(user, password)));
        services.AddDbContext<CatalogContext>((provider, options) =>
            options.UseNeo4j(provider.GetRequiredService<IDriver>(), "neo4j"));
    }
    // workshop:end Register

    // workshop:start Linq
    public static Task<List<Movie>> MoviesSince(MoviesContext db, int year)
        => db.Movies.Where(m => m.Released >= year).OrderBy(m => m.Title).ToListAsync();
    // workshop:end Linq

    // workshop:start Entities
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
    // workshop:end Entities

    // workshop:start Collect
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
    // workshop:end Collect

    // workshop:start Create
    public static async Task AddPerson(MoviesContext db, string name, int born)
    {
        db.People.Add(new Person { Id = Guid.NewGuid(), Name = name, Born = born });
        await db.SaveChangesAsync();
    }
    // workshop:end Create

    // workshop:start Upsert
    public static Task<GraphUpsertResult> SaveGenres(CatalogContext db, IEnumerable<Genre> genres)
        => db.Genres.ExecuteUpsertAsync(genres, updateProperties: g => new { g.Name });
    // workshop:end Upsert

    // workshop:start SetAndDelete
    public static async Task RenameAndPrune(MoviesContext db, Guid personId, int minimumRating)
    {
        await db.People.Where(p => p.Id == personId)
            .ExecuteUpdateAsync(set => set.SetProperty(p => p.Nickname, "The One"));
        await db.Reviews.Where(r => r.Rating < minimumRating)
            .ExecuteDeleteAsync();
    }
    // workshop:end SetAndDelete

    // workshop:start Native
    public static async Task<GraphWriteCounters> TagMovies(DbContext db, IReadOnlyList<string> titles, string tag)
    {
        var counters = await db.Database.ExecuteCypherAsync(
            "UNWIND $titles AS title MATCH (m:Movie {title: title}) SET m.tag = $tag",
            new Dictionary<string, object?> { ["titles"] = titles, ["tag"] = tag });
        return counters; // counters.PropertiesSet, counters.NodesCreated, ...
    }
    // workshop:end Native

    // workshop:start Operations
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
    // workshop:end Operations
}

public sealed record Filmography(Person Actor, IReadOnlyList<Movie> Movies);

/// <summary>A node type for the upsert example; the workshop's own types carry a row version or a native reference, which upserts refuse.</summary>
public sealed class Genre
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
}

// workshop:start ExistingGraph
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
// workshop:end ExistingGraph

public enum PersonStatus { Active, Retired }

public sealed class LegacyPerson
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public PersonStatus Status { get; set; }
    public DateTime Born { get; set; }
    public DateTimeOffset LastSeen { get; set; }
}
