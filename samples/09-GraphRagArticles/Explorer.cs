using System.Text.Json;
using GraphBridge.GraphRag;
using GraphBridge.GraphRag.Ontologies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using OrganizationEntity = GraphBridge.GraphRag.Ontologies.Organization;

namespace GraphBridge.Samples.GraphRagArticles;

/// <summary>
/// The explorer page (GR-56): ask, watch the answer stream in, and open a citation to see its passage, the quoted span and the
/// things and facts extracted from that passage. The page is static and calls the toolkit's endpoints, plus one route of the
/// sample's own for a passage. Like the endpoints, it is for this machine: add authentication before serving anyone else.
/// </summary>
public static class Explorer
{
    /// <summary><c>GET /explorer</c>, the page, and <c>GET /explorer/passages/{id}</c>, a passage with its graph.</summary>
    public static IEndpointRouteBuilder MapExplorer(this IEndpointRouteBuilder app)
    {
        var page = Path.Combine(AppContext.BaseDirectory, "explorer.html");
        app.MapGet("/explorer", () => Results.File(page, "text/html; charset=utf-8"));
        app.MapGet("/explorer/passages/{id}", async (string id, HttpContext http, CancellationToken cancellationToken) =>
            await PassageAsync(http.RequestServices, id, cancellationToken) is { } passage ? Results.Ok(passage) : Results.NotFound());
        return app;
    }

    /// <summary>
    /// A passage, its article, the things mentioned in it with each mention's bytes, the quotations in it, and the facts that
    /// join two of its things (or a thing and its article) and that its article states. Facts come from the toolkit's
    /// <c>related_facts</c> tool, so the page shows what an agent would see. Null when there is no such passage.
    /// </summary>
    public static async Task<ExplorerPassage?> PassageAsync(IServiceProvider services, string chunkId, CancellationToken cancellationToken = default)
    {
        var db = services.GetRequiredService<ArticlesContext>();
        var chunk = await db.Set<GraphRagChunk>().Where(c => c.Id == chunkId)
            .Select(c => new { c.Id, c.DocumentId, c.Text, c.StartByte, c.EndByte }).SingleOrDefaultAsync(cancellationToken);
        if (chunk is null) return null;
        var article = await db.Set<GraphRagDocument>().Where(d => d.Id == chunk.DocumentId)
            .Select(d => new { d.Title, d.Url, d.Publisher, d.Published }).SingleAsync(cancellationToken);

        var things = new Dictionary<string, ExplorerThing>(StringComparer.Ordinal);
        await AddAsync<Person>("person");
        await AddAsync<OrganizationEntity>("organization");
        await AddAsync<Place>("place");
        await AddAsync<Event>("event");
        await AddAsync<Concept>("concept");
        await AddAsync<Topic>("topic");

        var quotes = (await db.Set<Quoted>().Where(q => q.ChunkId == chunkId).ToListAsync(cancellationToken))
            .OrderBy(q => q.StartByte).Select(q => new ExplorerQuote(q.SpeakerId, q.Words, q.StartByte, q.EndByte)).ToArray();

        var ends = things.Keys.Append(chunk.DocumentId).ToHashSet(StringComparer.Ordinal);
        var related = GraphRagTools.Create<ArticlesContext>(services).Single(t => t.Name == GraphRagTools.RelatedFactsTool);
        var facts = new Dictionary<(string, string, string), ExplorerFact>();
        foreach (var id in things.Keys)
            foreach (var fact in Facts(await related.InvokeAsync(new AIFunctionArguments { ["thingId"] = id }, cancellationToken).ConfigureAwait(false)))
                if (ends.Contains(fact.FromId) && ends.Contains(fact.ToId) && fact.DocumentIds.Contains(chunk.DocumentId))
                    facts.TryAdd((fact.Type, fact.FromId, fact.ToId), new(fact.Type, fact.FromId, fact.ToId));

        return new(chunk.Id, chunk.DocumentId, article.Title, article.Url, article.Publisher, article.Published, chunk.StartByte, chunk.EndByte, chunk.Text,
            [.. things.Values.OrderBy(t => t.Kind, StringComparer.Ordinal).ThenBy(t => t.Name, StringComparer.Ordinal)], quotes,
            [.. facts.Values.OrderBy(f => f.Type, StringComparer.Ordinal).ThenBy(f => f.FromId, StringComparer.Ordinal).ThenBy(f => f.ToId, StringComparer.Ordinal)]);

        async Task AddAsync<T>(string kind) where T : GraphRagEntity
        {
            var mentions = await db.Set<GraphRagMention<T>>().Where(m => m.ChunkId == chunkId).ToListAsync(cancellationToken);
            if (mentions.Count == 0) return;
            var ids = mentions.Select(m => m.EntityId).Distinct().ToArray();
            var names = await db.Set<T>().Where(e => ids.Contains(e.Id)).Select(e => new { e.Id, e.Name }).ToDictionaryAsync(e => e.Id, e => e.Name, cancellationToken);
            foreach (var group in mentions.GroupBy(m => m.EntityId))
                things[group.Key] = new(group.Key, kind, names.GetValueOrDefault(group.Key, "?"),
                    [.. group.OrderBy(m => m.StartByte).Select(m => new ExplorerSpan(m.StartByte, m.EndByte, m.Quote))]);
        }
    }

    // The tool returns its facts serialized, as a model receives them.
    private static IReadOnlyList<GraphRagToolFact> Facts(object? result) => result switch
    {
        IReadOnlyList<GraphRagToolFact> list => list,
        JsonElement json => json.Deserialize<List<GraphRagToolFact>>(AIJsonUtilities.DefaultOptions) ?? [],
        _ => []
    };
}

/// <summary>A passage of an article, with its byte span in the article, and what was extracted from it.</summary>
public sealed record ExplorerPassage(string ChunkId, string DocumentId, string? Title, string? Url, string? Publisher, string? Published,
    int StartByte, int EndByte, string Text, IReadOnlyList<ExplorerThing> Things, IReadOnlyList<ExplorerQuote> Quotes, IReadOnlyList<ExplorerFact> Facts);

/// <summary>A thing mentioned in the passage, and each mention's bytes in the article.</summary>
public sealed record ExplorerThing(string Id, string Kind, string Name, IReadOnlyList<ExplorerSpan> Mentions);

public sealed record ExplorerSpan(int StartByte, int EndByte, string Quote);

public sealed record ExplorerQuote(string SpeakerId, string Words, int StartByte, int EndByte);

/// <summary>A fact between two of the passage's things, or between a thing and its article.</summary>
public sealed record ExplorerFact(string Type, string FromId, string ToId);
