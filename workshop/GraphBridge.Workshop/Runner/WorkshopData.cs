using System.Collections;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using GraphBridge.Workshop.Domain;
using Microsoft.EntityFrameworkCore;
using Neo4j.Driver;

namespace GraphBridge.Workshop;

/// <summary>Safety guard, schema, seed and reset for the workshop database. Every write is scoped to workshop labels.</summary>
public static class WorkshopData
{
    public static readonly IReadOnlyList<string> Labels = ["Person", "Movie", "Review"];
    public const string Marker = "GraphBridgeWorkshop";
    // The repository fixture's marker and GraphBridge's migration journal (lesson 23) are infrastructure, not application data.
    // The GraphRAG lessons' labels (31 to 35) are their own and removed when each ends; allowing them keeps an interrupted run
    // from blocking the next.
    private static readonly HashSet<string> Allowed =
        [.. Labels, Marker, "GraphBridgeFixture", "__GraphBridgeMigrationControl", "__GraphBridgeMigration", "__GraphBridgeMigrationStep",
         "WorkshopDocument", "WorkshopSection", "WorkshopChunk", "WorkshopIngestionRun", "WorkshopLease", .. Lessons.GraphRagWorkshop.Labels];
    private const string WorkshopNodes = "(n:Person OR n:Movie OR n:Review)";

    /// <summary>Refuses to write unless the database is empty or holds only workshop data. Creates the ownership marker.</summary>
    public static async Task GuardAsync(WorkshopConnection connection, CancellationToken cancellationToken)
    {
        await connection.Driver.VerifyConnectivityAsync();
        var labels = await ReadAsync(connection, "CALL db.labels() YIELD label RETURN label", r => r["label"].As<string>(), cancellationToken);
        var foreign = labels.Where(l => !Allowed.Contains(l)).Order(StringComparer.Ordinal).ToArray();
        if (foreign.Length > 0 && !connection.Options.AllowOtherData)
            throw new WorkshopConfigurationException($"This database holds other data (labels: {string.Join(", ", foreign.Take(10))}). Use an empty database, or pass --allow-other-data for a disposable one: the workshop writes Person, Movie and Review nodes, and lessons 31 to 35 write Workshop* nodes and workshop_rag_* schema that they remove when they end.");
        var markers = await ScalarAsync(connection, $"MATCH (m:{Marker}) RETURN count(m) AS value", cancellationToken);
        var workshopNodes = await ScalarAsync(connection, $"MATCH (n) WHERE {WorkshopNodes} RETURN count(n) AS value", cancellationToken);
        if (markers == 0 && workshopNodes > 0)
            throw new WorkshopConfigurationException($"This database already has {workshopNodes} Person/Movie/Review nodes that the workshop did not create. It will not touch them; use an empty database.");
        if (markers == 0)
            await ExecuteAsync(connection, $"CREATE (:{Marker} {{createdBy: 'GraphBridge.Workshop', seedVersion: 0}})", cancellationToken);
    }

    /// <summary>Applies the reviewed uniqueness constraints the provider reports as missing. Returns the applied statements.</summary>
    /// <remarks>The provider never creates schema itself; this is the explicit, visible step lesson 03 explains.</remarks>
    public static async Task<IReadOnlyList<string>> EnsureSchemaAsync(WorkshopConnection connection, CancellationToken cancellationToken)
    {
        await using var db = new MoviesContext(connection.Driver, connection.Database);
        var inspection = await db.Database.InspectGraphSchemaAsync(cancellationToken: cancellationToken);
        var applied = new List<string>();
        foreach (var proposal in inspection.Comparison.Proposals.Where(p => p.IsRequired))
        {
            await ExecuteAsync(connection, proposal.Cypher, cancellationToken);
            applied.Add(proposal.Cypher);
        }
        return applied;
    }

    /// <summary>Writes the deterministic movie graph once, through EF SaveChangesAsync.</summary>
    public static async Task<bool> EnsureSeedAsync(WorkshopConnection connection, CancellationToken cancellationToken)
    {
        var version = await ScalarAsync(connection, $"MATCH (m:{Marker}) RETURN coalesce(max(m.seedVersion), 0) AS value", cancellationToken);
        if (version == SeedData.Version) return false;
        var existing = await ScalarAsync(connection, $"MATCH (n) WHERE {WorkshopNodes} RETURN count(n) AS value", cancellationToken);
        if (existing > 0)
            throw new WorkshopConfigurationException("Workshop data exists but is not the current seed. Run the reset command, then try again.");
        await using (var db = new MoviesContext(connection.Driver, connection.Database))
        {
            db.People.AddRange(SeedData.People());
            db.Movies.AddRange(SeedData.Movies());
            db.Reviews.AddRange(SeedData.Reviews());
            db.Credits.AddRange(SeedData.Credits());
            db.Likes.AddRange(SeedData.Likes());
            db.Follows.AddRange(SeedData.Follows());
            await db.SaveChangesAsync(cancellationToken);
        }
        await ExecuteAsync(connection, $"MATCH (m:{Marker}) SET m.seedVersion = $version, m.seedFingerprint = $fingerprint", cancellationToken,
            ("version", (long)SeedData.Version), ("fingerprint", await FingerprintAsync(connection, cancellationToken)));
        return true;
    }

    /// <summary>False when the workshop graph no longer equals the loaded seed, for example after an interrupted lesson.</summary>
    /// <remarks>A graph seeded before fingerprints existed has nothing to compare and counts as matching.</remarks>
    public static async Task<bool> MatchesSeedAsync(WorkshopConnection connection, CancellationToken cancellationToken)
    {
        var stored = await ReadAsync(connection, $"MATCH (m:{Marker}) RETURN m.seedFingerprint AS value", r => r["value"] as string, cancellationToken);
        return stored.FirstOrDefault(s => s is not null) is not { } expected || await FingerprintAsync(connection, cancellationToken) == expected;
    }

    /// <summary>A digest of every workshop node and relationship with its properties.</summary>
    /// <remarks>Row versions are left out: lessons move them on purpose, and that does not change what later lessons read.</remarks>
    public static async Task<string> FingerprintAsync(WorkshopConnection connection, CancellationToken cancellationToken)
    {
        var nodes = await ReadAsync(connection, $"MATCH (n) WHERE {WorkshopNodes} RETURN labels(n) AS labels, properties(n) AS properties",
            r => $"{Text(r["labels"])} {Text(r["properties"])}", cancellationToken);
        var relationships = await ReadAsync(connection, $"MATCH (n)-[r]->(m) WHERE {WorkshopNodes} "
            + "RETURN type(r) AS type, n.id AS source, m.id AS target, properties(r) AS properties",
            r => $"{r["source"]}-{r["type"]}->{r["target"]} {Text(r["properties"])}", cancellationToken);
        var lines = nodes.Concat(relationships).Order(StringComparer.Ordinal);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", lines))));
    }

    private static string Text(object? value) => value switch
    {
        null => "null",
        string text => text,
        IDictionary<string, object> map => "{" + string.Join(",", map.Where(p => p.Key != "version").OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => $"{p.Key}:{Text(p.Value)}")) + "}",
        IEnumerable list => "[" + string.Join(",", list.Cast<object?>().Select(Text).Order(StringComparer.Ordinal)) + "]",
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""
    };

    public static async Task<(long Nodes, long Relationships)> CountAsync(WorkshopConnection connection, CancellationToken cancellationToken)
    {
        var nodes = await ScalarAsync(connection, $"MATCH (n) WHERE {WorkshopNodes} RETURN count(n) AS value", cancellationToken);
        var relationships = await ScalarAsync(connection, $"MATCH (n)-[r]-() WHERE {WorkshopNodes} RETURN count(DISTINCT r) AS value", cancellationToken);
        return (nodes, relationships);
    }

    /// <summary>Deletes only workshop nodes and their relationships, after an explicit confirmation. Constraints stay.</summary>
    public static async Task<bool> ResetAsync(WorkshopConnection connection, Func<long, long, bool> confirm, CancellationToken cancellationToken)
    {
        await GuardAsync(connection, cancellationToken);
        var (nodes, relationships) = await CountAsync(connection, cancellationToken);
        if (!confirm(nodes, relationships)) return false;
        // Confirmation can take a while: check ownership again, and delete only what was counted, in one transaction.
        await GuardAsync(connection, cancellationToken);
        await using var session = connection.Driver.AsyncSession(o => o.WithDatabase(connection.Database));
        var deleted = await session.ExecuteWriteAsync(async tx =>
        {
            var counted = await (await tx.RunAsync($"MATCH (n) WHERE {WorkshopNodes} OPTIONAL MATCH (n)-[r]-() "
                + "RETURN count(DISTINCT n) AS nodes, count(DISTINCT r) AS relationships")).SingleAsync();
            if (counted["nodes"].As<long>() != nodes || counted["relationships"].As<long>() != relationships) return false;
            await (await tx.RunAsync($"MATCH (n) WHERE {WorkshopNodes} DETACH DELETE n")).ConsumeAsync();
            await (await tx.RunAsync($"MATCH (m:{Marker}) SET m.seedVersion = 0, m.seedFingerprint = null")).ConsumeAsync();
            return true;
        });
        if (!deleted)
            throw new WorkshopConfigurationException("The workshop data changed while waiting for confirmation, so nothing was deleted. Run reset again.");
        return true;
    }

    public static async Task<long> ScalarAsync(WorkshopConnection connection, string cypher, CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
        => (await ReadAsync(connection, cypher, r => r["value"].As<long>(), cancellationToken, parameters)).Single();

    public static async Task<IReadOnlyList<T>> ReadAsync<T>(WorkshopConnection connection, string cypher, Func<IRecord, T> project,
        CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        await using var session = connection.Driver.AsyncSession(o => o.WithDatabase(connection.Database));
        return await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync(cypher, parameters.ToDictionary(p => p.Name, p => p.Value));
            return (IReadOnlyList<T>)(await cursor.ToListAsync(cancellationToken)).Select(project).ToList();
        });
    }

    /// <summary>Runs one statement in its own auto-commit transaction: the host's explicit, visible DDL and data steps.</summary>
    public static async Task ExecuteAsync(WorkshopConnection connection, string cypher, CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var session = connection.Driver.AsyncSession(o => o.WithDatabase(connection.Database));
        var cursor = await session.RunAsync(cypher, parameters.ToDictionary(p => p.Name, p => p.Value));
        await cursor.ConsumeAsync();
    }
}
