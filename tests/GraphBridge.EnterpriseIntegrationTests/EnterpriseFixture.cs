using Neo4j.Driver;

namespace GraphBridge.EnterpriseIntegrationTests;

/// <summary>Opt-in, owned Enterprise database only. Does not accept a license or start a server.</summary>
public sealed class EnterpriseFixture : IAsyncLifetime
{
    public const string Database = "neo4j";
    public const string PinnedImage = "neo4j:2026.09.0-enterprise@sha256:595846b50b04d5f93a4850f501c6105412dc3ea7b6594126d361720b0f450a52";
    public IDriver Admin { get; private set; } = null!;
    public IDriver Reader { get; private set; } = null!;
    public IDriver Writer { get; private set; } = null!;
    public IDriver NoSchemaWriter { get; private set; } = null!;
    public IReadOnlyList<PrivilegeRow> OriginalPublicPrivileges { get; private set; } = [];
    public IReadOnlyDictionary<string, IReadOnlyList<PrivilegeRow>> EffectivePrivileges { get; private set; }
        = new Dictionary<string, IReadOnlyList<PrivilegeRow>>();

    public async Task InitializeAsync()
    {
        if (Environment.GetEnvironmentVariable("GRAPHBRIDGE_ENTERPRISE_INTEGRATION") != "1") return;
        if (Required("GRAPHBRIDGE_ENTERPRISE_EDITION") != "enterprise"
            || Required("GRAPHBRIDGE_ENTERPRISE_LICENSE") is not ("yes" or "eval")
            || Required("GRAPHBRIDGE_ENTERPRISE_IMAGE") != PinnedImage
            || Required("GRAPHBRIDGE_ENTERPRISE_DATABASE") != Database)
            throw new InvalidOperationException("Enterprise acceptance requires the separately authorized, exact pinned fixture.");
        var uri = new Uri(Required("GRAPHBRIDGE_ENTERPRISE_URI"), UriKind.Absolute);
        if (uri.AbsoluteUri != "bolt://127.0.0.1:18687/")
            throw new InvalidOperationException("Enterprise acceptance accepts only bolt://127.0.0.1:18687.");
        var marker = Required("GRAPHBRIDGE_ENTERPRISE_FIXTURE_ID");
        if (!Guid.TryParseExact(marker, "N", out _)
            || Required("GRAPHBRIDGE_ENTERPRISE_PROJECT") != "graphbridge-enterprise-" + marker)
            throw new InvalidOperationException("Dedicated Enterprise fixture identity is invalid.");
        Admin = Driver(uri, "neo4j", RequiredSecret("GRAPHBRIDGE_ENTERPRISE_ADMIN_PASSWORD"));
        try
        {
            await using (var session = Admin.AsyncSession(o => o.WithDatabase(Database)))
            {
                var ownership = await session.RunAsync("MATCH (f:GraphBridgeEnterpriseFixture {id: $id}) RETURN count(f) AS count", new { id = marker });
                if ((await ownership.SingleAsync())["count"].As<long>() != 1)
                    throw new InvalidOperationException("Enterprise admin marker is absent; no fixture setup writes are allowed.");
                var components = await session.RunAsync("CALL dbms.components() YIELD name, versions, edition WHERE name = 'Neo4j Kernel' RETURN versions[0] AS version, edition");
                var component = await components.SingleAsync();
                if (component["version"].As<string>() != "2026.09.0"
                    || !string.Equals(component["edition"].As<string>(), "enterprise", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Enterprise restricted-user acceptance requires Neo4j 2026.09.0 Enterprise.");
            }

            // Precreate every application label/type/property and online uniqueness constraint as admin.
            // Runtime users are never granted schema or token creation privileges.
            await PrepareApplicationSchemaAsync();
            OriginalPublicPrivileges = await ReadPrivilegesAsync("SHOW ROLE PUBLIC PRIVILEGES");
            await SystemAsync("REVOKE GRANT ACCESS ON HOME DATABASE FROM PUBLIC");
            await SystemAsync("REVOKE GRANT EXECUTE PROCEDURE * ON DBMS FROM PUBLIC");
            await SystemAsync("REVOKE GRANT EXECUTE USER DEFINED FUNCTION * ON DBMS FROM PUBLIC");
            await SystemAsync("REVOKE GRANT LOAD ON ALL DATA FROM PUBLIC");
            if ((await ReadPrivilegesAsync("SHOW ROLE PUBLIC PRIVILEGES")).Count != 0)
                throw new InvalidOperationException("Unexpected PUBLIC privileges remain in the owned fixture; restricted-user evidence would be invalid.");

            var profiles = new[]
            {
                new Profile("gb_reader", "gb_reader_role", "GRAPHBRIDGE_ENTERPRISE_READER_PASSWORD", false, true),
                new Profile("gb_writer", "gb_writer_role", "GRAPHBRIDGE_ENTERPRISE_WRITER_PASSWORD", true, true),
                new Profile("gb_no_schema", "gb_no_schema_role", "GRAPHBRIDGE_ENTERPRISE_NO_SCHEMA_PASSWORD", true, false)
            };
            var audits = new Dictionary<string, IReadOnlyList<PrivilegeRow>>(StringComparer.Ordinal);
            foreach (var profile in profiles)
            {
                await SystemAsync($"CREATE ROLE {profile.Role} IF NOT EXISTS");
                await SystemAsync($"CREATE USER {profile.User} IF NOT EXISTS SET PASSWORD $password CHANGE NOT REQUIRED SET HOME DATABASE neo4j",
                    new { password = RequiredSecret(profile.SecretVariable) });
                await SystemAsync($"GRANT ROLE {profile.Role} TO {profile.User}");
                await SystemAsync($"GRANT ACCESS ON DATABASE neo4j TO {profile.Role}");
                await SystemAsync($"GRANT MATCH {{*}} ON GRAPH neo4j NODES * TO {profile.Role}");
                await SystemAsync($"GRANT MATCH {{*}} ON GRAPH neo4j RELATIONSHIPS * TO {profile.Role}");
                if (profile.ShowConstraint) await SystemAsync($"GRANT SHOW CONSTRAINT ON DATABASE neo4j TO {profile.Role}");
                if (profile.Write) await SystemAsync($"GRANT WRITE ON GRAPH neo4j TO {profile.Role}");
                var rows = await ReadPrivilegesAsync($"SHOW USER {profile.User} PRIVILEGES");
                ValidatePrivileges(profile, rows);
                audits.Add(profile.User, rows);
                await using var system = Admin.AsyncSession(o => o.WithDatabase("system"));
                var users = await system.RunAsync("SHOW USERS YIELD user, roles WHERE user = $name RETURN roles", new { name = profile.User });
                var roles = (await users.SingleAsync())["roles"].As<List<string>>();
                if (!roles.Order(StringComparer.Ordinal).SequenceEqual(new[] { "PUBLIC", profile.Role }.Order(StringComparer.Ordinal)))
                    throw new InvalidOperationException("A restricted fixture user has unexpected effective roles.");
            }
            EffectivePrivileges = audits;
            Reader = Driver(uri, "gb_reader", RequiredSecret("GRAPHBRIDGE_ENTERPRISE_READER_PASSWORD"));
            Writer = Driver(uri, "gb_writer", RequiredSecret("GRAPHBRIDGE_ENTERPRISE_WRITER_PASSWORD"));
            NoSchemaWriter = Driver(uri, "gb_no_schema", RequiredSecret("GRAPHBRIDGE_ENTERPRISE_NO_SCHEMA_PASSWORD"));
            // Verify the authorized database route directly; no default-database or admin procedure is needed.
            foreach (var runtime in new[] { Reader, Writer, NoSchemaWriter })
            {
                await using var session = runtime.AsyncSession(o => o.WithDatabase(Database));
                await (await session.RunAsync("RETURN 1 AS value")).ConsumeAsync();
            }
        }
        catch
        {
            try { await DisposeAsync(); }
            catch { /* Preserve initialization failure after attempting all driver cleanup. */ }
            throw;
        }
    }

    public async Task ExecuteAdminAsync(string cypher, object? parameters = null)
    {
        await using var session = Admin.AsyncSession(o => o.WithDatabase(Database));
        await (await session.RunAsync(cypher, parameters ?? new { })).ConsumeAsync();
    }

    public async Task<long> CountRunAsync(string run)
    {
        await using var session = Admin.AsyncSession(o => o.WithDatabase(Database));
        var result = await session.RunAsync("MATCH (n) WHERE n.Run = $run RETURN count(n) AS count", new { run });
        return (await result.SingleAsync())["count"].As<long>();
    }

    public async Task CleanupAsync(string run)
    {
        // Each edge and node belongs to this exact random test run; plain DELETE detects leftovers.
        await ExecuteAdminAsync("MATCH ()-[r:ENTERPRISE_CREDIT]->() WHERE r.Run = $run DELETE r", new { run });
        await ExecuteAdminAsync("MATCH (b:EnterpriseBlog)-[r:ENTERPRISE_HAS_POST]->(p:EnterprisePost) WHERE b.Run = $run AND p.Run = $run DELETE r", new { run });
        await ExecuteAdminAsync("MATCH (n) WHERE n.Run = $run DELETE n", new { run });
    }

    private async Task PrepareApplicationSchemaAsync()
    {
        await ExecuteAdminAsync("CREATE CONSTRAINT gb_enterprise_blog_key IF NOT EXISTS FOR (n:EnterpriseBlog) REQUIRE n.Id IS UNIQUE");
        await ExecuteAdminAsync("CREATE CONSTRAINT gb_enterprise_post_key IF NOT EXISTS FOR (n:EnterprisePost) REQUIRE n.Id IS UNIQUE");
        await ExecuteAdminAsync("CREATE CONSTRAINT gb_enterprise_movie_key IF NOT EXISTS FOR (n:EnterpriseMovie) REQUIRE n.Id IS UNIQUE");
        await ExecuteAdminAsync("CREATE CONSTRAINT gb_enterprise_credit_key IF NOT EXISTS FOR ()-[r:ENTERPRISE_CREDIT]-() REQUIRE r.Id IS UNIQUE");
        var warmup = Guid.NewGuid().ToString("N");
        try
        {
            await ExecuteAdminAsync("""
                CREATE (b:EnterpriseBlog {Id: $blog, Name: 'token warmup', Version: 1, Run: $run}),
                       (p:EnterprisePost {Id: $post, Title: 'token warmup', Version: 1, Run: $run}),
                       (m:EnterpriseMovie {Id: $movie, Title: 'token warmup', Run: $run}),
                       (b)-[:ENTERPRISE_HAS_POST]->(p),
                       (b)-[:ENTERPRISE_CREDIT {Id: $credit, Role: 'token warmup', Version: 1, Run: $run}]->(m)
                """, new { blog = Guid.NewGuid().ToString("D"), post = Guid.NewGuid().ToString("D"), movie = Guid.NewGuid().ToString("D"), credit = Guid.NewGuid().ToString("D"), run = warmup });
        }
        finally { await CleanupAsync(warmup); }
        await ExecuteAdminAsync("CALL db.awaitIndexes(30)");
        await using var session = Admin.AsyncSession(o => o.WithDatabase(Database));
        var indexes = await session.RunAsync("SHOW INDEXES YIELD owningConstraint, state WHERE owningConstraint IN $names RETURN state", new
        {
            names = new[] { "gb_enterprise_blog_key", "gb_enterprise_post_key", "gb_enterprise_movie_key", "gb_enterprise_credit_key" }
        });
        var states = await indexes.ToListAsync(record => record["state"].As<string>());
        if (states.Count != 4 || states.Any(state => state != "ONLINE"))
            throw new InvalidOperationException("Admin-created Enterprise fixture indexes are not all ONLINE.");
    }

    private async Task SystemAsync(string command, object? parameters = null)
    {
        await using var system = Admin.AsyncSession(o => o.WithDatabase("system"));
        await (await system.RunAsync(command, parameters ?? new { })).ConsumeAsync();
    }

    private async Task<IReadOnlyList<PrivilegeRow>> ReadPrivilegesAsync(string prefix)
    {
        await using var session = Admin.AsyncSession(o => o.WithDatabase("system"));
        var cursor = await session.RunAsync(prefix + " YIELD access, action, resource, graph, segment, role, immutable RETURN access, action, resource, graph, segment, role, immutable ORDER BY role, action, segment");
        return await cursor.ToListAsync(row => new PrivilegeRow(row["access"].As<string>(), row["action"].As<string>(),
            row["resource"].As<string>(), row["graph"].As<string>(), row["segment"].As<string>(), row["role"].As<string>(), row["immutable"].As<bool>()));
    }

    private static void ValidatePrivileges(Profile profile, IReadOnlyList<PrivilegeRow> rows)
    {
        var expected = new HashSet<(string Action, string Resource, string Segment)>
        {
            ("access", "database", "database"),
            ("match", "all_properties", "NODE(*)"), ("match", "all_properties", "RELATIONSHIP(*)")
        };
        if (profile.ShowConstraint) expected.Add(("show_constraint", "database", "database"));
        if (profile.Write)
        {
            expected.Add(("write", "graph", "NODE(*)"));
            expected.Add(("write", "graph", "RELATIONSHIP(*)"));
        }
        if (rows.Any(row => row.Access != "GRANTED" || row.Graph != Database || row.Role != profile.Role || row.Immutable)
            || rows.Count != expected.Count || !expected.SetEquals(rows.Select(row => (row.Action, row.Resource, row.Segment))))
            throw new InvalidOperationException("Effective restricted-user privileges differ from the reviewed full-application-database profile.");
    }

    public async Task DisposeAsync()
    {
        var drivers = new[] { NoSchemaWriter, Writer, Reader, Admin };
        NoSchemaWriter = Writer = Reader = Admin = null!;
        List<Exception>? failures = null;
        foreach (var driver in drivers)
        {
            if (driver is null) continue;
            try { await driver.DisposeAsync(); }
            catch (Exception error) { (failures ??= []).Add(error); }
        }
        if (failures is not null) throw new AggregateException("Enterprise fixture driver cleanup failed.", failures);
    }

    private static IDriver Driver(Uri uri, string username, string password)
        => GraphDatabase.Driver(uri, AuthTokens.Basic(username, password), options => options.WithConnectionTimeout(TimeSpan.FromSeconds(10)));
    private static string Required(string name)
        => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value
            : throw new InvalidOperationException("Enterprise acceptance requires generated environment variable " + name + ".");
    private static string RequiredSecret(string name)
    {
        var value = Required(name);
        if (value.Length != 64 || value.Any(c => !char.IsAsciiHexDigit(c)))
            throw new InvalidOperationException("Enterprise runtime credentials must be generated 256-bit hexadecimal secrets.");
        return value;
    }
    private sealed record Profile(string User, string Role, string SecretVariable, bool Write, bool ShowConstraint);
    public sealed record PrivilegeRow(string Access, string Action, string Resource, string Graph, string Segment, string Role, bool Immutable);
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class EnterpriseCollection : ICollectionFixture<EnterpriseFixture>
{
    public const string Name = "Owned Enterprise restricted-user acceptance";
}

public sealed class EnterpriseFactAttribute : FactAttribute
{
    public EnterpriseFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("GRAPHBRIDGE_ENTERPRISE_INTEGRATION") != "1")
            Skip = "Separate Enterprise fixture and explicit license authorization required; no acceptance has been inferred.";
    }
}
