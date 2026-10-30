using GraphBridge.EntityFrameworkCore.FluentCypher;
using Microsoft.EntityFrameworkCore;
using Neo4j.Driver;
using Graph = GraphBridge.EntityFrameworkCore.FluentCypher.Cypher;

namespace GraphBridge.IntegrationTests;

/// <summary>
/// ADR 0057 (GR-09) live: a Fluent query that starts from a vector or full-text search, follows the graph from the hits, and
/// refuses an unsuitable index before the query runs. The movie type has an additional label (ADR 0055).
/// </summary>
[Collection(Neo4jIntegrationCollection.Name)]
public sealed class ProviderFluentSearchTests(DisposableNeo4jFixture fixture) : IAsyncLifetime
{
    private readonly string _run = "fs_" + Guid.NewGuid().ToString("N")[..12];

    public async Task InitializeAsync()
    {
        if (Environment.GetEnvironmentVariable("GRAPHBRIDGE_INTEGRATION") != "1") return;
        await Execute("CREATE VECTOR INDEX fs_movie_embedding IF NOT EXISTS FOR (n:FSMovie) ON (n.embedding) OPTIONS {indexConfig: {`vector.dimensions`: 3, `vector.similarity_function`: 'cosine'}}");
        await Execute("CREATE FULLTEXT INDEX fs_movie_titles IF NOT EXISTS FOR (n:FSMovie) ON EACH [n.Title] OPTIONS {indexConfig: {`fulltext.eventually_consistent`: false}}");
        await Execute("CREATE VECTOR INDEX fs_person_embedding IF NOT EXISTS FOR (n:FSPerson) ON (n.embedding) OPTIONS {indexConfig: {`vector.dimensions`: 3, `vector.similarity_function`: 'cosine'}}");
        await Execute("CALL db.awaitIndexes(60)");
        await Execute("""
            CREATE (m1:FSMovie:FSSearchable {Id: $run + '-m1', Run: $run, Title: 'The Matrix', embedding: [0.9, 0.1, 0.0]}),
                   (m2:FSMovie:FSSearchable {Id: $run + '-m2', Run: $run, Title: 'Matrix Reloaded', embedding: [0.6, 0.4, 0.0]}),
                   (m3:FSMovie:FSSearchable {Id: $run + '-m3', Run: $run, Title: 'Gardens', embedding: [0.0, 0.1, 0.9]}),
                   (bare:FSMovie {Id: $run + '-bare', Run: $run, Title: 'The Matrix, unlabelled', embedding: [0.9, 0.1, 0.0]}),
                   (lone:FSMovie {Id: $run + '-lone', Run: $run, Title: 'Unlabelled only', embedding: [0.0, 0.9, 0.1]}),
                   (keanu:FSPerson {Id: $run + '-keanu', Run: $run, Name: 'Keanu', embedding: [0.9, 0.1, 0.0]}),
                   (carrie:FSPerson {Id: $run + '-carrie', Run: $run, Name: 'Carrie'}),
                   (hugo:FSPerson {Id: $run + '-hugo', Run: $run, Name: 'Hugo'}),
                   (studio:FSStudio {Id: $run + '-studio', Run: $run, Name: 'Warner'}),
                   (keanu)-[:FS_ACTED]->(m1), (carrie)-[:FS_ACTED]->(m1), (keanu)-[:FS_ACTED]->(m2),
                   (keanu)-[:FS_KNOWS]->(carrie), (carrie)-[:FS_KNOWS]->(hugo), (studio)-[:FS_MADE]->(m1)
            """, new { run = _run });
    }

    public async Task DisposeAsync()
    {
        if (Environment.GetEnvironmentVariable("GRAPHBRIDGE_INTEGRATION") != "1") return;
        await Execute("MATCH (n)-[r]-() WHERE (n:FSMovie OR n:FSPerson OR n:FSStudio) AND n.Run = $run DELETE r", new { run = _run });
        await Execute("MATCH (n) WHERE (n:FSMovie OR n:FSPerson OR n:FSStudio) AND n.Run = $run DELETE n", new { run = _run });
        foreach (var index in new[] { "fs_movie_embedding", "fs_movie_titles", "fs_person_embedding" }) await Execute($"DROP INDEX {index} IF EXISTS");
    }

    [Neo4jIntegrationFact]
    public async Task A_vector_search_ranks_the_typed_nodes_and_the_query_follows_the_graph_from_them()
    {
        await using var db = Context();
        var movie = Graph.Node<SearchMovie>(); var actor = Graph.Node<SearchPerson>(); var score = Graph.Score();
        var rows = await db.Cypher().SearchVector(movie, "fs_movie_embedding", [0.9f, 0.1f, 0f], 2, score)
            .Match(actor.Out<SearchCredit>(movie))
            .Return(() => new { movie.Value.Title, actor.Value.Name, Score = score.Value })
            .OrderByDescending(() => score.Value).ThenBy(() => actor.Value.Name).Limit(10).ToListAsync();

        // The unlabelled copy has the closest vector, but it isn't a movie of this type, so it is never a result.
        Assert.DoesNotContain(rows, r => r.Title.Contains("unlabelled", StringComparison.Ordinal));
        Assert.All(rows, r => Assert.InRange(r.Score, 0, 1));
        Assert.Equal(rows.OrderByDescending(r => r.Score).ThenBy(r => r.Name, StringComparer.Ordinal), rows);
        var top = rows.Where(r => r.Title == "The Matrix").Select(r => r.Name).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(["Carrie", "Keanu"], top);
        Assert.DoesNotContain(rows, r => r.Title == "Gardens");
    }

    [Neo4jIntegrationFact]
    public async Task A_full_text_search_returns_matching_titles_with_scores()
    {
        await using var db = Context();
        var movie = Graph.Node<SearchMovie>(); var score = Graph.Score();
        var rows = await db.Cypher().SearchFullText(movie, "fs_movie_titles", "matrix", 5, score)
            .Return(() => new { movie.Value.Title, movie.Value.Run, Score = score.Value }).Limit(10).ToListAsync();
        var mine = rows.Where(r => r.Run == _run).Select(r => r.Title).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(["Matrix Reloaded", "The Matrix"], mine);
        Assert.All(rows, r => Assert.True(r.Score > 0));
    }

    [Neo4jIntegrationFact]
    public async Task The_additional_labels_filter_the_indexs_top_hits_so_a_search_can_return_fewer_than_its_limit()
    {
        await using var db = Context();
        var movie = Graph.Node<SearchMovie>(); var score = Graph.Score();
        // The node without the additional label is by far the closest to this vector: it fills the one slot, then the
        // pattern's labels drop it (ADR 0057).
        var one = await db.Cypher().SearchVector(movie, "fs_movie_embedding", [0f, 0.9f, 0.1f], 1, score)
            .Return(() => movie.Value.Title).Limit(10).ToListAsync();
        Assert.Empty(one);
        var five = await db.Cypher().SearchVector(movie, "fs_movie_embedding", [0f, 0.9f, 0.1f], 5, score)
            .Return(() => new { movie.Value.Title, movie.Value.Run }).Limit(10).ToListAsync();
        Assert.DoesNotContain(five, r => r.Title == "Unlabelled only");
        Assert.Contains(five, r => r.Run == _run && r.Title == "Matrix Reloaded");
    }

    [Neo4jIntegrationFact]
    public async Task After_a_search_optional_matches_aggregates_and_EXISTS_run_under_Cypher_25_and_its_command_can_be_explained()
    {
        await using var db = Context();
        var movie = Graph.Node<SearchMovie>(); var actor = Graph.Node<SearchPerson>(); var score = Graph.Score();
        var names = Graph.CollectDistinct(actor.Property(a => a.Name), max: 5);
        var query = db.Cypher().SearchVector(movie, "fs_movie_embedding", [0.9f, 0.1f, 0f], 3, score)
            .OptionalMatch(actor.Out<SearchCredit>(movie))
            .Return(() => new { movie.Value.Title, movie.Value.Run, Score = score.Value, Names = names.Value })
            .OrderByDescending(() => score.Value).TagWith("fluent search, explained").Limit(10);
        var rows = (await query.ToListAsync()).Where(r => r.Run == _run).ToList();
        Assert.Equal(["Carrie", "Keanu"], rows.Single(r => r.Title == "The Matrix").Names.Order(StringComparer.Ordinal));
        Assert.Equal(rows.OrderByDescending(r => r.Score), rows);

        // EXISTS after a search, through a later MATCH (a WHERE straight after the search is refused).
        var other = Graph.Node<SearchMovie>();
        var acted = Graph.Exists(actor.Out<SearchCredit>(other), () => other.Value.Title == "Matrix Reloaded");
        var withReloaded = await db.Cypher().SearchFullText(movie, "fs_movie_titles", "matrix", 5)
            .Match(actor.Out<SearchCredit>(movie))
            .Where(() => actor.Value.Run == _run && Graph.Is(acted))
            .ReturnDistinct(() => actor.Value.Name).Limit(10).ToListAsync();
        Assert.Equal(["Keanu"], withReloaded);

        // Verify explains a tagged search command: the tags are dropped and CYPHER 25 is kept.
        var command = query.ToCommand();
        Assert.StartsWith("// fluent search, explained", command.Text, StringComparison.Ordinal);
        var plan = await db.Database.ExplainCypherAsync(command.Text, command.Parameters);
        Assert.NotNull(plan.Plan);
    }

    [Neo4jIntegrationFact]
    public async Task After_a_search_a_WITH_scope_a_bounded_path_and_whole_entities_with_a_native_reference_run_under_Cypher_25()
    {
        // GR-69: ADR 0057 listed these shapes as not yet run under CYPHER 25, which a search puts in front of the whole query.
        await using var db = Context();
        var movie = Graph.Node<SearchMovie>(); var actor = Graph.Node<SearchPerson>(); var score = Graph.Score();

        // A WITH scope that groups the hits.
        var actors = Graph.Count(actor);
        var counted = await db.Cypher().SearchVector(movie, "fs_movie_embedding", [0.9f, 0.1f, 0f], 3, score)
            .OptionalMatch(actor.Out<SearchCredit>(movie))
            .With(movie, actors)
            .Return(() => new { movie.Value.Title, movie.Value.Run, Actors = actors.Value })
            .Limit(10).ToListAsync();
        Assert.Equal(2L, counted.Single(r => r.Run == _run && r.Title == "The Matrix").Actors);
        Assert.Equal(1L, counted.Single(r => r.Run == _run && r.Title == "Matrix Reloaded").Actors);

        // A bounded path from a hit: friends of friends.
        var start = Graph.Node<SearchPerson>(); var end = Graph.Node<SearchPerson>(); var path = Graph.Path();
        var reached = await db.Cypher().SearchVector(start, "fs_person_embedding", [0.9f, 0.1f, 0f], 1)
            .Match(path, start.Out(Graph.Edge<SearchFriendship>(), end).Repeat(1, 2))
            .Return(() => end.Value.Name).Limit(10).ToListAsync();
        Assert.Equal(["Carrie", "Hugo"], reached.Order(StringComparer.Ordinal));

        // Whole entities, whose native reference is read with a COLLECT subquery.
        var movies = await db.Cypher().SearchFullText(movie, "fs_movie_titles", "matrix", 5)
            .Return(() => movie.Value).Limit(10).ToListAsync();
        Assert.Equal(_run + "-studio", movies.Single(m => m.Id == _run + "-m1").StudioId);
        Assert.Null(movies.Single(m => m.Id == _run + "-m2").StudioId);
    }

    [Neo4jIntegrationFact]
    public async Task An_unsuitable_index_is_refused_before_the_query_runs()
    {
        await using var db = Context();
        var movie = Graph.Node<SearchMovie>();
        Task Run(string index, float[] vector) => db.Cypher().SearchVector(movie, index, vector, 2).Return(() => movie.Value.Title).Limit(2).ToListAsync();
        Assert.Contains("one visible existing index", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run("fs_missing_index", [0.9f, 0.1f, 0f]))).Message);
        Assert.Contains("own label", (await Assert.ThrowsAsync<InvalidOperationException>(() => Run("fs_person_embedding", [0.9f, 0.1f, 0f]))).Message);
        Assert.Contains("dimensions", (await Assert.ThrowsAsync<ArgumentException>(() => Run("fs_movie_embedding", [0.9f, 0.1f]))).Message);
        // A full-text index named as a vector index is refused too.
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run("fs_movie_titles", [0.9f, 0.1f, 0f]));
    }

    private SearchContext Context() => new(new DbContextOptionsBuilder<SearchContext>().UseNeo4j(fixture.Driver, fixture.Database).Options);

    private async Task Execute(string text, object? parameters = null)
    {
        await using var session = fixture.OpenSession();
        await (parameters is null ? await session.RunAsync(text) : await session.RunAsync(text, parameters)).ConsumeAsync();
    }

    public sealed class SearchMovie { public string Id { get; set; } = ""; public string Run { get; set; } = ""; public string Title { get; set; } = ""; public string? StudioId { get; set; } }
    public sealed class SearchPerson { public string Id { get; set; } = ""; public string Run { get; set; } = ""; public string Name { get; set; } = ""; }
    public sealed class SearchStudio { public string Id { get; set; } = ""; public string Run { get; set; } = ""; public string Name { get; set; } = ""; }
    public sealed class SearchCredit { public string PersonId { get; set; } = ""; public string MovieId { get; set; } = ""; }
    public sealed class SearchFriendship { public string PersonId { get; set; } = ""; public string FriendId { get; set; } = ""; }

    public sealed class SearchContext(DbContextOptions<SearchContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder model)
        {
            model.Entity<SearchMovie>().ToGraphNode("FSMovie").HasAdditionalGraphLabels("FSSearchable").Property(m => m.Id).ValueGeneratedNever();
            model.Entity<SearchPerson>().ToGraphNode("FSPerson").Property(p => p.Id).ValueGeneratedNever();
            var credit = model.Entity<SearchCredit>();
            credit.HasKey(c => new { c.PersonId, c.MovieId });
            credit.HasOne<SearchPerson>().WithMany().HasForeignKey(c => c.PersonId);
            credit.HasOne<SearchMovie>().WithMany().HasForeignKey(c => c.MovieId);
            credit.ToGraphRelationship("FS_ACTED", [nameof(SearchCredit.PersonId)], [nameof(SearchCredit.MovieId)]);
            var knows = model.Entity<SearchFriendship>();
            knows.HasKey(k => new { k.PersonId, k.FriendId });
            knows.HasOne<SearchPerson>().WithMany().HasForeignKey(k => k.PersonId);
            knows.HasOne<SearchPerson>().WithMany().HasForeignKey(k => k.FriendId);
            knows.ToGraphRelationship("FS_KNOWS", [nameof(SearchFriendship.PersonId)], [nameof(SearchFriendship.FriendId)]);
            model.Entity<SearchStudio>().ToGraphNode("FSStudio").Property(s => s.Id).ValueGeneratedNever();
            model.Entity<SearchMovie>().HasOne<SearchStudio>().WithMany().HasForeignKey(m => m.StudioId)
                .OnDelete(DeleteBehavior.Restrict).ToGraphReference("FS_MADE");
        }
    }
}
