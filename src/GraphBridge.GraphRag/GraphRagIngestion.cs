using System.Diagnostics;
using System.Globalization;
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
    TimeProvider? clock = null, Func<IChatClient?>? extractionChat = null, IGraphRagExtractionVerifier? verifier = null,
    IGraphRagTokenCounter? tokenCounter = null)
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

    public async Task<GraphRagIngestionPreview> PreviewAsync(IEnumerable<GraphRagSource> sources, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sources);
        options.Ingestion.Validate();
        options.Chunking.Validate();
        var all = sources.Take(options.Ingestion.MaxDocuments + 1).ToList();
        var limits = new List<GraphRagLimit>();
        if (all.Count > options.Ingestion.MaxDocuments)
        {
            all.RemoveAt(all.Count - 1);
            limits.Add(new(nameof(GraphRagIngestionOptions.MaxDocuments), options.Ingestion.MaxDocuments));
        }
        RequireModel();
        GraphRagModelSchema? schema;
        using (var scope = scopes.CreateScope())
        {
            var model = scope.ServiceProvider.GetRequiredService<TContext>().Model;
            schema = model.FindAnnotation(GraphRagModelBuilderExtensions.OntologyAnnotation)?.Value is GraphRagOntology ? GraphRagModelSchema.From(model) : null;
        }
        var fingerprint = Hash(string.Join('\u001f',
            new[] { ChunkerFingerprint, embeddingSet.Id }.Concat(schema is null ? [] : [GraphRagExtractor.FingerprintFor(schema)])));
        var instructions = schema is null ? 0 : Tokens(GraphRagExtractor.InstructionsFor(schema));

        var documents = new List<GraphRagDocumentPreview>();
        var (chunks, embeddingCalls, extractionCalls, embeddingTokens, extractionTokens) = (0, 0, 0, 0L, 0L);
        foreach (var source in all)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var documentId = Hash(source.SourceUri);
            GraphRagLoadedDocument loaded;
            try
            {
                var loader = loaders.FirstOrDefault(l => l.CanLoad(source));
                if (loader is null)
                {
                    documents.Add(new(documentId, source.SourceUri, GraphRagPreviewStatus.WouldFail, "unsupported-media-type", 0, 0));
                    continue;
                }
                loaded = await loader.LoadAsync(source, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is GraphRagLoadException or IOException or UnauthorizedAccessException)
            {
                documents.Add(new(documentId, source.SourceUri, GraphRagPreviewStatus.WouldFail, (error as GraphRagLoadException)?.ErrorCode ?? "read-failed", 0, 0));
                continue;
            }
            var contentHash = Convert.ToHexString(SHA256.HashData(loaded.Utf8.Span)).ToLowerInvariant();
            if (await UnchangedAsync(documentId, contentHash, fingerprint, cancellationToken).ConfigureAwait(false))
            {
                documents.Add(new(documentId, source.SourceUri, GraphRagPreviewStatus.Unchanged, null, loaded.Utf8.Length, 0));
                continue;
            }
            var plan = chunker.Chunk(loaded);
            var title = source.Metadata?.Title ?? loaded.Title;
            foreach (var chunk in plan.Chunks)
            {
                var text = Encoding.UTF8.GetString(loaded.Utf8.Span[chunk.StartByte..chunk.EndByte]);
                embeddingTokens += Tokens(text);
                // Two messages, each with a few tokens of chat format, and the request's own.
                if (schema is not null) extractionTokens += instructions + Tokens(GraphRagExtractor.PromptFor(title, text)) + 2 * 4 + 3;
            }
            chunks += plan.Chunks.Count;
            embeddingCalls += (plan.Chunks.Count + options.Ingestion.EmbeddingBatchSize - 1) / options.Ingestion.EmbeddingBatchSize;
            if (schema is not null) extractionCalls += plan.Chunks.Count;
            documents.Add(new(documentId, source.SourceUri, GraphRagPreviewStatus.WouldIngest, null, loaded.Utf8.Length, plan.Chunks.Count));
        }
        return new(documents, chunks, embeddingCalls, extractionCalls, embeddingTokens, extractionTokens, tokenCounter is not null, limits);
    }

    // The registered counter's count, or four bytes of UTF-8 per token, rounded up.
    private long Tokens(string text) => tokenCounter?.Count(text) ?? (Encoding.UTF8.GetByteCount(text) + 3) / 4;

    // A source to load and chunk, or a document whose chunks were decided elsewhere.
    private sealed record Pending(GraphRagSource Source, GraphRagChunkedDocument? Chunked);

    private string ChunkerFingerprint => chunker is GraphRagChunker core ? core.Fingerprint : chunker.GetType().FullName ?? "custom";

    private async Task<GraphRagIngestionReceipt> RunAsync(IEnumerable<Pending> pending, string chunkerFingerprint, CancellationToken cancellationToken)
    {
        options.Ingestion.Validate();
        options.Chunking.Validate();
        options.Retry.Validate();
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
        var retries = new GraphRagRetryTally();
        try
        {
            foreach (var item in all)
            {
                cancellationToken.ThrowIfCancellationRequested();
                outcomes.Add(await IngestDocumentAsync(item, run, tally, retries, extractor, extraction, cancellationToken).ConfigureAwait(false));
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
                var retried = retries.Summary();
                activity.SetTag("graphrag.run_id", run.Id);
                activity.SetTag("graphrag.retries", retried.Retries);
                activity.SetTag("graphrag.rate_limited", retried.RateLimited);
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
        return new(run.Id, outcomes, tally.Chunks, tally.Calls, tally.Tokens, timer.Elapsed, limits, extraction?.Summary(), retries.Summary());
    }

    private async Task<GraphRagDocumentOutcome> IngestDocumentAsync(Pending item, GraphRagIngestionRun run, Tally tally, GraphRagRetryTally retries,
        GraphRagExtractor? extractor, GraphRagExtractionTally? extraction, CancellationToken cancellationToken)
    {
        using var activity = GraphRagDiagnostics.Start(GraphRagTracing.DocumentActivity);
        var outcome = await IngestDocumentCoreAsync(item, run, tally, retries, extractor, extraction, cancellationToken).ConfigureAwait(false);
        if (activity is not null)
        {
            activity.SetTag("graphrag.run_id", run.Id);
            activity.SetTag("graphrag.document_id", outcome.DocumentId);
            activity.SetTag("graphrag.status", outcome.Status.ToString());
            if (outcome.ErrorCode is { } code) GraphRagDiagnostics.Fail(activity, code);
        }
        return outcome;
    }

    private async Task<GraphRagDocumentOutcome> IngestDocumentCoreAsync(Pending item, GraphRagIngestionRun run, Tally tally, GraphRagRetryTally retries,
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
        // The lease is renewed while the document is embedded and extracted, which can take longer than the lease itself.
        var lease = new LeaseKeeper(this, documentId, run.Id, options.Ingestion.LeaseDuration, cancellationToken);
        try
        {
            var work = lease.Token;
            var contentHash = Convert.ToHexString(SHA256.HashData(loaded.Utf8.Span)).ToLowerInvariant();
            if (await UnchangedAsync(documentId, contentHash, run.SettingsFingerprint, work).ConfigureAwait(false))
                return new(documentId, source.SourceUri, GraphRagDocumentStatus.Unchanged, null);

            var plan = item.Chunked?.Plan ?? chunker.Chunk(loaded);
            var texts = plan.Chunks.Select(c => Encoding.UTF8.GetString(loaded.Utf8.Span[c.StartByte..c.EndByte])).ToArray();
            float[][] vectors;
            using (var embed = GraphRagDiagnostics.Start(GraphRagTracing.EmbedActivity))
            {
                embed?.SetTag("graphrag.texts", texts.Length);
                try { vectors = await EmbedAsync(texts, tally, retries, work).ConfigureAwait(false); }
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
                try { graph = await extractor.ExtractAsync(documentId, source.Metadata?.Title ?? loaded.Title, run.Id, items, extraction, retries, work).ConfigureAwait(false); }
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

            // The write renews the lease itself, first thing in its transaction.
            await lease.StopAsync().ConfigureAwait(false);
            if (lease.Lost) return Failed("lease-lost");
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
            // The document is committed: a release that fails now mustn't lose the batch's receipt. The lease expires on its own.
            try { await ReleaseAsync(documentId, run.Id, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception) when (!cancellationToken.IsCancellationRequested) { }
            tally.Chunks += plan.Chunks.Count;
            return new(documentId, source.SourceUri, GraphRagDocumentStatus.Ingested, null);
        }
        catch (OperationCanceledException) when (lease.Lost && !cancellationToken.IsCancellationRequested)
        {
            return Failed("lease-lost");
        }
        finally
        {
            await lease.DisposeAsync().ConfigureAwait(false);
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

    // Extends this run's lease if it still holds it; false when another run has taken it over.
    private async Task<bool> RenewAsync(string documentId, string runId, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();
        var now = _clock.GetUtcNow();
        var expires = now + options.Ingestion.LeaseDuration;
        try
        {
            return await db.Set<GraphRagLease>().Where(l => l.Key == documentId && l.RunId == runId && l.ExpiresUtc > now)
                .ExecuteUpdateAsync(s => s.SetProperty(l => l.ExpiresUtc, expires), cancellationToken).ConfigureAwait(false) == 1;
        }
        catch (DbUpdateConcurrencyException) { return false; }
    }

    /// <summary>
    /// Renews a document's lease every third of its duration until stopped. If a renewal finds the lease gone to another run,
    /// it cancels <see cref="Token"/> and sets <see cref="Lost"/>. A renewal that fails for any other reason is tried again at
    /// the next beat; the write's own renewal still fences the commit.
    /// </summary>
    private sealed class LeaseKeeper : IAsyncDisposable
    {
        private readonly CancellationTokenSource _work;
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _beats;
        private int _lost;

        public LeaseKeeper(GraphRagIngestion<TContext> owner, string documentId, string runId, TimeSpan duration, CancellationToken cancellationToken)
        {
            _work = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _beats = BeatAsync(owner, documentId, runId, duration / 3);
        }

        public CancellationToken Token => _work.Token;
        public bool Lost => Volatile.Read(ref _lost) == 1;

        private async Task BeatAsync(GraphRagIngestion<TContext> owner, string documentId, string runId, TimeSpan every)
        {
            while (!_stop.IsCancellationRequested)
            {
                bool renewed;
                try
                {
                    await Task.Delay(every, owner._clock, _stop.Token).ConfigureAwait(false);
                    renewed = await owner.RenewAsync(documentId, runId, _stop.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { return; }
                catch (Exception) when (!_stop.IsCancellationRequested) { continue; }
                if (!renewed)
                {
                    Volatile.Write(ref _lost, 1);
                    _work.Cancel();
                    return;
                }
            }
        }

        public async Task StopAsync()
        {
            if (!_stop.IsCancellationRequested) _stop.Cancel();
            await _beats.ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync().ConfigureAwait(false);
            _work.Dispose();
            _stop.Dispose();
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

    internal async Task<float[][]> EmbedAsync(string[] texts, Tally tally, GraphRagRetryTally retries, CancellationToken cancellationToken)
    {
        var batches = texts.Chunk(options.Ingestion.EmbeddingBatchSize).ToArray();
        var results = new float[batches.Length][][];
        await Parallel.ForEachAsync(Enumerable.Range(0, batches.Length),
            new ParallelOptions { MaxDegreeOfParallelism = options.Ingestion.Lanes, CancellationToken = cancellationToken },
            async (index, token) => results[index] = await EmbedBatchAsync(batches[index], tally, retries, token).ConfigureAwait(false)).ConfigureAwait(false);
        return results.SelectMany(batch => batch).ToArray();
    }

    private async Task<float[][]> EmbedBatchAsync(string[] batch, Tally tally, GraphRagRetryTally retries, CancellationToken cancellationToken)
    {
        var generated = await GraphRagRetry.RunAsync(token =>
        {
            Interlocked.Increment(ref tally.Calls);
            return generator.GenerateAsync(batch, cancellationToken: token);
        }, options.Retry, retries, _clock, "embedding-failed", (code, error) => new GraphRagEmbeddingException(code, error), cancellationToken).ConfigureAwait(false);
        if (generated.Count != batch.Length) throw new GraphRagEmbeddingException("embedding-count");
        if (generated.Usage?.InputTokenCount is { } tokens) tally.AddTokens(tokens);
        return generated.Select(embedding => Vector(embedding.Vector)).ToArray();
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

        await GraphRagRemoval.RemoveContentAsync(db, [documentId], cancellationToken).ConfigureAwait(false);
        var document = await db.Set<GraphRagDocument>().SingleOrDefaultAsync(d => d.Id == documentId, cancellationToken).ConfigureAwait(false);
        if (document is null) db.Add(document = new GraphRagDocument { Id = documentId });
        document.SourceUri = loaded.SourceUri;
        document.Title = metadata?.Title ?? loaded.Title;
        (document.Published, document.Publisher, document.Url, document.TextOrigin) = (metadata?.Published, metadata?.Publisher, metadata?.Url, metadata?.TextOrigin);
        document.MediaType = loaded.MediaType;
        document.ContentHash = contentHash;
        document.LengthBytes = loaded.Utf8.Length;
        document.PageStarts = loaded.PageStarts is { Count: > 0 } pages ? string.Join(',', pages.Select(p => p.ToString(CultureInfo.InvariantCulture))) : null;
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
        return new GraphRagExtractor(GraphRagModelSchema.From(model), chat, options.Extraction, options.Retry, verifier, _clock);
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

internal sealed class GraphRagEmbeddingException(string errorCode, Exception? inner = null) : Exception(errorCode, inner)
{
    public string ErrorCode { get; } = errorCode;
}
