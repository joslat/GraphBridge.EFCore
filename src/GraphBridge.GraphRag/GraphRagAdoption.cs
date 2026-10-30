using System.Security.Cryptography;
using System.Text;
using GraphBridge.EntityFrameworkCore.Infrastructure;
using GraphBridge.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Neo4j.Driver;

namespace GraphBridge.GraphRag;

/// <summary>Options for adopting a lexical graph another tool built (ADR 0052, section 13).</summary>
public sealed class GraphRagAdoptionOptions
{
    /// <summary>
    /// A local file per document, by the document's <see cref="DocumentPathProperty"/>. When every chunk of the document is
    /// found in the file, byte-exact and in order, the chunks get the file's byte spans; otherwise they are chunk-grounded.
    /// </summary>
    public IReadOnlyDictionary<string, string>? SourceFiles { get; init; }

    /// <summary>The media type recorded for a document without a source file; a source file's extension decides otherwise.</summary>
    public string MediaType { get; init; } = "text/plain";

    /// <summary>The document property that names it; neo4j-graphrag writes <c>path</c>.</summary>
    public string DocumentPathProperty { get; init; } = "path";
}

/// <summary>
/// What an adoption would do, read without writing anything. <see cref="Blockers"/> lists what stops it: a chunk without a
/// document, two documents with one path, a missing or different vector index, nodes a GraphBridge run already owns.
/// </summary>
public sealed record GraphRagAdoptionPreview(int Documents, int Chunks, int ChunkLinks, int NextLinks, int SourceGroundedDocuments,
    int ChunkGroundedDocuments, int ChunkGroundedChunks, IReadOnlyList<string> Blockers)
{
    public bool CanApply => Blockers.Count == 0;
}

/// <summary>What an adoption wrote, under one run.</summary>
public sealed record GraphRagAdoptionReceipt(string RunId, int Documents, int Chunks, int SourceGroundedDocuments, int ChunkGroundedDocuments);

/// <summary>What an undo removed: the properties of the run's documents and chunks, and the run on its relationships.</summary>
public sealed record GraphRagAdoptionUndo(int Documents, int Chunks, int Relationships);

/// <summary>Adopt mode (ADR 0052, section 13): take over a lexical graph neo4j-graphrag built, so GraphBridge can answer from it.</summary>
public static class GraphRagAdoption
{
    /// <summary>A plan for the graph under <paramref name="layout"/>'s labels, such as <see cref="GraphRagLayout.Neo4jGraphRag"/>.</summary>
    public static GraphRagAdoptionPlan Plan(GraphRagLayout layout, GraphRagEmbeddingSet embeddingSet, GraphRagAdoptionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(embeddingSet);
        layout.Validate();
        embeddingSet.Validate();
        options ??= new GraphRagAdoptionOptions();
        ArgumentException.ThrowIfNullOrWhiteSpace(options.MediaType);
        if (!IsName(options.DocumentPathProperty))
            throw new ArgumentException("GraphRagAdoptionOptions.DocumentPathProperty must match [A-Za-z_][A-Za-z0-9_]*.", nameof(options));
        return new(layout, embeddingSet, options);
    }

    private static bool IsName(string? value)
        => !string.IsNullOrEmpty(value) && (char.IsAsciiLetter(value[0]) || value[0] == '_') && value.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');
}

/// <summary>
/// One adoption: <see cref="PrepareSteps"/> in the application's migration plan, then <see cref="PreviewAsync"/>, the dry
/// run, then <see cref="ApplyAsync"/>. <see cref="UndoAsync"/> removes what a run added.
/// </summary>
// Workaround for GR-06 (entities without a key): the nodes another tool wrote have no key EF can track, so adoption reads and
// updates them with native Cypher by element ID.
public sealed class GraphRagAdoptionPlan
{
    private const string ChunkGrounding = "chunk";
    private static readonly UTF8Encoding Strict = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private readonly GraphRagLayout _layout;
    private readonly GraphRagEmbeddingSet _set;
    private readonly GraphRagAdoptionOptions _options;

    internal GraphRagAdoptionPlan(GraphRagLayout layout, GraphRagEmbeddingSet set, GraphRagAdoptionOptions options)
    {
        (_layout, _set, _options) = (layout, set, options);
        // An index the layout names is the other tool's: adopted as it is, and checked by the dry run. The migration engine
        // creates only absent names, so it isn't a step.
        PrepareSteps = Array.AsReadOnly(GraphRagSchema.CreateSteps(layout, set)
            .Where(step => step.Schema is not { IsIndex: true } index || (index.Name != layout.VectorIndexName && index.Name != layout.FullTextIndexName))
            .ToArray());
    }

    /// <summary>
    /// The key constraints, and the indexes the layout doesn't name. Name neo4j-graphrag's existing vector index with
    /// <see cref="GraphRagLayout.VectorIndexName"/> (Neo4j allows one per label and property), and its full-text index, if it
    /// has one, with <see cref="GraphRagLayout.FullTextIndexName"/>.
    /// </summary>
    public IReadOnlyList<GraphMigrationStep> PrepareSteps { get; }

    /// <summary>The dry run: reads the graph and the source files, and writes nothing.</summary>
    public async Task<GraphRagAdoptionPreview> PreviewAsync(DbContext db, CancellationToken cancellationToken = default)
    {
        var (preview, _) = await ReadAsync(Graph(db), cancellationToken).ConfigureAwait(false);
        return preview;
    }

    /// <summary>
    /// Adds the keys, the chunks' document, spans and grounding, the embedding set, and the run, document by document, one
    /// transaction each. Writes nothing else. Throws when the preview has blockers. When a document fails, or the call is
    /// cancelled, the run is marked failed and the exception names it: <see cref="UndoAsync"/> removes what it wrote.
    /// </summary>
    public async Task<GraphRagAdoptionReceipt> ApplyAsync(DbContext db, CancellationToken cancellationToken = default)
    {
        var graph = Graph(db);
        var (preview, documents) = await ReadAsync(graph, cancellationToken).ConfigureAwait(false);
        if (!preview.CanApply) throw new InvalidOperationException("The graph can't be adopted: " + string.Join("; ", preview.Blockers));
        var run = new GraphRagIngestionRun
        {
            Id = Guid.NewGuid().ToString("N"), StartedUtc = DateTimeOffset.UtcNow, Status = "Running", SettingsFingerprint = "adopted:" + _set.Id
        };
        db.Add(run);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var (chunks, adopted) = (0, 0);
        try
        {
            await using (var session = graph.Driver.AsyncSession(o => o.WithDatabase(graph.Database)))
            {
                foreach (var document in documents)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await session.ExecuteWriteAsync(async transaction =>
                    {
                        var cursor = await transaction.RunAsync(
                            $"""
                            MATCH (d:`{_layout.DocumentLabel}`) WHERE elementId(d) = $element AND d.RunId IS NULL
                            SET d.Id = $id, d.SourceUri = $uri, d.MediaType = $mediaType, d.ContentHash = $hash, d.LengthBytes = $length, d.RunId = $run
                            WITH d
                            UNWIND $chunks AS row
                            MATCH (c:`{_layout.ChunkLabel}`)-[r:`{_layout.ChunkDocumentType}`]->(d) WHERE elementId(c) = row.element AND c.RunId IS NULL
                            SET c.`{_layout.ChunkIdProperty}` = row.id, c.DocumentId = $id, c.StartByte = row.start, c.EndByte = row.end,
                                c.Grounding = row.grounding, c.EmbeddingSetId = $set, c.RunId = $run, r.RunId = $run
                            RETURN count(c) AS chunks
                            """,
                            new Dictionary<string, object?>
                            {
                                ["element"] = document.Element, ["id"] = document.Id, ["uri"] = document.SourceUri, ["mediaType"] = document.MediaType,
                                ["hash"] = document.ContentHash, ["length"] = document.LengthBytes, ["run"] = run.Id, ["set"] = _set.Id,
                                ["chunks"] = document.Chunks.Select(c => new Dictionary<string, object?>
                                {
                                    ["element"] = c.Element, ["id"] = c.Id, ["start"] = (long)c.StartByte, ["end"] = (long)c.EndByte,
                                    ["grounding"] = document.Grounded ? null : ChunkGrounding
                                }).ToList()
                            }).ConfigureAwait(false);
                        var written = (await cursor.SingleAsync().ConfigureAwait(false))["chunks"].As<int>();
                        if (written != document.Chunks.Count)
                            throw new InvalidOperationException($"The graph changed while it was adopted: {written} of {document.Chunks.Count} chunks of '{document.Path}' were written.");
                        var next = await transaction.RunAsync(
                            $"MATCH (a:`{_layout.ChunkLabel}`)-[r:`{_layout.NextChunkType}`]->(b:`{_layout.ChunkLabel}`) WHERE a.DocumentId = $id AND b.DocumentId = $id SET r.RunId = $run",
                            new Dictionary<string, object?> { ["id"] = document.Id, ["run"] = run.Id }).ConfigureAwait(false);
                        await next.ConsumeAsync().ConfigureAwait(false);
                    }).ConfigureAwait(false);
                    chunks += document.Chunks.Count;
                    adopted++;
                }
            }
        }
        catch (Exception error)
        {
            // The documents before the failed one are adopted: the run says so, and its ID is what undoes them.
            run.Status = "Failed";
            run.CompletedUtc = DateTimeOffset.UtcNow;
            run.DocumentsIngested = adopted;
            try { await db.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception) { /* The run stays Running; its ID is still in the exception. */ }
            var message = $"Adoption run {run.Id} stopped after {adopted} of {documents.Count} documents; UndoAsync with that run removes what it wrote.";
            if (error is OperationCanceledException) throw new OperationCanceledException(message, error, cancellationToken);
            throw new InvalidOperationException(message + " " + error.Message, error);
        }
        run.Status = "Completed";
        run.CompletedUtc = DateTimeOffset.UtcNow;
        run.DocumentsIngested = documents.Count;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new(run.Id, documents.Count, chunks, documents.Count(d => d.Grounded), documents.Count(d => !d.Grounded));
    }

    /// <summary>Removes exactly what <paramref name="runId"/> added, and its run. What a later run wrote stays. Throws, and
    /// changes nothing, when <paramref name="runId"/> isn't an adoption run.</summary>
    public async Task<GraphRagAdoptionUndo> UndoAsync(DbContext db, string runId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        var graph = Graph(db);
        await using var session = graph.Driver.AsyncSession(o => o.WithDatabase(graph.Database));
        return await session.ExecuteWriteAsync(async transaction =>
        {
            var parameters = new Dictionary<string, object?> { ["run"] = runId };
            async Task<int> CountAsync(string cypher)
            {
                var cursor = await transaction.RunAsync(cypher, parameters).ConfigureAwait(false);
                return (await cursor.SingleAsync().ConfigureAwait(false))["n"].As<int>();
            }
            // Only an adoption run: an ingestion run's documents would lose keys nothing can restore.
            if (await CountAsync($"MATCH (r:`{_layout.RunLabel}`) WHERE r.Id = $run AND r.SettingsFingerprint STARTS WITH 'adopted:' RETURN count(r) AS n").ConfigureAwait(false) == 0)
                throw new InvalidOperationException($"Run {runId} is not an adoption run of this graph: nothing was undone.");
            var relationships = await CountAsync(
                $"MATCH (:`{_layout.ChunkLabel}`)-[r:`{_layout.ChunkDocumentType}`|`{_layout.NextChunkType}`]->() WHERE r.RunId = $run REMOVE r.RunId RETURN count(r) AS n").ConfigureAwait(false);
            var chunks = await CountAsync(
                $"MATCH (c:`{_layout.ChunkLabel}`) WHERE c.RunId = $run "
                + $"REMOVE c.`{_layout.ChunkIdProperty}`, c.DocumentId, c.StartByte, c.EndByte, c.Grounding, c.EmbeddingSetId, c.RunId RETURN count(c) AS n").ConfigureAwait(false);
            var documents = await CountAsync(
                $"MATCH (d:`{_layout.DocumentLabel}`) WHERE d.RunId = $run REMOVE d.Id, d.SourceUri, d.MediaType, d.ContentHash, d.LengthBytes, d.RunId RETURN count(d) AS n")
                .ConfigureAwait(false);
            await CountAsync($"MATCH (r:`{_layout.RunLabel}`) WHERE r.Id = $run AND r.SettingsFingerprint STARTS WITH 'adopted:' DELETE r RETURN count(*) AS n").ConfigureAwait(false);
            return new GraphRagAdoptionUndo(documents, chunks, relationships);
        }).ConfigureAwait(false);
    }

    private sealed record AdoptedChunk(string Element, string Id, int StartByte, int EndByte);

    private sealed record AdoptedDocument(string Element, string Path, string Id, string SourceUri, string MediaType, string ContentHash, long LengthBytes,
        bool Grounded, IReadOnlyList<AdoptedChunk> Chunks);

    private sealed record GraphAccess(IDriver Driver, string? Database);

    private GraphAccess Graph(DbContext db)
    {
        ArgumentNullException.ThrowIfNull(db);
        var layout = db.Model.FindAnnotation(Microsoft.EntityFrameworkCore.GraphRagModelBuilderExtensions.LayoutAnnotation)?.Value as GraphRagLayout;
        if (layout != _layout) throw new ArgumentException("The context maps a different GraphRAG layout than this adoption plan.", nameof(db));
        var extension = db.GetService<IDbContextOptions>().FindExtension<Neo4jDbContextOptionsExtension>()
            ?? throw new InvalidOperationException("Adoption needs a context configured with UseNeo4j.");
        return new(extension.Driver, extension.DatabaseName);
    }

    // Reads the documents and their chunks, one document at a time, and decides each one's keys, spans and grounding.
    private async Task<(GraphRagAdoptionPreview Preview, IReadOnlyList<AdoptedDocument> Documents)> ReadAsync(GraphAccess graph, CancellationToken cancellationToken)
    {
        var blockers = new List<string>();
        var parameters = new Dictionary<string, object?> { ["dimensions"] = (long)_set.Dimensions, ["index"] = _layout.VectorIndexFor(_set) };
        await using var session = graph.Driver.AsyncSession(o => o.WithDatabase(graph.Database).WithDefaultAccessMode(AccessMode.Read));
        async Task<IReadOnlyList<IRecord>> ReadRowsAsync(string cypher, Dictionary<string, object?> values)
            => await session.ExecuteReadAsync(async transaction => await (await transaction.RunAsync(cypher, values).ConfigureAwait(false)).ToListAsync(cancellationToken)
                .ConfigureAwait(false)).ConfigureAwait(false);

        var counts = (await ReadRowsAsync(
            $"""
            CALL () {"{"} MATCH (d:`{_layout.DocumentLabel}`) RETURN count(d) AS documents, count(d.RunId) AS ownedDocuments {"}"}
            CALL () {"{"} MATCH (c:`{_layout.ChunkLabel}`) RETURN count(c) AS chunks, count(c.RunId) AS ownedChunks,
                count(CASE WHEN NOT EXISTS {"{"} (c)-[:`{_layout.ChunkDocumentType}`]->(:`{_layout.DocumentLabel}`) {"}"} THEN 1 END) AS orphans {"}"}
            CALL () {"{"} MATCH (:`{_layout.ChunkLabel}`)-[r:`{_layout.ChunkDocumentType}`]->(:`{_layout.DocumentLabel}`) RETURN count(r) AS chunkLinks {"}"}
            CALL () {"{"} MATCH (:`{_layout.ChunkLabel}`)-[r:`{_layout.NextChunkType}`]->(:`{_layout.ChunkLabel}`) RETURN count(r) AS nextLinks {"}"}
            RETURN documents, ownedDocuments, chunks, ownedChunks, orphans, chunkLinks, nextLinks
            """, parameters).ConfigureAwait(false)).Single();
        int Count(string name) => counts[name].As<int>();
        if (Count("ownedDocuments") + Count("ownedChunks") > 0)
            blockers.Add($"{Count("ownedDocuments")} documents and {Count("ownedChunks")} chunks already belong to a GraphBridge run");
        if (Count("orphans") > 0) blockers.Add($"{Count("orphans")} chunks have no {_layout.ChunkDocumentType} relationship to a document");
        var index = await ReadRowsAsync(
            "SHOW VECTOR INDEXES YIELD name, labelsOrTypes, properties, options WHERE name = $index "
            + "RETURN labelsOrTypes, properties, options.indexConfig['vector.dimensions'] AS dimensions", parameters).ConfigureAwait(false);
        if (index.Count == 0) blockers.Add($"no vector index named '{_layout.VectorIndexFor(_set)}'; name the existing one with GraphRagLayout.VectorIndexName");
        else if (index[0]["dimensions"].As<long?>() is { } dimensions && dimensions != _set.Dimensions)
            blockers.Add($"the vector index '{_layout.VectorIndexFor(_set)}' has {dimensions} dimensions; the embedding set has {_set.Dimensions}");
        if (_layout.FullTextIndexName is { } fullText
            && (await ReadRowsAsync("SHOW FULLTEXT INDEXES YIELD name WHERE name = $name RETURN name", new() { ["name"] = fullText }).ConfigureAwait(false)).Count == 0)
            blockers.Add($"no full-text index named '{fullText}'");

        var listed = await ReadRowsAsync(
            $"MATCH (d:`{_layout.DocumentLabel}`) WHERE d.RunId IS NULL RETURN elementId(d) AS element, d.`{_options.DocumentPathProperty}` AS path ORDER BY element",
            parameters).ConfigureAwait(false);
        var paths = listed.Select(r => r["path"].As<string?>()).ToArray();
        if (paths.Any(string.IsNullOrEmpty)) blockers.Add($"{paths.Count(string.IsNullOrEmpty)} documents have no '{_options.DocumentPathProperty}'");
        if (paths.Where(p => !string.IsNullOrEmpty(p)).GroupBy(p => p, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1) is { } duplicate)
            blockers.Add($"two documents have the path '{duplicate.Key}'");

        var documents = new List<AdoptedDocument>();
        var chunkGrounded = 0;
        foreach (var row in listed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var element = row["element"].As<string>();
            var path = row["path"].As<string?>();
            if (string.IsNullOrEmpty(path)) continue;
            var chunks = await ReadRowsAsync(
                $"MATCH (c:`{_layout.ChunkLabel}`)-[:`{_layout.ChunkDocumentType}`]->(d:`{_layout.DocumentLabel}`) WHERE elementId(d) = $element "
                + $"RETURN elementId(c) AS element, c.`{_layout.ChunkOrderProperty}` AS index, c.`{_layout.ChunkTextProperty}` AS text ORDER BY index",
                new Dictionary<string, object?> { ["element"] = element }).ConfigureAwait(false);
            if (chunks.Any(c => c["index"].As<long?>() is null || c["text"].As<string?>() is null))
            {
                blockers.Add($"chunks of '{path}' have no {_layout.ChunkOrderProperty} or {_layout.ChunkTextProperty}");
                continue;
            }
            var document = Decide(element, path, chunks.Select(c => (c["element"].As<string>(), (int)c["index"].As<long>(), c["text"].As<string>())).ToArray());
            if (!document.Grounded) chunkGrounded += document.Chunks.Count;
            documents.Add(document);
        }
        var preview = new GraphRagAdoptionPreview(Count("documents"), Count("chunks"), Count("chunkLinks"), Count("nextLinks"),
            documents.Count(d => d.Grounded), documents.Count(d => !d.Grounded), chunkGrounded, blockers);
        return (preview, documents);
    }

    // Source-grounded when the source file holds every chunk byte-exact, in order; chunk-grounded otherwise.
    private AdoptedDocument Decide(string element, string path, IReadOnlyList<(string Element, int Index, string Text)> chunks)
    {
        if (_options.SourceFiles?.TryGetValue(path, out var file) == true && File.Exists(file))
        {
            var source = GraphRagSources.File(file);
            var bytes = File.ReadAllBytes(file);
            string? text;
            try { text = Strict.GetString(bytes); }
            catch (DecoderFallbackException) { text = null; }
            if (text is not null && Place(text, chunks) is { } spans)
            {
                var id = GraphRagEmbeddingSet.Hash(source.SourceUri);
                return new(element, path, id, source.SourceUri, source.MediaType, Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.Length, true,
                    [.. chunks.Select((c, i) => new AdoptedChunk(c.Element, ChunkId(id, c.Index), spans[i].Start, spans[i].End))]);
            }
        }
        var uri = "adopted:" + path;
        var documentId = GraphRagEmbeddingSet.Hash(uri);
        var joined = Strict.GetBytes(string.Join('\n', chunks.Select(c => c.Text)));
        return new(element, path, documentId, uri, _options.MediaType, Convert.ToHexStringLower(SHA256.HashData(joined)),
            chunks.Sum(c => (long)Encoding.UTF8.GetByteCount(c.Text)), false,
            [.. chunks.Select(c => new AdoptedChunk(c.Element, ChunkId(documentId, c.Index), 0, Encoding.UTF8.GetByteCount(c.Text)))]);
    }

    // Each chunk byte-exact after the previous chunk's start, so overlapping chunks are found too; null when one isn't found.
    private static IReadOnlyList<(int Start, int End)>? Place(string text, IReadOnlyList<(string Element, int Index, string Text)> chunks)
    {
        var spans = new List<(int Start, int End)>();
        var from = 0;
        foreach (var chunk in chunks)
        {
            if (chunk.Text.Length == 0) return null;
            var found = text.IndexOf(chunk.Text, from, StringComparison.Ordinal);
            if (found < 0) return null;
            var start = Encoding.UTF8.GetByteCount(text.AsSpan(0, found));
            spans.Add((start, start + Encoding.UTF8.GetByteCount(chunk.Text)));
            from = found;
        }
        return spans;
    }

    private static string ChunkId(string documentId, int index) => GraphRagEmbeddingSet.Hash(string.Join('\u001f', documentId, "adopted-chunk", index));
}
