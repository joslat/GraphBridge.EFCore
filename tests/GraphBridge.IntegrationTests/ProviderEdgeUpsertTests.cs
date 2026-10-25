using GraphBridge.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Neo4j.Driver;
using Xunit.Abstractions;

namespace GraphBridge.IntegrationTests;

/// <summary>ADR 0051, section 6, live: relationship upserts between existing endpoints, refusals that write nothing, concurrent writers.</summary>
[Collection(Neo4jIntegrationCollection.Name)]
public sealed class ProviderEdgeUpsertTests(DisposableNeo4jFixture fixture, ITestOutputHelper output) : IAsyncLifetime
{
    private readonly string _run = "upe_" + Guid.NewGuid().ToString("N");

    public async Task InitializeAsync()
    {
        if (!Enabled) return;
        await ExecuteAsync("CREATE CONSTRAINT upe_person_key IF NOT EXISTS FOR (n:UPEPerson) REQUIRE n.Id IS UNIQUE");
        await ExecuteAsync("CREATE CONSTRAINT upe_shelf_key IF NOT EXISTS FOR (n:UPEShelf) REQUIRE n.Id IS UNIQUE");
        await ExecuteAsync("CREATE CONSTRAINT upe_visit_key IF NOT EXISTS FOR ()-[r:UPE_VISITED]-() REQUIRE r.Id IS UNIQUE");
    }

    public async Task DisposeAsync()
    {
        if (!Enabled) return;
        await ExecuteAsync("MATCH (n)-[r]-() WHERE (n:UPEPerson OR n:UPEShelf) AND n.Run = $run DELETE r", new { run = _run });
        await ExecuteAsync("MATCH (n) WHERE (n:UPEPerson OR n:UPEShelf) AND n.Run = $run DELETE n", new { run = _run });
    }

    private static bool Enabled => Environment.GetEnvironmentVariable("GRAPHBRIDGE_INTEGRATION") == "1";

    [Neo4jIntegrationFact]
    public async Task Associations_are_created_then_matched_on_their_pair_with_payload_updates()
    {
        var (people, shelves) = await SeedAsync(2, 2);
        await using var db = Context();
        Rating[] ratings = [new() { PersonId = people[0], ShelfId = shelves[0], Stars = 3 }, new() { PersonId = people[1], ShelfId = shelves[1], Stars = 5 }];
        Assert.Equal((2, 0), Counts(await db.Ratings.ExecuteUpsertAsync(ratings)));
        ratings[0].Stars = 4;
        Assert.Equal((0, 2), Counts(await db.Ratings.ExecuteUpsertAsync(ratings)));
        Assert.Equal(2L, await CountAsync("MATCH (:UPEPerson {Run: $run})-[r:UPE_RATED]->(:UPEShelf) RETURN count(r) AS c"));
        Assert.Equal(4L, await CountAsync("MATCH (:UPEPerson {Id: $id})-[r:UPE_RATED]->() RETURN r.Stars AS c", people[0]));

        ratings[0].Stars = 1;
        await db.Ratings.ExecuteUpsertAsync(ratings, r => new { });
        Assert.Equal(4L, await CountAsync("MATCH (:UPEPerson {Id: $id})-[r:UPE_RATED]->() RETURN r.Stars AS c", people[0]));
        // The relationships read back through EF like any association.
        Assert.Equal([4, 5], (await db.Ratings.Where(r => people.Contains(r.PersonId)).Select(r => r.Stars).ToListAsync()).Order());
    }

    [Neo4jIntegrationFact]
    public async Task Identified_edges_match_on_their_key_and_refusals_write_nothing()
    {
        var (people, shelves) = await SeedAsync(2, 2);
        await using var db = Context();
        var visit = new Visit { Id = Guid.NewGuid(), PersonId = people[0], ShelfId = shelves[0], Note = "first" };
        Assert.Equal((1, 0), Counts(await db.Visits.ExecuteUpsertAsync([visit])));
        visit.Note = "second";
        Assert.Equal((0, 1), Counts(await db.Visits.ExecuteUpsertAsync([visit])));
        Assert.Equal("second", (await db.Visits.SingleAsync(v => v.Id == visit.Id)).Note);

        // The same key between other endpoints: an upsert never moves a relationship.
        var moved = new Visit { Id = visit.Id, PersonId = people[1], ShelfId = shelves[1] };
        var fresh = new Visit { Id = Guid.NewGuid(), PersonId = people[1], ShelfId = shelves[1] };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => db.Visits.ExecuteUpsertAsync([fresh, moved]));
        Assert.Contains("Entities 1 (zero-based)", error.Message);
        // A missing endpoint: refused, and the valid row before it isn't written either.
        error = await Assert.ThrowsAsync<InvalidOperationException>(() => db.Visits.ExecuteUpsertAsync([fresh, new Visit { Id = Guid.NewGuid(), PersonId = Guid.NewGuid(), ShelfId = shelves[0] }]));
        Assert.Contains("doesn't exist", error.Message);
        Assert.Equal(1L, await CountAsync("MATCH (:UPEPerson {Run: $run})-[r:UPE_VISITED]->() RETURN count(r) AS c"));
    }

    [Neo4jIntegrationFact]
    public async Task A_pair_that_already_has_two_associations_is_refused()
    {
        var (people, shelves) = await SeedAsync(1, 1);
        await ExecuteAsync("MATCH (p:UPEPerson {Id: $p}), (s:UPEShelf {Id: $s}) CREATE (p)-[:UPE_RATED {Stars: 1}]->(s), (p)-[:UPE_RATED {Stars: 2}]->(s)",
            new { p = people[0].ToString("D"), s = shelves[0].ToString("D") });
        await using var db = Context();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => db.Ratings.ExecuteUpsertAsync([new Rating { PersonId = people[0], ShelfId = shelves[0], Stars = 9 }]));
        Assert.Contains("more than one such relationship", error.Message);
        Assert.Equal(0L, await CountAsync("MATCH (:UPEPerson {Run: $run})-[r:UPE_RATED {Stars: 9}]->() RETURN count(r) AS c"));
    }

    [Neo4jIntegrationFact]
    public async Task Concurrent_upserts_of_the_same_relationships_leave_one_per_key_and_pair()
    {
        var (people, shelves) = await SeedAsync(10, 10);
        var visits = Enumerable.Range(0, 100).Select(i => new Visit { Id = Guid.NewGuid(), PersonId = people[i % 10], ShelfId = shelves[i / 10] }).ToArray();
        var ratings = Enumerable.Range(0, 100).Select(i => new Rating { PersonId = people[i % 10], ShelfId = shelves[i / 10], Stars = i }).ToArray();
        async Task<GraphUpsertResult?> Upsert(Func<EdgeContext, Task<GraphUpsertResult>> call)
        {
            await using var db = Context();
            try { return await call(db); }
            catch (DbUpdateException error)
            {
                output.WriteLine($"One call failed: {(error.InnerException as Neo4jException)?.Code ?? error.Message}");
                return null;
            }
        }
        var results = await Task.WhenAll(
            Upsert(db => db.Visits.ExecuteUpsertAsync(visits)), Upsert(db => db.Visits.ExecuteUpsertAsync(visits)),
            Upsert(db => db.Ratings.ExecuteUpsertAsync(ratings)), Upsert(db => db.Ratings.ExecuteUpsertAsync(ratings)));
        // Either both calls of a kind succeed, or the loser fails and a repeat matches everything.
        if (results[0] is null || results[1] is null) await Upsert(db => db.Visits.ExecuteUpsertAsync(visits));
        if (results[2] is null || results[3] is null) await Upsert(db => db.Ratings.ExecuteUpsertAsync(ratings));
        Assert.Equal(100L, await CountAsync("MATCH (:UPEPerson {Run: $run})-[r:UPE_VISITED]->() RETURN count(r) AS c"));
        Assert.Equal(100L, await CountAsync("MATCH (:UPEPerson {Run: $run})-[r:UPE_RATED]->() RETURN count(r) AS c"));
        Assert.Equal(0L, await CountAsync("MATCH (p:UPEPerson {Run: $run})-[r:UPE_RATED]->(s) WITH p, s, count(r) AS c WHERE c > 1 RETURN count(*) AS c"));
    }

    [Neo4jIntegrationFact]
    public async Task An_identified_edge_without_its_relationship_constraint_is_refused_before_any_write()
    {
        var (people, shelves) = await SeedAsync(1, 1);
        await using var db = new LooseContext(new DbContextOptionsBuilder<LooseContext>().UseNeo4j(fixture.Driver, fixture.Database).Options);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => db.Visits.ExecuteUpsertAsync(
            [new Visit { Id = Guid.NewGuid(), PersonId = people[0], ShelfId = shelves[0] }]));
        Assert.Contains("relationship uniqueness constraint for :UPE_LOOSE", error.Message);
        Assert.Equal(0L, await CountAsync("MATCH (:UPEPerson {Run: $run})-[r:UPE_LOOSE]->() RETURN count(r) AS c"));
    }

    private async Task<(Guid[] People, Guid[] Shelves)> SeedAsync(int people, int shelves)
    {
        await using var db = Context();
        var p = Enumerable.Range(0, people).Select(_ => new Person { Id = Guid.NewGuid(), Run = _run }).ToArray();
        var s = Enumerable.Range(0, shelves).Select(_ => new Shelf { Id = Guid.NewGuid(), Run = _run }).ToArray();
        db.AddRange(p);
        db.AddRange(s);
        await db.SaveChangesAsync();
        return (p.Select(x => x.Id).ToArray(), s.Select(x => x.Id).ToArray());
    }

    private static (int, int) Counts(GraphUpsertResult result) => (result.Created, result.Matched);

    private EdgeContext Context() => new(new DbContextOptionsBuilder<EdgeContext>().UseNeo4j(fixture.Driver, fixture.Database).Options);

    private async Task ExecuteAsync(string text, object? parameters = null)
    {
        await using var session = fixture.OpenSession();
        await (parameters is null ? await session.RunAsync(text) : await session.RunAsync(text, parameters)).ConsumeAsync();
    }

    private async Task<long> CountAsync(string text, Guid? id = null)
    {
        await using var session = fixture.OpenSession();
        var cursor = await session.RunAsync(text, new { run = _run, id = id?.ToString("D") });
        return (await cursor.SingleAsync())["c"].As<long>();
    }

    public sealed class Person { public Guid Id { get; set; } public string Run { get; set; } = ""; }
    public sealed class Shelf { public Guid Id { get; set; } public string Run { get; set; } = ""; }
    public sealed class Rating { public Guid PersonId { get; set; } public Guid ShelfId { get; set; } public int Stars { get; set; } }
    public sealed class Visit { public Guid Id { get; set; } public Guid PersonId { get; set; } public Guid ShelfId { get; set; } public string? Note { get; set; } }

    public sealed class EdgeContext(DbContextOptions<EdgeContext> options) : DbContext(options)
    {
        public DbSet<Rating> Ratings => Set<Rating>();
        public DbSet<Visit> Visits => Set<Visit>();
        protected override void OnModelCreating(ModelBuilder b) => Map(b, "UPE_VISITED");
    }

    // The same model with the identified type renamed to one no constraint covers.
    public sealed class LooseContext(DbContextOptions<LooseContext> options) : DbContext(options)
    {
        public DbSet<Visit> Visits => Set<Visit>();
        protected override void OnModelCreating(ModelBuilder b) => Map(b, "UPE_LOOSE");
    }

    private static void Map(ModelBuilder b, string visitType)
    {
        b.Entity<Person>().ToGraphNode("UPEPerson").Property(p => p.Id).ValueGeneratedNever();
        b.Entity<Shelf>().ToGraphNode("UPEShelf").Property(s => s.Id).ValueGeneratedNever();
        var rating = b.Entity<Rating>();
        rating.HasKey(r => new { r.PersonId, r.ShelfId });
        rating.HasOne<Person>().WithMany().HasForeignKey(r => r.PersonId);
        rating.HasOne<Shelf>().WithMany().HasForeignKey(r => r.ShelfId);
        rating.ToGraphRelationship("UPE_RATED", [nameof(Rating.PersonId)], [nameof(Rating.ShelfId)]);
        var visit = b.Entity<Visit>();
        visit.Property(v => v.Id).ValueGeneratedNever();
        visit.HasOne<Person>().WithMany().HasForeignKey(v => v.PersonId);
        visit.HasOne<Shelf>().WithMany().HasForeignKey(v => v.ShelfId);
        visit.ToGraphIdentifiedRelationship(visitType, nameof(Visit.Id), [nameof(Visit.PersonId)], [nameof(Visit.ShelfId)]);
    }
}
