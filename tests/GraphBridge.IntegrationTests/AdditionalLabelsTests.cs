using GraphBridge.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Neo4j.Driver;

namespace GraphBridge.IntegrationTests;

/// <summary>
/// ADR 0055 (GR-40) live: a type's nodes carry its label and its additional labels; a node with the label but without them is
/// outside the type, for reads, keyed and bulk writes alike; an upsert by key gives it the labels.
/// </summary>
[Collection(Neo4jIntegrationCollection.Name)]
public sealed class AdditionalLabelsTests(DisposableNeo4jFixture fixture)
{
    [Neo4jIntegrationFact]
    public async Task Writes_carry_every_label_and_reads_and_writes_skip_a_node_without_them()
    {
        var run = await SetupAsync();
        try
        {
            await using (var db = Context())
            {
                var (ada, nasa) = (new LabelPerson { Id = run + "-ada", Run = run, Name = "Ada" }, new LabelOrganization { Id = run + "-nasa", Run = run, Name = "NASA" });
                db.AddRange(ada, nasa, new LabelWorksFor { PersonId = ada.Id, OrganizationId = nasa.Id, Since = 2024 });
                await db.SaveChangesAsync();
            }
            Assert.Equal(["ALPerson", "__Entity__", "__KGBuilder__"], await LabelsAsync(run + "-ada"));
            Assert.Equal(["ALOrganization", "__Entity__"], await LabelsAsync(run + "-nasa"));

            // A node with the type's label and a key, written by another tool without the additional labels.
            await ExecuteAsync("CREATE (:ALPerson {Id: $id, Run: $run, Name: 'Bare'})", new { id = run + "-bare", run });
            await using (var db = Context())
            {
                Assert.Equal(["Ada"], await db.People.Where(p => p.Run == run).Select(p => p.Name).ToListAsync());
                Assert.Equal(1, await db.People.CountAsync(p => p.Run == run));
                Assert.Null(await db.People.SingleOrDefaultAsync(p => p.Id == run + "-bare"));
                var job = Assert.Single(await db.Set<LabelWorksFor>().Where(w => w.PersonId == run + "-ada").ToListAsync());
                Assert.Equal((run + "-nasa", 2024), (job.OrganizationId, job.Since));

                // Bulk writes keep to the type too.
                Assert.Equal(1, await db.People.Where(p => p.Run == run).ExecuteUpdateAsync(s => s.SetProperty(p => p.Name, "Updated")));
            }
            Assert.Equal("Bare", await NameAsync(run + "-bare"));
            Assert.Equal("Updated", await NameAsync(run + "-ada"));
        }
        finally { await CleanupAsync(run); }
    }

    [Neo4jIntegrationFact]
    public async Task An_upsert_by_key_gives_a_bare_node_its_labels_and_then_it_is_read()
    {
        var run = await SetupAsync();
        try
        {
            await ExecuteAsync("CREATE (:ALPerson {Id: $id, Run: $run, Name: 'Bare'})", new { id = run + "-bare", run });
            await using var db = Context();
            var result = await db.People.ExecuteUpsertAsync([new LabelPerson { Id = run + "-bare", Run = run, Name = "Grace" }, new LabelPerson { Id = run + "-new", Run = run, Name = "New" }]);
            Assert.Equal((1, 1), (result.Created, result.Matched));
            Assert.Equal(["ALPerson", "__Entity__", "__KGBuilder__"], await LabelsAsync(run + "-bare"));
            Assert.Equal(["ALPerson", "__Entity__", "__KGBuilder__"], await LabelsAsync(run + "-new"));
            Assert.Equal(["Grace", "New"], await db.People.Where(p => p.Run == run).OrderBy(p => p.Name).Select(p => p.Name).ToListAsync());
        }
        finally { await CleanupAsync(run); }
    }

    [Neo4jIntegrationFact]
    public async Task A_tracked_node_that_lost_a_label_is_not_updated_and_the_save_fails()
    {
        var run = await SetupAsync();
        try
        {
            await using var db = Context();
            var ada = new LabelPerson { Id = run + "-ada", Run = run, Name = "Ada" };
            db.Add(ada);
            await db.SaveChangesAsync();
            await ExecuteAsync("MATCH (n:ALPerson {Id: $id}) REMOVE n:__Entity__", new { id = ada.Id });
            ada.Name = "Changed";
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => db.SaveChangesAsync());
            Assert.Equal("Ada", await NameAsync(ada.Id));
        }
        finally { await CleanupAsync(run); }
    }

    [Neo4jIntegrationFact]
    public async Task Deletes_and_a_relationship_write_skip_or_refuse_a_node_that_lacks_a_label()
    {
        // GR-69: the rest of ADR 0055's matched writes, live.
        var run = await SetupAsync();
        try
        {
            await ExecuteAsync("CREATE (:ALPerson {Id: $id, Run: $run, Name: 'Bare'})", new { id = run + "-bare", run });
            await using (var db = Context())
            {
                db.AddRange(new LabelPerson { Id = run + "-ada", Run = run, Name = "Ada" }, new LabelPerson { Id = run + "-grace", Run = run, Name = "Grace" },
                    new LabelOrganization { Id = run + "-nasa", Run = run, Name = "NASA" });
                await db.SaveChangesAsync();
                // A bulk delete keeps to the type: the bare node stays.
                Assert.Equal(1, await db.People.Where(p => p.Id == run + "-ada" || p.Id == run + "-bare").ExecuteDeleteAsync());
            }
            Assert.Equal("Bare", await NameAsync(run + "-bare"));
            await using (var session = fixture.OpenSession())
                Assert.Equal(0L, (await (await session.RunAsync("MATCH (n:ALPerson {Id: $id}) RETURN count(n) AS c", new { id = run + "-ada" })).SingleAsync())["c"].As<long>());

            // A relationship to an endpoint that lost a label is refused at that endpoint's lock.
            await ExecuteAsync("MATCH (n:ALOrganization {Id: $id}) REMOVE n:__Entity__", new { id = run + "-nasa" });
            await using (var db = Context())
            {
                db.Add(new LabelWorksFor { PersonId = run + "-grace", OrganizationId = run + "-nasa", Since = 2025 });
                var error = await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => db.SaveChangesAsync());
                Assert.Equal("lock 2 of 2", (string?)error.Data["GraphBridge.MutationStep"]);
            }

            // A tracked delete of a node that lost a label fails, and the node stays.
            await using (var db = Context())
            {
                var grace = await db.People.SingleAsync(p => p.Id == run + "-grace");
                await ExecuteAsync("MATCH (n:ALPerson {Id: $id}) REMOVE n:__KGBuilder__", new { id = grace.Id });
                db.Remove(grace);
                await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => db.SaveChangesAsync());
            }
            Assert.Equal("Grace", await NameAsync(run + "-grace"));
        }
        finally { await CleanupAsync(run); }
    }

    [Neo4jIntegrationFact]
    public async Task Through_a_native_reference_or_an_Include_a_node_without_its_labels_fails_the_read_instead_of_being_skipped()
    {
        // Skipping it there would hide a reference or a child; the read fails closed (ADR 0055, section 3).
        await ExecuteAsync("CREATE CONSTRAINT al_shelf_key IF NOT EXISTS FOR (n:ALShelf) REQUIRE n.Id IS UNIQUE");
        await ExecuteAsync("CREATE CONSTRAINT al_book_key IF NOT EXISTS FOR (n:ALBook) REQUIRE n.Id IS UNIQUE");
        var run = "al_" + Guid.NewGuid().ToString("N")[..12];
        await using var db = new ShelfContext(new DbContextOptionsBuilder<ShelfContext>().UseNeo4j(fixture.Driver, fixture.Database).Options);
        try
        {
            var shelf = new LabelShelf { Id = run + "-s1", Run = run };
            shelf.Books.Add(new LabelBook { Id = run + "-b1", Run = run, Title = "Labelled" });
            db.Add(shelf);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            // A book whose shelf lacks the shelf's additional label.
            await ExecuteAsync("CREATE (:ALShelf {Id: $s, Run: $run})-[:AL_ON_SHELF]->(:ALBook:__Entity__ {Id: $b, Run: $run, Title: 'On a bare shelf'})",
                new { s = run + "-s2", b = run + "-b2", run });
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.Books.Where(b => b.Run == run).ToListAsync());
            Assert.Equal(["Labelled"], await db.Books.Where(b => b.Id == run + "-b1").Select(b => b.Title).ToListAsync());
            await ExecuteAsync("MATCH (s:ALShelf {Id: $s})-[r:AL_ON_SHELF]->(b) DELETE r, s, b", new { s = run + "-s2" });

            // A book without its own additional label, on a labelled shelf: skipped by a query of books, fatal to an Include.
            await ExecuteAsync("MATCH (s:ALShelf {Id: $s}) CREATE (s)-[:AL_ON_SHELF]->(:ALBook {Id: $b, Run: $run, Title: 'Bare'})",
                new { s = shelf.Id, b = run + "-b3", run });
            Assert.Equal(["Labelled"], await db.Books.Where(b => b.Run == run).Select(b => b.Title).ToListAsync());
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.Shelves.Include(s => s.Books).Where(s => s.Id == shelf.Id).ToListAsync());
        }
        finally
        {
            await ExecuteAsync("MATCH (s:ALShelf {Run: $run})-[r:AL_ON_SHELF]->() DELETE r", new { run });
            await ExecuteAsync("MATCH (n) WHERE (n:ALShelf OR n:ALBook) AND n.Run = $run DELETE n", new { run });
        }
    }

    private LabelContext Context() => new(new DbContextOptionsBuilder<LabelContext>().UseNeo4j(fixture.Driver, fixture.Database).Options);

    private async Task<string> SetupAsync()
    {
        await ExecuteAsync("CREATE CONSTRAINT al_person_key IF NOT EXISTS FOR (n:ALPerson) REQUIRE n.Id IS UNIQUE");
        await ExecuteAsync("CREATE CONSTRAINT al_organization_key IF NOT EXISTS FOR (n:ALOrganization) REQUIRE n.Id IS UNIQUE");
        return "al_" + Guid.NewGuid().ToString("N")[..12];
    }

    private async Task<string[]> LabelsAsync(string id)
    {
        await using var session = fixture.OpenSession();
        var cursor = await session.RunAsync("MATCH (n {Id: $id}) RETURN labels(n) AS labels", new { id });
        return [.. (await cursor.SingleAsync())["labels"].As<List<string>>().Order(StringComparer.Ordinal)];
    }

    private async Task<string> NameAsync(string id)
    {
        await using var session = fixture.OpenSession();
        var cursor = await session.RunAsync("MATCH (n:ALPerson {Id: $id}) RETURN n.Name AS name", new { id });
        return (await cursor.SingleAsync())["name"].As<string>();
    }

    private async Task ExecuteAsync(string text, object? parameters = null)
    {
        await using var session = fixture.OpenSession();
        await (parameters is null ? await session.RunAsync(text) : await session.RunAsync(text, parameters)).ConsumeAsync();
    }

    // Relationships first, then the nodes; by run, never DETACH DELETE.
    private async Task CleanupAsync(string run)
    {
        await ExecuteAsync("MATCH (:ALPerson {Run: $run})-[r:AL_WORKS_FOR]->() DELETE r", new { run });
        await ExecuteAsync("MATCH (n) WHERE (n:ALPerson OR n:ALOrganization) AND n.Run = $run DELETE n", new { run });
    }
}

public sealed class LabelPerson
{
    public string Id { get; set; } = "";
    public string Run { get; set; } = "";
    public string Name { get; set; } = "";
}

public sealed class LabelOrganization
{
    public string Id { get; set; } = "";
    public string Run { get; set; } = "";
    public string Name { get; set; } = "";
}

public sealed class LabelWorksFor
{
    public string PersonId { get; set; } = "";
    public string OrganizationId { get; set; } = "";
    public int Since { get; set; }
}

public sealed class LabelShelf
{
    public string Id { get; set; } = "";
    public string Run { get; set; } = "";
    public List<LabelBook> Books { get; } = [];
}

public sealed class LabelBook
{
    public string Id { get; set; } = "";
    public string Run { get; set; } = "";
    public string Title { get; set; } = "";
    public string ShelfId { get; set; } = "";
    public LabelShelf Shelf { get; set; } = null!;
}

/// <summary>A native reference between two types with an additional label.</summary>
public sealed class ShelfContext(DbContextOptions<ShelfContext> options) : DbContext(options)
{
    public DbSet<LabelShelf> Shelves => Set<LabelShelf>();
    public DbSet<LabelBook> Books => Set<LabelBook>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<LabelShelf>().ToGraphNode("ALShelf").HasAdditionalGraphLabels("__Entity__").Property(s => s.Id).ValueGeneratedNever();
        model.Entity<LabelBook>().ToGraphNode("ALBook").HasAdditionalGraphLabels("__Entity__").Property(b => b.Id).ValueGeneratedNever();
        model.Entity<LabelBook>().HasOne(b => b.Shelf).WithMany(s => s.Books).HasForeignKey(b => b.ShelfId)
            .OnDelete(DeleteBehavior.Restrict).ToGraphReference("AL_ON_SHELF");
    }
}

/// <summary>neo4j-graphrag's entity labels on two types (ADR 0055); Doctor's label check uses it too.</summary>
public sealed class LabelContext(DbContextOptions<LabelContext> options) : DbContext(options)
{
    public DbSet<LabelPerson> People => Set<LabelPerson>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<LabelPerson>().ToGraphNode("ALPerson").HasAdditionalGraphLabels("__Entity__", "__KGBuilder__").Property(p => p.Id).ValueGeneratedNever();
        model.Entity<LabelOrganization>().ToGraphNode("ALOrganization").HasAdditionalGraphLabels("__Entity__").Property(o => o.Id).ValueGeneratedNever();
        var job = model.Entity<LabelWorksFor>();
        job.HasKey(w => new { w.PersonId, w.OrganizationId });
        job.HasOne<LabelPerson>().WithMany().HasForeignKey(w => w.PersonId);
        job.HasOne<LabelOrganization>().WithMany().HasForeignKey(w => w.OrganizationId);
        job.ToGraphRelationship("AL_WORKS_FOR", [nameof(LabelWorksFor.PersonId)], [nameof(LabelWorksFor.OrganizationId)]);
    }
}
