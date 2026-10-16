using System.Security.Cryptography;
using System.Text;
using GraphBridge.Tooling.Diagnostics;

namespace GraphBridge.Samples.FluentCypher;

/// <summary>Independent, hand-written controls for the Ada/afterYear=2000 disposable movie fixture.</summary>
/// <remarks>The application/test harness provisions this recipe and its ownership markers separately.
/// Random storage IDs and the run partition are intentionally excluded from the deterministic recipe fingerprint.</remarks>
public static class FluentMovieReferences
{
    public const string FixtureId = "movies.parallel-credits.v1";
    public const string FixtureDefinition = "movies.parallel-credits.v1|people:Ada,Bob,Cara,Dan,Eve|movies:First=2010,Second=2024|credits:Ada>First*2,Ada>Second,Bob>First,Bob>Second,Cara>First|actor:Ada|afterYear:2000|partition:RunId|unique-node-and-credit-ids";
    public static string FixtureFingerprint { get; } = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(FixtureDefinition)));

    public const string Coactors = """
        MATCH (actor:FGQPerson {RunId:$run,Name:$actor})-[:FGQ_ACTED]->(movie:FGQMovie)<-[:FGQ_ACTED]-(other:FGQPerson)
        WHERE other.Id <> actor.Id
        RETURN DISTINCT other.Name AS name ORDER BY name
        """;
    public const string Optional = """
        MATCH (person:FGQPerson {RunId:$run})
        OPTIONAL MATCH (person)-[:FGQ_ACTED]->(movie:FGQMovie) WHERE movie.Year > $year
        RETURN person.Name AS name, movie.Title AS title, movie.Year AS year ORDER BY name
        """;
    public const string Shared = """
        MATCH (actor:FGQPerson {RunId:$run,Name:$actor})-[:FGQ_ACTED]->(movie:FGQMovie)<-[:FGQ_ACTED]-(other:FGQPerson)
        WHERE other.Id <> actor.Id
        WITH other, count(DISTINCT movie) AS score, count(*) AS rows WHERE score >= 1
        RETURN other.Name AS name, score, rows ORDER BY score DESC, name
        """;

    public static IReadOnlyList<GraphNativeReference> Create(string runId)
    {
        var parameters = new Dictionary<string, object?> { ["run"] = runId, ["actor"] = "Ada", ["year"] = 2000 };
        return Array.AsReadOnly(new[]
        {
            new GraphNativeReference("movies.coactors.native.v1", FixtureId, Coactors, parameters, r => [r["name"]], [["Bob"], ["Cara"]]),
            new GraphNativeReference("movies.optional.native.v1", FixtureId, Optional, parameters, r => [r["name"], r["title"], r["year"]],
                [["Ada", "First", 2010], ["Ada", "First", 2010], ["Ada", "Second", 2024],
                 ["Bob", "First", 2010], ["Bob", "Second", 2024], ["Cara", "First", 2010],
                 ["Dan", null, null], ["Eve", null, null]]),
            new GraphNativeReference("movies.shared-movies.native.v1", FixtureId, Shared, parameters, r => [r["name"], r["score"], r["rows"]],
                [["Bob", 2L, 3L], ["Cara", 1L, 2L]])
        });
    }
}
