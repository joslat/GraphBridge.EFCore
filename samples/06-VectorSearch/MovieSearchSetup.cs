using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.VectorData;
using Neo4j.Driver;

namespace GraphBridge.Samples.VectorSearch;

/// <summary>What the application gets back: plain values, with the title as the key for later graph queries.</summary>
public sealed record MovieHit(string Title, long Released);

/// <summary>A GraphBridge context. The search reads native nodes through the projector, so no entity is mapped here.</summary>
public sealed class MoviesContext(DbContextOptions<MoviesContext> options) : DbContext(options);

/// <summary>Application setup: the only code that knows the adapter, the index name and the stored properties.</summary>
public static class MovieSearchSetup
{
    public const string Index = "vector_sample_movie_taste";

    // The index is an explicit prerequisite; GraphBridge never creates one. Coordinates are a toy "taste" embedding:
    // science fiction, romance, comedy, crime. A real application gets them from an embedding generator.
    public const string CreateIndex = "CREATE VECTOR INDEX vector_sample_movie_taste IF NOT EXISTS FOR (m:VectorSampleMovie) ON (m.taste) "
        + "OPTIONS {indexConfig: {`vector.dimensions`: 4, `vector.similarity_function`: 'cosine'}}";
    public const string AwaitIndex = "CALL db.awaitIndex('vector_sample_movie_taste', 60)";
    public const string Seed = "UNWIND $movies AS movie MERGE (m:VectorSampleMovie {title: movie.title}) SET m.released = movie.released, m.taste = movie.taste";
    public const string DeleteMovies = "MATCH (m:VectorSampleMovie) DELETE m";
    public const string DropIndex = "DROP INDEX vector_sample_movie_taste IF EXISTS";

    public static readonly IReadOnlyList<Dictionary<string, object>> Movies =
    [
        Movie("Interstellar", 2014, [0.95, 0.2, 0, 0]),
        Movie("Arrival", 2016, [0.9, 0.3, 0, 0.1]),
        Movie("Her", 2013, [0.6, 0.8, 0.1, 0]),
        Movie("Before Sunrise", 1995, [0, 0.95, 0.1, 0]),
        Movie("Paddington 2", 2017, [0.1, 0.2, 0.95, 0]),
        Movie("Heat", 1995, [0.1, 0.1, 0, 0.95]),
    ];

    /// <summary>Registers the search per scope, like the context it borrows. Never register it as a singleton.</summary>
    public static IServiceCollection AddMovieSearch(this IServiceCollection services) => services
        .AddScoped<IVectorSearchable<MovieHit>>(provider => provider.GetRequiredService<MoviesContext>().Database
            .AsNodeVectorSearchable(Index, movie => new MovieHit(movie["title"].As<string>(), movie["released"].As<long>())))
        .AddScoped<MovieFinder>();

    private static Dictionary<string, object> Movie(string title, long released, double[] taste)
        => new() { ["title"] = title, ["released"] = released, ["taste"] = taste };
}
