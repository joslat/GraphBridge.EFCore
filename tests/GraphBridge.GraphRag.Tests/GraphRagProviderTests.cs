using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text.Json;
using GraphBridge.GraphRag.Ontologies;
using Microsoft.Extensions.AI;
using OpenAI;

namespace GraphBridge.GraphRag.Tests;

/// <summary>
/// GraphRAG v2, wave 7 (GR-48), offline: what Microsoft.Extensions.AI's OpenAI client actually sends for an extraction call.
/// The request is captured at the HTTP layer and answered with a canned reply; no model is called.
/// </summary>
public sealed class GraphRagProviderTests
{
    private const string Passage = "Alice met the White Rabbit near the river.";

    [Fact]
    public async Task The_OpenAI_client_sends_the_extraction_schema_as_strict_json_schema_and_not_when_strict_is_off()
    {
        var strict = await ExtractAsync(new GraphRagExtractionOptions());
        var format = strict.GetProperty("response_format");
        Assert.Equal("json_schema", format.GetProperty("type").GetString());
        Assert.Equal("graphrag_extraction", format.GetProperty("json_schema").GetProperty("name").GetString());
        Assert.True(format.GetProperty("json_schema").GetProperty("strict").GetBoolean());

        var loose = await ExtractAsync(new GraphRagExtractionOptions { StrictSchema = false });
        var looseSchema = loose.GetProperty("response_format").GetProperty("json_schema");
        Assert.False(looseSchema.TryGetProperty("strict", out var flag) && flag.ValueKind == JsonValueKind.True);
    }

    [Fact]
    public async Task A_reasoning_effort_set_on_the_extraction_client_reaches_the_request()
    {
        var request = await ExtractAsync(new GraphRagExtractionOptions(), chat => chat.AsBuilder()
            .ConfigureOptions(options => options.Reasoning = new ReasoningOptions { Effort = ReasoningEffort.Low }).Build());
        Assert.Equal("low", request.GetProperty("reasoning_effort").GetString());
    }

    private static async Task<JsonElement> ExtractAsync(GraphRagExtractionOptions options, Func<IChatClient, IChatClient>? wrap = null)
    {
        var handler = new CapturingHandler();
        var client = new OpenAIClient(new ApiKeyCredential("test-key"), new OpenAIClientOptions
        {
            Endpoint = new Uri("https://models.example.invalid/v1"), Transport = new HttpClientPipelineTransport(new HttpClient(handler)),
            RetryPolicy = new ClientRetryPolicy(0)
        }).GetChatClient("test-model").AsIChatClient();
        using var db = new GraphRagOntologyTests.OntologyContext(GraphRagOntologies.Novel);
        var extractor = new GraphRagExtractor(GraphRagModelSchema.From(db.Model), wrap?.Invoke(client) ?? client, options);
        var items = new[] { new GraphRagEvidenceItem("chunk-0", "doc-1", Passage, 0, Passage.Length, 0, []) };
        var tally = new GraphRagExtractionTally();
        await extractor.ExtractAsync("doc-1", null, "run-1", items, tally, null, CancellationToken.None);
        Assert.Equal((1, 0), (tally.Calls, tally.Quarantined.Count));
        return JsonDocument.Parse(Assert.Single(handler.Bodies)).RootElement.Clone();
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private const string Reply = """
            {"id": "chatcmpl-test", "object": "chat.completion", "created": 0, "model": "test-model",
             "choices": [{"index": 0, "finish_reason": "stop", "message": {"role": "assistant",
               "content": "{\"entities\": [], \"relationships\": [], \"quotes\": []}"}}],
             "usage": {"prompt_tokens": 10, "completion_tokens": 5, "total_tokens": 15}}
            """;

        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(Reply, System.Text.Encoding.UTF8, "application/json") };
        }
    }
}
