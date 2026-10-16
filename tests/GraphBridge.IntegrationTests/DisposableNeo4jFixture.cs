using Neo4j.Driver;

namespace GraphBridge.IntegrationTests;

/// <summary>One shared driver, separate scoped sessions; never accepts a remote server.</summary>
public sealed class DisposableNeo4jFixture : IAsyncLifetime
{
    public IDriver Driver { get; private set; } = null!;
    public string Database { get; private set; } = "neo4j";
    public string ServerVersion { get; private set; } = "";
    public string ServerEdition { get; private set; } = "";
    public IReadOnlyList<string> CypherVersions { get; private set; } = [];

    public async Task InitializeAsync()
    {
        if (Environment.GetEnvironmentVariable("GRAPHBRIDGE_INTEGRATION") != "1")
        {
            return;
        }

        var uri = new Uri(Required("GRAPHBRIDGE_NEO4J_URI"), UriKind.Absolute);
        if (uri.Scheme != "bolt" || uri.Host != "127.0.0.1" || uri.Port != 17687
            || !string.IsNullOrEmpty(uri.UserInfo) || uri.AbsolutePath != "/"
            || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new InvalidOperationException("Integration tests require bolt://127.0.0.1:17687 from the dedicated disposable fixture.");
        }

        var fixtureId = Required("GRAPHBRIDGE_FIXTURE_ID");
        if (!Guid.TryParseExact(fixtureId, "N", out _))
        {
            throw new InvalidOperationException("GRAPHBRIDGE_FIXTURE_ID must be the generated disposable fixture marker.");
        }

        Database = Required("GRAPHBRIDGE_NEO4J_DATABASE");
        if (Database != "neo4j")
        {
            throw new InvalidOperationException("The Community fixture uses only the neo4j database.");
        }

        Driver = GraphDatabase.Driver(uri, AuthTokens.Basic(
            Required("GRAPHBRIDGE_NEO4J_USERNAME"), Required("GRAPHBRIDGE_NEO4J_PASSWORD")),
            builder => builder.WithConnectionTimeout(TimeSpan.FromSeconds(10)));

        try
        {
            await Driver.VerifyConnectivityAsync();
            await using var session = OpenSession();
            var markerCursor = await session.RunAsync(
                "MATCH (f:GraphBridgeFixture {id: $fixtureId}) RETURN count(f) AS count",
                new { fixtureId });
            var marker = await markerCursor.SingleAsync();
            if (marker["count"].As<long>() != 1)
            {
                throw new InvalidOperationException("Disposable fixture marker missing; refusing graph writes.");
            }

            var cursor = await session.RunAsync(
                "CALL dbms.components() YIELD name, versions, edition WHERE name = 'Neo4j Kernel' RETURN versions[0] AS version, edition");
            var component = await cursor.SingleAsync();
            ServerVersion = component["version"].As<string>();
            ServerEdition = component["edition"].As<string>();
            var cypherCursor = await session.RunAsync(
                "CALL dbms.components() YIELD name, versions WHERE name = 'Cypher' RETURN versions");
            var cypherComponents = await cypherCursor.ToListAsync();
            if (cypherComponents.Count > 1) throw new InvalidOperationException("Ambiguous Cypher component observations.");
            CypherVersions = cypherComponents.Count == 0 ? [] : cypherComponents[0]["versions"].As<List<string>>();
            if (!string.Equals(ServerEdition, "community", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("The baseline requires the disposable Community image.");
            }
        }
        catch
        {
            await Driver.DisposeAsync();
            Driver = null!;
            throw;
        }
    }

    public IAsyncSession OpenSession() => Driver.AsyncSession(builder => builder.WithDatabase(Database));

    // Plain DELETE deliberately fails if relationships exist. Every match is run-scoped.
    public async Task CleanupAsync(string runId)
    {
        await using var session = OpenSession();
        var cursor = await session.RunAsync(
            "MATCH (p:Person:GraphBridgeBaseline {runId: $runId}) DELETE p", new { runId });
        await cursor.ConsumeAsync();
    }

    public async Task DisposeAsync()
    {
        if (Driver is not null)
        {
            await Driver.DisposeAsync();
        }
    }

    private static string Required(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"Integration enabled but {name} is missing. Start the disposable fixture.");
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class Neo4jIntegrationCollection : ICollectionFixture<DisposableNeo4jFixture>
{
    public const string Name = "Disposable Neo4j";
}
