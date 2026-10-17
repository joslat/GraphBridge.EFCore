using System.Linq.Expressions;
using GraphBridge.EntityFrameworkCore.FluentCypher;
using GraphBridge.Samples.FluentCypher;
using Microsoft.EntityFrameworkCore;
using Neo4j.Driver;
using Graph = GraphBridge.EntityFrameworkCore.FluentCypher.Cypher;

namespace GraphBridge.IntegrationTests;

/// <summary>ADR 0047 live oracle: an accepted C# lambda returns exactly the rows its own C# meaning selects.</summary>
/// <remarks>Each predicate is written once as Expression&lt;Func&lt;Person, bool&gt;&gt;. The same tree is rebound onto a Fluent
/// symbol for the server and compiled for in-memory evaluation over the seed rows. The oracle is test code, not fallback.</remarks>
[Collection(Neo4jIntegrationCollection.Name)]
public sealed class ProviderFluentLambdaTests(DisposableNeo4jFixture fixture)
{
    [Neo4jIntegrationFact]
    public async Task Accepted_lambda_predicates_select_exactly_the_rows_their_CSharp_meaning_selects()
    {
        await using var db = await Seed();
        string? missing = null;
        string[] names = ["Ada", "Bob", "Zed"];
        string?[] withNull = ["Bob", null];
        var someIds = new HashSet<Guid> { db.People[0].Id, db.People[2].Id };
        var cases = new Expression<Func<Person, bool>>[]
        {
            p => p.Nickname == null,
            p => p.Nickname != null,
            p => p.Nickname == missing,
            p => p.Name != "Ada",
            p => p.Name == "Ada" || p.Nickname == "Bob",
            p => p.Nickname == "Bob" && p.Name != "Ada",
            p => p.Name == p.Nickname,
            p => p.Nickname == "A" || p.Nickname == null,
            // Wave 2: IN, ordinal string operators and negation over operands that can never be null.
            p => names.Contains(p.Name),
            p => withNull.Contains(p.Name),
            p => !withNull.Contains(p.Name),
            p => names.Contains(p.Nickname) || p.Nickname == null,
            p => someIds.Contains(p.Id),
            p => p.Name.Contains("a"),
            p => p.Name.EndsWith('a') || p.Name.StartsWith("D", StringComparison.Ordinal),
            p => !p.Name.StartsWith("C", StringComparison.Ordinal),
            p => !(p.Name == "Ada"),
            p => !(p.Nickname == null),
            p => !(p.Name == "Ada" || p.Name == "Bob") && p.Nickname != null
        };
        foreach (var predicate in cases)
        {
            var person = Graph.Node<Person>();
            var runId = db.RunId;
            var fluent = await db.Cypher().Match(person).Where(() => person.Value.RunId == runId)
                .Where(Bind(predicate, person)).Return(() => person.Value.Name).OrderBy(() => person.Value.Name)
                .Limit(100).ToListAsync();
            var expected = db.People.Where(predicate.Compile()).Select(p => p.Name).Order(StringComparer.Ordinal).ToArray();
            Assert.True(expected.SequenceEqual(fluent), $"{predicate}: expected [{string.Join(",", expected)}], Fluent returned [{string.Join(",", fluent)}]");
        }
        Assert.Empty(db.ChangeTracker.Entries());
    }

    [Neo4jIntegrationFact]
    public async Task A_null_member_never_selects_a_row_by_itself_as_in_EF_LINQ()
    {
        await using var db = await Seed();
        var person = Graph.Node<Person>();
        var runId = db.RunId;
        // In memory, C# would throw for a null nickname; like EF LINQ, the string test on a null member is not a match.
        var fluent = await db.Cypher().Match(person).Where(() => person.Value.RunId == runId)
            .Where(() => person.Value.Nickname!.StartsWith("B", StringComparison.Ordinal) || person.Value.Name == "Cara")
            .Return(() => person.Value.Name).OrderBy(() => person.Value.Name).Limit(100).ToListAsync();
        var expected = db.People.Where(p => (p.Nickname != null && p.Nickname.StartsWith("B", StringComparison.Ordinal)) || p.Name == "Cara")
            .Select(p => p.Name).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "Bob", "Cara", "Eve" }, expected);
        Assert.Equal(expected, fluent);
    }

    [Neo4jIntegrationFact]
    public async Task Embedded_EXISTS_joins_lambda_compositions_and_negation()
    {
        await using var db = await Seed();
        var person = Graph.Node<Person>(); var movie = Graph.Node<Movie>();
        var runId = db.RunId;
        var root = db.Cypher().Match(person).Where(() => person.Value.RunId == runId);
        var recentOrDan = await root
            .Where(() => person.Value.Name == "Dan" || Graph.Is(Graph.Exists(person.Out<Credit>(movie), movie.Property(m => m.Year).Gt(2015))))
            .Return(() => person.Value.Name).OrderBy(() => person.Value.Name).Limit(100).ToListAsync();
        var credited = Graph.Exists(person.Out<Credit>(Graph.Node<Movie>()));
        var uncredited = await root.Where(() => !Graph.Is(credited))
            .Return(() => person.Value.Name).OrderBy(() => person.Value.Name).Limit(100).ToListAsync();
        string[] Oracle(Func<Person, bool> keep) => db.People.Where(keep).Select(p => p.Name).Order(StringComparer.Ordinal).ToArray();
        bool Acted(Person p, int after) => db.Credits.Any(c => c.PersonId == p.Id && db.Movies.Single(m => m.Id == c.MovieId).Year > after);
        Assert.Equal(Oracle(p => p.Name == "Dan" || Acted(p, 2015)), recentOrDan);
        Assert.Equal(Oracle(p => !Acted(p, int.MinValue)), uncredited);
        Assert.Equal(new[] { "Ada", "Bob", "Dan" }, recentOrDan);
        // The same predicate returned as a Boolean column.
        var flags = await root.Return(() => new Flag(person.Value.Name, Graph.Is(credited)))
            .OrderBy(() => person.Value.Name).Limit(100).ToListAsync();
        Assert.Equal(db.People.OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => new Flag(p.Name, Acted(p, int.MinValue))), flags);
    }

    [Neo4jIntegrationFact]
    public async Task Aggregates_read_typed_values_and_native_null_groups()
    {
        await using var db = await Seed();
        var person = Graph.Node<Person>(); var movie = Graph.Node<Movie>();
        var runId = db.RunId;
        var earliest = Graph.Min(movie.Property(m => m.Year));
        var latest = Graph.Max(movie.Property(m => m.Title));
        var total = Graph.Sum(movie.Property(m => m.Year));
        var mean = Graph.Avg(movie.Property(m => m.Year));
        var ids = Graph.Collect(movie.Property(m => m.Id), 10);
        var rows = await db.Cypher().Match(person).Where(() => person.Value.RunId == runId)
            .OptionalMatch(person.Out<Credit>(movie))
            .With(person, earliest, latest, total, mean, ids)
            .Return(() => new Stats(person.Value.Name, (int?)earliest.Value, latest.Value, total.Value, mean.Value, ids.Value))
            .OrderBy(() => person.Value.Name).Limit(100).ToListAsync();
        foreach (var row in rows)
        {
            var credited = db.Credits.Where(c => c.PersonId == db.People.Single(p => p.Name == row.Name).Id)
                .Select(c => db.Movies.Single(m => m.Id == c.MovieId)).ToArray();
            // Native aggregates over an empty (all-null) group: min/max/avg null, sum 0, collect empty.
            Assert.Equal(credited.Length == 0 ? null : credited.Min(m => m.Year), row.Earliest);
            Assert.Equal(credited.Length == 0 ? null : credited.Select(m => m.Title).Max(StringComparer.Ordinal), row.Latest);
            Assert.Equal(credited.Sum(m => (long)m.Year), row.Total);
            Assert.Equal(credited.Length == 0 ? null : credited.Average(m => (double)m.Year), row.Mean);
            Assert.Equal(credited.Select(m => m.Id).Order(), row.Ids.Order());
        }
        Assert.Equal(db.People.Select(p => p.Name).Order(StringComparer.Ordinal), rows.Select(r => r.Name));
        Assert.Contains(rows, r => r.Name == "Dan" && r.Earliest is null && r.Total == 0 && r.Ids.Count == 0);
        // The same aggregates returned directly use native implicit grouping by the non-aggregate columns.
        var direct = await db.Cypher().Match(person).Where(() => person.Value.RunId == runId)
            .OptionalMatch(person.Out<Credit>(movie))
            .Return(() => new Stats(person.Value.Name, (int?)earliest.Value, latest.Value, total.Value, mean.Value, ids.Value))
            .OrderBy(() => person.Value.Name).Limit(100).ToListAsync();
        Assert.Equal(rows.Select(r => (r.Name, r.Earliest, r.Latest, r.Total, r.Mean)), direct.Select(r => (r.Name, r.Earliest, r.Latest, r.Total, r.Mean)));
        Assert.Equal(rows.Select(r => string.Join(",", r.Ids.Order())), direct.Select(r => string.Join(",", r.Ids.Order())));
        Assert.Empty(db.ChangeTracker.Entries());
    }

    [Neo4jIntegrationFact]
    public async Task Rejected_inequality_would_have_disagreed_with_CSharp_on_the_server()
    {
        await using var db = await Seed();
        var person = Graph.Node<Person>();
        var runId = db.RunId;
        var csharp = db.People.Where(p => p.Nickname != "Bob").Select(p => p.Name).Order(StringComparer.Ordinal).ToArray();
        var native = await db.Database.QueryCypherAsync(
            "MATCH (p:FGQPerson {RunId:$run}) WHERE p.Nickname <> 'Bob' RETURN p.Name AS name ORDER BY name",
            new Dictionary<string, object?> { ["run"] = runId }, r => r["name"].As<string>());
        // People without a nickname: C# keeps them, native Cypher drops them. The lambda form is therefore rejected.
        Assert.Equal(new[] { "Ada", "Bob", "Cara", "Dan", "Fay" }, csharp);
        Assert.Equal(new[] { "Ada", "Bob", "Fay" }, native);
        var error = Assert.Throws<FluentCypherException>(() => db.Cypher().Match(person)
            .Where(() => person.Value.RunId == runId && person.Value.Nickname != "Bob").Return(() => person.Value.Name).ToQueryString());
        Assert.Equal("GBFC110", error.Code);
        // The explicit combinators state either meaning on the server.
        var nativeMeaning = await db.Cypher().Match(person).Where(() => person.Value.RunId == runId)
            .Where(person.Property(p => p.Nickname).Ne("Bob")).Return(() => person.Value.Name).OrderBy(() => person.Value.Name).Limit(100).ToListAsync();
        var csharpMeaning = await db.Cypher().Match(person).Where(() => person.Value.RunId == runId)
            .Where(person.Property(p => p.Nickname).IsNull().Or(person.Property(p => p.Nickname).Ne("Bob")))
            .Return(() => person.Value.Name).OrderBy(() => person.Value.Name).Limit(100).ToListAsync();
        Assert.Equal(native, nativeMeaning);
        Assert.Equal(csharp, csharpMeaning);
    }

    [Neo4jIntegrationFact]
    public async Task Optional_match_predicates_keep_their_clause_and_null_rows()
    {
        await using var db = await Seed();
        var person = Graph.Node<Person>(); var movie = Graph.Node<Movie>();
        var runId = db.RunId;
        var root = db.Cypher().Match(person).Where(() => person.Value.RunId == runId);
        var inline = await root.OptionalMatch(person.Out<Credit>(movie), where: () => movie.Value.Year > 2015 && movie.Value.Title != "First")
            .Return(() => new Row(person.Value.Name, movie.Value.Title)).OrderBy(() => person.Value.Name).ThenBy(() => movie.Value.Title).Limit(100).ToListAsync();
        var afterWith = await root.OptionalMatch(person.Out<Credit>(movie)).With(person, movie).Where(() => movie.Value.Year > 2015)
            .Return(() => new Row(person.Value.Name, movie.Value.Title)).OrderBy(() => person.Value.Name).ThenBy(() => movie.Value.Title).Limit(100).ToListAsync();
        // Left-join oracle over the seed rows, with the same predicate.
        var joined = db.People.SelectMany(p =>
        {
            var titles = db.Credits.Where(c => c.PersonId == p.Id).Select(c => db.Movies.Single(m => m.Id == c.MovieId))
                .Where(m => m.Year > 2015 && m.Title != "First").Select(m => (string?)m.Title).ToArray();
            return titles.Length == 0 ? [new Row(p.Name, null)] : titles.Select(t => new Row(p.Name, t));
        }).OrderBy(r => r.Name, StringComparer.Ordinal).ThenBy(r => r.Title, StringComparer.Ordinal).ToArray();
        Assert.Equal(joined, inline);
        Assert.Equal(joined.Where(r => r.Title is not null), afterWith);
        Assert.Contains(inline, r => r.Name == "Cara" && r.Title is null);
    }

    [Neo4jIntegrationFact]
    public async Task Fluent_keys_load_tracked_entities_through_bounded_EF_Contains()
    {
        await using var db = await Seed();
        var actor = Graph.Node<Person>(); var movie = Graph.Node<Movie>(); var coactor = Graph.Node<Person>();
        var runId = db.RunId;
        var ids = await db.Cypher().Match(actor.Out<Credit>(movie).In<Credit>(coactor))
            .Where(() => actor.Value.RunId == runId && actor.Value.Name == "Ada" && coactor.Value.Id != actor.Value.Id)
            .ReturnDistinct(() => coactor.Value.Id).Limit(100).ToListAsync();
        Assert.Empty(db.ChangeTracker.Entries());
        var keys = ids.ToArray();
        var coactors = await db.Set<Person>().Where(p => keys.Contains(p.Id)).OrderBy(p => p.Name).ToListAsync();
        Assert.Equal(new[] { "Bob", "Cara" }, coactors.Select(p => p.Name));
        Assert.Equal(2, db.ChangeTracker.Entries<Person>().Count());
        Assert.All(coactors, p => Assert.Equal(EntityState.Unchanged, db.Entry(p).State));
    }

    [Neo4jIntegrationFact]
    public async Task Enum_comparisons_and_ordering_use_the_numeric_value_like_CSharp()
    {
        // Severity's numeric order differs from both its name order and the text order of its values ("10" < "5").
        await using var db = new TicketContext(fixture.Driver, fixture.Database);
        await db.Database.BeginTransactionAsync();
        foreach (var ticket in db.Tickets)
            await db.Database.QueryCypherAsync(ticket.Escalation is null
                    ? "CREATE (:FGQTicket {Id:$id, RunId:$run, Title:$title, Level:$level}) RETURN 1 AS done"
                    : "CREATE (:FGQTicket {Id:$id, RunId:$run, Title:$title, Level:$level, Escalation:$escalation}) RETURN 1 AS done",
                new Dictionary<string, object?>
                {
                    ["id"] = ticket.Id.ToString("D"), ["run"] = db.RunId, ["title"] = ticket.Title,
                    ["level"] = (long)ticket.Level, ["escalation"] = (long?)ticket.Escalation
                }, r => r["done"].As<long>());
        var cases = new Expression<Func<Ticket, bool>>[]
        {
            t => t.Level >= Severity.Medium,
            t => t.Level < Severity.High,
            t => t.Level == Severity.Low || t.Level > Severity.Medium,
            t => !(t.Level <= Severity.Low),
            t => t.Escalation > Severity.Low,
            t => t.Escalation <= Severity.Medium || t.Escalation == null
        };
        var runId = db.RunId;
        foreach (var predicate in cases)
        {
            var ticket = Graph.Node<Ticket>();
            var fluent = await db.Cypher().Match(ticket).Where(() => ticket.Value.RunId == runId)
                .Where(Bind(predicate, ticket)).Return(() => ticket.Value.Title).OrderBy(() => ticket.Value.Title)
                .Limit(100).ToListAsync();
            var expected = db.Tickets.Where(predicate.Compile()).Select(t => t.Title).Order(StringComparer.Ordinal).ToArray();
            Assert.True(expected.SequenceEqual(fluent), $"{predicate}: expected [{string.Join(",", expected)}], Fluent returned [{string.Join(",", fluent)}]");
        }
        var symbol = Graph.Node<Ticket>();
        var ordered = await db.Cypher().Match(symbol).Where(() => symbol.Value.RunId == runId)
            .Return(() => new TicketRow(symbol.Value.Title, symbol.Value.Level))
            .OrderByDescending(() => symbol.Value.Level).ThenBy(() => symbol.Value.Title).Limit(100).ToListAsync();
        Assert.Equal(db.Tickets.OrderByDescending(t => t.Level).ThenBy(t => t.Title, StringComparer.Ordinal).Select(t => new TicketRow(t.Title, t.Level)), ordered);
    }

    private static Expression<Func<bool>> Bind<T>(Expression<Func<T, bool>> predicate, CypherNode<T> symbol) where T : class
        => Expression.Lambda<Func<bool>>(new Rebind(predicate.Parameters[0],
            Expression.Property(Expression.Constant(symbol), nameof(CypherNode<T>.Value))).Visit(predicate.Body));

    private sealed class Rebind(ParameterExpression parameter, Expression replacement) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == parameter ? replacement : node;
    }

    private async Task<LambdaContext> Seed()
    {
        var db = new LambdaContext(fixture.Driver, fixture.Database);
        try
        {
            // Never committed: disposing the context rolls back this run's graph.
            await db.Database.BeginTransactionAsync();
            foreach (var person in db.People)
                await Execute(db, person.Nickname is null
                    ? "CREATE (:FGQPerson {Id:$id, RunId:$run, Name:$name})"
                    : "CREATE (:FGQPerson {Id:$id, RunId:$run, Name:$name, Nickname:$nickname})",
                    ("id", person.Id.ToString("D")), ("name", person.Name), ("nickname", person.Nickname));
            foreach (var movie in db.Movies)
                await Execute(db, "CREATE (:FGQMovie {Id:$id, RunId:$run, Title:$title, Year:$year})",
                    ("id", movie.Id.ToString("D")), ("title", movie.Title), ("year", (long)movie.Year));
            foreach (var credit in db.Credits)
                await Execute(db, "MATCH (p:FGQPerson {Id:$person}), (m:FGQMovie {Id:$movie}) CREATE (p)-[:FGQ_ACTED {Id:$id, Role:'lead', Billing:1}]->(m)",
                    ("id", credit.Id.ToString("D")), ("person", credit.PersonId.ToString("D")), ("movie", credit.MovieId.ToString("D")));
            return db;
        }
        catch { await db.DisposeAsync(); throw; }
    }

    private static Task<IReadOnlyList<long>> Execute(LambdaContext db, string text, params (string Name, object? Value)[] values)
    {
        var parameters = new Dictionary<string, object?> { ["run"] = db.RunId };
        foreach (var (name, value) in values) parameters[name] = value;
        return db.Database.QueryCypherAsync(text + " RETURN 1 AS done", parameters, r => r["done"].As<long>());
    }

    private sealed record Row(string Name, string? Title);
    private sealed record Flag(string Name, bool Credited);
    public enum Severity { Low = 1, Medium = 5, High = 10 }

    public sealed class Ticket
    {
        public Guid Id { get; set; }
        public string RunId { get; set; } = "";
        public string Title { get; set; } = "";
        public Severity Level { get; set; }
        public Severity? Escalation { get; set; }
    }

    private sealed record TicketRow(string Title, Severity Level);

    private sealed class TicketContext(IDriver driver, string database) : DbContext(new DbContextOptionsBuilder().UseNeo4j(driver, database).Options)
    {
        public string RunId { get; } = "enum_" + Guid.NewGuid().ToString("N");
        // Seed rows double as the in-memory oracle; they are never attached to this context.
        public IReadOnlyList<Ticket> Tickets { get; } = new[]
        {
            Make("Backup", Severity.High, null), Make("Cache", Severity.Low, Severity.Medium), Make("Deploy", Severity.Medium, Severity.High),
            Make("Email", Severity.Low, null), Make("Fonts", Severity.High, Severity.Low)
        };
        private static Ticket Make(string title, Severity level, Severity? escalation)
            => new() { Id = Guid.NewGuid(), Title = title, Level = level, Escalation = escalation };
        protected override void OnModelCreating(ModelBuilder b) => b.Entity<Ticket>().ToGraphNode("FGQTicket");
    }

    private sealed record Stats(string Name, int? Earliest, string? Latest, long Total, double? Mean, IReadOnlyList<Guid> Ids);

    private sealed class LambdaContext(IDriver driver, string database) : DbContext(new DbContextOptionsBuilder().UseNeo4j(driver, database).Options)
    {
        public string RunId { get; } = "lambda_" + Guid.NewGuid().ToString("N");
        // Seed rows double as the in-memory oracle; they are never attached to this context.
        public IReadOnlyList<Person> People { get; } = new[]
        {
            Make("Ada", "A"), Make("Bob", "B"), Make("Cara", null), Make("Dan", null), Make("Eve", "Bob"), Make("Fay", "Fay")
        };
        public IReadOnlyList<Movie> Movies { get; } = new[]
        {
            new Movie { Id = Guid.NewGuid(), Title = "First", Year = 2010 }, new Movie { Id = Guid.NewGuid(), Title = "Second", Year = 2024 },
            new Movie { Id = Guid.NewGuid(), Title = "Third", Year = 2020 }
        };
        public IReadOnlyList<Credit> Credits => _credits ??= new[]
        {
            Credit("Ada", "First"), Credit("Ada", "Second"), Credit("Bob", "Second"), Credit("Bob", "Third"), Credit("Cara", "First")
        };
        private Credit[]? _credits;

        private Credit Credit(string person, string movie) => new()
        {
            Id = Guid.NewGuid(), PersonId = People.Single(p => p.Name == person).Id, MovieId = Movies.Single(m => m.Title == movie).Id
        };
        private static Person Make(string name, string? nickname) => new() { Id = Guid.NewGuid(), Name = name, Nickname = nickname };
        protected override void OnModelCreating(ModelBuilder b) => FluentMovieQueries.Configure(b);
    }
}
