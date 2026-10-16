using System.Collections.ObjectModel;
using GraphBridge.Samples.Movies;
using Microsoft.EntityFrameworkCore;
using Neo4j.Driver;

namespace GraphBridge.Samples.MoviesDemo;

public sealed record MoviesResult(int MoviesRead, int ParallelActingCredits, int UpdatedEntries,
    bool ReferenceFixup, bool IdentityResolution, bool RolesPreserved, bool RollbackVerified,
    long OriginalBillingOrder, string Cypher, IReadOnlyList<string> Roles, string SourceFingerprint, string TargetFingerprint);

/// <summary>Runs generated EF mappings against the reviewed fixed fixture. The edit demonstration is always rolled back.</summary>
public static class MoviesWorkflow
{
    public static readonly Guid FirstPersonId = Guid.Parse("11000000-0000-0000-0000-000000000001");
    public static readonly Guid FirstMovieId = Guid.Parse("22000000-0000-0000-0000-000000000001");
    public static readonly Guid FirstActingCreditId = Guid.Parse("33000000-0000-0000-0000-000000000001");

    public static MoviesContext Context(IDriver driver, string database)
        => new(new DbContextOptionsBuilder().UseNeo4j(driver, database).Options);

    /// <summary>Empty EF model for native DTO browsing before keys are resolved. No generated entity is tracked or materialized.</summary>
    public static DbContext BrowseContext(IDriver driver, string database)
        => new(new DbContextOptionsBuilder().UseNeo4j(driver, database).Options);

    public static async Task<IReadOnlyList<ScaffoldRelationshipDto>> BrowseAsync(IDriver driver, string database,
        int limit = 20, CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        cancellationToken.ThrowIfCancellationRequested();
        await using var db = BrowseContext(driver, database);
        // Literal identifiers are reviewed sample scope; both returned rows and buffered rows have a finite bound.
        return await db.Database.QueryCypherAsync(
            "MATCH (p:GraphBridgeMoviesPerson)-[r:GRAPHBRIDGE_MOVIES_ACTED_IN|GRAPHBRIDGE_MOVIES_DIRECTED]->(m:GraphBridgeMoviesMovie) "
            + "RETURN type(r) AS type,labels(p) AS sourceLabels,labels(m) AS targetLabels,properties(r) AS properties LIMIT $limit",
            new Dictionary<string, object?> { ["limit"] = (long)limit }, ProjectRelationship, maxRows: limit, cancellationToken);
    }

    public static async Task<MoviesResult> RunAsync(IDriver driver, string database, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sourceFingerprint = (await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "movies-schema", "graphbridge.binding.sha256"), cancellationToken)).Trim();
        string targetFingerprint;
        int movieCount;
        string cypher;
        await using (var read = Context(driver, database))
        {
            targetFingerprint = read.Database.GetGraphSchemaManifest().Fingerprint;
            if (sourceFingerprint != targetFingerprint)
                throw new InvalidOperationException("The reviewed scaffold fingerprint differs from the finalized runtime storage mapping.");
            var releasedFrom = 2024L;
            var query = read.MovieSet.AsNoTracking().Where(movie => movie.Released >= releasedFrom)
                .OrderBy(movie => movie.Released).Take(10);
            cypher = query.ToQueryString();
            var movies = await query.ToListAsync(cancellationToken);
            if (movies.Count != 2 || movies[0].MovieId != FirstMovieId || movies[1].Tagline is not null)
                throw new InvalidOperationException("The reviewed two-movie fixture or generated required/nullable scalar mappings differ from this sample.");
            if (read.ChangeTracker.Entries().Any())
                throw new InvalidOperationException("AsNoTracking unexpectedly retained movie entities.");
            movieCount = movies.Count;
        }

        long originalBillingOrder;
        int updated;
        int parallel;
        bool fixup;
        bool identity;
        IReadOnlyList<string> originalRoles;
        await using (var edit = Context(driver, database))
        await using (var transaction = await edit.Database.BeginTransactionAsync(cancellationToken))
        {
            var personId = FirstPersonId;
            var movieId = FirstMovieId;
            var creditId = FirstActingCreditId;
            var person = await edit.PersonSet.Where(person => person.PersonId == personId).SingleAsync(cancellationToken);
            var movie = await edit.MovieSet.Where(movie => movie.MovieId == movieId).SingleAsync(cancellationToken);
            var credits = await edit.ActingCreditSet.Where(credit => credit.SourcePersonId == personId && credit.TargetMovieId == movieId)
                .OrderBy(credit => credit.BillingOrder).ToListAsync(cancellationToken);
            parallel = credits.Count;
            if (parallel != 2) throw new InvalidOperationException("The sample expects two independently identified parallel acting credits.");
            var first = credits.Single(credit => credit.ActingCreditId == creditId);
            fixup = credits.All(credit => ReferenceEquals(credit.Source, person) && ReferenceEquals(credit.Target, movie));
            identity = ReferenceEquals(first, await edit.ActingCreditSet.Where(credit => credit.ActingCreditId == creditId).SingleAsync(cancellationToken));
            if (!fixup || !identity) throw new InvalidOperationException("Generated endpoint roles or EF tracking identity did not fix up as expected.");
            var wireBefore = await ReadActingCreditAsync(edit, creditId, cancellationToken);
            originalRoles = Roles(wireBefore);
            if (!originalRoles.SequenceEqual(new[] { "Navigator", "Narrator" }))
                throw new InvalidOperationException("The native DTO did not preserve the original roles array.");
            originalBillingOrder = first.BillingOrder;
            first.BillingOrder = checked(originalBillingOrder + 10);
            updated = await edit.SaveChangesAsync(cancellationToken);
            var wireAfter = await ReadActingCreditAsync(edit, creditId, cancellationToken);
            if (updated != 1 || wireAfter.Properties["billingOrder"].As<long>() != first.BillingOrder
                || !Roles(wireAfter).SequenceEqual(originalRoles))
                throw new InvalidOperationException("The tracked scalar update or shared-transaction DTO observation changed the omitted roles payload.");
            // There is no commit, cleanup deletion or restoration write: the entire example mutation is rolled back.
            await transaction.RollbackAsync(CancellationToken.None);
        }

        await using (var verify = Context(driver, database))
        {
            var creditId = FirstActingCreditId;
            var unchanged = await verify.ActingCreditSet.Where(credit => credit.ActingCreditId == creditId).SingleAsync(cancellationToken);
            var wire = await ReadActingCreditAsync(verify, creditId, cancellationToken);
            if (unchanged.BillingOrder != originalBillingOrder || !Roles(wire).SequenceEqual(originalRoles))
                throw new InvalidOperationException("A fresh context did not observe the original billing order and roles after rollback.");
            var directing = await verify.DirectingCreditSet.Where(credit => credit.TargetMovieId == FirstMovieId).ToListAsync(cancellationToken);
            if (directing.Count != 1 || directing[0].CreditNote != "Original cut")
                throw new InvalidOperationException("The separately identified directed edge mapping did not read its payload.");
        }
        return new(movieCount, parallel, updated, fixup, identity, true, true, originalBillingOrder, cypher, originalRoles, sourceFingerprint, targetFingerprint);
    }

    public static async Task<ScaffoldRelationshipDto> ReadActingCreditAsync(DbContext db, Guid id,
        CancellationToken cancellationToken = default)
    {
        var records = await db.Database.QueryCypherAsync(
            "MATCH (p:GraphBridgeMoviesPerson)-[r:GRAPHBRIDGE_MOVIES_ACTED_IN {actingCreditId:$id}]->(m:GraphBridgeMoviesMovie) "
            + "RETURN type(r) AS type,labels(p) AS sourceLabels,labels(m) AS targetLabels,properties(r) AS properties LIMIT 2",
            new Dictionary<string, object?> { ["id"] = id.ToString("D") }, ProjectRelationship, maxRows: 1, cancellationToken);
        return records.Single();
    }

    public static IReadOnlyList<string> Roles(ScaffoldRelationshipDto relationship)
    {
        if (!relationship.Properties.TryGetValue("roles", out var value) || value is null)
            throw new InvalidOperationException("Acting-credit DTO lacks its original roles payload.");
        return Array.AsReadOnly(value.As<List<string>>().ToArray());
    }

    private static ScaffoldRelationshipDto ProjectRelationship(IRecord record)
        => new(record["type"].As<string>(), Array.AsReadOnly(record["sourceLabels"].As<List<string>>().ToArray()),
            Array.AsReadOnly(record["targetLabels"].As<List<string>>().ToArray()),
            new ReadOnlyDictionary<string, object?>(record["properties"].As<Dictionary<string, object>>()
                .ToDictionary(pair => pair.Key, pair => (object?)pair.Value, StringComparer.Ordinal)));
}
