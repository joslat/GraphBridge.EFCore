using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Neo4j.Driver;

namespace GraphBridge.GraphRag;

/// <summary>
/// Ingestion (ADR 0052, sections 4 and 7). Per document: load, lease, skip if unchanged, chunk, embed in parallel lanes,
/// extract when the model maps an ontology, then replace the document's sections, chunks, edges and extraction in one
/// transaction that first renews the lease. A run killed mid-way leaves each document complete or absent, and a rerun
/// skips what is already written. A failed extraction writes nothing of the document, so the next run tries it again.
/// </summary>
internal sealed class GraphRagIngestion<TContext>(IServiceScopeFactory scopes, GraphRagEmbeddingSet embeddingSet, GraphRagOptions options,
    IEnumerable<IGraphRagDocumentLoader> loaders, IGraphRagChunker chunker, IEmbeddingGenerator<string, Embedding<float>> generator,
    TimeProvider? clock = null, Func<IChatClient?>? extractionChat = null, IGraphRagExtractionVerifier? verifier = null)
    : IGraphRagIngestion where TContext : DbContext
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public Task<GraphRagIngestionReceipt> IngestAsync(IEnumerable<GraphRagSource> sources, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sources);
        return RunAsync(sources.Select(source => new Pending(source, null)), ChunkerFingerprint, cancellationToken);
    }

    public Task<GraphRagIngestionReceipt> IngestChunkedAsync(IEnumerable<GraphRagChunkedDocument> documents, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(documents);
        var list = documents.ToList();
        var fingerprints = list.Select(d => d.Plan.Fingerprint).Distinct(StringComparer.Ordinal).ToArray();
        if (fingerprints.Length > 1) throw new ArgumentException("Documents ingested together must come from one chunker; ingest each chunker's documents in a call of their own.", nameof(documents));
        return RunAsync(list.Select(document => new Pending(document.Source, document)), fingerprints.FirstOrDefault() ?? ChunkerFingerprint, cancellationToken);
    }

    // A source to load and chunk, or a document whose chunks were decided elsewhere.
    private sealed record Pending(GraphRagSource Source, GraphRagChunkedDocument? Chunked);

    private string ChunkerFingerprint => chunker is GraphRagChunker core ? core.Fingerprint : chunker.GetType().FullName ?? "custom";

    private async Task<GraphRagIngestionReceipt> RunAsync(IEnumerable<Pending> pending, string chunkerFingerprint, CancellationToken cancellationToken)
    {
        options.Ingestion.Validate();
        options.Chunking.Validate();
        var timer = Stopwatch.StartNew();
        using var activity = GraphRagDiagnostics.Start(GraphRagTracing.IngestActivity);
        var all = pending.Take(options.Ingestion.MaxDocuments + 1).ToList();
        var limits = new List<GraphRagLimit>();
        if (all.Count > options.Ingestion.MaxDocuments)
        {
            all.RemoveAt(all.Count - 1);
            limits.Add(new(nameof(GraphRagIngestionOptions.MaxDocuments), options.Ingestion.MaxDocuments));
        }
        RequireModel();
        var extractor = CreateExtractor();
        var extraction = extractor is null ? null : new GraphRagExtractionTally();

        var run = new GraphRagIngestionRun
        {
            Id = Guid.NewGuid().ToString("N"), StartedUtc = _clock.GetUtcNow(), Status = "Running",
            SettingsFingerprint = SettingsFingerprint(chunkerFingerprint, extractor)
        };
        await UpsertRunAsync(run, cancellationToken).ConfigureAwait(false);
        var outcomes = new List<GraphRagDocumentOutcome>();
        var tally = new Tally();
        try
        {
            foreach (var item in all)
            {
                cancellationToken.ThrowIfCancellationRequested();
                outcomes.Add(await IngestDocumentAsync(item, run, tally, extractor, extraction, cancellationToken).ConfigureAwait(false));
            }
        }
        finally
        {
            run.CompletedUtc = _clock.GetUtcNow();
            run.DocumentsIngested = outcomes.Count(o => o.Status == GraphRagDocumentStatus.Ingested);
            run.DocumentsUnchanged = outcomes.Count(o => o.Status == GraphRagDocumentStatus.Unchanged);
            run.DocumentsFailed = outcomes.Count(o => o.Status == GraphRagDocumentStatus.Failed);
            run.Status = cancellationToken.IsCancellationRequested || outcomes.Count < all.Count ? "Failed" : "Completed";
            await UpsertRunAsync(run, CancellationToken.None).ConfigureAwait(false);
            if (activity is not null)
            {
                activity.SetTag("graphrag.run_id", run.Id);
                activity.SetTag("graphrag.documents_ingested", run.DocumentsIngested);
                activity.SetTag("graphrag.documents_unchanged", run.DocumentsUnchanged);
                activity.SetTag("graphrag.documents_failed", run.DocumentsFailed);
                activity.SetTag("graphrag.chunks_written", tally.Chunks);
                activity.SetTag("graphrag.embedding_calls", tally.Calls);
                if (tally.Tokens is { } tokens) activity.SetTag("graphrag.input_tokens", tokens);
                if (extraction is not null)
                {
                    activity.SetTag("graphrag.extraction_calls", extraction.Calls);
                    activity.SetTag("graphrag.entities", extraction.Entities);
                    activity.SetTag("graphrag.facts", extraction.Facts);
                    activity.SetTag("graphrag.pruned", extraction.Pruned.Count);
                    activity.SetTag("graphrag.quarantined", extraction.Quarantined.Count);
                }
                if (run.Status == "Failed") GraphRagDiagnostics.Fail(activity, cancellationToken.IsCancellationRequested ? "cancelled" : "incomplete");
            }
        }
        return new(run.Id, outcomes, tally.Chunks, tally.Calls, tally.Tokens, timer.Elapsed, limits, extraction?.Summary());
    }

    private async Task<GraphRagDocumentOutcome> IngestDocumentAsync(Pending item, GraphRagIngestionRun run, Tally tally,
        GraphRagExtractor? extractor, GraphRagExtractionTally? extraction, CancellationToken cancellationToken)
    {
        using var activity = GraphRagDiagnostics.Start(GraphRagTracing.DocumentActivity);
        var outcome = await IngestDocumentCoreAsync(item, run, tally, extractor, extraction, cancellationToken).ConfigureAwait(false);
        if (activity is not null)
        {
            activity.SetTag("graphrag.run_id", run.Id);
            activity.SetTag("graphrag.document_id", outcome.DocumentId);
            activity.SetTag("graphrag.status", outcome.Status.ToString());
            if (outcome.ErrorCode is { } code) GraphRagDiagnostics.Fail(activity, code);
        }
        return outcome;
    }

    private async Task<GraphRagDocumentOutcome> IngestDocumentCoreAsync(Pending item, GraphRagIngestionRun run, Tally tally,
        GraphRagExtractor? extractor, GraphRagExtractionTally? extraction, CancellationToken cancellationToken)
    {
        var source = item.Source;
        var documentId = Hash(source.SourceUri);
        GraphRagDocumentOutcome Failed(string code) => new(documentId, source.SourceUri, GraphRagDocumentStatus.Failed, code);

        GraphRagLoadedDocument loaded;
        if (item.Chunked is { } chunked)
        {
            if (chunked.Utf8.Length > options.Ingestion.MaxDocumentBytes) return Failed("document-too-large");
            loaded = new(source.SourceUri, source.MediaType, chunked.Title, chunked.Utf8);
        }
        else
        {
            try
            {
                var loader = loaders.FirstOrDefault(l => l.CanLoad(source));
                if (loader is null) return Failed("unsupported-media-type");
                loaded = await loader.LoadAsync(source, cancellationToken).ConfigureAwait(false);
            }
            catch (GraphRagLoadException error) { return Failed(error.ErrorCode); }
            catch (IOException) { return Failed("read-failed"); }
            catch (UnauthorizedAccessException) { return Failed("read-failed"); }
        }

        if (!await TryLeaseAsync(documentId, run.Id, cancellationToken).ConfigureAwait(false)) return Failed("lease-held");
        var released = false;
        try
        {
            var contentHash = Convert.ToHexString(SHA256.HashData(loaded.Utf8.Span)).ToLowerInvariant();
            if (await UnchangedAsync(documentId, contentHash, run.SettingsFingerprint, cancellationToken).ConfigureAwait(false))
                return new(documentId, source.SourceUri, GraphRagDocumentStatus.Unchanged, null);

            var plan = item.Chunked?.Plan ?? chunker.Chunk(loaded);
            var texts = plan.Chunks.Select(c => Encoding.UTF8.GetString(loaded.Utf8.Span[c.StartByte..c.EndByte])).ToArray();
            float[][] vectors;
            using (var embed = GraphRagDiagnostics.Start(GraphRagTracing.EmbedActivity))
            {
                embed?.SetTag("graphrag.texts", texts.Length);
                try { vectors = await EmbedAsync(texts, tally, cancellationToken).ConfigureAwait(false); }
                catch (GraphRagEmbeddingException error)
                {
                    GraphRagDiagnostics.Fail(embed, error.ErrorCode);
                    return Failed(error.ErrorCode);
                }
            }

            var chunkIds = plan.Chunks.Select(c => ElementId(documentId, contentHash, plan.Fingerprint, "chunk", c.StartByte, c.EndByte)).ToArray();
            GraphRagExtractedGraph? graph = null;
            if (extractor is not null)
            {
                var items = plan.Chunks.Select(c => new GraphRagEvidenceItem(chunkIds[c.Order], documentId, texts[c.Order], c.StartByte, c.EndByte, 0, [])).ToArray();
                using var extract = GraphRagDiagnostics.Start(GraphRagTracing.ExtractActivity);
                var (calls, pruned, quarantined) = (extraction!.Calls, extraction.Pruned.Count, extraction.Quarantined.Count);
                try { graph = await extractor.ExtractAsync(documentId, source.Metadata?.Title ?? loaded.Title, run.Id, items, extraction, cancellationToken).ConfigureAwait(false); }
                catch (GraphRagExtractionException error)
                {
                    GraphRagDiagnostics.Fail(extract, error.ErrorCode);
                    return Failed(error.ErrorCode);
                }
                extract?.SetTag("graphrag.chunks", items.Length);
                extract?.SetTag("graphrag.chat_calls", extraction.Calls - calls);
                extract?.SetTag("graphrag.entities", graph.Entities.Sum(e => e.Value.Count));
                extract?.SetTag("graphrag.pruned", extraction.Pruned.Count - pruned);
                extract?.SetTag("graphrag.quarantined", extraction.Quarantined.Count - quarantined);
            }

            string? write;
            using var writing = GraphRagDiagnostics.Start(GraphRagTracing.WriteActivity);
            writing?.SetTag("graphrag.chunks", plan.Chunks.Count);
            try
            {
                write = await WriteAsync(loaded, source.Metadata, documentId, contentHash, plan, chunkIds, texts, vectors, run.Id,
                    graph is null ? null : (extractor!.Runtime, graph, extraction!), cancellationToken).ConfigureAwait(false);
            }
            catch (DbUpdateConcurrencyException)
            {
                GraphRagDiagnostics.Fail(writing, "lease-lost");
                return Failed("lease-lost");
            }
            catch (DbUpdateException)
            {
                GraphRagDiagnostics.Fail(writing, "write-failed");
                return Failed("write-failed");
            }
            if (write is not null)
            {
                GraphRagDiagnostics.Fail(writing, write);
                return Failed(write);
            }
            released = true;
            await ReleaseAsync(documentId, run.Id, CancellationToken.None).ConfigureAwait(false);
            tally.Chunks += plan.Chunks.Count;
            return new(documentId, source.SourceUri, GraphRagDocumentStatus.Ingested, null);
        }
        finally
        {
            if (!released)
            {
                try { await ReleaseAsync(documentId, run.Id, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception) when (!cancellationToken.IsCancellationRequested) { /* The lease expires on its own. */ }
            }
        }
    }

    // A lease is inserted, so the uniqueness constraint refuses a second holder; an expired one is taken over conditionally.
    private async Task<bool> TryLeaseAsync(string documentId, string runId, CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow();
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TContext>();
            db.Set<GraphRagLease>().Add(new GraphRagLease { Key = documentId, RunId = runId, ExpiresUtc = now + options.Ingestion.LeaseDuration });
            try
            {
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (DbUpdateException error) when (error.InnerException is Neo4jException { Code: "Neo.ClientError.Schema.ConstraintValidationFailed" }) { }
        }
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TContext>();
            var expires = now + options.Ingestion.LeaseDuration;
            try
            {
                return await db.Set<GraphRagLease>().Where(l => l.Key == documentId && l.ExpiresUtc < now)
                    .ExecuteUpdateAsync(s => s.SetProperty(l => l.RunId, runId).SetProperty(l => l.ExpiresUtc, expires), cancellationToken)
                    .ConfigureAwait(false) == 1;
            }
            catch (DbUpdateConcurrencyException) { return false; }   // another run took it over first
        }
    }

    private async Task ReleaseAsync(string documentId, string runId, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();
        await db.Set<GraphRagLease>().Where(l => l.Key == documentId && l.RunId == runId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> UnchangedAsync(string documentId, string contentHash, string fingerprint, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();
        var existing = await db.Set<GraphRagDocument>().AsNoTracking().Where(d => d.Id == documentId)
            .Select(d => new { d.ContentHash, d.RunId }).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (existing is null || existing.ContentHash != contentHash) return false;
        var writer = await db.Set<GraphRagIngestionRun>().AsNoTracking().Where(r => r.Id == existing.RunId)
            .Select(r => r.SettingsFingerprint).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return writer == fingerprint;
    }

    internal async Task<float[][]> EmbedAsync(string[] texts, Tally tally, CancellationToken cancellationToken)
    {
        var batches = texts.Chunk(options.Ingestion.EmbeddingBatchSize).ToArray();
        var results = new float[batches.Length][][];
        await Parallel.ForEachAsync(Enumerable.Range(0, batches.Length),
            new ParallelOptions { MaxDegreeOfParallelism = options.Ingestion.Lanes, CancellationToken = cancellationToken },
            async (index, token) => results[index] = await EmbedBatchAsync(batches[index], tally, token).ConfigureAwait(false)).ConfigureAwait(false);
        return results.SelectMany(batch => batch).ToArray();
    }

    private async Task<float[][]> EmbedBatchAsync(string[] batch, Tally tally, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            GeneratedEmbeddings<Embedding<float>> generated;
            try
            {
                Interlocked.Increment(ref tally.Calls);
                generated = await generator.GenerateAsync(batch, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                if (attempt >= 2 || !(options.Ingestion.IsTransient ?? IsTransient)(error))
                    throw new GraphRagEmbeddingException("embedding-failed");
                await Task.Delay(TimeSpan.FromMilliseconds(200 * (1 << (2 * attempt))), _clock, cancellationToken).ConfigureAwait(false);
                continue;
            }
            if (generated.Count != batch.Length) throw new GraphRagEmbeddingException("embedding-count");
            if (generated.Usage?.InputTokenCount is { } tokens) tally.AddTokens(tokens);
            return generated.Select(embedding => Vector(embedding.Vector)).ToArray();
        }
    }

    // Vectors are checked before the write; EF checks them again when it converts them for storage (ADR 0054).
    private float[] Vector(ReadOnlyMemory<float> generated)
    {
        if (generated.Length != embeddingSet.Dimensions) throw new GraphRagEmbeddingException("embedding-dimensions");
        var vector = generated.ToArray();
        double norm = 0;
        foreach (var value in vector)
        {
            if (!float.IsFinite(value)) throw new GraphRagEmbeddingException("invalid-embedding");
            norm += (double)value * value;
        }
        if (norm == 0) throw new GraphRagEmbeddingException("invalid-embedding");
        if (embeddingSet.Normalize)
        {
            var scale = 1 / Math.Sqrt(norm);
            for (var i = 0; i < vector.Length; i++) vector[i] = (float)(vector[i] * scale);
        }
        return vector;
    }

    private static bool IsTransient(Exception error) => error switch
    {
        HttpRequestException { StatusCode: { } status } => status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)status >= 500,
        TimeoutException or TaskCanceledException => true,
        _ => false
    };

    private async Task<string?> WriteAsync(GraphRagLoadedDocument loaded, GraphRagSourceMetadata? metadata, string documentId, string contentHash,
        GraphRagChunkPlan plan, string[] chunkIds, string[] texts, float[][] vectors, string runId,
        (GraphRagOntologyRuntime Runtime, GraphRagExtractedGraph Graph, GraphRagExtractionTally Tally)? extraction, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        // Fencing: renew the lease first. Its write lock means a takeover and this commit can't both succeed.
        var now = _clock.GetUtcNow();
        var expires = now + options.Ingestion.LeaseDuration;
        var renewed = await db.Set<GraphRagLease>().Where(l => l.Key == documentId && l.RunId == runId && l.ExpiresUtc > now)
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.ExpiresUtc, expires), cancellationToken).ConfigureAwait(false);
        if (renewed != 1) return "lease-lost";

        await GraphRagRemoval.RemoveContentAsync(db, documentId, cancellationToken).ConfigureAwait(false);
        var document = await db.Set<GraphRagDocument>().SingleOrDefaultAsync(d => d.Id == documentId, cancellationToken).ConfigureAwait(false);
        if (document is null) db.Add(document = new GraphRagDocument { Id = documentId });
        document.SourceUri = loaded.SourceUri;
        document.Title = metadata?.Title ?? loaded.Title;
        (document.Published, document.Publisher, document.Url) = (metadata?.Published, metadata?.Publisher, metadata?.Url);
        document.MediaType = loaded.MediaType;
        document.ContentHash = contentHash;
        document.LengthBytes = loaded.Utf8.Length;
        document.RunId = runId;

        var sectionIds = plan.Sections.Select(s => ElementId(documentId, contentHash, plan.Fingerprint, "section", s.StartByte, s.EndByte)).ToArray();
        foreach (var section in plan.Sections)
        {
            var id = sectionIds[section.Order];
            db.Add(new GraphRagSection
            {
                Id = id, DocumentId = documentId, Title = section.Title, Level = section.Level, Order = section.Order,
                StartByte = section.StartByte, EndByte = section.EndByte, RunId = runId
            });
            db.Add(new GraphRagSectionDocument { SectionId = id, DocumentId = documentId, RunId = runId });
            if (section.ParentOrder is { } parent)
                db.Add(new GraphRagSubsection { SectionId = id, ParentSectionId = sectionIds[parent], RunId = runId });
        }
        foreach (var chunk in plan.Chunks)
        {
            var id = chunkIds[chunk.Order];
            db.Add(new GraphRagChunk
            {
                Id = id, DocumentId = documentId, Order = chunk.Order, Text = texts[chunk.Order], StartByte = chunk.StartByte,
                EndByte = chunk.EndByte, TokenCount = chunk.TokenCount, Embedding = vectors[chunk.Order], EmbeddingSetId = embeddingSet.Id, RunId = runId
            });
            db.Add(new GraphRagChunkDocument { ChunkId = id, DocumentId = documentId, RunId = runId });
            if (chunk.SectionOrder is { } section)
                db.Add(new GraphRagChunkSection { ChunkId = id, SectionId = sectionIds[section], RunId = runId });
            if (chunk.Order > 0)
                db.Add(new GraphRagNextChunk { ChunkId = chunkIds[chunk.Order - 1], NextChunkId = id, RunId = runId });
        }
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var written = extraction is { } extracted
            ? await extracted.Runtime.WriteAsync(db, extracted.Graph, documentId, cancellationToken).ConfigureAwait(false)
            : default;
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        if (extraction is { Tally: var counts })
        {
            counts.Entities += written.Entities;
            counts.Mentions += written.Mentions;
            counts.Facts += written.Facts;
            counts.Quotes += written.Quotes;
        }
        return null;
    }

    // Section and chunk IDs: the document, its content, the chunk plan, the kind and the span, so a rerun writes the same IDs.
    internal static string ElementId(string documentId, string contentHash, string planFingerprint, string kind, int start, int end)
        => Hash(string.Join('\u001f', documentId, contentHash, planFingerprint, kind,
            start.ToString(CultureInfo.InvariantCulture), end.ToString(CultureInfo.InvariantCulture)));

    // Extraction runs when the model maps an ontology, through the keyed extraction client or the container's IChatClient.
    private GraphRagExtractor? CreateExtractor()
    {
        using var scope = scopes.CreateScope();
        var model = scope.ServiceProvider.GetRequiredService<TContext>().Model;
        if (model.FindAnnotation(GraphRagModelBuilderExtensions.OntologyAnnotation)?.Value is not GraphRagOntology) return null;
        options.Extraction.Validate();
        var chat = extractionChat?.Invoke() ?? throw new InvalidOperationException(
            $"{typeof(TContext).Name} maps a GraphRAG ontology, so ingestion extracts: register an IChatClient, or one keyed '{GraphRagServiceKeys.ExtractionChatClient}'.");
        return new GraphRagExtractor(GraphRagModelSchema.From(model), chat, options.Extraction, verifier, _clock);
    }

    private void RequireModel()
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();
        var chunk = db.Model.FindEntityType(typeof(GraphRagChunk))
            ?? throw new InvalidOperationException($"{typeof(TContext).Name} doesn't map the GraphRAG model; call modelBuilder.AddGraphRag(...) in OnModelCreating.");
        var dimensions = chunk.FindProperty(nameof(GraphRagChunk.Embedding))?.FindAnnotation(GraphBridge.EntityFrameworkCore.Metadata.GraphAnnotationNames.VectorDimensions)?.Value;
        if (dimensions is not int mapped || mapped != embeddingSet.Dimensions)
            throw new InvalidOperationException($"The embedding set has {embeddingSet.Dimensions} dimensions but AddGraphRag mapped {dimensions ?? "no"} dimensions; they must be equal.");
    }

    private async Task UpsertRunAsync(GraphRagIngestionRun run, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();
        await db.Set<GraphRagIngestionRun>().ExecuteUpsertAsync([run], cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    // The chunker's settings, the embedding set and, with an ontology, the extraction schema and extractor version: a document
    // is unchanged only if all of them are the same as when it was written.
    private string SettingsFingerprint(string chunkerFingerprint, GraphRagExtractor? extractor) => Hash(string.Join('\u001f',
        new[] { chunkerFingerprint, embeddingSet.Id }.Concat(extractor is null ? [] : [extractor.Fingerprint])));

    internal static string Hash(string text) => GraphRagEmbeddingSet.Hash(text);

    internal sealed class Tally
    {
        public int Chunks;
        public int Calls;
        private long _tokens;
        private bool _reported;
        public long? Tokens => _reported ? Interlocked.Read(ref _tokens) : null;
        public void AddTokens(long tokens)
        {
            Interlocked.Add(ref _tokens, tokens);
            _reported = true;
        }
    }
}

internal sealed class GraphRagEmbeddingException(string errorCode) : Exception(errorCode)
{
    public string ErrorCode { get; } = errorCode;
}
