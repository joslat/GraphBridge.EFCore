using System.Runtime.CompilerServices;
using GraphBridge.Samples.GraphRagShared;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace GraphBridge.GraphRag.RealModelTests;

/// <summary>
/// How this process reaches a model (ADR 0052, section 11). With <c>GRAPHBRIDGE_AI_RECORD=1</c> and a provider named, as in
/// <c>AI_INFERENCE_PROVIDER=bitdeer</c> with <c>BITDEER_API_KEY</c>, it records: requests already
/// in the sample's recordings are replayed, the others go to the real model within <c>GRAPHBRIDGE_AI_MAX_CALLS</c> (40 by
/// default) and are written to the recordings in the repository. <c>GRAPHBRIDGE_AI_FRESH=1</c> ignores what's recorded and
/// records into a scratch file instead. Without an endpoint it replays, at zero calls, and a request that wasn't recorded fails the test.
/// </summary>
public static class ModelProfile
{
    private static readonly ModelCallBudget Budget = new(int.TryParse(Environment.GetEnvironmentVariable("GRAPHBRIDGE_AI_MAX_CALLS"), out var max) ? max : 40);

    /// <summary>
    /// Runs before the samples read their settings. Unless this run records, they replay their recordings: a provider named
    /// for every tool on a developer machine doesn't make a test run spend.
    /// </summary>
    [ModuleInitializer]
    internal static void Initialize()
    {
        Records = Environment.GetEnvironmentVariable("GRAPHBRIDGE_AI_RECORD") == "1"
            && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AI_INFERENCE_PROVIDER"));
        if (Records) return;
        Environment.SetEnvironmentVariable("AI_INFERENCE_PROVIDER", null);
        Environment.SetEnvironmentVariable("GRAPHBRIDGE_AI_MODE", "replay");
    }

    /// <summary>Whether this run records: <c>GRAPHBRIDGE_AI_RECORD=1</c> and a provider named.</summary>
    public static bool Records { get; private set; }

    /// <summary>Real calls made so far by this process.</summary>
    public static int Calls => Budget.Calls;

    /// <summary>
    /// When recording, puts recording clients over the sample's own, writing to <paramref name="sourceRecordings"/> in the
    /// repository. When replaying, the sample's own clients already replay, and nothing changes.
    /// </summary>
    public static IServiceCollection Record(this IServiceCollection services, SampleModelSettings? models, string sourceRecordings)
    {
        if (!Records) return services;
        ArgumentNullException.ThrowIfNull(models);
        // A fresh run records into a scratch file, so it calls the model for every request and leaves the repository alone.
        var recordings = Environment.GetEnvironmentVariable("GRAPHBRIDGE_AI_FRESH") == "1"
            ? ModelRecordings.Load(Path.Combine(Path.GetTempPath(), "graphbridge-fresh-" + Guid.NewGuid().ToString("N") + "-" + Path.GetFileName(sourceRecordings)))
            : ModelRecordings.Load(sourceRecordings);
        var live = new OpenAiCompatibleClient(models);
        return services
            .AddSingleton(models.WithReasoning(new RecordedChatClient(recordings, live, models.ChatModel, Budget, provider: models.Provider)))
            .AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(new RecordedEmbeddingGenerator(recordings, live, models.EmbeddingModel, Budget, models.Provider));
    }

    /// <summary>A file of the repository, found from the test's output folder.</summary>
    public static string Repository(params string[] path)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "GraphBridge.sln"))) directory = directory.Parent;
        return Path.Combine([directory?.FullName ?? throw new InvalidOperationException("The repository root was not found."), .. path]);
    }
}

/// <summary>A test that needs a real model: it runs only when recording, never in the default suites or CI.</summary>
public sealed class RealModelFactAttribute : FactAttribute
{
    public RealModelFactAttribute()
    {
        if (!ModelProfile.Records) Skip = "Needs a real model: set GRAPHBRIDGE_AI_RECORD=1 and AI_INFERENCE_PROVIDER with its key, such as bitdeer and BITDEER_API_KEY.";
        else if (Environment.GetEnvironmentVariable("GRAPHBRIDGE_INTEGRATION") != "1") Skip = "Live disposable Neo4j fixture required; set GRAPHBRIDGE_INTEGRATION=1.";
    }
}
