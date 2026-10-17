using System.Globalization;
using GraphBridge.Samples.VectorSearch;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Neo4j.Driver;

const string usage = "Usage: dotnet run --project samples/06-VectorSearch -- setup | search [sci-fi,romance,comedy,crime] | cleanup";
var names = new[] { "GRAPHBRIDGE_NEO4J_URI", "GRAPHBRIDGE_NEO4J_DATABASE", "GRAPHBRIDGE_NEO4J_USERNAME", "GRAPHBRIDGE_NEO4J_PASSWORD" };
var settings = names.Select(Environment.GetEnvironmentVariable).ToArray();
if (args.Length is 0 or > 2 || args[0] is not ("setup" or "search" or "cleanup") || args.Length == 2 && args[0] != "search")
{
    Console.Error.WriteLine(usage);
    return 2;
}
if (settings.Any(string.IsNullOrWhiteSpace))
{
    Console.Error.WriteLine("Set the four GRAPHBRIDGE_NEO4J_* variables to a disposable Neo4j database first.");
    return 2;
}
float[] taste;
try { taste = args.Length == 2 ? args[1].Split(',').Select(v => float.Parse(v, CultureInfo.InvariantCulture)).ToArray() : [1, 0, 0, 0]; }
catch (FormatException)
{
    Console.Error.WriteLine(usage);
    return 2;
}

await using var driver = GraphDatabase.Driver(settings[0], AuthTokens.Basic(settings[2], settings[3]));
var database = settings[1]!;
if (args[0] is "setup" or "cleanup")
{
    await using var session = driver.AsyncSession(options => options.WithDatabase(database));
    if (args[0] == "setup")
    {
        await (await session.RunAsync(MovieSearchSetup.CreateIndex)).ConsumeAsync();
        await (await session.RunAsync(MovieSearchSetup.AwaitIndex)).ConsumeAsync();
        await (await session.RunAsync(MovieSearchSetup.Seed, new { movies = MovieSearchSetup.Movies })).ConsumeAsync();
        Console.WriteLine($"Created the {MovieSearchSetup.Index} index and {MovieSearchSetup.Movies.Count} movies.");
    }
    else
    {
        await (await session.RunAsync(MovieSearchSetup.DeleteMovies)).ConsumeAsync();
        await (await session.RunAsync(MovieSearchSetup.DropIndex)).ConsumeAsync();
        Console.WriteLine("Removed the sample movies and the index.");
    }
    return 0;
}

// Application setup is the only place that knows GraphBridge; MovieFinder sees IVectorSearchable<MovieHit>.
await using var services = new ServiceCollection()
    .AddDbContext<MoviesContext>(options => options.UseNeo4j(driver, database))
    .AddMovieSearch()
    .BuildServiceProvider();
await using var scope = services.CreateAsyncScope();
var finder = scope.ServiceProvider.GetRequiredService<MovieFinder>();
foreach (var match in await finder.FindAsync(taste))
    Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{match.Score:0.000}  {match.Record.Title} ({match.Record.Released})"));
return 0;
