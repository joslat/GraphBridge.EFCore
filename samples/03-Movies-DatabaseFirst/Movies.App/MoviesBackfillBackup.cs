using System.Text.Json;
using System.Text.Json.Serialization;
using Neo4j.Driver;

namespace GraphBridge.Samples.MoviesDemo;

/// <summary>Before-image of the one reviewed legacy row only; not a database backup or automatic restore facility.</summary>
public static class MoviesBackfillBackup
{
    public const int MaximumBytes = 16 * 1024;
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true, MaxDepth = 8, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static async Task SaveAsync(IDriver driver, string database, string fixtureId, string path,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateIdentity(database, fixtureId);
        var fullPath = BackupPath(path);
        if (File.Exists(fullPath)) throw new IOException("Backup path already exists; preserved evidence is never overwritten.");
        await MoviesFixture.VerifyFixtureAsync(driver, database, fixtureId, cancellationToken);
        var validation = await MoviesBackfill.ValidateAsync(driver, database, cancellationToken);
        if (validation.AlreadyApplied) throw new InvalidOperationException("A before-image must be captured before the identity migration is applied.");
        var observed = await ObserveAsync(driver, database, fixtureId, cancellationToken);
        ValidateObserved(observed, applied: false);
        var plan = MoviesBackfill.Plan();
        var artifact = new Backup(1, database, fixtureId, plan.Id, plan.Checksum, plan.PredecessorId!,
            MoviesBackfill.SchemaId, observed.SchemaChecksum, observed.SchemaPredecessor,
            MoviesBackfill.SchemaId, observed.Labels, new(MoviesBackfill.LegacyKey, MoviesBackfill.Title),
            ApplicationIdWasAbsent: true);
        ValidateArtifact(artifact, database);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(artifact, Json);
        if (bytes.Length > MaximumBytes) throw Invalid();
        cancellationToken.ThrowIfCancellationRequested();
        // CreateNew supplies the race-safe no-overwrite guarantee. On interrupted I/O a partial file may remain;
        // validation rejects it. Choose a new path after review, never replace earlier evidence automatically.
        await using var stream = new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
        stream.Flush(flushToDisk: true);
    }

    internal static async Task ValidateForApplyAsync(IDriver driver, string database, string path,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Bound and validate persisted evidence before any transport or mutation. Never trust JSON to carry secrets.
        var artifact = await ReadAsync(path, cancellationToken);
        ValidateArtifact(artifact, database);
        await MoviesFixture.VerifyFixtureAsync(driver, database, artifact.FixtureId, cancellationToken);
        var validation = await MoviesBackfill.ValidateAsync(driver, database, cancellationToken);
        var current = await ObserveAsync(driver, database, artifact.FixtureId, cancellationToken);
        ValidateObserved(current, validation.AlreadyApplied);
        if (!current.Labels.SequenceEqual(artifact.Labels, StringComparer.Ordinal)
            || current.SchemaChecksum != artifact.SchemaChecksum || current.SchemaPredecessor != artifact.SchemaPredecessor)
            throw new InvalidOperationException("Current legacy row labels or reviewed schema history differ from the preserved before-image.");
        // ValidateAsync checks present-day data even on replay. The original file remains an absent-ID before-image.
        // These read-committed preflights are not a global snapshot/lock; serialize sample commands and external writers.
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static async Task<Backup> ReadAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(BackupPath(path), FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is <= 0 or > MaximumBytes) throw Invalid();
        var bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes, cancellationToken);
        if (stream.ReadByte() != -1) throw Invalid();
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
            RejectDuplicates(document.RootElement);
            if (document.RootElement.ValueKind != JsonValueKind.Object || document.RootElement.EnumerateObject().Count() != 13
                || !document.RootElement.TryGetProperty("Properties", out var properties)
                || properties.ValueKind != JsonValueKind.Object || properties.EnumerateObject().Count() != 2)
                throw Invalid();
            return JsonSerializer.Deserialize<Backup>(bytes, Json) ?? throw Invalid();
        }
        catch (JsonException) { throw Invalid(); }
    }

    private static void RejectDuplicates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw Invalid();
                RejectDuplicates(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) RejectDuplicates(item);
    }

    private static void ValidateArtifact(Backup value, string database)
    {
        ValidateIdentity(database, value.FixtureId);
        var plan = MoviesBackfill.Plan();
        if (value.FormatVersion != 1 || value.Database != database || value.PlanId != plan.Id || value.PlanChecksum != plan.Checksum
            || value.PredecessorId != plan.PredecessorId || value.SchemaId != MoviesBackfill.SchemaId
            || value.JournalHeadBefore != MoviesBackfill.SchemaId || value.SchemaPredecessor is { Length: > 128 }
            || value.SchemaChecksum != MoviesBackfill.SchemaPlan(value.SchemaPredecessor).Checksum
            || !ValidLabels(value.Labels) || value.Properties is null || value.Properties.LegacyCatalogKey != MoviesBackfill.LegacyKey
            || value.Properties.Title != MoviesBackfill.Title || !value.ApplicationIdWasAbsent)
            throw Invalid();
    }

    private static void ValidateIdentity(string database, string? fixtureId)
    {
        if (database != "neo4j" || !Guid.TryParseExact(fixtureId, "N", out var id) || id.ToString("N") != fixtureId)
            throw Invalid();
    }

    private static bool ValidLabels(string[]? labels) => labels is { Length: > 0 and <= 16 }
        && labels.Contains(MoviesBackfill.Label, StringComparer.Ordinal)
        && labels.All(label => !string.IsNullOrEmpty(label) && label.Length <= 128)
        && labels.SequenceEqual(labels.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal), StringComparer.Ordinal);

    private static void ValidateObserved(Observation value, bool applied)
    {
        string[] expectedKeys = applied ? ["appId", "legacyCatalogKey", "title"] : ["legacyCatalogKey", "title"];
        if (!ValidLabels(value.Labels) || value.LabelCount != value.Labels.Length || value.PropertyCount != expectedKeys.Length
            || !value.Keys.Order(StringComparer.Ordinal).SequenceEqual(expectedKeys, StringComparer.Ordinal)
            || !value.ValuesMatch || !value.MarkerMatch || !value.OwnerFree || !value.ActiveFree
            || value.HasApplicationId != applied || !value.ApplicationIdMatches
            || (!applied && value.Head != MoviesBackfill.SchemaId)
            || value.SchemaStatus != "Completed" || value.SchemaPredecessor is { Length: > 128 }
            || value.SchemaChecksum != MoviesBackfill.SchemaPlan(value.SchemaPredecessor).Checksum)
            throw new InvalidOperationException("Legacy before-image capture found changed or unreviewed row/schema/journal state.");
    }

    private static async Task<Observation> ObserveAsync(IDriver driver, string database, string fixtureId, CancellationToken cancellationToken)
    {
        // Only finite labels/keys and exact-match booleans leave Neo4j; unknown property values and owner tokens never do.
        const string query = "MATCH (f:GraphBridgeFixture), (n:GraphBridgeMoviesLegacyMovie), "
            + "(c:__GraphBridgeMigrationControl {id:'database'}), (h:__GraphBridgeMigration {id:$schema}) "
            + "RETURN f.id = $fixture AS marker, labels(n)[..17] AS labels,size(labels(n)) AS labelCount, "
            + "keys(n)[..4] AS keys,size(keys(n)) AS propertyCount, "
            + "coalesce(n.legacyCatalogKey = $legacy AND n.title = $title,false) AS valuesMatch, "
            + "n.appId IS NOT NULL AS hasId,coalesce(n.appId = $appId,n.appId IS NULL) AS idMatches, "
            + "substring(c.headId,0,129) AS head,c.ownerToken IS NULL AS ownerFree,c.activeMigrationId IS NULL AS activeFree, "
            + "substring(h.checksum,0,65) AS checksum,substring(h.predecessorId,0,129) AS predecessor, "
            + "substring(h.status,0,16) AS status LIMIT 2";
        IAsyncSession? session = null;
        IAsyncTransaction? transaction = null;
        Exception? primary = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            session = driver.AsyncSession(builder => builder.WithDatabase(database).WithDefaultAccessMode(AccessMode.Read).WithFetchSize(2));
            transaction = await session.BeginTransactionAsync(builder => builder.WithTimeout(TimeSpan.FromSeconds(30)));
            var cursor = await transaction.RunAsync(query, new { schema = MoviesBackfill.SchemaId, fixture = fixtureId,
                legacy = MoviesBackfill.LegacyKey, title = MoviesBackfill.Title, appId = MoviesBackfill.ApplicationId.ToString("D") });
            var rows = new List<Observation>();
            while (await cursor.FetchAsync())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var row = cursor.Current;
                rows.Add(new(row["labels"].As<List<string>>().Order(StringComparer.Ordinal).ToArray(), row["labelCount"].As<long>(),
                    row["keys"].As<List<string>>().ToArray(), row["propertyCount"].As<long>(), row["valuesMatch"].As<bool>(),
                    row["marker"].As<bool>(), row["hasId"].As<bool>(), row["idMatches"].As<bool>(), row["head"] as string,
                    row["ownerFree"].As<bool>(), row["activeFree"].As<bool>(), row["checksum"] as string,
                    row["predecessor"] as string, row["status"] as string));
                if (rows.Count > 1) throw Invalid();
            }
            await cursor.ConsumeAsync();
            cancellationToken.ThrowIfCancellationRequested();
            if (rows.Count != 1) throw Invalid();
            return rows[0];
        }
        catch (Exception error) { primary = error; throw; }
        finally
        {
            Exception? cleanup = null;
            if (transaction is not null)
            {
                try { await transaction.RollbackAsync(); } catch (Exception error) { cleanup = error; }
                try { await transaction.DisposeAsync(); } catch (Exception error) { cleanup ??= error; }
            }
            if (session is not null)
                try { await session.DisposeAsync(); } catch (Exception error) { cleanup ??= error; }
            if (primary is null && cleanup is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(cleanup).Throw();
        }
    }

    private static string BackupPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var result = Path.GetFullPath(path);
        if (!Directory.Exists(Path.GetDirectoryName(result))) throw new DirectoryNotFoundException("Create the backup directory explicitly before capture.");
        return result;
    }
    private static InvalidOperationException Invalid() => new("Movies before-image backup is missing required bounded data or does not match the reviewed sample contract.");
    private sealed record Backup(int FormatVersion, string Database, string FixtureId, string PlanId, string PlanChecksum,
        string PredecessorId, string SchemaId, string? SchemaChecksum, string? SchemaPredecessor, string JournalHeadBefore,
        string[] Labels, BeforeProperties Properties, bool ApplicationIdWasAbsent);
    private sealed record BeforeProperties(string LegacyCatalogKey, string Title);
    private sealed record Observation(string[] Labels, long LabelCount, string[] Keys, long PropertyCount, bool ValuesMatch,
        bool MarkerMatch, bool HasApplicationId, bool ApplicationIdMatches, string? Head, bool OwnerFree, bool ActiveFree,
        string? SchemaChecksum, string? SchemaPredecessor, string? SchemaStatus);
}
