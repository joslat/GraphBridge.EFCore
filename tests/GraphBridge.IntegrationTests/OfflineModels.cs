using System.Runtime.CompilerServices;

namespace GraphBridge.IntegrationTests;

/// <summary>
/// The GraphRAG samples choose a model from <c>AI_INFERENCE_PROVIDER</c>, which a developer machine may set for every tool.
/// This suite runs the samples offline, with their stand-ins, so it never calls a model: the variable is cleared for this
/// process before any sample reads it. The samples with a real model's recorded calls are GraphBridge.GraphRag.RealModelTests.
/// </summary>
internal static class OfflineModels
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        Environment.SetEnvironmentVariable("AI_INFERENCE_PROVIDER", null);
        Environment.SetEnvironmentVariable("GRAPHBRIDGE_AI_MODE", null);
    }
}
