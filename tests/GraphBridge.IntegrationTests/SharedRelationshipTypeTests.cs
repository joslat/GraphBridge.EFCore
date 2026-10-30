using GraphBridge.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Neo4j.Driver;

namespace GraphBridge.IntegrationTests;

/// <summary>
/// ADR 0056 (GR-62) live: two identified-relationship owners share one type, as neo4j-graphrag's FROM_CHUNK links every
/// entity to its chunk. Each owner writes, reads, updates and deletes only its own relationships; keys are unique across both.
/// </summary>
[Collection(Neo4jIntegrationCollection.Name)]
public sealed class SharedRelationshipTypeTests(DisposableNeo4jFixture fixture)
{
    [Neo4jIntegrationFact]
    public async Task Owners_of_one_type_each_see_and_change_only_their_own_relationships()
    {
        var run = await SetupAsync();
        try
        {
            var (ada, nasa, chunk) = (new SrPerson { Id = run + "-ada", Run = run }, new SrOrganization { Id = run + "-nasa", Run = run }, new SrChunk { Id = run + "-c1", Run = run });
            var (person, organization) = (new SrPersonMention { Id = run + "-m1", PersonId = ada.Id, ChunkId = chunk.Id, Quote = "Ada" },
                new SrOrganizationMention { Id = run + "-m2", OrganizationId = nasa.Id, ChunkId = chunk.Id, Quote = "NASA" });
            await using (var db = Context())
            {
                db.AddRange(ada, nasa, chunk, person, organization);
                await db.SaveChangesAsync();
            }
            Assert.Equal(2L, await CountAsync(run));

            await using (var db = Context())
            {
                Assert.Equal(["Ada"], await db.Set<SrPersonMention>().Where(m => m.ChunkId == chunk.Id).Select(m => m.Quote).ToListAsync());
                Assert.Equal(["NASA"], await db.Set<SrOrganizationMention>().Where(m => m.ChunkId == chunk.Id).Select(m => m.Quote).ToListAsync());
                Assert.Null(await db.Set<SrPersonMention>().SingleOrDefaultAsync(m => m.Id == run + "-m2"));

                // A keyed update and delete of one owner leave the other's relationship alone.
                var tracked = await db.Set<SrPersonMention>().SingleAsync(m => m.Id == person.Id);
                tracked.Quote = "Ada Lovelace";
                db.Remove(await db.Set<SrOrganizationMention>().SingleAsync(m => m.Id == organization.Id));
                await db.SaveChangesAsync();
            }
            Assert.Equal(1L, await CountAsync(run));
            Assert.Equal("Ada Lovelace", await QuoteAsync(person.Id));

            await using (var db = Context())
            {
                // Upserts: each owner merges between its own endpoints; a key the other owner holds is refused.
                var result = await db.Set<SrOrganizationMention>().ExecuteUpsertAsync([organization]);
                Assert.Equal((1, 0), (result.Created, result.Matched));
                var taken = new SrOrganizationMention { Id = person.Id, OrganizationId = nasa.Id, ChunkId = chunk.Id, Quote = "taken" };
                await Assert.ThrowsAsync<InvalidOperationException>(() => db.Set<SrOrganizationMention>().ExecuteUpsertAsync([taken]));
            }
            Assert.Equal(2L, await CountAsync(run));
            Assert.Equal("Ada Lovelace", await QuoteAsync(person.Id));
        }
        finally { await CleanupAsync(run); }
    }

    [Neo4jIntegrationFact]
    public async Task A_key_the_other_owner_holds_is_refused_by_the_constraint_and_by_the_endpoint_check()
    {
        var run = await SetupAsync();
        try
        {
            var (ada, nasa, chunk) = (new SrPerson { Id = run + "-ada", Run = run }, new SrOrganization { Id = run + "-nasa", Run = run }, new SrChunk { Id = run + "-c1", Run = run });
            var person = new SrPersonMention { Id = run + "-m1", PersonId = ada.Id, ChunkId = chunk.Id, Quote = "Ada" };
            await using (var db = Context())
            {
                db.AddRange(ada, nasa, chunk, person);
                await db.SaveChangesAsync();
            }

            // Adding the organization's mention under the person's key: the type's one uniqueness constraint refuses it.
            await using (var db = Context())
            {
                db.Add(new SrOrganizationMention { Id = person.Id, OrganizationId = nasa.Id, ChunkId = chunk.Id, Quote = "taken" });
                await Assert.ThrowsAnyAsync<DbUpdateException>(() => db.SaveChangesAsync());
            }

            // A keyed update through the other owner finds the relationship by key, but its endpoints aren't that owner's.
            await using (var db = Context())
            {
                var wrong = new SrOrganizationMention { Id = person.Id, OrganizationId = nasa.Id, ChunkId = chunk.Id, Quote = "Ada" };
                db.Attach(wrong);
                wrong.Quote = "overwritten";
                await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => db.SaveChangesAsync());
            }
            Assert.Equal(1L, await CountAsync(run));
            Assert.Equal("Ada", await QuoteAsync(person.Id));
        }
        finally { await CleanupAsync(run); }
    }

    private SharedTypeContext Context() => new(new DbContextOptionsBuilder<SharedTypeContext>().UseNeo4j(fixture.Driver, fixture.Database).Options);

    private async Task<string> SetupAsync()
    {
        foreach (var (name, label) in new[] { ("sr_person_key", "SRPerson"), ("sr_organization_key", "SROrganization"), ("sr_chunk_key", "SRChunk") })
            await ExecuteAsync($"CREATE CONSTRAINT {name} IF NOT EXISTS FOR (n:{label}) REQUIRE n.Id IS UNIQUE");
        await ExecuteAsync("CREATE CONSTRAINT sr_from_chunk_id IF NOT EXISTS FOR ()-[r:SR_FROM_CHUNK]-() REQUIRE r.id IS UNIQUE");
        return "sr_" + Guid.NewGuid().ToString("N")[..12];
    }

    private async Task<long> CountAsync(string run)
    {
        await using var session = fixture.OpenSession();
        var cursor = await session.RunAsync("MATCH (:SRChunk {Run: $run})<-[r:SR_FROM_CHUNK]-() RETURN count(r) AS c", new { run });
        return (await cursor.SingleAsync())["c"].As<long>();
    }

    private async Task<string> QuoteAsync(string id)
    {
        await using var session = fixture.OpenSession();
        var cursor = await session.RunAsync("MATCH ()-[r:SR_FROM_CHUNK {id: $id}]->() RETURN r.Quote AS quote", new { id });
        return (await cursor.SingleAsync())["quote"].As<string>();
    }

    private async Task ExecuteAsync(string text, object? parameters = null)
    {
        await using var session = fixture.OpenSession();
        await (parameters is null ? await session.RunAsync(text) : await session.RunAsync(text, parameters)).ConsumeAsync();
    }

    // Relationships first, then nodes; by run, never DETACH DELETE.
    private async Task CleanupAsync(string run)
    {
        await ExecuteAsync("MATCH (:SRChunk {Run: $run})<-[r:SR_FROM_CHUNK]-() DELETE r", new { run });
        await ExecuteAsync("MATCH (n) WHERE (n:SRPerson OR n:SROrganization OR n:SRChunk) AND n.Run = $run DELETE n", new { run });
    }
}

public sealed class SrPerson { public string Id { get; set; } = ""; public string Run { get; set; } = ""; }
public sealed class SrOrganization { public string Id { get; set; } = ""; public string Run { get; set; } = ""; }
public sealed class SrChunk { public string Id { get; set; } = ""; public string Run { get; set; } = ""; }

public sealed class SrPersonMention
{
    public string Id { get; set; } = "";
    public string PersonId { get; set; } = "";
    public string ChunkId { get; set; } = "";
    public string Quote { get; set; } = "";
}

public sealed class SrOrganizationMention
{
    public string Id { get; set; } = "";
    public string OrganizationId { get; set; } = "";
    public string ChunkId { get; set; } = "";
    public string Quote { get; set; } = "";
}

/// <summary>neo4j-graphrag's shape: entities with __Entity__, linked to their chunk by one shared type (ADR 0055, 0056).</summary>
public sealed class SharedTypeContext(DbContextOptions<SharedTypeContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<SrPerson>().ToGraphNode("SRPerson").HasAdditionalGraphLabels("__Entity__").Property(p => p.Id).ValueGeneratedNever();
        model.Entity<SrOrganization>().ToGraphNode("SROrganization").HasAdditionalGraphLabels("__Entity__").Property(o => o.Id).ValueGeneratedNever();
        model.Entity<SrChunk>().ToGraphNode("SRChunk").Property(c => c.Id).ValueGeneratedNever();

        var person = model.Entity<SrPersonMention>();
        person.Property(m => m.Id).HasGraphPropertyName("id").ValueGeneratedNever();
        person.HasOne<SrPerson>().WithMany().HasForeignKey(m => m.PersonId);
        person.HasOne<SrChunk>().WithMany().HasForeignKey(m => m.ChunkId);
        person.ToGraphIdentifiedRelationship("SR_FROM_CHUNK", nameof(SrPersonMention.Id), [nameof(SrPersonMention.PersonId)], [nameof(SrPersonMention.ChunkId)]);

        var organization = model.Entity<SrOrganizationMention>();
        organization.Property(m => m.Id).HasGraphPropertyName("id").ValueGeneratedNever();
        organization.HasOne<SrOrganization>().WithMany().HasForeignKey(m => m.OrganizationId);
        organization.HasOne<SrChunk>().WithMany().HasForeignKey(m => m.ChunkId);
        organization.ToGraphIdentifiedRelationship("SR_FROM_CHUNK", nameof(SrOrganizationMention.Id), [nameof(SrOrganizationMention.OrganizationId)], [nameof(SrOrganizationMention.ChunkId)]);
    }
}
