using GraphBridge.EntityFrameworkCore;
using GraphBridge.EntityFrameworkCore.Metadata;
using GraphBridge.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Neo4j.Driver;

namespace GraphBridge.GraphRag;

/// <summary>
/// Re-embedding as a reviewed plan (ADR 0052, section 8; GR-37). A vector index covers one label and property, so the new
/// embedding set is written to a staging property with its own index, and the application cuts over by naming that property
/// in its layout. Nothing is replaced in place, and every stage can be previewed.
/// </summary>
public static class GraphRagReembedding
{
    /// <param name="layout">The layout the application uses now.</param>
    /// <param name="from">The embedding set of the vectors stored now.</param>
    /// <param name="to">The new embedding set.</param>
    /// <param name="stagingProperty">An unused chunk property for the new vectors, such as <c>EmbeddingNext</c>; the next
    /// re-embedding swaps back.</param>
    public static GraphRagReembeddingPlan Plan(GraphRagLayout layout, GraphRagEmbeddingSet from, GraphRagEmbeddingSet to, string stagingProperty)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        layout.Validate();
        from.Validate();
        to.Validate();
        if (from.Id == to.Id) throw new ArgumentException("The new embedding set is the current one; there is nothing to re-embed.", nameof(to));
        // After cut-over the new set's index is searched by its derived name.
        var newLayout = layout with { ChunkEmbeddingProperty = stagingProperty, VectorIndexName = null };
        newLayout.Validate();
        string[] taken = [layout.ChunkEmbeddingProperty, layout.ChunkIdProperty, layout.ChunkTextProperty, layout.ChunkOrderProperty,
            nameof(GraphRagChunk.DocumentId), nameof(GraphRagChunk.StartByte), nameof(GraphRagChunk.EndByte), nameof(GraphRagChunk.TokenCount),
            nameof(GraphRagChunk.EmbeddingSetId), nameof(GraphRagChunk.RunId)];
        if (taken.Contains(stagingProperty, StringComparer.Ordinal))
            throw new ArgumentException($"The staging property '{stagingProperty}' is already a chunk property.", nameof(stagingProperty));
        return new(layout, newLayout, from, to);
    }
}

/// <summary>What a re-embedding plan would do, and how far it has got.</summary>
public sealed record GraphRagReembeddingPreview(int Chunks, int Backfilled, int Remaining, IReadOnlyList<string> PrepareCypher, IReadOnlyList<string> CleanupCypher);

/// <summary>
/// The stages of one re-embedding:
/// <list type="number">
/// <item><see cref="PrepareSteps"/> in the application's migration plan;</item>
/// <item><see cref="BackfillAsync"/> until <see cref="PreviewAsync"/> shows nothing remaining;</item>
/// <item>the application deploys <see cref="NewLayout"/> with the new set, then runs <see cref="CompleteAsync"/>;</item>
/// <item><see cref="CleanupSteps"/>, then <see cref="CleanupAsync"/>.</item>
/// </list>
/// </summary>
public sealed class GraphRagReembeddingPlan
{
    private const int MaxBatch = 1_000;

    internal GraphRagReembeddingPlan(GraphRagLayout layout, GraphRagLayout newLayout, GraphRagEmbeddingSet from, GraphRagEmbeddingSet to)
    {
        (Layout, NewLayout, From, To) = (layout, newLayout, from, to);
        var prefix = layout.SchemaPrefix;
        PrepareSteps = Array.AsReadOnly(new[]
        {
            GraphMigrationStep.CreateIndex("create-" + to.VectorIndexNameFor(prefix), GraphMigrationSchemaDefinition.VectorIndex(
                to.VectorIndexNameFor(prefix), "NODE", layout.ChunkLabel, newLayout.ChunkEmbeddingProperty, to.Dimensions, to.Similarity))
        });
        CleanupSteps = Array.AsReadOnly(new[]
        {
            GraphMigrationStep.DropIndex("drop-" + layout.VectorIndexFor(from), GraphMigrationSchemaDefinition.VectorIndex(
                layout.VectorIndexFor(from), "NODE", layout.ChunkLabel, layout.ChunkEmbeddingProperty, from.Dimensions, from.Similarity),
                $"Re-embedding to {to.Model}: the vectors of {from.Model} are no longer searched.")
        });
    }

    public GraphRagLayout Layout { get; }
    /// <summary>The layout after cut-over: the staging property becomes the chunk embedding property.</summary>
    public GraphRagLayout NewLayout { get; }
    public GraphRagEmbeddingSet From { get; }
    public GraphRagEmbeddingSet To { get; }
    /// <summary>The new set's vector index on the staging property.</summary>
    public IReadOnlyList<GraphMigrationStep> PrepareSteps { get; }
    /// <summary>Drops the old set's index; apply it after cut-over.</summary>
    public IReadOnlyList<GraphMigrationStep> CleanupSteps { get; }

    private string StagingSet => NewLayout.ChunkEmbeddingProperty + nameof(GraphRagChunk.EmbeddingSetId);
    private string Label => CypherName(Layout.ChunkLabel);
    private string Id => CypherName(Layout.ChunkIdProperty);

    /// <summary>Counts chunks, and those whose staging vector is already from the new set. Reads only.</summary>
    public async Task<GraphRagReembeddingPreview> PreviewAsync(DbContext db, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var counts = await db.Database.QueryCypherAsync(
            $"MATCH (c:{Label}) RETURN count(c) AS chunks, count(CASE WHEN c.{CypherName(StagingSet)} = $to THEN 1 END) AS done",
            new Dictionary<string, object?> { ["to"] = To.Id }, record => ((int)record["chunks"].As<long>(), (int)record["done"].As<long>()),
            maxRows: 1, cancellationToken).ConfigureAwait(false);
        var (chunks, done) = counts.Count == 1 ? counts[0] : (0, 0);
        return new(chunks, done, chunks - done, PrepareSteps.Select(s => GraphMigrationStepRenderer.Render(s).Cypher).ToArray(),
            CleanupSteps.Select(s => GraphMigrationStepRenderer.Render(s).Cypher).ToArray());
    }

    /// <summary>
    /// Embeds chunks whose staging vector isn't from the new set yet, <paramref name="batchSize"/> at a time, until none remain
    /// or <paramref name="maxChunks"/> are done. Safe to stop and run again. Returns how many chunks it embedded.
    /// </summary>
    public async Task<int> BackfillAsync(DbContext db, IEmbeddingGenerator<string, Embedding<float>> generator, int batchSize = 64, int? maxChunks = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(generator);
        if (batchSize is < 1 or > MaxBatch) throw new ArgumentOutOfRangeException(nameof(batchSize), $"batchSize must be 1 to {MaxBatch}.");
        if (maxChunks is < 1) throw new ArgumentOutOfRangeException(nameof(maxChunks), "maxChunks must be positive.");
        var done = 0;
        while (maxChunks is null || done < maxChunks)
        {
            var take = maxChunks is { } max ? Math.Min(batchSize, max - done) : batchSize;
            var page = await db.Database.QueryCypherAsync(
                $"MATCH (c:{Label}) WHERE c.{CypherName(StagingSet)} IS NULL OR c.{CypherName(StagingSet)} <> $to "
                + $"RETURN c.{Id} AS id, c.{CypherName(Layout.ChunkTextProperty)} AS text ORDER BY id LIMIT $limit",
                new Dictionary<string, object?> { ["to"] = To.Id, ["limit"] = (long)take },
                record => (Id: record["id"].As<string>(), Text: record["text"].As<string>()), maxRows: take, cancellationToken).ConfigureAwait(false);
            if (page.Count == 0) break;
            var generated = await generator.GenerateAsync(page.Select(p => p.Text), cancellationToken: cancellationToken).ConfigureAwait(false);
            if (generated.Count != page.Count) throw new InvalidOperationException("The embedding generator returned a different number of vectors than texts.");
            var rows = page.Select((p, i) => (object)new Dictionary<string, object?> { ["id"] = p.Id, ["vector"] = Vector(generated[i].Vector) }).ToArray();
            // Workaround for GR-06 (re-embedding): EF maps one embedding property, so the staging one is written by key until cut-over.
            await db.Database.ExecuteCypherAsync(
                $"UNWIND $rows AS row MATCH (c:{Label} {{{Id}: row.id}}) SET c.{CypherName(NewLayout.ChunkEmbeddingProperty)} = row.vector, c.{CypherName(StagingSet)} = $to",
                new Dictionary<string, object?> { ["rows"] = rows, ["to"] = To.Id }, cancellationToken: cancellationToken).ConfigureAwait(false);
            done += page.Count;
        }
        return done;
    }

    /// <summary>
    /// After cut-over, records the new set on every chunk whose vector is from it, so the health report's mixed-set check passes.
    /// Run it with the context that maps <see cref="NewLayout"/>. Returns how many chunks it marked.
    /// </summary>
    public async Task<int> CompleteAsync(DbContext db, CancellationToken cancellationToken = default)
    {
        RequireCutOver(db);
        var total = 0;
        int marked;
        do
        {
            var counts = await db.Database.ExecuteCypherAsync(
                $"MATCH (c:{Label}) WHERE c.{CypherName(StagingSet)} = $to WITH c LIMIT $limit "
                + $"SET c.{CypherName(nameof(GraphRagChunk.EmbeddingSetId))} = $to REMOVE c.{CypherName(StagingSet)}",
                new Dictionary<string, object?> { ["to"] = To.Id, ["limit"] = (long)MaxBatch }, cancellationToken: cancellationToken).ConfigureAwait(false);
            marked = counts.PropertiesSet / 2;
            total += marked;
        } while (marked == MaxBatch);
        return total;
    }

    /// <summary>After cut-over and <see cref="CleanupSteps"/>, removes the old vectors in pages. Returns how many it removed.</summary>
    public async Task<int> CleanupAsync(DbContext db, CancellationToken cancellationToken = default)
    {
        RequireCutOver(db);
        var total = 0;
        int removed;
        do
        {
            var counts = await db.Database.ExecuteCypherAsync(
                $"MATCH (c:{Label}) WHERE c.{CypherName(Layout.ChunkEmbeddingProperty)} IS NOT NULL WITH c LIMIT $limit REMOVE c.{CypherName(Layout.ChunkEmbeddingProperty)}",
                new Dictionary<string, object?> { ["limit"] = (long)MaxBatch }, cancellationToken: cancellationToken).ConfigureAwait(false);
            removed = counts.PropertiesSet;
            total += removed;
        } while (removed == MaxBatch);
        return total;
    }

    // Completing or cleaning up with the old model would mark or remove the vectors it still searches.
    private void RequireCutOver(DbContext db)
    {
        ArgumentNullException.ThrowIfNull(db);
        var embedding = db.Model.FindEntityType(typeof(GraphRagChunk))?.FindProperty(nameof(GraphRagChunk.Embedding));
        var stored = embedding?[GraphAnnotationNames.PropertyName] as string ?? embedding?.Name;
        var dimensions = embedding?[GraphAnnotationNames.VectorDimensions] as int?;
        if (stored != NewLayout.ChunkEmbeddingProperty || dimensions != To.Dimensions)
            throw new InvalidOperationException($"Cut over first: the context must map the chunk embedding as '{NewLayout.ChunkEmbeddingProperty}' with {To.Dimensions} dimensions (AddGraphRag with plan.NewLayout and the new set).");
    }

    private double[] Vector(ReadOnlyMemory<float> generated)
    {
        if (generated.Length != To.Dimensions) throw new InvalidOperationException($"The new generator returned {generated.Length} dimensions; the set has {To.Dimensions}.");
        var vector = new double[generated.Length];
        double norm = 0;
        for (var i = 0; i < vector.Length; i++)
        {
            var value = generated.Span[i];
            if (!float.IsFinite(value)) throw new InvalidOperationException("The new generator returned a vector with a non-finite value.");
            vector[i] = value;
            norm += (double)value * value;
        }
        if (norm == 0) throw new InvalidOperationException("The new generator returned a zero vector.");
        if (To.Normalize)
        {
            var scale = 1 / Math.Sqrt(norm);
            for (var i = 0; i < vector.Length; i++) vector[i] = (float)(vector[i] * scale);
        }
        return vector;
    }

    private static string CypherName(string name) => "`" + name + "`";
}
