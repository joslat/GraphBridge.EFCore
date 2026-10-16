using Microsoft.EntityFrameworkCore;
using Neo4j.Driver;

namespace GraphBridge.IntegrationTests;

[Collection(Neo4jIntegrationCollection.Name)]
public sealed class ProviderNativeQueryTests(DisposableNeo4jFixture fixture)
{
    private const string Label = "GraphBridgeNativePerson";
    private const string TargetLabel = "GraphBridgeNativeTarget";
    private const string EdgeType = "GRAPHBRIDGE_NATIVE_PATH";
    private static readonly IReadOnlyDictionary<string, object?> Empty = new Dictionary<string, object?>();

    [Neo4jIntegrationFact]
    public async Task Native_DTO_and_entity_shaped_projection_are_untracked_and_default_diagnostics_are_redacted()
    {
        var logs = new List<string>();
        await using var context = Context(logs.Add);
        var secret = "native_secret_" + Guid.NewGuid().ToString("N") + "' MATCH (n) DELETE n //";
        var text = "RETURN $value AS name, 42 AS number, $optional AS optional";
        var parameters = new Dictionary<string, object?> { ["value"] = secret, ["optional"] = null };

        var dto = Assert.Single(await context.Database.QueryCypherAsync(text, parameters,
            record => new NativeDto(record["name"].As<string>(), record["number"].As<long>(), record["optional"])));
        Assert.Equal(secret, dto.Name);
        Assert.Equal(42L, dto.Number);
        Assert.Null(dto.Optional);
        var person = Assert.Single(await context.Database.QueryCypherAsync("RETURN $value AS name", parameters,
            record => new NativePerson { Id = Guid.NewGuid(), Name = record["name"].As<string>() }));
        Assert.Equal(secret, person.Name);
        Assert.Empty(context.ChangeTracker.Entries());
        Assert.Null(context.Database.CurrentTransaction);
        Assert.DoesNotContain(logs, message => message.Contains(secret, StringComparison.Ordinal));
        Assert.DoesNotContain(logs, message => message.Contains(text, StringComparison.Ordinal));
        // A completed native operation releases its session and leaves the caller-owned driver usable.
        Assert.Equal(1L, Assert.Single(await context.Database.QueryCypherAsync("RETURN 1 AS n", Empty, r => r["n"].As<long>())));
        await fixture.Driver.VerifyConnectivityAsync();
    }

    [Neo4jIntegrationFact]
    public async Task Native_path_and_explicit_path_DTO_remain_usable_after_owned_session_disposal()
    {
        var runId = NewRunId();
        try
        {
            await using var context = Context();
            var parameters = Params(runId);
            var path = Assert.Single(await context.Database.QueryCypherAsync(
                $"CREATE (s:{Label} {{id:$id, runId:$runId, name:'source'}})-[:{EdgeType} {{weight:7}}]->(t:{TargetLabel} {{runId:$runId, name:'target'}}) RETURN s, t",
                parameters, record => new { Source = record["s"].As<INode>(), Target = record["t"].As<INode>() }));
            Assert.Equal("source", path.Source["name"].As<string>());
            Assert.Equal("target", path.Target["name"].As<string>());
            var text = $"MATCH path=(s:{Label} {{runId:$runId}})-[:{EdgeType}]->(t:{TargetLabel} {{runId:$runId}}) RETURN path";
            var nativePath = Assert.Single(await context.Database.QueryCypherAsync(text, parameters, r => r["path"].As<IPath>()));
            Assert.Equal("source", nativePath.Start["name"].As<string>());
            Assert.Equal("target", nativePath.End["name"].As<string>());
            Assert.Equal(2, nativePath.Nodes.Count);
            Assert.Equal(EdgeType, Assert.Single(nativePath.Relationships).Type);
            Assert.Equal(7L, Assert.Single(nativePath.Relationships)["weight"].As<long>());
            var dto = Assert.Single(await context.Database.QueryCypherAsync(text, parameters, r =>
            {
                var value = r["path"].As<IPath>();
                return new PathDto(value.Nodes.Select(n => n["name"].As<string>()).ToArray(),
                    value.Relationships.Select(e => e.Type).ToArray());
            }));
            Assert.Equal(new[] { "source", "target" }, dto.Names);
            Assert.Equal(new[] { EdgeType }, dto.Types);
            Assert.True(Assert.Single(await context.Database.QueryCypherAsync(
                $"RETURN EXISTS {{ MATCH (s:{Label} {{runId:$runId}})-[:{EdgeType}]->(t:{TargetLabel} {{runId:$runId}}) }} AS found",
                parameters, r => r["found"].As<bool>())));
            Assert.False(Assert.Single(await context.Database.QueryCypherAsync(
                $"RETURN EXISTS {{ MATCH (s:{Label} {{runId:$runId}})<-[:{EdgeType}]-(t:{TargetLabel} {{runId:$runId}}) }} AS found",
                parameters, r => r["found"].As<bool>())));
            Assert.False(Assert.Single(await context.Database.QueryCypherAsync(
                $"RETURN EXISTS {{ MATCH (s:{Label} {{runId:$runId}})-[:{EdgeType}]->(t:{TargetLabel} {{runId:$runId}}) }} AS found",
                Params(NewRunId()), r => r["found"].As<bool>())));
            Assert.Empty(context.ChangeTracker.Entries());
            Assert.Null(context.Database.CurrentTransaction);
        }
        finally { await CleanupAsync(runId); }
    }

    [Neo4jIntegrationFact]
    public async Task Native_and_EF_commands_share_read_own_write_and_explicit_rollback()
    {
        var runId = NewRunId();
        try
        {
            await EnsureSchemaAsync();
            await using var context = Context();
            await using var transaction = await context.Database.BeginTransactionAsync();
            var person = new NativePerson { RunId = runId, Name = "EF pending" };
            context.Add(person);
            Assert.Equal(1, await context.SaveChangesAsync());
            var parameters = Params(runId, person.Id);
            Assert.Equal("EF pending", Assert.Single(await context.Database.QueryCypherAsync(
                $"MATCH (n:{Label} {{id:$id}}) RETURN n.name AS name", parameters, r => r["name"].As<string>())));
            Assert.Same(transaction, context.Database.CurrentTransaction);
            Assert.Equal("native pending", Assert.Single(await context.Database.QueryCypherAsync(
                $"MATCH (n:{Label} {{id:$id}}) SET n.name=$name RETURN n.name AS name",
                new Dictionary<string, object?>(parameters) { ["name"] = "native pending" }, r => r["name"].As<string>())));
            Assert.Equal("native pending", (await context.Set<NativePerson>().AsNoTracking().SingleAsync(p => p.Id == person.Id)).Name);
            Assert.Equal("EF pending", person.Name); // Native writes do not synchronize EF's existing tracker.
            Assert.Single(context.ChangeTracker.Entries());
            await transaction.RollbackAsync();
            await using var fresh = Context();
            Assert.Empty(await fresh.Set<NativePerson>().Where(p => p.RunId == runId).ToListAsync());
            Assert.Equal(0L, await NativeCountAsync(fresh, runId));
        }
        finally { await CleanupAsync(runId); }
    }

    [Neo4jIntegrationFact]
    public async Task Autonomous_projection_failure_rolls_back_native_writes_and_context_can_be_reused()
    {
        var runId = NewRunId();
        try
        {
            await using var context = Context();
            var primary = new FormatException("explicit DTO conversion rejected");
            Assert.Same(primary, await Assert.ThrowsAsync<FormatException>(() => context.Database.QueryCypherAsync<long>(
                $"CREATE (n:{Label} {{id:$id, runId:$runId}}) RETURN n", Params(runId), _ => throw primary)));
            Assert.Null(context.Database.CurrentTransaction);
            Assert.Equal(0L, await NativeCountAsync(context, runId));
            Assert.Empty(context.ChangeTracker.Entries());
        }
        finally { await CleanupAsync(runId); }
    }

    [Neo4jIntegrationFact]
    public async Task Borrowed_projection_failure_leaves_native_writes_pending_for_caller_rollback()
    {
        var runId = NewRunId();
        try
        {
            await using var context = Context();
            await using var transaction = await context.Database.BeginTransactionAsync();
            await Assert.ThrowsAsync<FormatException>(() => context.Database.QueryCypherAsync<long>(
                $"CREATE (n:{Label} {{id:$id, runId:$runId}}) RETURN n", Params(runId), _ => throw new FormatException()));
            Assert.Same(transaction, context.Database.CurrentTransaction);
            Assert.Equal(1L, await NativeCountAsync(context, runId));
            Assert.Empty(context.ChangeTracker.Entries());
            await transaction.RollbackAsync();
            Assert.Null(context.Database.CurrentTransaction);
            await using var fresh = Context();
            Assert.Equal(0L, await NativeCountAsync(fresh, runId));
        }
        finally { await CleanupAsync(runId); }
    }

    [Neo4jIntegrationFact]
    public async Task Buffer_limit_and_projection_cancellation_roll_back_autonomous_native_writes()
    {
        var runId = NewRunId();
        try
        {
            await using var context = Context();
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => context.Database.QueryCypherAsync(
                $"UNWIND [1,2] AS i CREATE (n:{Label} {{runId:$runId}}) RETURN i", Params(runId), r => r["i"].As<long>(), maxRows: 1));
            Assert.Contains("maxRows", error.Message);
            Assert.Equal(0L, await NativeCountAsync(context, runId));
            using var cancellation = new CancellationTokenSource();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => context.Database.QueryCypherAsync(
                $"CREATE (n:{Label} {{id:$id, runId:$runId}}) RETURN n", Params(runId), r =>
                {
                    cancellation.Cancel();
                    return r["n"].As<INode>();
                }, cancellationToken: cancellation.Token));
            Assert.Null(context.Database.CurrentTransaction);
            Assert.Equal(0L, await NativeCountAsync(context, runId));
        }
        finally { await CleanupAsync(runId); }
    }

    [Neo4jIntegrationFact]
    public async Task Native_query_honors_database_option_and_precancel_without_leaking_a_transaction()
    {
        await using var missing = new DbContext(new DbContextOptionsBuilder().UseNeo4j(fixture.Driver,
            "graphbridge_native_missing_" + Guid.NewGuid().ToString("N")).Options);
        await Assert.ThrowsAnyAsync<Neo4jException>(() => missing.Database.QueryCypherAsync("RETURN 1 AS n", Empty, r => r["n"]));
        Assert.Null(missing.Database.CurrentTransaction);
        await using var valid = Context();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => valid.Database.QueryCypherAsync(
            "RETURN 1 AS n", Empty, r => r["n"], cancellationToken: cancellation.Token));
        Assert.Null(valid.Database.CurrentTransaction);
        Assert.Equal(1L, Assert.Single(await valid.Database.QueryCypherAsync("RETURN 1 AS n", Empty, r => r["n"].As<long>())));
    }

    private NativeContext Context(Action<string>? log = null)
    {
        var builder = new DbContextOptionsBuilder<NativeContext>().UseNeo4j(fixture.Driver, fixture.Database);
        if (log is not null) builder.LogTo(log);
        return new(builder.Options);
    }
    private async Task<long> NativeCountAsync(DbContext context, string runId) => Assert.Single(await context.Database.QueryCypherAsync(
        $"MATCH (n:{Label} {{runId:$runId}}) RETURN count(n) AS count", Params(runId), r => r["count"].As<long>()));
    private async Task EnsureSchemaAsync()
    {
        await using var session = fixture.OpenSession();
        await (await session.RunAsync($"CREATE CONSTRAINT graphbridge_native_person_id IF NOT EXISTS FOR (n:{Label}) REQUIRE n.id IS UNIQUE")).ConsumeAsync();
    }
    private async Task CleanupAsync(string runId)
    {
        await using var session = fixture.OpenSession();
        await (await session.RunAsync($"MATCH (s:{Label} {{runId:$runId}})-[r:{EdgeType}]->(t:{TargetLabel} {{runId:$runId}}) DELETE r", new { runId })).ConsumeAsync();
        await (await session.RunAsync($"MATCH (n:{TargetLabel} {{runId:$runId}}) DELETE n", new { runId })).ConsumeAsync();
        await (await session.RunAsync($"MATCH (n:{Label} {{runId:$runId}}) DELETE n", new { runId })).ConsumeAsync();
    }
    private static Dictionary<string, object?> Params(string runId, Guid? id = null) => new()
    {
        ["runId"] = runId, ["id"] = (id ?? Guid.NewGuid()).ToString("D")
    };
    private static string NewRunId() => Guid.NewGuid().ToString("N");
    private sealed record NativeDto(string Name, long Number, object? Optional);
    private sealed record PathDto(IReadOnlyList<string> Names, IReadOnlyList<string> Types);
    private sealed class NativeContext(DbContextOptions<NativeContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder model)
        {
            var person = model.Entity<NativePerson>().ToGraphNode(Label);
            person.Property(p => p.Id).HasGraphPropertyName("id");
            person.Property(p => p.RunId).HasGraphPropertyName("runId");
            person.Property(p => p.Name).HasGraphPropertyName("name");
        }
    }
    private sealed class NativePerson
    {
        public Guid Id { get; set; }
        public string RunId { get; set; } = "";
        public string Name { get; set; } = "";
    }
}
