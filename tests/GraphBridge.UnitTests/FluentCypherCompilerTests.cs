using GraphBridge.EntityFrameworkCore.FluentCypher;
using Microsoft.EntityFrameworkCore;
using Graph = GraphBridge.EntityFrameworkCore.FluentCypher.Cypher;

namespace GraphBridge.UnitTests;

public sealed class FluentCypherCompilerTests
{
    [Fact]
    public void Fixed_patterns_bind_real_labels_roles_and_properties_and_parameterize_values()
    {
        using var db = Context();
        var actor = Graph.Node<Person>(); var movie = Graph.Node<Movie>(); var credit = Graph.Edge<Credit>();
        var input = "secret' MATCH (n) DELETE n //";
        var query = db.Cypher().Match(actor.Out(credit, movie)).Where(actor.Property(p => p.Name).Eq(input))
            .Return(() => new { actor.Value.Id, movie.Value.Title, credit.Value.Role })
            .OrderBy(movie.Property(p => p.Title)).Skip(2).Limit(10);
        var command = query.ToCommand();
        Assert.Equal("MATCH (n0:`FluentPerson`)-[r2:`FLUENT_ACTED`]->(n1:`FluentMovie`)\nWHERE n0.`display_name` = $p0\nRETURN n0.`Id` AS c0, n1.`Title` AS c1, r2.`Role` AS c2\nORDER BY c1 ASC\nSKIP $p1\nLIMIT $p2", command.Text.Replace("\r", ""));
        Assert.Equal(input, command.Parameters["p0"]);
        Assert.Equal(2L, command.Parameters["p1"]); Assert.Equal(10L, command.Parameters["p2"]);
        Assert.DoesNotContain(input, query.ToQueryString());
        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, object?>)command.Parameters).Clear());
        Assert.Empty(db.ChangeTracker.Entries());
    }

    [Fact]
    public void Coactors_use_independent_same_type_symbols_and_preserve_one_MATCH_clause()
    {
        using var db = Context();
        var actor = Graph.Node<Person>(); var coactor = Graph.Node<Person>(); var movie = Graph.Node<Movie>();
        var text = db.Cypher().Match(actor.Out(Graph.Edge<Credit>(), movie).In(Graph.Edge<Credit>(), coactor))
            .Where(actor.Property(p => p.Id).Ne(coactor.Property(p => p.Id)))
            .ReturnDistinct(() => new { coactor.Value.Id, coactor.Value.Name }).Limit(20).ToQueryString();
        Assert.Contains("<-[r4:`FLUENT_ACTED`]-(n3:`FluentPerson`)", text);
        Assert.Contains("WHERE n0.`Id` <> n3.`Id`", text);
        Assert.Contains("RETURN DISTINCT n3.`Id` AS c0, n3.`display_name` AS c1", text);
        Assert.DoesNotContain("coalesce", text);
        Assert.Equal(1, text.Split("MATCH").Length - 1);
    }

    [Fact]
    public void Optional_predicate_stays_before_WITH_and_native_null_sort_is_unchanged()
    {
        using var db = Context();
        var actor = Graph.Node<Person>(); var movie = Graph.Node<Movie>(); var edge = Graph.Edge<Credit>();
        var text = db.Cypher().Match(actor).OptionalMatch(actor.Out(edge, movie))
            .Where(movie.Property(p => p.Year).Gt(2000)).With(actor, movie)
            .Where(movie.Property(p => p.Title).IsNull().Not())
            .Return(() => new { actor.Value.Name, movie.Value.Title })
            .OrderBy(movie.Property(p => p.Title)).ToQueryString().Replace("\r", "");
        Assert.Contains("WHERE n1.`Year` > $p0\nWITH n0, n1\nWHERE NOT (n1.`Title` IS NULL)", text);
        Assert.EndsWith("ORDER BY c1 ASC", text);
        Assert.DoesNotContain("coalesce", text);
    }

    [Fact]
    public void WITH_introduces_count_grouping_and_retains_only_declared_scope()
    {
        using var db = Context();
        var person = Graph.Node<Person>(); var movie = Graph.Node<Movie>();
        var count = Graph.CountDistinct(movie);
        var query = db.Cypher().Match(person.Out(Graph.Edge<Credit>(), movie)).With(person, count)
            .Where(count.Expression.Gt(1L));
        var text = query.Return(() => new { person.Value.Name, Count = count.Value })
            .OrderByDescending(count.Expression).ThenBy(person.Property(p => p.Name)).ToQueryString();
        Assert.Contains("WITH n0, count(DISTINCT n1) AS v3", text);
        Assert.Contains("WHERE v3 > $p0", text);
        Assert.Contains("ORDER BY c1 DESC, c0 ASC", text);
        Assert.Contains("outside", Assert.Throws<FluentCypherException>(() => query.Return(() => movie.Value.Title).ToQueryString()).Message);
        Assert.Contains("rebound", Assert.Throws<FluentCypherException>(() => query.Match(movie).Return(() => movie.Value.Title).ToQueryString()).Message);
    }

    [Fact]
    public void Correlated_EXISTS_does_not_export_its_new_symbols()
    {
        using var db = Context();
        var person = Graph.Node<Person>(); var friend = Graph.Node<Person>(); var friendship = Graph.Edge<Friendship>();
        var query = db.Cypher().Match(person).Where(Graph.Exists(person.Out(friendship, friend), friend.Property(p => p.Name).Eq("Ada")).Not());
        var text = query.Return(() => person.Value.Name).ToQueryString();
        Assert.Contains("NOT EXISTS { MATCH (n0:`FluentPerson`)-[r2:`FLUENT_FRIEND`]->(n1:`FluentPerson`)", text);
        Assert.Contains("n1.`display_name` = $p0", text);
        Assert.Throws<FluentCypherException>(() => query.Return(() => friend.Value.Name).ToQueryString());
    }

    [Fact]
    public void Relationship_identity_payload_and_derived_FK_use_their_physical_storage_owners()
    {
        using var db = Context();
        var person = Graph.Node<Person>(); var movie = Graph.Node<Movie>(); var edge = Graph.Edge<Credit>();
        var compiled = db.Cypher().Match(person.Out(edge, movie))
            .Return(() => new { edge.Value.Id, edge.Value.PersonId, edge.Value.MovieId, edge.Value.Role }).Compile();
        Assert.Contains("RETURN r2.`Id` AS c0, startNode(r2).`Id` AS c1, endNode(r2).`Id` AS c2, r2.`Role` AS c3", compiled.Command.Text);
        var row = compiled.Read(new Dictionary<string, object> { ["c0"] = 10L, ["c1"] = 2L, ["c2"] = 3L, ["c3"] = "actor" });
        Assert.Equal(10, row.Id); Assert.Equal(2, row.PersonId); Assert.Equal(3, row.MovieId);
    }

    [Fact]
    public void Converters_apply_to_parameters_and_results_without_running_client_predicates()
    {
        using var db = Context(); var person = Graph.Node<Person>(); var token = Guid.NewGuid();
        var compiled = db.Cypher().Match(person).Where(person.Property(p => p.Token).Eq(token)).Return(() => person.Value.Token).Compile();
        Assert.Equal(token.ToString("D"), compiled.Command.Parameters["p0"]);
        Assert.Equal(token, compiled.Read(new Dictionary<string, object> { ["c0"] = token.ToString("D") }));
        Assert.Throws<FormatException>(() => compiled.Read(new Dictionary<string, object> { ["c0"] = "bad" }));
    }

    [Fact]
    public void Nullable_converter_result_is_not_replaced_with_the_original_store_value()
    {
        using var db = Context(b => b.Entity<Person>().Property(p => p.Name).IsRequired(false)
            .HasConversion(value => value, value => value == "<absent>" ? null! : value));
        var person = Graph.Node<Person>();
        var compiled = db.Cypher().Match(person).Return(() => person.Value.Name).Compile();
        Assert.Null(compiled.Read(new Dictionary<string, object> { ["c0"] = "<absent>" }));
        Assert.Equal("Ada", compiled.Read(new Dictionary<string, object> { ["c0"] = "Ada" }));
    }

    [Fact]
    public void Optional_nulls_require_nullable_value_projection_and_missing_columns_still_fail()
    {
        using var db = Context(); var person = Graph.Node<Person>(); var movie = Graph.Node<Movie>();
        var query = db.Cypher().Match(person).OptionalMatch(person.Out(Graph.Edge<Credit>(), movie));
        var nullable = query.Return(() => new { Year = (int?)movie.Value.Year, movie.Value.Title }).Compile();
        var row = nullable.Read(new Dictionary<string, object> { ["c0"] = null!, ["c1"] = null! });
        Assert.Null(row.Year); Assert.Null(row.Title);
        Assert.Throws<InvalidOperationException>(() => nullable.Read(new Dictionary<string, object>()));
        var required = query.Return(() => movie.Value.Year).Compile();
        Assert.Throws<InvalidOperationException>(() => required.Read(new Dictionary<string, object> { ["c0"] = null! }));
        Assert.Throws<OverflowException>(() => required.Read(new Dictionary<string, object> { ["c0"] = long.MaxValue }));
    }

    [Fact]
    public void Null_equality_keeps_unknown_instead_of_CLR_null_compensation()
    {
        using var db = Context(); var person = Graph.Node<Person>();
        var compiled = db.Cypher().Match(person).Where(person.Property(p => p.Name).Eq((string)null!)).Return(() => person.Value.Id).Compile();
        Assert.Contains("WHERE n0.`display_name` = $p0", compiled.Command.Text);
        Assert.Null(compiled.Command.Parameters["p0"]);
    }

    [Fact]
    public void Filters_wrong_roles_and_unmapped_symbols_reject_before_driver_IO()
    {
        using var filtered = Context(b => b.Entity<Person>().HasQueryFilter(p => p.Id > 0));
        var person = Graph.Node<Person>(); var movie = Graph.Node<Movie>();
        Assert.Contains("query filters", Assert.Throws<FluentCypherException>(() => filtered.Cypher().Match(person).Return(() => person.Value.Id).ToQueryString()).Message);
        using var db = Context();
        Assert.Contains("roles", Assert.Throws<FluentCypherException>(() => db.Cypher().Match(movie.Out(Graph.Edge<Credit>(), person)).Return(() => person.Value.Id).ToQueryString()).Message);
        var missing = Graph.Node<Unmapped>();
        Assert.Throws<FluentCypherException>(() => db.Cypher().Match(missing).Return(() => missing.Value.Id).ToQueryString());
        Assert.Throws<FluentCypherException>(() => db.Cypher().Match(person.Out(Graph.Edge<Person>(), person)).Return(() => person.Value.Id).ToQueryString());
    }

    [Fact]
    public void Unsupported_projection_computation_entities_and_hidden_sort_reject()
    {
        using var db = Context(); var person = Graph.Node<Person>(); var query = db.Cypher().Match(person);
        Assert.Throws<FluentCypherException>(() => query.Return(() => person.Value.Name.ToUpper()).ToQueryString());
        Assert.Throws<FluentCypherException>(() => query.Return(() => person.Value).ToQueryString());
        Assert.Throws<FluentCypherException>(() => query.Return(() => new Person { Id = person.Value.Id }).ToQueryString());
        Assert.Throws<FluentCypherException>(() => query.Return(() => person.Value.Id + 1).ToQueryString());
        Assert.Throws<FluentCypherException>(() => query.ReturnDistinct(() => person.Value.Name).OrderBy(person.Property(p => p.Id)).ToQueryString());
    }

    [Fact]
    public async Task Execution_requires_a_limit_and_observes_pre_cancellation_before_compilation()
    {
        using var db = Context(); var person = Graph.Node<Person>();
        var query = db.Cypher().Match(person).Return(() => person.Value.Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => query.ToListAsync());
        await Assert.ThrowsAsync<OperationCanceledException>(() => query.Limit(1).ToListAsync(new CancellationToken(true)));
    }

    internal static ProviderModelContext Context(Action<ModelBuilder>? customize = null) => new(ProviderTestDriver.Create(), b =>
    {
        b.Entity<Person>().ToGraphNode("FluentPerson");
        b.Entity<Person>().Property(p => p.Id).ValueGeneratedNever();
        b.Entity<Person>().Property(p => p.Name).HasGraphPropertyName("display_name");
        b.Entity<Movie>().ToGraphNode("FluentMovie");
        b.Entity<Movie>().Property(p => p.Id).ValueGeneratedNever();
        b.Entity<Credit>().HasOne<Person>().WithMany().HasForeignKey(e => e.PersonId);
        b.Entity<Credit>().HasOne<Movie>().WithMany().HasForeignKey(e => e.MovieId);
        b.Entity<Credit>().Property(p => p.Id).ValueGeneratedNever();
        b.Entity<Credit>().ToGraphIdentifiedRelationship("FLUENT_ACTED", nameof(Credit.Id), [nameof(Credit.PersonId)], [nameof(Credit.MovieId)]);
        b.Entity<Friendship>().HasKey(e => new { e.SourceId, e.TargetId });
        b.Entity<Friendship>().HasOne<Person>().WithMany().HasForeignKey(e => e.SourceId);
        b.Entity<Friendship>().HasOne<Person>().WithMany().HasForeignKey(e => e.TargetId);
        b.Entity<Friendship>().ToGraphRelationship("FLUENT_FRIEND", [nameof(Friendship.SourceId)], [nameof(Friendship.TargetId)]);
        customize?.Invoke(b);
    });
    public sealed class Person { public int Id { get; set; } public string Name { get; set; } = ""; public Guid Token { get; set; } }
    public sealed class Movie { public int Id { get; set; } public string Title { get; set; } = ""; public int Year { get; set; } }
    public sealed class Credit { public int Id { get; set; } public int PersonId { get; set; } public int MovieId { get; set; } public string Role { get; set; } = ""; }
    public sealed class Friendship { public int SourceId { get; set; } public int TargetId { get; set; } }
    public sealed class Unmapped { public int Id { get; set; } }
}
