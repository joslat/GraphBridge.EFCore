using System.Linq.Expressions;
using System.Text.Json;
using System.Text.Json.Nodes;
using GraphBridge.GraphRag.Ontologies;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Neo4j.Driver;

namespace GraphBridge.GraphRag;

/// <summary>
/// Two things a person may decide are one (ADR 0052, section 16): <see cref="KeepId"/> stays, <see cref="MergeId"/> would be
/// merged into it. <see cref="Reason"/> is <c>same-name</c> or <c>alias</c>.
/// </summary>
public sealed record GraphRagMergeSuggestion(string Kind, string KeepId, string KeepName, string MergeId, string MergeName, string Reason);

/// <summary>What a merge plan would move, and what stops it.</summary>
public sealed record GraphRagMergePreview(int Merges, int Mentions, int Facts, int Quotes, IReadOnlyList<string> Blockers)
{
    public bool CanApply => Blockers.Count == 0;
}

/// <summary>What a plan did: one run per merge, each undoable on its own.</summary>
public sealed record GraphRagMergeReceipt(IReadOnlyList<string> RunIds, int Mentions, int FactsMoved, int FactsJoined, int FactsDropped, int Quotes);

/// <summary>
/// A merge plan stopped at one merge (ADR 0052, section 16). The merges before it were written, each as a run in
/// <see cref="AppliedRunIds"/> that <see cref="GraphRagMergePlan.UndoAsync"/> reverses, last first. When the failed merge's save
/// failed after its commit, its own run was written too: the message says so, and that run is the last in
/// <see cref="AppliedRunIds"/>. When that can't be told, the message names the run to look for; otherwise nothing of it was written. A cancelled plan
/// throws <see cref="OperationCanceledException"/> instead; its applied merges are the runs whose fingerprint starts with "merge:".
/// </summary>
public sealed class GraphRagMergeException(string message, GraphRagMergeSuggestion failed, IReadOnlyList<string> appliedRunIds, Exception inner)
    : Exception(message, inner)
{
    /// <summary>The merge that failed.</summary>
    public GraphRagMergeSuggestion Failed { get; } = failed;

    /// <summary>The runs written, in the plan's order: the merges before it, and the failed merge's own run when it was written.</summary>
    public IReadOnlyList<string> AppliedRunIds { get; } = appliedRunIds;
}

/// <summary>What an undo put back.</summary>
public sealed record GraphRagMergeUndo(int Mentions, int Facts, int Quotes);

/// <summary>
/// Entity resolution suggestions (ADR 0052, section 16). Suggestions pair things of one class whose names are equal once case,
/// extra whitespace and a leading article are ignored, or whose name is the other's alias. Nothing is merged automatically: a
/// person keeps the suggestions they agree with and applies them as a <see cref="GraphRagMergePlan"/>, which can be undone.
/// </summary>
public static class GraphRagResolution
{
    private const int MaxThings = 50_000;

    public static async Task<IReadOnlyList<GraphRagMergeSuggestion>> SuggestAsync(DbContext db, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var ontology = GraphRagRemoval.OntologyOf(db) ?? throw new InvalidOperationException("Merge suggestions need a context that maps an ontology.");
        var things = new List<GraphRagThing>();
        foreach (var type in ontology.Classes)
        {
            var read = await Typed.For(type.ClrType).ThingsAsync(db, MaxThings + 1, cancellationToken).ConfigureAwait(false);
            if (read.Count > MaxThings) throw new InvalidOperationException($"Suggestions read at most {MaxThings:N0} things of a class; {type.Kind} has more.");
            // Workaround for GR-06 (Count over identified relationships, GBQ170): mentions counted per thing in one bounded read.
            var mentions = (await db.Database.QueryCypherAsync($"MATCH (t:`{type.Label}`)-[m:`{type.MentionType}`]->() RETURN t.Id AS id, count(m) AS n",
                    new Dictionary<string, object?>(), record => (record["id"].As<string>(), (int)record["n"].As<long>()), MaxThings, cancellationToken)
                .ConfigureAwait(false)).ToDictionary(r => r.Item1, r => r.Item2, StringComparer.Ordinal);
            things.AddRange(read.Select(e => new GraphRagThing(e.Id, type.Kind, e.Name, e.Aliases, e.IsPlaceholder, mentions.GetValueOrDefault(e.Id))));
        }
        return Suggest(things);
    }

    /// <summary>The plan for the suggestions a person kept.</summary>
    public static GraphRagMergePlan Plan(IEnumerable<GraphRagMergeSuggestion> accepted)
    {
        ArgumentNullException.ThrowIfNull(accepted);
        return new([.. accepted]);
    }

    internal sealed record GraphRagThing(string Id, string Kind, string Name, string? Aliases, bool IsPlaceholder, int Mentions);

    internal static IReadOnlyList<GraphRagMergeSuggestion> Suggest(IEnumerable<GraphRagThing> things)
    {
        var suggestions = new List<GraphRagMergeSuggestion>();
        foreach (var kind in things.Where(t => !t.IsPlaceholder).GroupBy(t => t.Kind, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var list = kind.OrderBy(t => t.Id, StringComparer.Ordinal).ToArray();
            var parent = Enumerable.Range(0, list.Length).ToArray();
            int Root(int i)
            {
                var root = i;
                while (parent[root] != root) root = parent[root];
                while (parent[i] != root)
                {
                    var next = parent[i];
                    parent[i] = root;
                    i = next;
                }
                return root;
            }
            var byKey = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var i = 0; i < list.Length; i++)
                if (byKey.TryGetValue(Key(list[i].Name), out var first)) parent[Root(i)] = Root(first);
                else byKey[Key(list[i].Name)] = i;
            for (var i = 0; i < list.Length; i++)
                foreach (var alias in GraphRagOntologyRuntime.Lines(list[i].Aliases))
                    if (byKey.TryGetValue(Key(alias), out var other) && Root(other) != Root(i)) parent[Root(i)] = Root(other);
            foreach (var group in Enumerable.Range(0, list.Length).GroupBy(Root).Where(g => g.Count() > 1))
            {
                // The most-mentioned name stays; on a tie, a name without an article, then the shorter, then ordinal order.
                var members = group.Select(i => list[i]).ToArray();
                var keep = members.OrderByDescending(t => t.Mentions).ThenBy(t => Key(t.Name) != t.Name.Trim().ToLowerInvariant() ? 1 : 0)
                    .ThenBy(t => t.Name.Length).ThenBy(t => t.Name, StringComparer.Ordinal).First();
                foreach (var merged in members.Where(t => t.Id != keep.Id).OrderBy(t => t.Name, StringComparer.Ordinal))
                    suggestions.Add(new(kind.Key, keep.Id, keep.Name, merged.Id, merged.Name, Key(merged.Name) == Key(keep.Name) ? "same-name" : "alias"));
            }
        }
        return suggestions;
    }

    /// <summary>A name read for comparison: lower case, single spaces, without a leading article or trailing punctuation.</summary>
    internal static string Key(string name)
    {
        var key = string.Join(' ', name.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).TrimEnd('.', ',', ';', ':', '!', '?');
        foreach (var article in (string[])["the ", "a ", "an "])
            if (key.StartsWith(article, StringComparison.Ordinal) && key.Length > article.Length) return key[article.Length..];
        return key;
    }

    // EF access to the ontology's generic types, made once per type, as the ontology runtime does.
    internal abstract class Typed
    {
        private static readonly Dictionary<Type, Typed> Cache = [];

        public static Typed For(Type type)
        {
            lock (Cache)
            {
                if (!Cache.TryGetValue(type, out var typed)) Cache[type] = typed = (Typed)Activator.CreateInstance(typeof(Typed<>).MakeGenericType(type))!;
                return typed;
            }
        }

        public abstract Task<List<GraphRagEntity>> ThingsAsync(DbContext db, int limit, CancellationToken cancellationToken);
        public abstract Task<List<object>> WhereAsync(DbContext db, string property, string value, CancellationToken cancellationToken);
        public abstract Task<object?> PairAsync(DbContext db, string from, string to, CancellationToken cancellationToken);
    }

    private sealed class Typed<T> : Typed where T : class
    {
        public override async Task<List<GraphRagEntity>> ThingsAsync(DbContext db, int limit, CancellationToken cancellationToken)
            => [.. (await db.Set<T>().AsNoTracking().Take(limit).ToListAsync(cancellationToken).ConfigureAwait(false)).Cast<GraphRagEntity>()];

        public override async Task<List<object>> WhereAsync(DbContext db, string property, string value, CancellationToken cancellationToken)
            => [.. await db.Set<T>().Where(Equal((property, value))).ToListAsync(cancellationToken).ConfigureAwait(false)];

        public override async Task<object?> PairAsync(DbContext db, string from, string to, CancellationToken cancellationToken)
            => await db.Set<T>().Where(Equal((nameof(GraphRagFact.FromId), from), (nameof(GraphRagFact.ToId), to)))
                .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);

               // e => e.A == a && e.B == b, with the values as parameters.
        private static Expression<Func<T, bool>> Equal(params (string Property, string Value)[] conditions)
        {
            var entity = Expression.Parameter(typeof(T), "e");
            var body = conditions.Select(c => (Expression)Expression.Equal(Expression.Property(entity, c.Property),
                Expression.Field(Expression.Constant(new Holder(c.Value)), nameof(Holder.Value)))).Aggregate(Expression.AndAlso);
            return Expression.Lambda<Func<T, bool>>(body, entity);
        }

 private sealed class Holder(string value)
        {
            public readonly string Value = value;
        }
    }
}

/// <summary>
/// The merges a person accepted (ADR 0052, section 16). <see cref="PreviewAsync"/> counts and lists blockers;
/// <see cref="ApplyAsync"/> merges each pair in one save, as one run that records what it changed; <see cref="UndoAsync"/>
/// puts a run's changes back.
/// </summary>
public sealed class GraphRagMergePlan
{
    internal GraphRagMergePlan(IReadOnlyList<GraphRagMergeSuggestion> merges) => Merges = merges;

    public IReadOnlyList<GraphRagMergeSuggestion> Merges { get; }

    public async Task<GraphRagMergePreview> PreviewAsync(DbContext db, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var ontology = GraphRagRemoval.OntologyOf(db) ?? throw new InvalidOperationException("Merges need a context that maps an ontology.");
        var blockers = new List<string>();
        foreach (var id in Merges.Select(m => m.MergeId).GroupBy(i => i).Where(g => g.Count() > 1)) blockers.Add($"'{id.Key}' is merged twice");
        foreach (var merge in Merges.Where(m => Merges.Any(o => o.MergeId == m.KeepId))) blockers.Add($"'{merge.KeepName}' is kept here and merged elsewhere");
        var (mentions, facts, quotes) = (0, 0, 0);
        foreach (var merge in Merges)
        {
            if (merge.KeepId == merge.MergeId) { blockers.Add($"'{merge.KeepName}' would be merged into itself"); continue; }
            if (ontology.Classes.SingleOrDefault(c => c.Kind == merge.Kind) is not { } type) { blockers.Add($"no class '{merge.Kind}'"); continue; }
            var typed = GraphRagResolution.Typed.For(type.ClrType);
            if ((await typed.WhereAsync(db, nameof(GraphRagEntity.Id), merge.KeepId, cancellationToken).ConfigureAwait(false)).Count == 0
                || (await typed.WhereAsync(db, nameof(GraphRagEntity.Id), merge.MergeId, cancellationToken).ConfigureAwait(false)).Count == 0)
            {
                blockers.Add($"'{merge.KeepName}' or '{merge.MergeName}' no longer exists as a {merge.Kind}");
                continue;
            }
            var (m, f, q) = await TouchingAsync(db, ontology, type, merge.MergeId, cancellationToken).ConfigureAwait(false);
            (mentions, facts, quotes) = (mentions + m.Count, facts + f.Sum(x => x.Rows.Count), quotes + q.Sum(x => x.Rows.Count));
        }
        db.ChangeTracker.Clear();
        return new(Merges.Count, mentions, facts, quotes, blockers);
    }

    public async Task<GraphRagMergeReceipt> ApplyAsync(DbContext db, CancellationToken cancellationToken = default)
    {
        var preview = await PreviewAsync(db, cancellationToken).ConfigureAwait(false);
        if (!preview.CanApply) throw new InvalidOperationException("The merges can't be applied: " + string.Join("; ", preview.Blockers));
        var ontology = GraphRagRemoval.OntologyOf(db)!;
        var runs = new List<string>();
        var (mentions, moved, joined, dropped, quotes) = (0, 0, 0, 0, 0);
        foreach (var merge in Merges)
        {
            Merged result;
            var runId = Guid.NewGuid().ToString("N");
            try
            {
                result = await MergeAsync(db, ontology, merge, runId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                // The failed merge's save rolled back, but the merges before it are written: the caller needs their runs to undo them.
                // A save whose commit outcome was lost may have written this one too: its run then exists.
                var at = At(error);
                db.ChangeTracker.Clear();
                bool? written;
                try { written = await db.Set<GraphRagIngestionRun>().AnyAsync(r => r.Id == runId, cancellationToken).ConfigureAwait(false); }
                catch (Exception check) when (check is not OperationCanceledException) { written = null; }
                db.ChangeTracker.Clear();
                if (written == true) runs.Add(runId);
                var before = runs.Count - (written == true ? 1 : 0);
                var state = written switch
                {
                    true => $"Its save failed after the commit, so it was written, as run {runId}; ",
                    false => "Nothing of this merge was written; ",
                    null => $"Whether this merge was written is unknown: it was if run {runId} exists. "
                };
                throw new GraphRagMergeException($"Merging {merge.Kind} '{merge.MergeName}' into '{merge.KeepName}' failed{at}: {error.Message} " + state
                    + (before == 0 ? "no merge before it was applied."
                        : $"the {before} merges before it were, as runs {string.Join(", ", runs.Take(before))}.")
                    + (runs.Count == 0 ? "" : " Undo the runs last first, or keep them."),
                    merge, runs.ToArray(), error);
            }
            runs.Add(result.RunId);
            (mentions, moved, joined, dropped, quotes) = (mentions + result.Mentions, moved + result.Moved, joined + result.Joined, dropped + result.Dropped, quotes + result.Quotes);
        }
        return new(runs, mentions, moved, joined, dropped, quotes);
    }

    /// <summary>Puts back what the merge run <paramref name="runId"/> changed, and removes the run.</summary>
    public static async Task<GraphRagMergeUndo> UndoAsync(DbContext db, string runId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        var ontology = GraphRagRemoval.OntologyOf(db) ?? throw new InvalidOperationException("Merges need a context that maps an ontology.");
        var run = await db.Set<GraphRagIngestionRun>().SingleOrDefaultAsync(r => r.Id == runId, cancellationToken).ConfigureAwait(false);
        if (run?.Undo is null || !run.SettingsFingerprint.StartsWith("merge:", StringComparison.Ordinal))
            throw new InvalidOperationException($"'{runId}' is not a merge run; nothing was undone.");
        var undo = JsonNode.Parse(run.Undo)!.AsObject();
        // The record names rows by their place in the ontology, so it is read only under the ontology it was written with.
        if (undo["ontology"]?.GetValue<string>() is { } fingerprint && fingerprint != ontology.Fingerprint)
            throw new InvalidOperationException($"Merge run '{runId}' was recorded under another ontology than the context maps; nothing was undone.");
        var type = ontology.Classes.Single(c => c.Kind == undo["kind"]!.GetValue<string>());

        // Refused when the merged thing or a row the merge removed was written again since, by extraction or by hand:
        // putting them back would duplicate them.
        var again = (await GraphRagResolution.Typed.For(type.ClrType).WhereAsync(db, nameof(GraphRagEntity.Id), undo["mergeId"]!.GetValue<string>(), cancellationToken)
            .ConfigureAwait(false)).Count > 0;
        foreach (var removed in undo["removed"]!.AsArray())
        {
            if (again) break;
            var (role, clr) = Resolve(ontology, removed!["role"]!.GetValue<string>(), removed["index"]!.GetValue<int>());
            var values = removed["values"]!.AsObject();
            again = role == "fact"
                ? await GraphRagResolution.Typed.For(clr).PairAsync(db, values[nameof(GraphRagFact.FromId)]!.GetValue<string>(), values[nameof(GraphRagFact.ToId)]!.GetValue<string>(),
                    cancellationToken).ConfigureAwait(false) is not null
                : (await GraphRagResolution.Typed.For(clr).WhereAsync(db, nameof(GraphRagMention.Id), values[nameof(GraphRagMention.Id)]!.GetValue<string>(), cancellationToken)
                    .ConfigureAwait(false)).Count > 0;
        }
        db.ChangeTracker.Clear();
        if (again)
            throw new InvalidOperationException($"Merge run '{runId}' can't be undone: '{undo["mergeId"]}' or one of its rows was written again since the merge, " +
                "for example by re-ingesting a document. Nothing was undone.");

        // Refused, too, when what the merge wrote was changed since: by a later merge into the same thing, or by a document
        // ingested after it that states one of the facts it moved. Putting the old values back would lose those writes.
        var changed = false;
        foreach (var created in undo["created"]!.AsArray())
        {
            if (changed) break;
            var (role, clr) = Resolve(ontology, created!["role"]!.GetValue<string>(), created["index"]!.GetValue<int>());
            var typed = GraphRagResolution.Typed.For(clr);
            var row = role == "fact"
                ? await typed.PairAsync(db, created["from"]!.GetValue<string>(), created["to"]!.GetValue<string>(), cancellationToken).ConfigureAwait(false)
                : (await typed.WhereAsync(db, nameof(GraphRagMention.Id), created["id"]!.GetValue<string>(), cancellationToken).ConfigureAwait(false)).SingleOrDefault();
            changed = row is null || row is GraphRagFact fact && created["after"] is { } after && fact.DocumentIds != after.GetValue<string>();
        }
        foreach (var updated in undo["updated"]!.AsArray())
        {
            if (changed) break;
            if (updated!["after"] is not { } after) continue;
            var (_, clr) = Resolve(ontology, "fact", updated["index"]!.GetValue<int>());
            changed = await GraphRagResolution.Typed.For(clr).PairAsync(db, updated["from"]!.GetValue<string>(), updated["to"]!.GetValue<string>(), cancellationToken)
                .ConfigureAwait(false) is not GraphRagFact fact || fact.DocumentIds != after.GetValue<string>();
        }
        if (!changed && undo.ContainsKey("keepAliasesAfter"))
            changed = (await GraphRagResolution.Typed.For(type.ClrType).WhereAsync(db, nameof(GraphRagEntity.Id), undo["keepId"]!.GetValue<string>(), cancellationToken)
                    .ConfigureAwait(false)).SingleOrDefault() is not GraphRagEntity kept
                || kept.Aliases != undo["keepAliasesAfter"]?.GetValue<string>() || kept.Description != undo["keepDescriptionAfter"]?.GetValue<string>();
        db.ChangeTracker.Clear();
        if (changed)
            throw new InvalidOperationException($"Merge run '{runId}' can't be undone: what it wrote was changed since, by a later merge into '{undo["keepId"]}' " +
                "or by ingesting a document that states one of its facts. Undo later merge runs first, last first. Nothing was undone.");

        var (mentions, facts, quotes) = (0, 0, 0);
        foreach (var created in undo["created"]!.AsArray())
        {
            var (role, clr) = Resolve(ontology, created!["role"]!.GetValue<string>(), created["index"]!.GetValue<int>());
            var typed = GraphRagResolution.Typed.For(clr);
            var row = role == "fact"
                ? await typed.PairAsync(db, created["from"]!.GetValue<string>(), created["to"]!.GetValue<string>(), cancellationToken).ConfigureAwait(false)
                : (await typed.WhereAsync(db, nameof(GraphRagMention.Id), created["id"]!.GetValue<string>(), cancellationToken).ConfigureAwait(false)).SingleOrDefault();
            if (row is not null) db.Remove(row);
        }
        foreach (var updated in undo["updated"]!.AsArray())
        {
            var (_, clr) = Resolve(ontology, "fact", updated!["index"]!.GetValue<int>());
            if (await GraphRagResolution.Typed.For(clr).PairAsync(db, updated["from"]!.GetValue<string>(), updated["to"]!.GetValue<string>(), cancellationToken)
                    .ConfigureAwait(false) is GraphRagFact fact)
                fact.DocumentIds = updated["documentIds"]!.GetValue<string>();
        }
        if ((await GraphRagResolution.Typed.For(type.ClrType).WhereAsync(db, nameof(GraphRagEntity.Id), undo["keepId"]!.GetValue<string>(), cancellationToken)
                .ConfigureAwait(false)).SingleOrDefault() is GraphRagEntity keep)
            (keep.Aliases, keep.Description) = (undo["keepAliases"]?.GetValue<string>(), undo["keepDescription"]?.GetValue<string>());
        Restore(db, type.ClrType, undo["merged"]!.AsObject());
        foreach (var removed in undo["removed"]!.AsArray())
        {
            var (role, clr) = Resolve(ontology, removed!["role"]!.GetValue<string>(), removed["index"]!.GetValue<int>());
            Restore(db, clr, removed["values"]!.AsObject());
            if (role == "mention") mentions++; else if (role == "fact") facts++; else quotes++;
        }
        db.Remove(run);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        db.ChangeTracker.Clear();
        return new(mentions, facts, quotes);
    }

    private sealed record Merged(string RunId, int Mentions, int Moved, int Joined, int Dropped, int Quotes);

    // The element the database refused, when the failed save names one: its entity type and key, and the provider's step.
    private static string At(Exception error)
    {
        if (error is not DbUpdateException { Entries: [var entry] }) return "";
        var key = entry.Metadata.FindPrimaryKey()?.Properties.Select(p => $"{p.Name}={entry.Property(p.Name).CurrentValue}") ?? [];
        var step = error.Data["GraphBridge.MutationStep"] is string name ? $" ({name}: {error.Data["GraphBridge.MutationResult"]})" : "";
        return $" at {entry.Metadata.DisplayName()} ({entry.State}) {string.Join(", ", key)}{step}";
    }

    private static async Task<Merged> MergeAsync(DbContext db, GraphRagOntology ontology, GraphRagMergeSuggestion merge, string runId,
        CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        var type = ontology.Classes.Single(c => c.Kind == merge.Kind);
        var typed = GraphRagResolution.Typed.For(type.ClrType);
        var keep = (GraphRagEntity)(await typed.WhereAsync(db, nameof(GraphRagEntity.Id), merge.KeepId, cancellationToken).ConfigureAwait(false)).Single();
        var merged = (GraphRagEntity)(await typed.WhereAsync(db, nameof(GraphRagEntity.Id), merge.MergeId, cancellationToken).ConfigureAwait(false)).Single();
        var (created, removed, updated) = (new JsonArray(), new JsonArray(), new JsonArray());
        var undo = new JsonObject
        {
            ["ontology"] = ontology.Fingerprint, ["kind"] = type.Kind, ["keepId"] = keep.Id, ["mergeId"] = merged.Id, ["keepAliases"] = keep.Aliases,
            ["keepDescription"] = keep.Description,
            ["merged"] = Snapshot(db.Entry(merged)), ["created"] = created, ["removed"] = removed, ["updated"] = updated
        };
        var (mentionRows, factRows, quoteRows) = await TouchingAsync(db, ontology, type, merged.Id, cancellationToken).ConfigureAwait(false);
        var classIndex = ontology.Classes.ToList().IndexOf(type);

        // Mentions and quotations move to the kept thing, keeping their bytes; their IDs hash the chunk, the start and the thing.
        var mentionTyped = GraphRagResolution.Typed.For(type.MentionClrType);
        var mentions = 0;
        foreach (GraphRagMention mention in mentionRows)
        {
            removed.Add(Row("mention", classIndex, db.Entry(mention)));
            var id = GraphRagEmbeddingSet.Hash(string.Join('\u001f', mention.ChunkId, mention.StartByte, keep.Id));
            if ((await mentionTyped.WhereAsync(db, nameof(GraphRagMention.Id), id, cancellationToken).ConfigureAwait(false)).Count == 0)
            {
                var copy = (GraphRagMention)Copy(db, mention);
                (copy.Id, copy.EntityId) = (id, keep.Id);
                db.Add(copy);
                created.Add(new JsonObject { ["role"] = "mention", ["index"] = classIndex, ["id"] = id });
                mentions++;
            }
            db.Remove(mention);
        }
        var quotes = 0;
        foreach (var (index, rows) in quoteRows)
        {
            var quoteTyped = GraphRagResolution.Typed.For(ontology.Quotes[index].ClrType);
            foreach (GraphRagQuote quote in rows)
            {
                removed.Add(Row("quote", index, db.Entry(quote)));
                var id = GraphRagEmbeddingSet.Hash(string.Join('\u001f', quote.ChunkId, quote.StartByte, keep.Id));
                if ((await quoteTyped.WhereAsync(db, nameof(GraphRagQuote.Id), id, cancellationToken).ConfigureAwait(false)).Count == 0)
                {
                    var copy = (GraphRagQuote)Copy(db, quote);
                    (copy.Id, copy.SpeakerId) = (id, keep.Id);
                    db.Add(copy);
                    created.Add(new JsonObject { ["role"] = "quote", ["index"] = index, ["id"] = id });
                    quotes++;
                }
                db.Remove(quote);
            }
        }

        // Facts are re-pointed; one the kept thing already states gets the union of the documents; one joining it to itself goes.
        var (moved, joined, dropped) = (0, 0, 0);
        // The facts this merge writes, to record their document lists as it leaves them: an undo checks them first.
        var written = new List<(JsonObject Entry, GraphRagFact Fact)>();
        foreach (var (index, rows) in factRows)
        {
            var factTyped = GraphRagResolution.Typed.For(ontology.Relationships[index].ClrType);
            var added = new Dictionary<(string, string), GraphRagFact>();
            foreach (GraphRagFact fact in rows)
            {
                removed.Add(Row("fact", index, db.Entry(fact)));
                db.Remove(fact);
                var (from, to) = (fact.FromId == merged.Id ? keep.Id : fact.FromId, fact.ToId == merged.Id ? keep.Id : fact.ToId);
                if (from == to) { dropped++; continue; }
                if (added.TryGetValue((from, to), out var pending)) { pending.DocumentIds = GraphRagOntologyRuntime.Union(pending.DocumentIds, fact.DocumentIds)!; joined++; continue; }
                if (await factTyped.PairAsync(db, from, to, cancellationToken).ConfigureAwait(false) is GraphRagFact existing)
                {
                    if (!updated.Any(u => u!["index"]!.GetValue<int>() == index && u["from"]!.GetValue<string>() == from && u["to"]!.GetValue<string>() == to))
                    {
                        var entry = new JsonObject { ["index"] = index, ["from"] = from, ["to"] = to, ["documentIds"] = existing.DocumentIds };
                        updated.Add(entry);
                        written.Add((entry, existing));
                    }
                    existing.DocumentIds = GraphRagOntologyRuntime.Union(existing.DocumentIds, fact.DocumentIds)!;
                    joined++;
                    continue;
                }
                var copy = (GraphRagFact)Copy(db, fact);
                (copy.FromId, copy.ToId) = (from, to);
                db.Add(copy);
                added[(from, to)] = copy;
                var record = new JsonObject { ["role"] = "fact", ["index"] = index, ["from"] = from, ["to"] = to };
                created.Add(record);
                written.Add((record, copy));
                moved++;
            }
        }

        if (ontology.Classes.Single(c => c.Kind == merge.Kind).Aliases)
        {
            var aliases = GraphRagOntologyRuntime.Union(keep.Aliases, GraphRagOntologyRuntime.Union(merged.Name, merged.Aliases));
            keep.Aliases = string.Join('\n', GraphRagOntologyRuntime.Lines(aliases).Where(a => a != keep.Name)) is { Length: > 0 } list ? list : null;
        }
        keep.Description ??= merged.Description;
        foreach (var (entry, fact) in written) entry["after"] = fact.DocumentIds;
        (undo["keepAliasesAfter"], undo["keepDescriptionAfter"]) = (keep.Aliases, keep.Description);
        db.Remove(merged);
        var run = new GraphRagIngestionRun
        {
            Id = runId, StartedUtc = DateTimeOffset.UtcNow, CompletedUtc = DateTimeOffset.UtcNow, Status = "Completed",
            SettingsFingerprint = "merge:" + type.Kind, Undo = undo.ToJsonString()
        };
        db.Add(run);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        db.ChangeTracker.Clear();
        return new(run.Id, mentions, moved, joined, dropped, quotes);
    }

    // The merged thing's mentions, the facts that touch it, by relationship index, and its quotations, by quote index.
    private static async Task<(List<object> Mentions, List<(int Index, List<object> Rows)> Facts, List<(int Index, List<object> Rows)> Quotes)> TouchingAsync(
        DbContext db, GraphRagOntology ontology, GraphRagOntologyClass type, string id, CancellationToken cancellationToken)
    {
        var mentions = await GraphRagResolution.Typed.For(type.MentionClrType).WhereAsync(db, nameof(GraphRagMention.EntityId), id, cancellationToken).ConfigureAwait(false);
        var facts = new List<(int, List<object>)>();
        for (var i = 0; i < ontology.Relationships.Count; i++)
        {
            var relationship = ontology.Relationships[i];
            var typed = GraphRagResolution.Typed.For(relationship.ClrType);
            var rows = new List<object>();
            if (relationship.FromKind == type.Kind) rows.AddRange(await typed.WhereAsync(db, nameof(GraphRagFact.FromId), id, cancellationToken).ConfigureAwait(false));
            if (relationship.ToKind == type.Kind)
                rows.AddRange((await typed.WhereAsync(db, nameof(GraphRagFact.ToId), id, cancellationToken).ConfigureAwait(false)).Where(r => !rows.Contains(r)));
            if (rows.Count > 0) facts.Add((i, rows));
        }
        var quotes = new List<(int, List<object>)>();
        for (var i = 0; i < ontology.Quotes.Count; i++)
            if (ontology.Quotes[i].SpeakerKind == type.Kind
                && await GraphRagResolution.Typed.For(ontology.Quotes[i].ClrType).WhereAsync(db, nameof(GraphRagQuote.SpeakerId), id, cancellationToken).ConfigureAwait(false) is { Count: > 0 } rows)
                quotes.Add((i, rows));
        return (mentions, facts, quotes);
    }

    private static (string Role, Type Clr) Resolve(GraphRagOntology ontology, string role, int index) => role switch
    {
        "mention" => (role, ontology.Classes[index].MentionClrType),
        "fact" => (role, ontology.Relationships[index].ClrType),
        "quote" => (role, ontology.Quotes[index].ClrType),
        _ => throw new InvalidOperationException($"The merge record names an unknown row kind '{role}'.")
    };

    private static JsonObject Row(string role, int index, EntityEntry entry) => new() { ["role"] = role, ["index"] = index, ["values"] = Snapshot(entry) };

    private static JsonObject Snapshot(EntityEntry entry)
        => new(entry.Properties.Where(p => !p.Metadata.IsShadowProperty()).Select(p => KeyValuePair.Create(p.Metadata.Name, JsonSerializer.SerializeToNode(p.CurrentValue))));

    private static object Copy(DbContext db, object source)
    {
        var copy = Activator.CreateInstance(source.GetType())!;
        var target = db.Entry(copy);
        foreach (var property in db.Entry(source).Properties.Where(p => !p.Metadata.IsShadowProperty())) target.Property(property.Metadata.Name).CurrentValue = property.CurrentValue;
        return copy;
    }

    private static void Restore(DbContext db, Type type, JsonObject values)
    {
        var entity = Activator.CreateInstance(type)!;
        var entry = db.Entry(entity);
        foreach (var (name, value) in values) entry.Property(name).CurrentValue = value?.Deserialize(entry.Property(name).Metadata.ClrType);
        db.Add(entity);
    }
}
