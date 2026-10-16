// Copied into an isolated PackageReference-only application by Test-ProviderPackage.ps1.
using Microsoft.EntityFrameworkCore;
using Neo4j.Driver;
using Graph = GraphBridge.EntityFrameworkCore.FluentCypher.Cypher;

static string Required(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
    ? value : throw new InvalidOperationException("Missing owned-fixture environment: " + name);
static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

Require(Required("GRAPHBRIDGE_INTEGRATION") == "1", "Explicit integration opt-in required.");
Require(Required("GRAPHBRIDGE_NEO4J_URI") == "bolt://127.0.0.1:17687"
    && Required("GRAPHBRIDGE_NEO4J_DATABASE") == "neo4j", "Only the owned local fixture is accepted.");
var fixtureId = Required("GRAPHBRIDGE_FIXTURE_ID");
Require(Guid.TryParseExact(fixtureId, "N", out _), "Invalid fixture marker.");
await using var driver = GraphDatabase.Driver(Required("GRAPHBRIDGE_NEO4J_URI"),
    AuthTokens.Basic(Required("GRAPHBRIDGE_NEO4J_USERNAME"), Required("GRAPHBRIDGE_NEO4J_PASSWORD")));
await using (var setup = driver.AsyncSession(b => b.WithDatabase("neo4j")))
{
    var marker = await setup.RunAsync("MATCH (f:GraphBridgeFixture {id: $id}) RETURN count(f) AS count", new { id = fixtureId });
    Require((await marker.SingleAsync())["count"].As<long>() == 1, "Owned fixture marker missing; refusing writes.");
    var components = await setup.RunAsync("CALL dbms.components() YIELD name, versions, edition WHERE name = 'Neo4j Kernel' RETURN versions[0] AS version, edition");
    var component = await components.SingleAsync();
    Require(component["version"].As<string>() == "2026.09.0" && component["edition"].As<string>() == "community",
        "Package smoke requires the pinned Community server.");
    // Explicit disposable setup, not a provider side effect. No application data is removed.
    await (await setup.RunAsync("CREATE CONSTRAINT graphbridge_package_smoke_id IF NOT EXISTS FOR (n:GraphBridgePackageSmoke) REQUIRE n.id IS UNIQUE")).ConsumeAsync();
}

var id = Guid.NewGuid();
Exception? primaryFailure = null;
try
{
    await using (var create = new SmokeContext(driver))
    {
        create.Items.Add(new SmokeItem { Id = id, Name = "package-created", Score = 10 });
        Require(await create.SaveChangesAsync() == 1, "Expected one created EF entry.");
    }
    await using (var edit = new SmokeContext(driver))
    {
        var query = edit.Items.Where(item => item.Id == id && item.Score >= 10);
        Console.WriteLine(query.ToQueryString());
        Require(!query.ToQueryString().Contains(id.ToString(), StringComparison.Ordinal), "Query inspection leaked an application ID.");
        var item = await query.SingleAsync();
        Require(item.Name == "package-created" && item.Version == 1, "Fresh-context create did not round-trip.");
        item.Name = "package-updated";
        Require(await edit.SaveChangesAsync() == 1 && item.Version == 2, "Update/version failed.");
    }
    await using (var fluent = new SmokeContext(driver))
    {
        var item = Graph.Node<SmokeItem>();
        var query = fluent.Cypher().Match(item).Where(item.Property(p => p.Id).Eq(id))
            .Return(() => new { item.Value.Name, item.Value.Score }).Limit(2);
        var rows = await query.ToListAsync();
        Require(rows.Count == 1 && rows[0].Name == "package-updated" && rows[0].Score == 10,
            "Package-only fluent result did not match the committed EF write.");
        Require(!fluent.ChangeTracker.Entries().Any() && fluent.Database.CurrentTransaction is null,
            "Fluent package consumer retained EF state or an owned transaction.");
        Console.WriteLine("Package-only fluent native DTO execution verified.");
    }
    await using (var pending = new SmokeContext(driver))
    await using (var transaction = await pending.Database.BeginTransactionAsync())
    {
        var item = await pending.Items.Where(item => item.Id == id).SingleAsync();
        item.Name = "rolled-back";
        Require(await pending.SaveChangesAsync() == 1, "Transactional write failed.");
        await transaction.RollbackAsync();
    }
    await using (var delete = new SmokeContext(driver))
    {
        var item = await delete.Items.Where(item => item.Id == id).SingleAsync();
        Require(item.Name == "package-updated" && item.Version == 2, "Rollback did not preserve the committed update.");
        delete.Remove(item);
        Require(await delete.SaveChangesAsync() == 1, "Expected one deleted EF entry.");
    }
    await using (var absent = new SmokeContext(driver))
        Require(!await absent.Items.AnyAsync(item => item.Id == id), "Deleted node remains.");
    Console.WriteLine("Package-only live CRUD verified: create, parameterized fresh read, update/version, explicit rollback, delete and absence.");
}
catch (Exception error) { primaryFailure = error; throw; }
finally
{
    // Own random ID only, plain DELETE. Constraint survives until this disposable fixture is removed.
    try
    {
        await using var cleanup = driver.AsyncSession(b => b.WithDatabase("neo4j"));
        await (await cleanup.RunAsync("MATCH (n:GraphBridgePackageSmoke {id: $id}) DELETE n", new { id = id.ToString("D") })).ConsumeAsync();
    }
    catch (Exception cleanupError) when (primaryFailure is not null)
    {
        primaryFailure.Data["GraphBridge.PackageSmokeCleanupError"] = cleanupError;
    }
}

public sealed class SmokeItem
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public int Score { get; set; }
    public long Version { get; set; }
}

public sealed class SmokeContext(IDriver driver)
    : DbContext(new DbContextOptionsBuilder<SmokeContext>().UseNeo4j(driver, "neo4j").Options)
{
    public DbSet<SmokeItem> Items => Set<SmokeItem>();
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var item = modelBuilder.Entity<SmokeItem>().ToGraphNode("GraphBridgePackageSmoke");
        item.HasKey(value => value.Id);
        item.Property(value => value.Id).HasGraphPropertyName("id").ValueGeneratedNever();
        item.Property(value => value.Name).HasGraphPropertyName("name").IsRequired();
        item.Property(value => value.Score).HasGraphPropertyName("score");
        item.Property(value => value.Version).IsRowVersion();
    }
}
