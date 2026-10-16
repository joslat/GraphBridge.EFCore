using Microsoft.EntityFrameworkCore;
using Neo4j.Driver;

internal static class CombinedLiveProbe
{
    internal static async Task RunAsync()
    {
        static string Required(string key) => Environment.GetEnvironmentVariable(key) is { Length: > 0 } v
            ? v : throw new InvalidOperationException("Missing owned fixture variable: " + key);
        if (Required("GRAPHBRIDGE_INTEGRATION") != "1"
            || Required("GRAPHBRIDGE_NEO4J_URI") != "bolt://127.0.0.1:17687"
            || Required("GRAPHBRIDGE_NEO4J_DATABASE") != "neo4j"
            || !Guid.TryParseExact(Required("GRAPHBRIDGE_FIXTURE_ID"), "N", out _))
            throw new InvalidOperationException("Only the explicitly owned local fixture is accepted.");
        await using var driver = GraphDatabase.Driver(Required("GRAPHBRIDGE_NEO4J_URI"),
            AuthTokens.Basic(Required("GRAPHBRIDGE_NEO4J_USERNAME"), Required("GRAPHBRIDGE_NEO4J_PASSWORD")));
        await using var native = driver.AsyncSession(b => b.WithDatabase("neo4j"));
        var marker = await native.RunAsync("MATCH (f:GraphBridgeFixture {id:$id}) RETURN count(f) AS count",
            new { id = Required("GRAPHBRIDGE_FIXTURE_ID") });
        if ((await marker.SingleAsync())["count"].As<long>() != 1)
            throw new InvalidOperationException("Fixture marker absent; refusing writes.");
        var server = await native.RunAsync("CALL dbms.components() YIELD name, versions, edition WHERE name='Neo4j Kernel' RETURN versions[0] AS version,edition");
        var component = await server.SingleAsync();
        if (component["version"].As<string>() != "2026.09.0" || component["edition"].As<string>() != "community")
            throw new InvalidOperationException("Unexpected fixture server profile.");
        await (await native.RunAsync("CREATE CONSTRAINT graphbridge_combined_package_id IF NOT EXISTS FOR (n:CombinedPackageItem) REQUIRE n.id IS UNIQUE")).ConsumeAsync();
        var id = Guid.NewGuid();
        Exception? primary = null;
        try
        {
            await using (var db = new CombinedContext(driver, "neo4j"))
            {
                db.Add(new Item { Id = id, Name = "combined-created" });
                if (await db.SaveChangesAsync() != 1) throw new InvalidOperationException("Generated mapping insert failed.");
            }
            var control = await native.RunAsync("MATCH (n:CombinedPackageItem {id:$id}) RETURN n.name AS name", new { id = id.ToString("D") });
            if ((await control.SingleAsync())["name"].As<string>() != "combined-created")
                throw new InvalidOperationException("Native control did not observe the generated-mapping write.");
            await using (var db = new CombinedContext(driver, "neo4j"))
            await using (var tx = await db.Database.BeginTransactionAsync())
            {
                var item = await db.Set<Item>().SingleAsync(x => x.Id == id);
                item.Name = "rolled-back";
                if (await db.SaveChangesAsync() != 1) throw new InvalidOperationException("Generated mapping update failed.");
                await tx.RollbackAsync();
            }
            await using (var db = new CombinedContext(driver, "neo4j"))
            {
                var item = await db.Set<Item>().SingleAsync(x => x.Id == id);
                if (item.Name != "combined-created") throw new InvalidOperationException("Rollback did not retain committed state.");
                db.Remove(item);
                if (await db.SaveChangesAsync() != 1 || await db.Set<Item>().AnyAsync(x => x.Id == id))
                    throw new InvalidOperationException("Generated mapping deletion failed.");
            }
        }
        catch (Exception failure) { primary = failure; throw; }
        finally
        {
            try { await (await native.RunAsync("MATCH (n:CombinedPackageItem {id:$id}) DELETE n", new { id = id.ToString("D") })).ConsumeAsync(); }
            catch (Exception cleanup) when (primary is not null) { primary.Data["CombinedCleanupFailure"] = cleanup; }
        }
    }
}
