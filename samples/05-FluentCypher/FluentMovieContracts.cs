using GraphBridge.Tooling.Diagnostics;

namespace GraphBridge.Samples.FluentCypher;

/// <summary>Public synthetic contract metadata; runtime parameter values stay in the trusted factory closures.</summary>
public static class FluentMovieContracts
{
    public static GraphQueryCatalog Create(string runId, string actorName = "Ada", int afterYear = 2020)
    {
        const string source = "samples/05-FluentCypher/FluentMovieQueries.cs";
        const string fixture = "movies.parallel-credits.v1";
        var budget = new GraphQueryBudget(100, TimeSpan.FromSeconds(10));
        return new([
            GraphQueryContract.Fluent(new GraphQueryDefinition("movies.coactors.v1",
                "Distinct coactor names ordered by name; physical parallel credits must not duplicate names.",
                source + "#Coactors", [new("name", GraphResultKind.String)], GraphResultComparison.Ordered,
                GraphDuplicatePolicy.Eliminate, budget, ["identified-relationships", "distinct"],
                "movies.coactors.native.v1", fixture), db => FluentMovieQueries.Coactors(db, runId, actorName), value => [value]),
            GraphQueryContract.Fluent(new GraphQueryDefinition("movies.optional.v1",
                "People with optional qualifying movies; preserve nulls and parallel credit rows. Name ordering leaves ties unspecified, so compare multisets.",
                source + "#OptionalMovies", [new("name", GraphResultKind.String), new("title", GraphResultKind.String, true), new("year", GraphResultKind.Integer, true)],
                GraphResultComparison.Multiset, GraphDuplicatePolicy.Preserve, budget, ["identified-relationships", "optional-match"],
                "movies.optional.native.v1", fixture), db => FluentMovieQueries.OptionalMovies(db, runId, afterYear), value => [value.Name, value.Title, value.Year]),
            GraphQueryContract.Fluent(new GraphQueryDefinition("movies.shared-movies.v1",
                "Distinct shared movie count and physical match-row count, ordered by score descending then name.",
                source + "#SharedMovies", [new("name", GraphResultKind.String), new("score", GraphResultKind.Integer), new("rows", GraphResultKind.Integer)],
                GraphResultComparison.Ordered, GraphDuplicatePolicy.Preserve, budget, ["identified-relationships", "aggregation", "with"],
                "movies.shared-movies.native.v1", fixture), db => FluentMovieQueries.SharedMovies(db, runId, actorName), value => [value.Name, value.Score, value.Rows])
        ]);
    }
}
