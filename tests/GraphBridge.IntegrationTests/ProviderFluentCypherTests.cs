using GraphBridge.EntityFrameworkCore.FluentCypher;
using GraphBridge.EntityFrameworkCore.Paths;
using GraphBridge.Samples.FluentCypher;
using Microsoft.EntityFrameworkCore;
using Neo4j.Driver;
using Graph = GraphBridge.EntityFrameworkCore.FluentCypher.Cypher;

namespace GraphBridge.IntegrationTests;

[Collection(Neo4jIntegrationCollection.Name)]
public sealed class ProviderFluentCypherTests(DisposableNeo4jFixture fixture)
{

    [Neo4jIntegrationFact]
    public async Task C1_coactors_distinct_rows_and_relationship_reuse_match_native_clauses()
    {
        await using var db = await Seed();
        var actor = Graph.Node<Person>(); var coactor = Graph.Node<Person>(); var movie = Graph.Node<Movie>();
        var query = FluentMovieQueries.Coactors(db, db.RunId, "Ada");
        var oracle = await Native(db, "MATCH (a:FGQPerson {RunId:$run, Name:'Ada'})-[:FGQ_ACTED]->(m:FGQMovie)<-[:FGQ_ACTED]-(c:FGQPerson) WHERE a.Id <> c.Id RETURN DISTINCT c.Name AS value ORDER BY value LIMIT 100", r => r["value"].As<string>());
        Assert.Equal(new[] { "Bob", "Cara" }, oracle);
        Assert.Equal(oracle, await query.ToListAsync());

        // A single MATCH cannot reuse a physical relationship; separate MATCH clauses can.
        var single = db.Cypher().Match(actor.Out(Graph.Edge<Credit>(), movie).In(Graph.Edge<Credit>(), actor))
            .Where(actor.Property(p => p.RunId).Eq(db.RunId).And(actor.Property(p => p.Name).Eq("Cara")))
            .Return(() => actor.Value.Name).Limit(100);
        Assert.Empty(await single.ToListAsync());
        var separate = db.Cypher().Match(actor.Out(Graph.Edge<Credit>(), movie))
            .Where(actor.Property(p => p.RunId).Eq(db.RunId).And(actor.Property(p => p.Name).Eq("Cara")))
            .Match(movie.In(Graph.Edge<Credit>(), actor)).Return(() => actor.Value.Name).Limit(100);
        Assert.Equal(new[] { "Cara" }, await separate.ToListAsync());
        Assert.Empty(db.ChangeTracker.Entries());
    }

    [Neo4jIntegrationFact]
    public async Task C2_edge_identity_payload_parameters_and_parallel_edges_match_native()
    {
        await using var db = await Seed();
        var actor = Graph.Node<Person>(); var movie = Graph.Node<Movie>(); var credit = Graph.Edge<Credit>();
        var query = db.Cypher().Match(actor.Out(credit, movie))
            .Where(actor.Property(p => p.RunId).Eq(db.RunId).And(actor.Property(p => p.Id).Eq(db.Ada))
                .And(credit.Property(p => p.Role).Eq("lead")).And(credit.Property(p => p.Billing).Ge(1)))
            .Return(() => new CreditRow(credit.Value.Id, credit.Value.PersonId, credit.Value.Role, credit.Value.Billing))
            .OrderBy(credit.Property(p => p.Billing)).Limit(100);
        var oracle = await Native(db, "MATCH (a:FGQPerson {RunId:$run, Id:$ada})-[e:FGQ_ACTED]->(:FGQMovie) WHERE e.Role=$role AND e.Billing>=1 RETURN e.Id AS id, a.Id AS person, e.Role AS role, e.Billing AS billing ORDER BY billing LIMIT 100",
            r => new CreditRow(Guid.Parse(r["id"].As<string>()), Guid.Parse(r["person"].As<string>()), r["role"].As<string>(), checked((int)r["billing"].As<long>())), ("ada", db.Ada.ToString("D")), ("role", "lead"));
        Assert.Equal(2, oracle.Count);
        Assert.Equal(oracle, await query.ToListAsync());
        Assert.DoesNotContain(db.Ada.ToString("D"), query.ToQueryString());
    }

    [Neo4jIntegrationFact]
    public async Task C3_optional_pattern_filter_and_post_WITH_filter_match_different_native_controls()
    {
        await using var db = await Seed();
        var first = await FluentMovieQueries.OptionalMovies(db, db.RunId, 2020).ToListAsync();
        var second = await FluentMovieQueries.FilteredOptionalMovies(db, db.RunId, 2020).ToListAsync();
        const string projection = " RETURN a.Name AS name, m.Title AS title, m.Year AS year ORDER BY name LIMIT 100";
        var oracle1 = await Native(db, "MATCH (a:FGQPerson {RunId:$run}) OPTIONAL MATCH (a)-[:FGQ_ACTED]->(m:FGQMovie) WHERE m.Year>2020" + projection, Optional);
        var oracle2 = await Native(db, "MATCH (a:FGQPerson {RunId:$run}) OPTIONAL MATCH (a)-[:FGQ_ACTED]->(m:FGQMovie) WITH a,m WHERE m.Year>2020" + projection, Optional);
        Assert.Equal(oracle1, first); Assert.Equal(oracle2, second);
        Assert.Equal(5, first.Count); Assert.Equal(2, second.Count);
        Assert.Contains(first, r => r.Name == "Dan" && r.Title is null && r.Year is null);
        Assert.DoesNotContain(second, r => r.Title is null);
    }

    [Neo4jIntegrationFact]
    public async Task C4_count_rows_and_distinct_movies_preserve_recommendation_scores()
    {
        await using var db = await Seed();
        var query = FluentMovieQueries.SharedMovies(db, db.RunId, "Ada");
        var oracle = await Native(db, "MATCH (a:FGQPerson {RunId:$run, Name:'Ada'})-[:FGQ_ACTED]->(m:FGQMovie)<-[:FGQ_ACTED]-(c:FGQPerson) WHERE a.Id<>c.Id WITH c,count(DISTINCT m) AS score,count(*) AS rows WHERE score>=1 RETURN c.Name AS name,score,rows ORDER BY score DESC,name LIMIT 100",
            r => new ScoreRow(r["name"].As<string>(), r["score"].As<long>(), r["rows"].As<long>()));
        Assert.Equal(new[] { new ScoreRow("Bob", 2, 3), new ScoreRow("Cara", 1, 2) }, oracle);
        Assert.Equal(oracle, await query.ToListAsync());
    }

    [Neo4jIntegrationFact]
    public async Task C7_list_membership_and_ordinal_prefix_match_native_IN_and_STARTS_WITH()
    {
        await using var db = await Seed();
        var cases = new (string[] Names, string Prefix, string[] Expected)[]
        {
            (new[] { "Ada", "Cara", "Dan" }, "Fir", new[] { "Ada", "Cara" }),
            (new[] { "Ada", "Cara", "Dan" }, "Sec", new[] { "Ada" }),
            (new[] { "Bob", "Eve" }, "S", new[] { "Bob" }),
            (Array.Empty<string>(), "F", Array.Empty<string>()),
            (new[] { "Ada" }, "fir", Array.Empty<string>()) // STARTS WITH is case-sensitive, like StringComparison.Ordinal
        };
        foreach (var (names, prefix, expected) in cases)
        {
            var oracle = await Native(db, "MATCH (a:FGQPerson {RunId:$run})-[:FGQ_ACTED]->(m:FGQMovie) WHERE a.Name IN $names AND m.Title STARTS WITH $prefix RETURN DISTINCT a.Name AS name ORDER BY name LIMIT 100",
                r => r["name"].As<string>(), ("names", names), ("prefix", prefix));
            Assert.Equal(expected, oracle);
            Assert.Equal(oracle, await FluentMovieQueries.CastSearch(db, db.RunId, names, prefix).ToListAsync());
        }
    }

    [Neo4jIntegrationFact]
    public async Task C8_returned_aggregates_group_by_name_and_keep_people_without_credits()
    {
        await using var db = await Seed();
        var oracle = await Native(db, "MATCH (a:FGQPerson {RunId:$run}) OPTIONAL MATCH (a)-[:FGQ_ACTED]->(m:FGQMovie) RETURN a.Name AS name, min(m.Year) AS first, max(m.Year) AS last, collect(DISTINCT m.Title) AS titles ORDER BY name LIMIT 100",
            r => new CareerRow(r["name"].As<string>(), r["first"] is null ? null : checked((int)r["first"].As<long>()),
                r["last"] is null ? null : checked((int)r["last"].As<long>()), r["titles"].As<List<string>>()));
        var fluent = await FluentMovieQueries.Careers(db, db.RunId).ToListAsync();
        // collect order is not guaranteed, so titles are compared sorted.
        static string Describe(CareerRow row) => $"{row.Name}|{row.FirstYear}|{row.LastYear}|{string.Join(",", row.Titles.Order(StringComparer.Ordinal))}";
        Assert.Equal(new[] { "Ada|2010|2024|First,Second", "Bob|2010|2024|First,Second", "Cara|2010|2010|First", "Dan|||", "Eve|||" }, oracle.Select(Describe));
        Assert.Equal(oracle.Select(Describe), fluent.Select(Describe));
    }

    [Neo4jIntegrationFact]
    public async Task C5_friends_of_friends_excludes_self_and_direct_friends_on_server()
    {
        await using var db = await Seed();
        var actor = Graph.Node<Person>(); var friend = Graph.Node<Person>(); var candidate = Graph.Node<Person>();
        var query = db.Cypher().Match(actor.Out(Graph.Edge<Friendship>(), friend).Out(Graph.Edge<Friendship>(), candidate))
            .Where(actor.Property(p => p.RunId).Eq(db.RunId).And(actor.Property(p => p.Name).Eq("Ada"))
                .And(actor.Property(p => p.Id).Ne(candidate.Property(p => p.Id)))
                .And(Graph.Exists(actor.Out(Graph.Edge<Friendship>(), candidate)).Not()))
            .ReturnDistinct(() => candidate.Value.Name).OrderBy(candidate.Property(p => p.Name)).Limit(100);
        var oracle = await Native(db, "MATCH (a:FGQPerson {RunId:$run, Name:'Ada'})-[:FGQ_FRIEND]->(:FGQPerson)-[:FGQ_FRIEND]->(c:FGQPerson) WHERE a.Id<>c.Id AND NOT EXISTS { MATCH (a)-[:FGQ_FRIEND]->(c) } RETURN DISTINCT c.Name AS name ORDER BY name LIMIT 100", r => r["name"].As<string>());
        Assert.Equal(new[] { "Dan" }, oracle);
        Assert.Equal(oracle, await query.ToListAsync());
    }

    [Neo4jIntegrationFact]
    public async Task Native_null_equality_negation_and_ordering_match_server_semantics()
    {
        await using var db = await Seed(); var actor = Graph.Node<Person>();
        var root = db.Cypher().Match(actor).Where(actor.Property(p => p.RunId).Eq(db.RunId));
        Assert.Empty(await root.Where(actor.Property(p => p.Nickname).Eq((string?)null)).Return(() => actor.Value.Name).Limit(100).ToListAsync());
        var actual = await root.Where(actor.Property(p => p.Nickname).Eq("A").Not())
            .Return(() => actor.Value.Name).OrderBy(actor.Property(p => p.Name)).Limit(100).ToListAsync();
        var oracle = await Native(db, "MATCH (a:FGQPerson {RunId:$run}) WHERE NOT a.Nickname='A' RETURN a.Name AS name ORDER BY name LIMIT 100", r => r["name"].As<string>());
        Assert.Equal(new[] { "Bob" }, oracle); Assert.Equal(oracle, actual);
        var ordered = await root.Return(() => new NameRow(actor.Value.Name, actor.Value.Nickname))
            .OrderBy(actor.Property(p => p.Nickname)).ThenBy(actor.Property(p => p.Name)).Limit(100).ToListAsync();
        Assert.Equal(new[] { "Ada", "Bob", "Cara", "Dan", "Eve" }, ordered.Select(r => r.Name));
        Assert.Null(ordered[^1].Nickname);
    }

    [Neo4jIntegrationFact]
    public async Task Fluent_uses_borrowed_transaction_and_rollback_hides_native_seed()
    {
        await using var db = await Seed();
        var transaction = db.Database.CurrentTransaction!; var actor = Graph.Node<Person>();
        var query = db.Cypher().Match(actor).Where(actor.Property(p => p.RunId).Eq(db.RunId)).Return(() => actor.Value.Name).Limit(100);
        Assert.Equal(5, (await query.ToListAsync()).Count);
        Assert.Same(transaction, db.Database.CurrentTransaction);
        Assert.Empty(db.ChangeTracker.Entries());
        await transaction.RollbackAsync(); await transaction.DisposeAsync();
        Assert.Empty(await query.ToListAsync()); // Owns and closes a new read transaction.
        Assert.Null(db.Database.CurrentTransaction);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => query.ToListAsync(cancelled.Token));
        Assert.Null(db.Database.CurrentTransaction);
        await fixture.Driver.VerifyConnectivityAsync();
    }

    [Neo4jIntegrationFact]
    public async Task C6_bounded_paths_preserve_cycles_parallel_relationships_self_loops_and_direction()
    {
        await using var db = await Seed();
        await Native(db, "MATCH (a:FGQPerson {RunId:$run,Name:'Ada'}), (b:FGQPerson {RunId:$run,Name:'Bob'}) CREATE (a)-[:FGQ_FRIEND]->(a),(a)-[:FGQ_FRIEND]->(b) RETURN 1 AS seeded", r => r["seeded"].As<long>());
        foreach (var direction in Enum.GetValues<GraphTraversalDirection>())
        {
            var query = FluentMovieQueries.FriendPaths(db, db.RunId, "Ada", direction);
            var actual = await query.ToListAsync();
            var relationship = direction switch { GraphTraversalDirection.Outgoing => "-[:FGQ_FRIEND*1..3]->", GraphTraversalDirection.Incoming => "<-[:FGQ_FRIEND*1..3]-", _ => "-[:FGQ_FRIEND*1..3]-" };
            var native = await Native(db, "MATCH p=(a:FGQPerson {RunId:$run,Name:'Ada'})" + relationship + "(b:FGQPerson) RETURN p LIMIT 500", r => r["p"].As<IPath>());
            Assert.Equal(native.Select(p => Signature(p.Nodes, p.Relationships)).Order(StringComparer.Ordinal), actual.Select(p => Signature(p.Nodes, p.Relationships)).Order(StringComparer.Ordinal));
            Assert.NotEmpty(actual);
            Assert.Contains(actual, p => p.Nodes.Select(n => n.ElementId).Distinct().Count() < p.Nodes.Count);
            Assert.All(actual, p =>
            {
                Assert.InRange(p.Relationships.Count, 1, 3);
                Assert.Equal(p.Relationships.Count, p.Relationships.Select(e => e.ElementId).Distinct().Count());
                Assert.Equal(p.Relationships.Count, p.StepDirections.Count);
                for (var i = 0; i < p.Relationships.Count; i++)
                {
                    var relation = p.Relationships[i];
                    var outgoing = relation.StartNodeElementId == p.Nodes[i].ElementId && relation.EndNodeElementId == p.Nodes[i + 1].ElementId;
                    Assert.Equal(direction == GraphTraversalDirection.Incoming || !outgoing ? GraphTraversalDirection.Incoming : GraphTraversalDirection.Outgoing, p.StepDirections[i]);
                }
            });
            if (direction == GraphTraversalDirection.Outgoing)
                Assert.Equal(2, actual.Count(p => p.Relationships.Count == 1 && p.Nodes[^1]["Name"].As<string>() == "Bob"));
        }
        Assert.Empty(db.ChangeTracker.Entries());
    }

    [Neo4jIntegrationFact]
    public async Task C6_zero_hops_empty_and_optional_results_and_limits_remain_native_rows()
    {
        await using var db = await Seed();
        var start = Graph.Node<Person>(); var end = Graph.Node<Person>(); var path = Graph.Path();
        var root = db.Cypher().Match(start).Where(start.Property(p => p.RunId).Eq(db.RunId).And(start.Property(p => p.Name).Eq("Eve")));
        var zero = Assert.Single(await root.Match(path, start.Out(Graph.Edge<Friendship>(), end).Repeat(0, 0)).Return(() => path.Value).Limit(10).ToListAsync());
        Assert.Equal("Eve", Assert.Single(zero.Nodes)["Name"].As<string>()); Assert.Empty(zero.Relationships);
        Assert.Empty(await root.Match(path, start.Out(Graph.Edge<Friendship>(), end).Repeat(1, 3)).Return(() => path.Value).Limit(10).ToListAsync());
        Assert.Null(Assert.Single(await root.OptionalMatch(path, start.Out(Graph.Edge<Friendship>(), end).Repeat(1, 3)).Return(() => path.Value).Limit(10).ToListAsync()));
        var bounded = db.Cypher().Match(path, start.Out(Graph.Edge<Friendship>(), end).Repeat(1, 3))
            .Where(start.Property(p => p.RunId).Eq(db.RunId).And(start.Property(p => p.Name).Eq("Ada")))
            .Return(() => path.Value).Limit(1);
        var retained = Assert.Single(await bounded.ToListAsync());
        await db.Database.CurrentTransaction!.RollbackAsync();
        await db.DisposeAsync();
        Assert.InRange(retained.Relationships.Count, 1, 3);
        Assert.Equal("Ada", retained.Nodes[0]["Name"].As<string>());
    }

    [Neo4jIntegrationFact]
    public async Task Optional_relationship_FKs_remain_null_when_the_source_node_is_bound()
    {
        await using var db = await Seed();
        var actor = Graph.Node<Person>(); var movie = Graph.Node<Movie>(); var credit = Graph.Edge<Credit>();
        var optional = db.Cypher().Match(actor).Where(actor.Property(p => p.RunId).Eq(db.RunId))
            .OptionalMatch(actor.Out(credit, movie));
        var actual = await optional.With(actor, credit)
            .Return(() => new EndpointRow(actor.Value.Name, (Guid?)credit.Value.PersonId, (Guid?)credit.Value.MovieId))
            .OrderBy(actor.Property(p => p.Name)).ThenBy(credit.Property(p => p.MovieId)).Limit(100).ToListAsync();
        var oracle = await Native(db, "MATCH (a:FGQPerson {RunId:$run}) OPTIONAL MATCH (a)-[e:FGQ_ACTED]->(:FGQMovie) WITH a,e RETURN a.Name AS name,startNode(e).Id AS source,endNode(e).Id AS target ORDER BY name,target LIMIT 100", Endpoints);
        Assert.Equal(oracle, actual); Assert.Equal(8, actual.Count);
        Assert.All(actual.Where(r => r.Name is "Dan" or "Eve"), r => { Assert.Null(r.Source); Assert.Null(r.Target); });
        var count = Graph.Count(credit.Property(p => p.PersonId));
        var counts = await optional.With(actor, count).Return(() => new { actor.Value.Name, Count = count.Value })
            .OrderBy(actor.Property(p => p.Name)).Limit(100).ToListAsync();
        Assert.Equal(new long[] { 3, 2, 1, 0, 0 }, counts.Select(r => r.Count));
    }

    [Neo4jIntegrationFact]
    public async Task Incoming_relationship_FKs_use_mapped_direction_and_survive_WITH_of_edge_only()
    {
        await using var db = await Seed();
        var actor = Graph.Node<Person>(); var movie = Graph.Node<Movie>(); var credit = Graph.Edge<Credit>();
        var root = db.Cypher().Match(movie).Where(movie.Property(p => p.RunId).Eq(db.RunId));
        foreach (var minimumBilling in new[] { 0, 100 })
        {
            var actual = await root.OptionalMatch(movie.In(credit, actor)).Where(credit.Property(p => p.Billing).Ge(minimumBilling))
                .With(credit).Return(() => new EndpointPair((Guid?)credit.Value.PersonId, (Guid?)credit.Value.MovieId))
                .OrderBy(credit.Property(p => p.PersonId)).ThenBy(credit.Property(p => p.MovieId)).Limit(100).ToListAsync();
            var oracle = await Native(db, "MATCH (m:FGQMovie {RunId:$run}) OPTIONAL MATCH (m)<-[e:FGQ_ACTED]-(:FGQPerson) WHERE e.Billing >= $billing WITH e RETURN startNode(e).Id AS source,endNode(e).Id AS target ORDER BY source,target LIMIT 100",
                r => new EndpointPair(NullableGuid(r["source"]), NullableGuid(r["target"])), ("billing", minimumBilling));
            Assert.Equal(oracle, actual);
            Assert.Equal(minimumBilling == 0 ? 6 : 2, actual.Count);
            if (minimumBilling != 0) Assert.All(actual, r => { Assert.Null(r.Source); Assert.Null(r.Target); });
        }
    }

    [Neo4jIntegrationFact]
    public async Task Reusable_nested_EXISTS_is_lexically_scoped_and_does_not_export_bindings()
    {
        await using var db = await Seed();
        var actor = Graph.Node<Person>(); var friend = Graph.Node<Person>(); var candidate = Graph.Node<Person>();
        var inner = Graph.Exists(friend.Out(Graph.Edge<Friendship>(), candidate), candidate.Property(p => p.Name).Eq("Dan"));
        var outer = Graph.Exists(actor.Out(Graph.Edge<Friendship>(), friend), inner.And(inner));
        var root = db.Cypher().Match(actor).Where(actor.Property(p => p.RunId).Eq(db.RunId).And(outer.And(outer)));
        var actual = await root.Return(() => actor.Value.Name).OrderBy(actor.Property(p => p.Name)).Limit(100).ToListAsync();
        var oracle = await Native(db, "MATCH (a:FGQPerson {RunId:$run}) WHERE EXISTS { MATCH (a)-[:FGQ_FRIEND]->(f:FGQPerson) WHERE EXISTS { MATCH (f)-[:FGQ_FRIEND]->(:FGQPerson {Name:'Dan'}) } } RETURN a.Name AS name ORDER BY name LIMIT 100", r => r["name"].As<string>());
        Assert.Equal(new[] { "Ada", "Bob" }, oracle); Assert.Equal(oracle, actual);
        Assert.Equal(oracle, await FluentMovieQueries.FriendsReaching(db, db.RunId, "Dan").ToListAsync());
        Assert.Throws<NotSupportedException>(() => root.Return(() => friend.Value.Name).ToQueryString());
        // Explicitly matching the same API symbol outside the subquery creates a new outer binding.
        var fresh = await root.Match(friend).Where(friend.Property(p => p.RunId).Eq(db.RunId).And(friend.Property(p => p.Name).Eq("Eve")))
            .Return(() => friend.Value.Name).Limit(100).ToListAsync();
        Assert.Equal(new[] { "Eve", "Eve" }, fresh);
    }

    private static Guid? NullableGuid(object? value) => value is null ? null : Guid.Parse(value.As<string>());
    [Neo4jIntegrationFact]
    public async Task MATCH_after_OPTIONAL_preserves_native_clause_order_on_the_supported_server()
    {
        await using var db = await Seed();
        var actor = Graph.Node<Person>(); var friend = Graph.Node<Person>(); var movie = Graph.Node<Movie>();
        var root = db.Cypher().Match(actor).Where(actor.Property(p => p.RunId).Eq(db.RunId))
            .OptionalMatch(actor.Out(Graph.Edge<Credit>(), movie));
        var supported = await root.With(actor, movie).Match(actor.Out(Graph.Edge<Friendship>(), friend))
            .ReturnDistinct(() => actor.Value.Name).OrderBy(actor.Property(p => p.Name)).Limit(100).ToListAsync();
        Assert.Equal(new[] { "Ada", "Bob", "Cara" }, supported);
        var native = await Native(db,
            "MATCH (a:FGQPerson {RunId:$run}) OPTIONAL MATCH (a)-[:FGQ_ACTED]->(m:FGQMovie) MATCH (a)-[:FGQ_FRIEND]->(f:FGQPerson) RETURN a.Name AS name ORDER BY name LIMIT 100", r => r["name"].As<string>());
        var actual = await root.Match(actor.Out(Graph.Edge<Friendship>(), friend)).Return(() => actor.Value.Name)
            .OrderBy(actor.Property(p => p.Name)).Limit(100).ToListAsync();
        Assert.Equal(13, native.Count); Assert.Equal(native, actual);
    }

    private static EndpointRow Endpoints(IRecord row) => new(row["name"].As<string>(), NullableGuid(row["source"]), NullableGuid(row["target"]));
    private sealed record EndpointRow(string Name, Guid? Source, Guid? Target);
    private sealed record EndpointPair(Guid? Source, Guid? Target);

    private static string Signature(IReadOnlyList<INode> nodes, IReadOnlyList<IRelationship> edges)
        => string.Join(",", nodes.Select(n => n.ElementId)) + "|" + string.Join(",", edges.Select(e => e.ElementId));

    private async Task<FluentContext> Seed()
    {
        var db = new FluentContext(fixture.Driver, fixture.Database);
        try
        {
            await db.Database.BeginTransactionAsync();
            await db.Database.QueryCypherAsync("""
                CREATE (a:FGQPerson {Id:$ada,RunId:$run,Name:'Ada',Nickname:'A'}),
                       (b:FGQPerson {Id:$bob,RunId:$run,Name:'Bob',Nickname:'B'}),
                       (c:FGQPerson {Id:$cara,RunId:$run,Name:'Cara'}),
                       (d:FGQPerson {Id:$dan,RunId:$run,Name:'Dan'}),
                       (e:FGQPerson {Id:$eve,RunId:$run,Name:'Eve'}),
                       (m:FGQMovie {Id:$film1,RunId:$run,Title:'First',Year:2010}),
                       (n:FGQMovie {Id:$film2,RunId:$run,Title:'Second',Year:2024}),
                       (a)-[:FGQ_ACTED {Id:$credit1,Role:'lead',Billing:1}]->(m),
                       (a)-[:FGQ_ACTED {Id:$credit2,Role:'lead',Billing:2}]->(m),
                       (a)-[:FGQ_ACTED {Id:$credit3,Role:'guest',Billing:3}]->(n),
                       (b)-[:FGQ_ACTED {Id:$credit4,Role:'lead',Billing:1}]->(m),
                       (b)-[:FGQ_ACTED {Id:$credit5,Role:'lead',Billing:1}]->(n),
                       (c)-[:FGQ_ACTED {Id:$credit6,Role:'lead',Billing:1}]->(m),
                       (a)-[:FGQ_FRIEND]->(b), (a)-[:FGQ_FRIEND]->(c),
                       (b)-[:FGQ_FRIEND]->(c), (b)-[:FGQ_FRIEND]->(d), (b)-[:FGQ_FRIEND]->(a),
                       (c)-[:FGQ_FRIEND]->(d)
                RETURN 1 AS seeded
                """, db.Parameters, r => r["seeded"].As<long>());
            return db;
        }
        catch { await db.DisposeAsync(); throw; }
    }
    private static Task<IReadOnlyList<T>> Native<T>(FluentContext db, string text, Func<IRecord, T> projector, params (string Name, object? Value)[] extra)
    {
        var parameters = new Dictionary<string, object?> { ["run"] = db.RunId };
        foreach (var (name, value) in extra) parameters.Add(name, value);
        return db.Database.QueryCypherAsync(text, parameters, projector);
    }
    private static OptionalMovieRow Optional(IRecord r) => new(r["name"].As<string>(), r["title"] as string, r["year"] is null ? null : checked((int)r["year"].As<long>()));
    private sealed record CreditRow(Guid Id, Guid PersonId, string Role, int Billing);
    private sealed record NameRow(string Name, string? Nickname);

    private sealed class FluentContext : DbContext
    {
        public FluentContext(IDriver driver, string database) : base(new DbContextOptionsBuilder().UseNeo4j(driver, database).Options)
        {
            Parameters = new Dictionary<string, object?> { ["run"] = RunId, ["ada"] = Ada.ToString("D") };
            foreach (var key in new[] { "bob", "cara", "dan", "eve", "film1", "film2", "credit1", "credit2", "credit3", "credit4", "credit5", "credit6" })
                Parameters.Add(key, Guid.NewGuid().ToString("D"));
        }
        public string RunId { get; } = "fluent_" + Guid.NewGuid().ToString("N");
        public Guid Ada { get; } = Guid.NewGuid();
        public Dictionary<string, object?> Parameters { get; }
        protected override void OnModelCreating(ModelBuilder b) => FluentMovieQueries.Configure(b);
    }
}
