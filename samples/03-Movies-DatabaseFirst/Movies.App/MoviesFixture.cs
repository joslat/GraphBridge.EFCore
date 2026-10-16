using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GraphBridge.EntityFrameworkCore.Schema;
using Neo4j.Driver;

namespace GraphBridge.Samples.MoviesDemo;

/// <summary>Explicit setup for this repository's disposable fixture; never retries writes or deletes existing data.</summary>
public static class MoviesFixture
{
    private const int MaximumRows = 4096;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private const string QueryHash = "b05ce9bca41d2ee176a3359d9f2bd7191110b749de43b57b657b5a7996ba6ca2";
    private const string ParametersHash = "87eba71a8513db0c4b6867650e4859ddd0a79de1b1555d0f6ed1f60fd9310b00";
    private static readonly Definition[] Definitions =
    [
        new("graphbridge_movies_person_id", "NODE", "GraphBridgeMoviesPerson", "personId",
            "CREATE CONSTRAINT graphbridge_movies_person_id FOR (person:GraphBridgeMoviesPerson) REQUIRE person.personId IS UNIQUE"),
        new("graphbridge_movies_movie_id", "NODE", "GraphBridgeMoviesMovie", "movieId",
            "CREATE CONSTRAINT graphbridge_movies_movie_id FOR (movie:GraphBridgeMoviesMovie) REQUIRE movie.movieId IS UNIQUE"),
        new("graphbridge_movies_acting_credit_id", "RELATIONSHIP", "GRAPHBRIDGE_MOVIES_ACTED_IN", "actingCreditId",
            "CREATE CONSTRAINT graphbridge_movies_acting_credit_id FOR ()-[credit:GRAPHBRIDGE_MOVIES_ACTED_IN]-() REQUIRE credit.actingCreditId IS UNIQUE"),
        new("graphbridge_movies_directing_credit_id", "RELATIONSHIP", "GRAPHBRIDGE_MOVIES_DIRECTED", "directingCreditId",
            "CREATE CONSTRAINT graphbridge_movies_directing_credit_id FOR ()-[credit:GRAPHBRIDGE_MOVIES_DIRECTED]-() REQUIRE credit.directingCreditId IS UNIQUE")
    ];

    public static IReadOnlyList<string> ConstraintNames { get; } = Array.AsReadOnly(Definitions.Select(d => d.Name).ToArray());

    /// <summary>The caller also checks the driver's loopback endpoint. This check binds the selected database to its exact disposable marker.</summary>
    public static Task VerifyFixtureAsync(IDriver driver, string database, string fixtureId, CancellationToken cancellationToken = default)
    {
        ValidateArguments(driver, database, fixtureId);
        return TransactionAsync(driver, database, false, transaction => VerifyMarkerAsync(transaction, fixtureId, cancellationToken), cancellationToken);
    }

    /// <summary>Loads only the pinned original fixture version, validates counts before commit, and refuses reseeding.</summary>
    public static async Task SeedAsync(IDriver driver, string database, string fixtureId, string sourceDirectory,
        CancellationToken cancellationToken = default)
    {
        ValidateArguments(driver, database, fixtureId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory);
        cancellationToken.ThrowIfCancellationRequested();
        var query = ReadPinned(Path.Combine(sourceDirectory, "movies.fixture.cypher"), QueryHash);
        var json = ReadPinned(Path.Combine(sourceDirectory, "movies.fixture.parameters.json"), ParametersHash);
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
        var parameters = Decode(document.RootElement) as Dictionary<string, object>
            ?? throw new InvalidOperationException("Movies fixture parameters must be an object.");
        await TransactionAsync(driver, database, true, async transaction =>
        {
            await VerifyMarkerAsync(transaction, fixtureId, cancellationToken).ConfigureAwait(false);
            var rows = await ReadAsync(transaction, query, parameters, 1, cancellationToken).ConfigureAwait(false);
            if (rows.Count != 1 || Number(rows[0], "peopleCreated") != 3 || Number(rows[0], "moviesCreated") != 2
                || Number(rows[0], "actingCreditsCreated") != 4 || Number(rows[0], "directingCreditsCreated") != 1)
                throw new InvalidOperationException("Movies fixture was not empty or creation counts differed; the transaction was not approved for commit.");
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Explicit stepwise DDL. All definitions are inspected first; prior exact complete steps are reusable without a journal reset.</summary>
    public static async Task PrepareSchemaAsync(IDriver driver, string database, string fixtureId,
        CancellationToken cancellationToken = default)
    {
        ValidateArguments(driver, database, fixtureId);
        // Complete preflight before the first DDL write, including every reserved name and equivalent foreign shape.
        await TransactionAsync(driver, database, false, async transaction =>
        {
            await VerifyMarkerAsync(transaction, fixtureId, cancellationToken).ConfigureAwait(false);
            ValidateSchema(await ObserveAsync(transaction, cancellationToken).ConfigureAwait(false), requireComplete: false);
        }, cancellationToken).ConfigureAwait(false);

        foreach (var definition in Definitions)
        {
            await TransactionAsync(driver, database, true, async transaction =>
            {
                await VerifyMarkerAsync(transaction, fixtureId, cancellationToken).ConfigureAwait(false);
                var schema = await ObserveAsync(transaction, cancellationToken).ConfigureAwait(false);
                ValidateSchema(schema, requireComplete: false);
                if (schema.Constraints.Any(c => c.Name == definition.Name)) return;
                cancellationToken.ThrowIfCancellationRequested();
                var cursor = await transaction.RunAsync(definition.Cypher).ConfigureAwait(false);
                await cursor.ConsumeAsync().ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);
            // Schema observations after DDL use a fresh transaction; an incomplete result is never adopted as ready.
            await TransactionAsync(driver, database, false, async transaction =>
            {
                await VerifyMarkerAsync(transaction, fixtureId, cancellationToken).ConfigureAwait(false);
                var schema = await ObserveAsync(transaction, cancellationToken).ConfigureAwait(false);
                ValidateSchema(schema, requireComplete: false);
                if (!schema.Constraints.Any(c => c.Name == definition.Name))
                    throw new InvalidOperationException("A committed Movies constraint was not observed; inspect the database before continuing.");
            }, cancellationToken).ConfigureAwait(false);
        }
        await TransactionAsync(driver, database, false, async transaction =>
        {
            await VerifyMarkerAsync(transaction, fixtureId, cancellationToken).ConfigureAwait(false);
            ValidateSchema(await ObserveAsync(transaction, cancellationToken).ConfigureAwait(false), requireComplete: true);
        }, cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateArguments(IDriver driver, string database, string fixtureId)
    {
        ArgumentNullException.ThrowIfNull(driver);
        if (database != "neo4j") throw new InvalidOperationException("Movies setup accepts only the disposable neo4j database.");
        if (!Guid.TryParseExact(fixtureId, "N", out _)) throw new InvalidOperationException("Movies setup requires the disposable GUID-N fixture marker.");
    }

    private static async Task VerifyMarkerAsync(IAsyncTransaction transaction, string fixtureId, CancellationToken cancellationToken)
    {
        var rows = await ReadAsync(transaction, "MATCH (f:GraphBridgeFixture) RETURN f.id AS id LIMIT $limit",
            new Dictionary<string, object> { ["limit"] = 2L }, 1, cancellationToken).ConfigureAwait(false);
        if (rows.Count != 1 || !rows[0].TryGetValue("id", out var value) || value is not string id || id != fixtureId)
            throw new InvalidOperationException("Disposable fixture marker missing or different; refusing Movies setup.");
    }

    private static async Task<GraphSchemaObservation> ObserveAsync(IAsyncTransaction transaction, CancellationToken cancellationToken)
    {
        var parameters = new Dictionary<string, object> { ["limit"] = MaximumRows + 1L };
        var constraints = await ReadAsync(transaction,
            "SHOW CONSTRAINTS YIELD name, type, entityType, labelsOrTypes, properties, propertyType RETURN name, type, entityType, labelsOrTypes, properties, propertyType ORDER BY name LIMIT $limit",
            parameters, MaximumRows, cancellationToken).ConfigureAwait(false);
        var indexes = await ReadAsync(transaction,
            "SHOW INDEXES YIELD name, state, type, entityType, labelsOrTypes, properties, owningConstraint RETURN name, state, type, entityType, labelsOrTypes, properties, owningConstraint ORDER BY name LIMIT $limit",
            parameters, MaximumRows, cancellationToken).ConfigureAwait(false);
        return new(constraints.Select(GraphSchemaObservationParser.ParseConstraint), indexes.Select(GraphSchemaObservationParser.ParseIndex));
    }

    private static void ValidateSchema(GraphSchemaObservation schema, bool requireComplete)
    {
        foreach (var definition in Definitions)
        {
            var constraint = schema.Constraints.SingleOrDefault(c => c.Name == definition.Name);
            var index = schema.Indexes.SingleOrDefault(i => i.Name == definition.Name);
            bool Shape(string entity, IReadOnlyList<string>? tokens, IReadOnlyList<string>? properties)
                => entity == definition.EntityType && tokens is { Count: 1 } && tokens[0] == definition.Token
                    && properties is { Count: 1 } && properties[0] == definition.Property;
            if (schema.Constraints.Any(c => c.Name != definition.Name && Shape(c.EntityType, c.LabelsOrTypes, c.Properties))
                || schema.Indexes.Any(i => i.Name != definition.Name
                    && (i.OwningConstraint == definition.Name || Shape(i.EntityType, i.LabelsOrTypes, i.Properties))))
                throw new InvalidOperationException("Movies schema conflicts with an existing constraint/index under another name: " + definition.Name);
            if (constraint is null)
            {
                if (index is not null || schema.Indexes.Any(i => i.OwningConstraint == definition.Name) || requireComplete)
                    throw new InvalidOperationException("Movies schema is incomplete or its reserved name is occupied: " + definition.Name);
                continue;
            }
            var uniqueness = definition.EntityType == "NODE"
                ? constraint.Type is "UNIQUENESS" or "NODE_PROPERTY_UNIQUENESS"
                : constraint.Type is "RELATIONSHIP_UNIQUENESS" or "RELATIONSHIP_PROPERTY_UNIQUENESS";
            if (!uniqueness || constraint.PropertyType is not null || !Shape(constraint.EntityType, constraint.LabelsOrTypes, constraint.Properties)
                || index is null || index.State != "ONLINE" || index.Type != "RANGE" || index.OwningConstraint != definition.Name
                || !Shape(index.EntityType, index.LabelsOrTypes, index.Properties))
                throw new InvalidOperationException("Movies schema requires the exact uniqueness constraint and owned ONLINE backing index: " + definition.Name);
        }
    }

    private static async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ReadAsync(IAsyncTransaction transaction,
        string query, Dictionary<string, object> parameters, int maximumRows, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IResultCursor? cursor = null;
        try
        {
            cursor = await transaction.RunAsync(query, parameters).ConfigureAwait(false);
            var rows = new List<IReadOnlyDictionary<string, object?>>();
            while (await cursor.FetchAsync().ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (rows.Count == maximumRows) throw new InvalidOperationException("Movies setup observation exceeded its finite row bound.");
                rows.Add(cursor.Current.Values.ToDictionary(p => p.Key, p => (object?)p.Value, StringComparer.Ordinal));
            }
            await cursor.ConsumeAsync().ConfigureAwait(false);
            cursor = null;
            cancellationToken.ThrowIfCancellationRequested();
            return rows;
        }
        catch
        {
            try { if (cursor is { IsOpen: true }) await cursor.ConsumeAsync().ConfigureAwait(false); }
            catch { /* Preserve the primary observation error. */ }
            throw;
        }
    }

    private static async Task TransactionAsync(IDriver driver, string database, bool commit,
        Func<IAsyncTransaction, Task> action, CancellationToken cancellationToken)
    {
        IAsyncSession? session = null;
        IAsyncTransaction? transaction = null;
        var committed = false;
        var completed = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            session = driver.AsyncSession(builder => builder.WithDatabase(database).WithDefaultAccessMode(commit ? AccessMode.Write : AccessMode.Read).WithFetchSize(64));
            transaction = await session.BeginTransactionAsync(builder => builder.WithTimeout(Timeout)
                .WithMetadata(new Dictionary<string, object> { ["graphbridgeOperation"] = "movies-sample-setup" })).ConfigureAwait(false);
            await action(transaction).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (commit)
            {
                await transaction.CommitAsync().ConfigureAwait(false);
                committed = true;
            }
            completed = true;
        }
        finally
        {
            // A lost commit acknowledgement is surfaced, never replayed; rollback is cleanup, not proof of non-commit.
            Exception? cleanupError = null;
            if (transaction is not null)
            {
                if (!committed)
                    try { await transaction.RollbackAsync().ConfigureAwait(false); } catch (Exception error) { cleanupError = error; }
                try { await transaction.DisposeAsync().ConfigureAwait(false); } catch (Exception error) { cleanupError ??= error; }
            }
            if (session is not null)
                try { await session.DisposeAsync().ConfigureAwait(false); } catch (Exception error) { cleanupError ??= error; }
            if (completed && cleanupError is not null) ExceptionDispatchInfo.Capture(cleanupError).Throw();
        }
    }

    private static string ReadPinned(string path, string expectedHash)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > 65536) throw new IOException("Movies fixture source exceeds 64 KiB.");
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: false);
        var text = reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
        if (hash != expectedHash) throw new InvalidOperationException("Movies fixture source differs from its reviewed version; no setup was performed.");
        return text;
    }

    private static object Decode(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => value.EnumerateObject().ToDictionary(p => p.Name, p => Decode(p.Value), StringComparer.Ordinal),
        JsonValueKind.Array => value.EnumerateArray().Select(Decode).ToList(),
        JsonValueKind.String => value.GetString()!,
        JsonValueKind.Number when value.TryGetInt64(out var integer) => integer,
        JsonValueKind.Null => null!,
        _ => throw new InvalidOperationException("Unsupported Movies fixture parameter kind.")
    };

    private static long Number(IReadOnlyDictionary<string, object?> row, string name)
        => row.TryGetValue(name, out var value) && value is long number ? number
            : throw new InvalidOperationException("Movies fixture returned an invalid creation count.");

    private sealed record Definition(string Name, string EntityType, string Token, string Property, string Cypher);
}
