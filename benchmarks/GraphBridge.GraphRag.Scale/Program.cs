using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using GraphBridge.GraphRag;
using GraphBridge.GraphRag.Scale;
using GraphBridge.Samples.GraphRagShared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Neo4j.Driver;

// GraphRAG v2, GR-59: measures the toolkit on a disposable database, inside the 5 GiB memory reserve, for the
// "Limits and performance" page (docs/graphrag/LIMITS.md).
//   generated [documents]  a deterministic corpus, no download and no model: hashed embeddings
//   folder path            Markdown files; real embeddings when AI_INFERENCE_PROVIDER names a provider
// Each run uses labels of its own and removes everything it wrote. Results go to .artifacts/graphrag-scale.
const string usage = "Usage: dotnet run --project benchmarks/GraphBridge.GraphRag.Scale -- generated [documents] | folder path";
var names = new[] { "GRAPHBRIDGE_NEO4J_URI", "GRAPHBRIDGE_NEO4J_DATABASE", "GRAPHBRIDGE_NEO4J_USERNAME", "GRAPHBRIDGE_NEO4J_PASSWORD" };
var settings = names.Select(Environment.GetEnvironmentVariable).ToArray();
if (args is not (["generated"] or ["generated", _] or ["folder", _]) || args is ["generated", var n] && !int.TryParse(n, out _))
{
    Console.Error.WriteLine(usage);
    return 2;
}
if (settings.Any(string.IsNullOrWhiteSpace))
{
    Console.Error.WriteLine("Set the four GRAPHBRIDGE_NEO4J_* variables to a disposable Neo4j 2026.09 or later database first.");
    return 2;
}

var models = args[0] == "folder" ? SampleModelSettings.FromEnvironment(Path.Combine(AppContext.BaseDirectory, "unused-recordings.json")) : null;
if (models?.ReplayFile is not null) models = null;   // a measurement asks the model or uses the stand-in; it never replays
var set = models is null ? new GraphRagEmbeddingSet("graphbridge-sample-hashing", 256) : new GraphRagEmbeddingSet(models.EmbeddingModel, models.EmbeddingDimensions);
Scale.Dimensions = set.Dimensions;
var sources = args[0] == "generated"
    ? GeneratedCorpus.Create(args.Length == 2 ? int.Parse(args[1], CultureInfo.InvariantCulture) : 2_000)
    : GraphRagSources.Folder(args[1], recursive: true).ToArray();
var queries = args[0] == "generated" ? GeneratedCorpus.Queries(200) : FolderQueries(sources, 10);

Memory.Require("start");
await using var driver = GraphDatabase.Driver(settings[0], AuthTokens.Basic(settings[2], settings[3]));
var services = new ServiceCollection().AddDbContext<ScaleContext>(o => o.UseNeo4j(driver, settings[1]!));
IEmbeddingGenerator<string, Embedding<float>> generator;
if (models is not null)
{
    var (chat, embeddings) = models.CreateClients();
    services.AddSingleton(chat);
    generator = embeddings;
}
else generator = new HashingEmbedder(set.Dimensions);
services.AddSingleton(generator);
services.AddGraphRag<ScaleContext>(set, o => o.Ingestion.MaxDocuments = 100_000);
await using var provider = services.BuildServiceProvider();

var results = new Dictionary<string, object?>
{
    ["corpus"] = args[0] == "generated" ? $"generated, {sources.Count} documents" : $"folder {args[1]}",
    ["embeddings"] = models is null ? "hashed (no model)" : models.EmbeddingModel,
    ["date"] = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
    ["machine"] = $"{Environment.OSVersion.VersionString}, {Environment.ProcessorCount} logical processors",
    ["inputMegabytes"] = Math.Round((args[0] == "generated" ? GeneratedCorpus.TotalBytes : sources.Sum(s => new FileInfo(s.Path!).Length)) / (double)(1 << 20), 2)
};
// The schema steps run directly, not through a migration journal, so the harness leaves no history behind.
await Scale.CreateAsync(driver, settings[1]!, GraphRagSchema.CreateSteps(Scale.Layout, set));
var ingestion = provider.GetRequiredService<IGraphRagIngestion>();
var maintenance = provider.GetRequiredService<IGraphRagMaintenance>();
var retriever = provider.GetRequiredService<IGraphRagRetriever>();
try
{
    var timer = Stopwatch.StartNew();
    var receipt = await ingestion.IngestAsync(sources);
    var ingest = timer.Elapsed;
    Memory.Require("after ingestion");
    results["documents"] = receipt.Documents.Count(d => d.Status == GraphRagDocumentStatus.Ingested);
    results["failed"] = receipt.Documents.Count(d => d.Status == GraphRagDocumentStatus.Failed);
    results["chunks"] = receipt.ChunksWritten;
    results["embeddingCalls"] = receipt.EmbeddingCalls;
    results["embeddingInputTokens"] = receipt.InputTokens;
    results["ingestSeconds"] = Math.Round(ingest.TotalSeconds, 1);
    results["documentsPerSecond"] = Math.Round(receipt.Documents.Count / ingest.TotalSeconds, 1);
    results["chunksPerSecond"] = Math.Round(receipt.ChunksWritten / ingest.TotalSeconds, 1);

    results["retrieval"] = await LatencyAsync(queries, q => retriever.RetrieveAsync(q));
    var someDocuments = receipt.Documents.Take(10).Select(d => d.DocumentId).ToArray();
    results["scopedRetrieval"] = await LatencyAsync(queries.Take(50).ToArray(),
        q => retriever.RetrieveAsync(q, new GraphRagRetrievalOptions { Scope = GraphRagScope.Documents(someDocuments) }));
    Memory.Require("after retrieval");

    timer.Restart();
    var again = await ingestion.IngestAsync(sources);
    results["reingestUnchangedSeconds"] = Math.Round(timer.Elapsed.TotalSeconds, 1);
    results["reingestUnchanged"] = again.Documents.Count(d => d.Status == GraphRagDocumentStatus.Unchanged);

    timer.Restart();
    var health = await maintenance.CheckAsync();
    results["healthSeconds"] = Math.Round(timer.Elapsed.TotalSeconds, 1);
    results["healthFindings"] = health.Findings;

    var hundred = receipt.Documents.Skip(10).Take(100).Select(d => d.DocumentId).ToArray();
    timer.Restart();
    var removed = await maintenance.ResetAsync(new GraphRagResetScope(DocumentIds: hundred));
    results["remove100Seconds"] = Math.Round(timer.Elapsed.TotalSeconds, 1);
    results["remove100Chunks"] = removed.Chunks;

    if (models is null)
    {
        // A re-embedding backfill onto a staging property, as GraphRagReembedding plans it; the harness stops before cut-over.
        var next = new GraphRagEmbeddingSet("graphbridge-sample-hashing-128", 128);
        var reembedding = GraphRagReembedding.Plan(Scale.Layout, set, next, "EmbeddingNext");
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ScaleContext>();
        await Scale.CreateAsync(driver, settings[1]!, reembedding.PrepareSteps);
        timer.Restart();
        var backfilled = await reembedding.BackfillAsync(db, new HashingEmbedder(128), batchSize: 256);
        results["reembedSeconds"] = Math.Round(timer.Elapsed.TotalSeconds, 1);
        results["reembedChunks"] = backfilled;
        Memory.Require("after re-embedding");
    }
}
finally
{
    var timer = Stopwatch.StartNew();
    var all = await maintenance.ResetAsync(new GraphRagResetScope());
    results["resetSeconds"] = Math.Round(timer.Elapsed.TotalSeconds, 1);
    results["resetChunks"] = all.Chunks;
    await Scale.DropAsync(driver, settings[1]!, set);
}

results["peakWorkingSetMegabytes"] = Process.GetCurrentProcess().PeakWorkingSet64 >> 20;
var json = JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true });
Console.WriteLine(json);
var folder = Path.Combine(Scale.Repository(), ".artifacts", "graphrag-scale");
Directory.CreateDirectory(folder);
await File.WriteAllTextAsync(Path.Combine(folder, $"{args[0]}-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json"), json);
return 0;

static async Task<object> LatencyAsync(IReadOnlyList<string> queries, Func<string, Task<GraphRagEvidence>> retrieve)
{
    await retrieve(queries[0]);   // warm-up, not counted
    var times = new List<double>();
    var items = 0;
    foreach (var query in queries)
    {
        var timer = Stopwatch.StartNew();
        var evidence = await retrieve(query);
        times.Add(timer.Elapsed.TotalMilliseconds);
        items += evidence.Items.Count;
    }
    times.Sort();
    double At(double p) => Math.Round(times[(int)Math.Min(times.Count - 1, Math.Ceiling(p * times.Count) - 1)], 1);
    return new { queries = times.Count, p50Milliseconds = At(0.50), p95Milliseconds = At(0.95), maxMilliseconds = Math.Round(times[^1], 1), itemsPerQuery = Math.Round((double)items / times.Count, 1) };
}

// The first sentence of some files' paragraphs, as questions that have an answer somewhere in the corpus.
static string[] FolderQueries(IReadOnlyList<GraphRagSource> sources, int count)
    => sources.Select(s => File.ReadAllText(s.Path!)).SelectMany(t => t.Split("\n\n"))
        .Where(p => p.Length > 80 && char.IsLetter(p[0])).Select(p => p.Split(". ")[0]).Where(p => p.Length is > 30 and < 200)
        .Where((_, i) => i % 37 == 0).Take(count).ToArray();
