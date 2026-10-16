using GraphBridge.Workshop.Domain;
using Microsoft.EntityFrameworkCore;
using Neo4j.Driver;

namespace GraphBridge.Workshop.Lessons;

public sealed class Lesson20Search : ILesson
{
    public LessonInfo Info { get; } = new("20", 'D', "Full-text and vector search", "20-Search", LessonMode.Connected, Core: false,
        Requirement.Seed, "Search movies by text and by vector similarity through existing indexes, with scores and provenance.");

    public const string TextIndex = "workshop_movie_text";
    public const string VectorIndex = "workshop_movie_embedding";

    // Toy three-dimensional embeddings: action, philosophy, drama.
    private static readonly (Guid Movie, double[] Embedding)[] Embeddings =
    [
        (SeedData.Matrix, [0.9, 0.8, 0.3]), (SeedData.Reloaded, [0.95, 0.5, 0.2]), (SeedData.Revolutions, [0.9, 0.6, 0.3]),
        (SeedData.CloudAtlas, [0.3, 0.9, 0.9]), (SeedData.JohnWick, [1.0, 0.1, 0.2])
    ];

    public async Task RunAsync(LessonContext lesson, CancellationToken cancellationToken)
    {
        var connection = lesson.Connection!;
        var version = (await WorkshopData.ReadAsync(connection,
            "CALL dbms.components() YIELD name, versions WHERE name = 'Neo4j Kernel' RETURN versions[0] AS version",
            r => r["version"].As<string>(), cancellationToken)).Single();
        if (!Version.TryParse(version.Split('-')[0], out var parsed) || parsed < new Version(2026, 9))
        {
            lesson.Skip($"Neo4j {version} does not offer GraphBridge's search capability, which needs Neo4j 2026.09 or later with Cypher 25");
            return;
        }

        lesson.Step("Create the indexes deliberately: the provider never creates schema");
        lesson.Code("Indexes");
        // workshop:start Indexes
        await WorkshopData.ExecuteAsync(connection, """
            CREATE FULLTEXT INDEX workshop_movie_text IF NOT EXISTS FOR (n:Movie) ON EACH [n.title, n.tagline]
            OPTIONS {indexConfig: {`fulltext.eventually_consistent`: false}}
            """, cancellationToken);
        await WorkshopData.ExecuteAsync(connection, """
            CREATE VECTOR INDEX workshop_movie_embedding IF NOT EXISTS FOR (n:Movie) ON (n.embedding)
            OPTIONS {indexConfig: {`vector.dimensions`: 3, `vector.similarity_function`: 'cosine'}}
            """, cancellationToken);
        // workshop:end Indexes
        foreach (var index in new[] { TextIndex, VectorIndex })
            await WorkshopData.ExecuteAsync(connection, "CALL db.awaitIndex($index, 60)", cancellationToken, ("index", index));
        foreach (var (movie, embedding) in Embeddings)
            await WorkshopData.ExecuteAsync(connection, "MATCH (m:Movie {id: $id}) SET m.embedding = $embedding", cancellationToken,
                ("id", movie.ToString("D")), ("embedding", embedding));
        lesson.Note("The embeddings are plain list properties written with native Cypher. EF does not map them and leaves them untouched.");

        await using var db = lesson.CreateContext();
        try
        {
            lesson.Step("Full-text search");
            lesson.Code("Text");
            // workshop:start Text
            var text = await db.Database.QueryFullTextNodesAsync(TextIndex, "matrix",
                node => node["title"].As<string>(), limit: 5, cancellationToken: cancellationToken);
            // workshop:end Text
            lesson.Table(text.Results.Select(r => new { Title = r.Value, Score = Math.Round(r.Score, 3) }));
            lesson.Expect("three Matrix movies match", 3, text.Results.Count);
            lesson.Check("every match mentions Matrix", text.Results.All(r => r.Value.Contains("Matrix", StringComparison.Ordinal)));
            lesson.Expect("provenance names the index that answered", TextIndex, text.Provenance.IndexName);

            lesson.Step("Vector search: what is closest to 'thoughtful drama'?");
            lesson.Code("Vector");
            // workshop:start Vector
            var similar = await db.Database.QueryVectorNodesAsync(VectorIndex, new[] { 0.1, 1.0, 0.9 },
                node => node["title"].As<string>(), limit: 2, cancellationToken: cancellationToken);
            // workshop:end Vector
            lesson.Table(similar.Results.Select(r => new { Title = r.Value, Score = Math.Round(r.Score, 3) }));
            lesson.Expect("Cloud Atlas is the closest", "Cloud Atlas", similar.Results[0].Value);
            lesson.Expect("cosine similarity, as declared by the index", "cosine", similar.Provenance.SimilarityFunction?.ToLowerInvariant());
        }
        catch (NotSupportedException error) when (error.Message.StartsWith("Typed graph SEARCH", StringComparison.Ordinal))
        {
            lesson.Skip("this server does not offer GraphBridge's search capability (Neo4j 2026.x with Cypher 25; full-text needs 2026.09 or later): " + error.Message);
        }
        finally
        {
            // Clean up even when canceled.
            await WorkshopData.ExecuteAsync(connection, "MATCH (m:Movie) REMOVE m.embedding", CancellationToken.None);
        }
        lesson.Limit("Search reads existing ONLINE indexes only and returns untracked results. Scores are native and not comparable across index types.");
    }
}
