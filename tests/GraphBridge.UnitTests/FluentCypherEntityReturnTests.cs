using GraphBridge.EntityFrameworkCore;
using GraphBridge.EntityFrameworkCore.FluentCypher;
using Microsoft.EntityFrameworkCore;
using Neo4j.Driver;
using static GraphBridge.UnitTests.FluentCypherLambdaTests;
using Graph = GraphBridge.EntityFrameworkCore.FluentCypher.Cypher;

namespace GraphBridge.UnitTests;

/// <summary>ADR 0049: node and relationship symbols returned as their mapped entities, untracked.</summary>
public sealed class FluentCypherEntityReturnTests
{
    private const string MemberMap = "{.`Id`, .`Active`, .`Born`, .`Code`, .`Joined`, .`Level`, .`Name`, .`Nickname`, .`Score`, .`Token`}";
    private const string ClubMap = "{.`Id`, .`Founded`, .`Title`}";

    [Fact]
    public void A_node_returns_a_map_of_its_mapped_properties_and_reads_untracked_through_its_converters()
    {
        using var db = Context();
        var member = Graph.Node<Member>();
        var query = db.Cypher().Match(member).Return(() => member.Value).OrderBy(() => member.Value.Name).Limit(10);
        Assert.Equal("MATCH (n0:`LambdaMember`)\nRETURN n0 " + MemberMap + " AS c0\nORDER BY c0.`Name` ASC\nLIMIT $p0",
            query.ToQueryString().Replace("\r", ""));

        var token = Guid.NewGuid();
        var joined = new DateTimeOffset(2026, 10, 22, 9, 30, 0, TimeSpan.FromHours(2));
        var read = new CypherCompiler(db).Compile(query).Read(Row(new Dictionary<string, object>
        {
            ["Id"] = 7L, ["Active"] = true, ["Born"] = null!, ["Code"] = "code:A-7", ["Joined"] = new ZonedDateTime(joined),
            ["Level"] = 2L, ["Name"] = "Ada", ["Nickname"] = null!, ["Score"] = 1.5, ["Token"] = token.ToString("D"),
            ["embedding"] = new List<object> { 0.1, 0.2 }
        }));
        Assert.Equal(7, read.Id);
        Assert.True(read.Active);
        Assert.Null(read.Born);
        Assert.Equal("A-7", read.Code);
        Assert.Equal(joined, read.Joined);
        Assert.Equal(Level.Gold, read.Level);
        Assert.Equal("Ada", read.Name);
        Assert.Equal(1.5, read.Score);
        Assert.Equal(token, read.Token);
        Assert.Empty(db.ChangeTracker.Entries());
    }

    [Fact]
    public void A_node_value_reads_like_LINQ_and_refuses_missing_required_values_and_non_canonical_keys()
    {
        using var db = Context();
        var club = Graph.Node<Club>();
        var compiled = new CypherCompiler(db).Compile(db.Cypher().Match(club).Return(() => club.Value).Limit(1));
        Assert.Contains("Club.Title' is null or missing",
            Assert.Throws<InvalidOperationException>(() => compiled.Read(Row(new Dictionary<string, object> { ["Id"] = 1L, ["Founded"] = 1990L }))).Message);
        Assert.Contains("expects store type",
            Assert.Throws<InvalidOperationException>(() => compiled.Read(Row(new Dictionary<string, object> { ["Id"] = "1", ["Founded"] = 1990L, ["Title"] = "x" }))).Message);
    }

    [Fact]
    public void An_optional_symbol_reads_as_a_null_entity_and_entities_compose_with_DTO_members()
    {
        using var db = Context();
        var club = Graph.Node<Club>(); var member = Graph.Node<Member>();
        var query = db.Cypher().Match(club).OptionalMatch(member.Out<Membership>(club))
            .Return(() => new { club.Value.Title, Club = club.Value, Member = member.Value }).Limit(10);
        Assert.Equal("MATCH (n0:`LambdaClub`)\nOPTIONAL MATCH (n1:`LambdaMember`)-[r2:`LAMBDA_MEMBER_OF`]->(n0:`LambdaClub`)\n"
            + "RETURN n0.`Title` AS c0, n0 " + ClubMap + " AS c1, n1 " + MemberMap + " AS c2\nLIMIT $p0", query.ToQueryString().Replace("\r", ""));
        var row = new CypherCompiler(db).Compile(query).Read(new Dictionary<string, object>
        {
            ["c0"] = "Chess", ["c1"] = new Dictionary<string, object> { ["Id"] = 3L, ["Founded"] = 1990L, ["Title"] = "Chess" }, ["c2"] = null!
        });
        Assert.Equal("Chess", row.Title);
        Assert.Equal(1990, row.Club.Founded);
        Assert.Null(row.Member);
    }

    [Fact]
    public void An_identified_relationship_returns_its_stored_properties_and_endpoint_keys()
    {
        using var db = Context();
        var member = Graph.Node<Member>(); var club = Graph.Node<Club>(); var membership = Graph.Edge<Membership>();
        var query = db.Cypher().Match(member.Out(membership, club)).With(membership).Return(() => membership.Value).Limit(10);
        Assert.Equal("MATCH (n0:`LambdaMember`)-[r2:`LAMBDA_MEMBER_OF`]->(n1:`LambdaClub`)\nWITH r2\n"
            + "RETURN r2 {.`Id`, .`Role`, __gb_source: {`Id`: startNode(r2).`Id`}, __gb_target: {`Id`: endNode(r2).`Id`}} AS c0\nLIMIT $p0",
            query.ToQueryString().Replace("\r", ""));
        var read = new CypherCompiler(db).Compile(query).Read(Row(new Dictionary<string, object>
        {
            ["Id"] = 9L, ["Role"] = "captain",
            ["__gb_source"] = new Dictionary<string, object> { ["Id"] = 1L }, ["__gb_target"] = new Dictionary<string, object> { ["Id"] = 2L }
        }));
        Assert.Equal((9, 1, 2, "captain"), (read.Id, read.MemberId, read.ClubId, read.Role));
        Assert.Contains("endpoint keys", Assert.Throws<InvalidOperationException>(() => new CypherCompiler(db).Compile(query)
            .Read(Row(new Dictionary<string, object> { ["Id"] = 9L, ["Role"] = "x" }))).Message);
    }

    [Fact]
    public void An_association_returns_its_payload_with_endpoint_keys_under_their_stored_names()
    {
        using var db = AssociationContext();
        var person = Graph.Node<Person>(); var film = Graph.Node<Film>(); var rating = Graph.Edge<Rating>();
        var query = db.Cypher().Match(person.Out(rating, film)).Return(() => rating.Value).Limit(10);
        Assert.Equal("MATCH (n0:`EntityPerson`)-[r2:`RATED`]->(n1:`EntityFilm`)\n"
            + "RETURN r2 {.`stars`, __gb_source: {`person_key`: startNode(r2).`person_key`}, __gb_target: {`film_key`: endNode(r2).`film_key`}} AS c0\nLIMIT $p0",
            query.ToQueryString().Replace("\r", ""));
        var film1 = Guid.NewGuid();
        var read = new CypherCompiler(db).Compile(query).Read(Row(new Dictionary<string, object>
        {
            ["stars"] = 4L,
            ["__gb_source"] = new Dictionary<string, object> { ["person_key"] = "ada" },
            ["__gb_target"] = new Dictionary<string, object> { ["film_key"] = film1.ToString("D") }
        }));
        Assert.Equal(("ada", film1, 4), (read.PersonId, read.FilmId, read.Stars));
    }

    [Fact]
    public void A_native_reference_dependent_reads_its_foreign_key_from_exactly_one_valid_parent()
    {
        using var db = ReferenceContext();
        var book = Graph.Node<Book>();
        var query = db.Cypher().Match(book).Return(() => book.Value).Limit(10);
        Assert.Equal("MATCH (n0:`EntityBook`)\nRETURN n0 {.`Id`, .`Title`, __gb_reference: COLLECT { MATCH (__gb_parent)-[:`ON_SHELF`]->(n0) "
            + "RETURN __gb_parent {.`shelf_key`, __gb_principal: (__gb_parent:`EntityShelf`)} LIMIT 2 }} AS c0\nLIMIT $p0",
            query.ToQueryString().Replace("\r", ""));
        var compiled = new CypherCompiler(db).Compile(query);
        var shelf = Guid.NewGuid();
        var read = compiled.Read(Row(Book(Parent(shelf, true))));
        Assert.Equal(shelf, read.ShelfId);
        Assert.Equal("Dune", read.Title);
        foreach (var invalid in new[] { Book(), Book(Parent(shelf, false)), Book(Parent(shelf, true), Parent(Guid.NewGuid(), true)) })
            Assert.Contains("membership", Assert.Throws<InvalidOperationException>(() => compiled.Read(Row(invalid))).Message);

        static Dictionary<string, object> Parent(Guid key, bool principal)
            => new() { ["shelf_key"] = key.ToString("D"), ["__gb_principal"] = principal };
        static Dictionary<string, object> Book(params Dictionary<string, object>[] parents)
            => new() { ["Id"] = 5L, ["Title"] = "Dune", ["__gb_reference"] = parents.Cast<object>().ToList() };
    }

    [Fact]
    public void Collect_over_entities_is_bounded_in_WITH_and_inline()
    {
        using var db = Context();
        var member = Graph.Node<Member>(); var club = Graph.Node<Club>();
        var clubs = Graph.Collect(club, 5);
        var query = db.Cypher().Match(member.Out<Membership>(club)).With(member, clubs)
            .Return(() => new { member.Value.Name, Clubs = clubs.Value }).Limit(10);
        Assert.Equal("MATCH (n0:`LambdaMember`)-[r2:`LAMBDA_MEMBER_OF`]->(n1:`LambdaClub`)\n"
            + "WITH n0, collect(n1 " + ClubMap + ")[..$p0] AS v3\nRETURN n0.`Name` AS c0, v3 AS c1\nLIMIT $p1", query.ToQueryString().Replace("\r", ""));
        var row = new CypherCompiler(db).Compile(query).Read(new Dictionary<string, object>
        {
            ["c0"] = "Ada",
            ["c1"] = new List<object>
            {
                new Dictionary<string, object> { ["Id"] = 1L, ["Founded"] = 1990L, ["Title"] = "Chess" },
                new Dictionary<string, object> { ["Id"] = 2L, ["Founded"] = 2001L, ["Title"] = "Go" }
            }
        });
        Assert.Equal(new[] { "Chess", "Go" }, row.Clubs.Select(c => c.Title));

        var distinct = Graph.CollectDistinct(club, 3);
        Assert.Equal("MATCH (n0:`LambdaMember`)-[r2:`LAMBDA_MEMBER_OF`]->(n1:`LambdaClub`)\n"
            + "RETURN n0.`Name` AS c0, collect(DISTINCT n1 " + ClubMap + ")[..$p0] AS c1\nLIMIT $p1",
            db.Cypher().Match(member.Out<Membership>(club)).Return(() => new { member.Value.Name, Clubs = distinct.Value }).Limit(10)
                .ToQueryString().Replace("\r", ""));
        Assert.Throws<ArgumentOutOfRangeException>(() => Graph.Collect(club, 0));
    }

    [Fact]
    public void Ordering_by_a_returned_entity_member_reads_the_returned_map_even_after_DISTINCT()
    {
        using var db = Context();
        var member = Graph.Node<Member>(); var club = Graph.Node<Club>(); var membership = Graph.Edge<Membership>();
        Assert.Equal("MATCH (n0:`LambdaMember`)-[r2:`LAMBDA_MEMBER_OF`]->(n1:`LambdaClub`)\nRETURN DISTINCT n1 " + ClubMap
            + " AS c0\nORDER BY c0.`Founded` DESC, c0.`Title` ASC\nLIMIT $p0",
            db.Cypher().Match(member.Out<Membership>(club)).ReturnDistinct(() => club.Value)
                .OrderByDescending(() => club.Value.Founded).ThenBy(club.Property(c => c.Title)).Limit(10).ToQueryString().Replace("\r", ""));
        // A foreign key is read from an endpoint, not stored on the relationship, so it is not a member of the returned map.
        Assert.Equal("GBFC405", Code(() => db.Cypher().Match(member.Out(membership, club)).Return(() => membership.Value)
            .OrderBy(() => membership.Value.MemberId).ToQueryString()));
        // Ordering by a member of an entity that is not returned still requires returning it.
        Assert.Equal("GBFC405", Code(() => db.Cypher().Match(member.Out<Membership>(club)).Return(() => club.Value)
            .OrderBy(() => member.Value.Name).ToQueryString()));
    }

    [Fact]
    public void Constructing_entities_reserved_names_and_query_filters_stay_refused()
    {
        using var db = Context();
        var club = Graph.Node<Club>();
        Assert.Equal("GBFC404", Code(() => db.Cypher().Match(club).Return(() => new Club { Title = club.Value.Title }).ToQueryString()));

        using var reserved = new ProviderModelContext(ProviderTestDriver.Create(), b =>
        {
            b.Entity<Club>().ToGraphNode("ReservedClub").Property(c => c.Title).HasGraphPropertyName("__gb_title");
            b.Entity<Club>().Property(c => c.Id).ValueGeneratedNever();
        });
        var reservedClub = Graph.Node<Club>();
        Assert.Equal("GBFC408", Code(() => reserved.Cypher().Match(reservedClub).Return(() => reservedClub.Value).ToQueryString()));
        Assert.Equal("MATCH (n0:`ReservedClub`)\nRETURN n0.`__gb_title` AS c0",
            reserved.Cypher().Match(reservedClub).Return(() => reservedClub.Value.Title).ToQueryString().Replace("\r", ""));

        using var filtered = new ProviderModelContext(ProviderTestDriver.Create(), b =>
        {
            b.Entity<Club>().ToGraphNode("FilteredClub").HasQueryFilter(c => c.Founded > 0);
            b.Entity<Club>().Property(c => c.Id).ValueGeneratedNever();
        });
        var filteredClub = Graph.Node<Club>();
        Assert.Equal("GBFC212", Code(() => filtered.Cypher().Match(filteredClub).Return(() => filteredClub.Value).ToQueryString()));
    }

    [Fact]
    public void An_entity_return_keeps_a_complete_semantics_snapshot()
    {
        using var db = Context();
        var club = Graph.Node<Club>();
        var snapshot = db.Cypher().Match(club).Return(() => club.Value).Limit(1).Inspect().Semantics!;
        Assert.Equal(CypherSnapshotStatus.Complete, snapshot.Status);
        var returned = Assert.Single(snapshot.Clauses, c => c.Kind == CypherClauseKind.Return);
        Assert.Equal(new[] { "c0" }, returned.ScopeAfter);
    }

    [Fact]
    public void The_guide_example_renders_as_documented()
    {
        using var db = new ProviderModelContext(ProviderTestDriver.Create(), b =>
        {
            b.Entity<GuidePerson>().ToGraphNode("Person").Property(p => p.Id).ValueGeneratedNever();
            b.Entity<GuideMovie>().ToGraphNode("Movie").Property(m => m.Id).ValueGeneratedNever();
            b.Entity<GuideCredit>().HasOne<GuidePerson>().WithMany().HasForeignKey(c => c.PersonId);
            b.Entity<GuideCredit>().HasOne<GuideMovie>().WithMany().HasForeignKey(c => c.MovieId);
            b.Entity<GuideCredit>().Property(c => c.Id).ValueGeneratedNever();
            b.Entity<GuideCredit>().ToGraphIdentifiedRelationship("ACTED_IN", nameof(GuideCredit.Id), [nameof(GuideCredit.PersonId)], [nameof(GuideCredit.MovieId)]);
        });
        var actor = Graph.Node<GuidePerson>(); var movie = Graph.Node<GuideMovie>();
        var name = "Keanu Reeves";
        // docs/features/FLUENT_CYPHER.md, "Returning whole entities".
        Assert.Equal("MATCH (n0:`Person`)-[r2:`ACTED_IN`]->(n1:`Movie`)\nWHERE n0.`Name` = $p0\nRETURN n1 {.`Id`, .`Title`, .`Year`} AS c0\nORDER BY c0.`Year` ASC\nLIMIT $p1",
            db.Cypher().Match(actor.Out<GuideCredit>(movie)).Where(() => actor.Value.Name == name)
                .Return(() => movie.Value).OrderBy(() => movie.Value.Year).Limit(100).ToQueryString().Replace("\r", ""));
    }

    private static IReadOnlyDictionary<string, object> Row(Dictionary<string, object> entity) => new Dictionary<string, object> { ["c0"] = entity };
    private static string? Code(Action action) => Assert.Throws<FluentCypherException>(action).Code;

    private static ProviderModelContext AssociationContext() => new(ProviderTestDriver.Create(), b =>
    {
        b.Entity<Person>().ToGraphNode("EntityPerson").HasKey(p => p.Handle);
        b.Entity<Person>().Property(p => p.Handle).HasGraphPropertyName("person_key");
        b.Entity<Film>().ToGraphNode("EntityFilm").Property(f => f.Id).HasGraphPropertyName("film_key");
        var rating = b.Entity<Rating>();
        rating.HasKey(r => new { r.PersonId, r.FilmId });
        rating.HasOne<Person>().WithMany().HasForeignKey(r => r.PersonId);
        rating.HasOne<Film>().WithMany().HasForeignKey(r => r.FilmId);
        rating.Property(r => r.Stars).HasGraphPropertyName("stars");
        rating.ToGraphRelationship("RATED", [nameof(Rating.PersonId)], [nameof(Rating.FilmId)]);
    });

    private static ProviderModelContext ReferenceContext() => new(ProviderTestDriver.Create(), b =>
    {
        b.Entity<Shelf>().ToGraphNode("EntityShelf").Property(s => s.Id).HasGraphPropertyName("shelf_key");
        b.Entity<Book>().ToGraphNode("EntityBook").Property(x => x.Id).ValueGeneratedNever();
        b.Entity<Book>().HasOne<Shelf>().WithMany().HasForeignKey(x => x.ShelfId).IsRequired()
            .OnDelete(DeleteBehavior.Restrict).ToGraphReference("ON_SHELF");
    });

    public sealed class GuidePerson { public int Id { get; set; } public string Name { get; set; } = ""; }
    public sealed class GuideMovie { public int Id { get; set; } public string Title { get; set; } = ""; public int Year { get; set; } }
    public sealed class GuideCredit { public int Id { get; set; } public int PersonId { get; set; } public int MovieId { get; set; } }
    public sealed class Person { public string Handle { get; set; } = ""; }
    public sealed class Film { public Guid Id { get; set; } }
    public sealed class Rating { public string PersonId { get; set; } = ""; public Guid FilmId { get; set; } public int Stars { get; set; } }
    public sealed class Shelf { public Guid Id { get; set; } }
    public sealed class Book { public int Id { get; set; } public Guid ShelfId { get; set; } public string Title { get; set; } = ""; }
}
